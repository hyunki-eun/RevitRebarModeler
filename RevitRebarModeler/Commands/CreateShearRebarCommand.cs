using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

using RevitRebarModeler.Models;

namespace RevitRebarModeler.Commands
{
    /// <summary>
    /// 전단철근 배치 (Phase 2).
    ///
    /// 동작:
    /// 1. SessionCache의 종방향 철근 설정을 사용하여 구조도별 단(段) 위치 산출
    ///    (종방향 배치와 동일한 기준 곡선 + CTC 샘플링 사용).
    /// 2. 사용자가 지정한 묶음 수로 횡철근을 분할.
    ///    - 횡철근은 JSON 저장 순서 (앞 절반=내측, 뒤 절반=외측) 그대로 사용.
    /// 3. 홀수 단마다 시작 묶음(A 또는 B)부터 교대로 묶음 매핑:
    ///    1단=A, 3단=B, 5단=A, 7단=B, ... (사용자가 시작=B로 정하면 반대)
    /// 4. 각 (단, 묶음) 쌍에 대해 U자형 고리 4점 + 후크 2점 좌표를 계산하여
    ///    Revit Rebar (Standard → FreeForm 폴백) 로 생성.
    ///
    /// 형상:
    /// - 정면 평면 = "외측에서 본 단면" — 종축(extrude 방향) × 횡축(접선 방향)
    /// - 가로 = 묶음의 첫 횡철근 ↔ 마지막 횡철근 거리
    /// - 세로 = 종방향 철근 직경 (사용자가 종방향 배치할 때 설정한 값)
    /// - Z 위치 = 종방향 철근 상단 Z (= depth) 부근 (종철근 하단 Z = 전단철근 상단 Z 규약)
    /// - 후크 = 사각형 양쪽 변 끝에서 외측 방향으로 짧게 돌출 (HookLengthMm)
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CreateShearRebarCommand : IExternalCommand
    {
        private const double MmToFt = 1.0 / 304.8;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument.Document;

            var window = new UI.ShearRebarWindow(doc);
            if (window.ShowDialog() != true)
                return Result.Cancelled;

            var loadedData = window.LoadedData;
            var shearSettings = window.SheetSettings;

            if (loadedData == null || shearSettings == null || shearSettings.Count == 0)
            {
                TaskDialog.Show("전단철근 배치", "처리할 구조도 설정이 없습니다.");
                return Result.Cancelled;
            }

            // ※ SessionCache.LongitudinalSettings는 실제 배치에서 사용하지 않음.
            //   종방향 철근 위치는 Revit 모델에 배치된 Rebar의 Mark(구조도(N)_longi_outer_M단)에서 직접 읽음.

            Civil3DCoordinate.ResetGlobalOrigin();
            Civil3DCoordinate.AutoSetGlobalOrigin(loadedData);

            var hostMap = BuildHostMap(doc);
            if (hostMap.Count == 0)
            {
                TaskDialog.Show("오류", "프로젝트에 구조 프레임 요소가 없습니다.\n먼저 '구조물 생성'을 실행하세요.");
                return Result.Failed;
            }

            int created = 0;
            int createdStandard = 0;
            int createdFreeForm = 0;
            int failed = 0;
            var debugLog = new List<string>();
            var sheetStats = new Dictionary<string, int>();
            var errors = new List<string>();

            using (var tr = new Transaction(doc, "전단철근 배치"))
            {
                tr.Start();

                // 실제 Rebar 생성 모드: Rebar.CreateFromCurves(StirrupTie) 우선 → FreeForm 폴백

                // ── Revit 모델에 배치된 종방향 철근(Rebar) 전부를 한 번에 수집 ──
                // Mark 형식: 구조도(N)_longi_outer_M단  /  구조도(N)_longi_inner_M단
                var allRebars = new FilteredElementCollector(doc)
                    .OfClass(typeof(Rebar))
                    .Cast<Rebar>()
                    .ToList();

                var longiByKey = new Dictionary<string, List<LongiRebarRef>>();
                var markRegex = new Regex(@"^(구조도\(\d+\))_longi_(outer|inner)_(\d+)단$");
                foreach (var r in allRebars)
                {
                    string mk = r.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                    var m = markRegex.Match(mk);
                    if (!m.Success) continue;
                    string sk = m.Groups[1].Value;
                    string side = m.Groups[2].Value;
                    int dan = int.Parse(m.Groups[3].Value);
                    if (!longiByKey.TryGetValue(sk, out var list))
                    {
                        list = new List<LongiRebarRef>();
                        longiByKey[sk] = list;
                    }
                    if (TryGetRebarLine(r, out XYZ start, out XYZ end))
                    {
                        double diamFt = 0;
                        var bType = doc.GetElement(r.GetTypeId()) as RebarBarType;
                        if (bType != null) diamFt = GetBarDiameterFt(bType);

                        list.Add(new LongiRebarRef
                        {
                            Side = side,
                            Dan = dan,
                            Start = start,
                            End = end,
                            DiameterFt = diamFt
                        });
                    }
                }

                debugLog.Add($"[수집] Revit 종방향 Rebar Mark 매칭: 구조도 {longiByKey.Count}개");
                foreach (var kv in longiByKey.OrderBy(x => x.Key))
                {
                    var outers = kv.Value.Where(x => x.Side == "outer").OrderBy(x => x.Dan).ToList();
                    var inners = kv.Value.Where(x => x.Side == "inner").OrderBy(x => x.Dan).ToList();
                    debugLog.Add($"  {kv.Key}: outer={outers.Count}개(단:{string.Join(",", outers.Take(5).Select(x=>x.Dan))}..)" +
                                 $" inner={inners.Count}개");
                    if (outers.Count > 0)
                    {
                        var o = outers[0];
                        debugLog.Add($"    outer1단 Start=({o.Start.X:F3},{o.Start.Y:F3},{o.Start.Z:F3}) ft");
                    }
                    if (inners.Count > 0)
                    {
                        var i2 = inners[0];
                        debugLog.Add($"    inner1단 Start=({i2.Start.X:F3},{i2.Start.Y:F3},{i2.Start.Z:F3}) ft");
                    }
                }

                // 횡방향 CTC 맵 (구조도별) — 세션 우선, 없으면 Revit 모델에서 자동 추출
                var transCtcMap = SessionCache.TransverseCtcMap ?? new Dictionary<string, double>();
                var autoTransCtc = ExtractTransverseCtcFromModel(allRebars);
                var transDiamMap = ExtractTransverseDiameterFromModel(allRebars, doc);
                foreach (var kv in autoTransCtc)
                {
                    if (!transCtcMap.ContainsKey(kv.Key)) transCtcMap[kv.Key] = kv.Value;
                }
                if (transCtcMap.Count == 0)
                {
                    tr.RollBack();
                    TaskDialog.Show("전단철근 배치",
                        "횡방향 CTC 정보를 찾을 수 없습니다.\n" +
                        "[횡방향 철근 배치]를 먼저 실행했거나, 도면에 횡방향 Rebar(Mark=구조도(N)_M단_...)가 있어야 합니다.");
                    return Result.Cancelled;
                }
                debugLog.Add($"[수집] 횡방향 CTC: 세션 {(SessionCache.TransverseCtcMap?.Count ?? 0)}개 + 자동 {autoTransCtc.Count}개 → 적용 {transCtcMap.Count}개");
                foreach (var kv in transCtcMap.OrderBy(x => x.Key))
                    debugLog.Add($"  {kv.Key}: CTC={kv.Value:F1}mm (stride={kv.Value/2:F1}mm)");

                foreach (var kv in shearSettings)
                {
                    string structureKey = kv.Key;
                    var shear = kv.Value;

                    if (!longiByKey.TryGetValue(structureKey, out var longiRefs) || longiRefs.Count == 0)
                    {
                        errors.Add($"[{structureKey}] 종방향 Rebar 없음 — 종방향 철근을 먼저 배치하세요");
                        continue;
                    }
                    if (!transCtcMap.TryGetValue(structureKey, out double transCtc) || transCtc <= 0)
                    {
                        errors.Add($"[{structureKey}] 횡방향 CTC 없음");
                        continue;
                    }
                    if (!hostMap.TryGetValue(structureKey, out Element hostElement))
                    {
                        errors.Add($"[{structureKey}] Host 매칭 실패");
                        continue;
                    }

                    // 철근 규격 (DiameterLabel 우선, 없으면 DiameterMm 구체점 매칭)
                    RebarBarType barType = FindRebarBarType(doc, shear.DiameterMm, shear.DiameterLabel);
                    if (barType == null)
                    {
                        errors.Add($"[{structureKey}] RebarBarType 매칭 실패 ({shear.DiameterLabel})");
                        continue;
                    }
                    debugLog.Add($"  {structureKey}: barType={barType.Name}");
                    double depthMm = ParseDepthFromHost(hostElement);
                    if (depthMm <= 0) depthMm = 1000;

                    // 90도 + 100mm 후크 타입 확보 (없으면 생성). barType별 1회.
                    var hookType = EnsureHookType90(doc, GetBarDiameterFt(barType), 100.0);
                    debugLog.Add($"  {structureKey}: hookType={hookType?.Name ?? "<null>"}");

                    // 종방향 단별 outer/inner 페어링
                    var byDan = longiRefs.GroupBy(x => x.Dan)
                        .OrderBy(g => g.Key)
                        .Select(g => new
                        {
                            Dan = g.Key,
                            Outer = g.FirstOrDefault(x => x.Side == "outer"),
                            Inner = g.FirstOrDefault(x => x.Side == "inner")
                        })
                        .Where(x => x.Outer != null && x.Inner != null)
                        .ToList();
                    if (byDan.Count == 0)
                    {
                        errors.Add($"[{structureKey}] 종방향 outer/inner 페어 0개");
                        continue;
                    }

                    // 횡방향 단 수 산출: depth / (CTC/2) + 1
                    double stride = transCtc / 2.0;
                    int transTotalDan = (int)Math.Floor(depthMm / stride) + 1;
                    int g = shear.GroupSize;
                    if (g < 2) g = 2;

                    // 횡방향 묶음 분할: GroupSize 씩 한 단 겹침
                    //  GroupSize=3 → [1,2,3] [3,4,5] ...
                    var bundles = new List<int[]>();
                    int step = g - 1;
                    int s = 1; // 1-based 횡방향 단 번호
                    while (s + g - 1 <= transTotalDan)
                    {
                        bundles.Add(new[] { s, s + g - 1 });
                        s += step;
                    }
                    if (bundles.Count == 0)
                    {
                        errors.Add($"[{structureKey}] 횡방향 묶음 0개 (단={transTotalDan}, g={g})");
                        continue;
                    }

                    debugLog.Add($"[{structureKey}] 종방향 페어단={byDan.Count}(홀수만 사용), 횡방향 단={transTotalDan} " +
                                 $"(depth={depthMm:F0}mm, CTC={transCtc:F1}mm, stride={stride:F1}mm), " +
                                 $"GroupSize={g}, 묶음={bundles.Count}개");
                    debugLog.Add($"  묶음 목록(처음5): {string.Join(" ", bundles.Take(5).Select(b => $"[{b[0]}-{b[1]}]"))}");

                    int sheetCreated = 0;
                    int sampleLogged = 0;
                    // ── 종방향 홀수 단마다 × 횡방향 묶음 → 사각형 1개씩 ──
                    int oddIndex = 0; // 종방향 홀수 단 카운터 (0=1단, 1=3단, 2=5단...)

                    foreach (var pair in byDan)
                    {
                        int dan = pair.Dan;
                        if (dan % 2 == 0) continue; // 종방향 홀수 단만

                        // StartGroup이 A면 0번째 홀수단=A, 1번째=B, 2번째=A...
                        // StartGroup이 B면 0번째 홀수단=B, 1번째=A, 2번째=B...
                        bool isGroupA = (shear.StartGroup == UI.ShearStartGroup.A)
                                      ? (oddIndex % 2 == 0)
                                      : (oddIndex % 2 != 0);

                        // 종방향 단위체 (X, Z) 좌표 → outer/inner 로부터 획득
                        // Revit 좌표계: Civil3D X→RevitX, Civil3D Y→RevitZ, 종방향→RevitY
                        XYZ outXY = pair.Outer.Start;
                        XYZ inXY  = pair.Inner.Start;

                        for (int i = 0; i < bundles.Count; i++)
                        {
                            bool isBundleA = (i % 2 == 0);
                            
                            // ❗ 종방향 단이 A그룹이면 횡방향 A번들(짝수 인덱스)만 배치, B면 B만 배치
                            if (isGroupA != isBundleA)
                                continue;

                            var bundle = bundles[i];
                            int sDan = bundle[0];
                            int eDan = bundle[1];

                            // 횡방향 Z 위치 (stride 단위)
                            double zStartMm = (sDan - 1) * stride;
                            double zEndMm   = (eDan - 1) * stride;
                            double zStartFt = zStartMm * MmToFt;
                            double zEndFt   = zEndMm   * MmToFt;

                            // ────────────────────────────────────────
                            // 5선 U자형 + 사용자 지정 길이(100mm) 후크
                            // ────────────────────────────────────────
                            double transDiamFt = 0;
                            if (transDiamMap.TryGetValue(structureKey, out double td))
                                transDiamFt = td;

                            // 레그 길이 연장량: 횡철근 두께 + 종철근 두께
                            double extOuter = transDiamFt + pair.Outer.DiameterFt;
                            double extInner = transDiamFt + pair.Inner.DiameterFt;

                            // 내측→외측 방향 벡터 (X-Z 평면 기준; Revit Y는 종방향이므로 제외)
                            double dx = outXY.X - inXY.X;
                            double dz = outXY.Z - inXY.Z;
                            double dlen = Math.Sqrt(dx * dx + dz * dz);
                            double ndx = (dlen > 1e-9) ? dx / dlen : 1.0;
                            double ndz = (dlen > 1e-9) ? dz / dlen : 0.0;

                            // Z 드롭: 종철근 두께(직경)만큼 수직 이동
                            double outOffsetZ = pair.Outer.DiameterFt;
                            double inOffsetZ = pair.Inner.DiameterFt;

                            // 상단 가로(pSO↔pEO) Y방향 양끝 연장량 = (횡철근 + 외측 종철근) / 2
                            double topExtFt = (transDiamFt + pair.Outer.DiameterFt) / 2.0;

                            // 외측 끝점: 내측→외측 방향으로 extOuter 연장 + Y(종방향) topExtFt 추가 + Z 드롭
                            XYZ pSO = new XYZ(outXY.X + ndx * extOuter, zStartFt - topExtFt, outXY.Z + ndz * extOuter - outOffsetZ);
                            XYZ pEO = new XYZ(outXY.X + ndx * extOuter, zEndFt   + topExtFt, outXY.Z + ndz * extOuter - outOffsetZ);
                            // 내측 끝점: 외측→내측 방향으로 extInner 연장 + Y(종방향) topExtFt 동일하게 적용 + Z 드롭
                            XYZ pSI = new XYZ(inXY.X - ndx * extInner, zStartFt - topExtFt, inXY.Z - ndz * extInner - inOffsetZ);
                            XYZ pEI = new XYZ(inXY.X - ndx * extInner, zEndFt   + topExtFt, inXY.Z - ndz * extInner - inOffsetZ);

                            // ★ Shape-based: 3선 U자 (pSI → pSO → pEO → pEI). 후크는 RebarHookType이 자동 생성.
                            var curves = new List<Curve>();
                            TryAddLine(curves, pSI, pSO); // (1) 시작 레그 (내→외)
                            TryAddLine(curves, pSO, pEO); // (2) 상단 가로 (시작→끝)
                            TryAddLine(curves, pEO, pEI); // (3) 끝 레그 (외→내)

                            if (curves.Count < 3) { failed++; continue; }

                            string mark = $"{structureKey}_shear_종{dan}_횡{sDan}-{eDan}_{(isGroupA ? "A" : "B")}";

                            bool ok = TryCreateShearRebar(doc, curves, barType, hookType,
                                hostElement, mark, out string createMethod, out string err);
                            if (ok) { created++; sheetCreated++; createdStandard++; }
                            else
                            {
                                failed++;
                                if (failed <= 10)
                                    debugLog.Add($"  [{structureKey}] 종{dan} 횡{sDan}-{eDan} 실패: {err}");
                            }
                        }

                        oddIndex++;
                    }

                    sheetStats[structureKey] = sheetCreated;
                    debugLog.Add($"[{structureKey}] 완료: 생성 {sheetCreated}개 (종방향 홀수단 {byDan.Count(p => p.Dan % 2 != 0)}개 × 묶음 {bundles.Count}개)");

                }

                tr.Commit();
            }

            // 결과 메시지
            string msg = "═══════════════════════════════════\n" +
                         "  전단철근 배치 (시각화 모드)\n" +
                         "═══════════════════════════════════\n" +
                         $"── 총 생성: {created}개 (모델 선) | 실패: {failed}개\n";
            if (sheetStats.Count > 0)
            {
                msg += "\n── 구조도별 ──\n";
                foreach (var kv in sheetStats.OrderBy(k => k.Key))
                    msg += $"  {kv.Key}: {kv.Value}개\n";
            }
            if (errors.Count > 0)
                msg += "\n오류:\n" + string.Join("\n", errors.Take(20));

            // 로그 파일
            try
            {
                string logDir = Path.Combine(Path.GetTempPath(), "RevitRebarModeler", "Logs");
                Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, $"ShearRebar_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                File.WriteAllText(logPath, msg + "\n\n[디버그]\n" + string.Join("\n", debugLog), System.Text.Encoding.UTF8);
                msg += $"\n\n로그: {logPath}";
            }
            catch { }

            TaskDialog.Show("전단철근 배치", msg);
            return Result.Succeeded;
        }

        // ============================================================
        // 기준 곡선 생성 (종방향 명령과 동일 로직)
        // ============================================================
        private List<RebarSegment> BuildBaseCurve(UI.Pos1Kind pos1,
            List<TransverseRebarData> innerPolys, List<TransverseRebarData> outerPolys, out string error)
        {
            error = null;
            switch (pos1)
            {
                case UI.Pos1Kind.Inner:
                {
                    var lists = innerPolys.Select(p => p.Segments).Where(s => s != null && s.Count > 0).ToList();
                    var chain = LongiCurveSampler.ConcatenatePolylinesTrimmed(lists);
                    if (chain.Count == 0) { error = "내측 체인 비어 있음"; return null; }
                    return LongiCurveSampler.MaterializeTrimmed(chain);
                }
                case UI.Pos1Kind.Center:
                {
                    var iLists = innerPolys.Select(p => p.Segments).Where(s => s != null && s.Count > 0).ToList();
                    var oLists = outerPolys.Select(p => p.Segments).Where(s => s != null && s.Count > 0).ToList();
                    var iChain = LongiCurveSampler.ConcatenatePolylinesTrimmed(iLists);
                    var oChain = LongiCurveSampler.ConcatenatePolylinesTrimmed(oLists);
                    if (iChain.Count == 0 || oChain.Count == 0) { error = "내/외측 체인 부족"; return null; }
                    var iMat = LongiCurveSampler.MaterializeTrimmed(iChain);
                    var oMat = LongiCurveSampler.MaterializeTrimmed(oChain);
                    return LongiCurveSampler.BuildCenterCurve(iMat, oMat);
                }
                case UI.Pos1Kind.Outer:
                default:
                {
                    var lists = outerPolys.Select(p => p.Segments).Where(s => s != null && s.Count > 0).ToList();
                    var chain = LongiCurveSampler.ConcatenatePolylinesTrimmed(lists);
                    if (chain.Count == 0) { error = "외측 체인 비어 있음"; return null; }
                    return LongiCurveSampler.MaterializeTrimmed(chain);
                }
            }
        }

        /// <summary>
        /// 횡철근 segments 중 BC 로부터 가장 멀리 있는 끝점 반환 (외측 끝).
        /// </summary>
        private RebarPoint GetEndpointFarFromBC(List<RebarSegment> segs, double bCx, double bCy)
        {
            RebarPoint best = null;
            double bestD = -1;
            foreach (var s in segs)
            {
                foreach (var pt in new[] { s.StartPoint, s.EndPoint })
                {
                    if (pt == null) continue;
                    if (pt.X == 0 && pt.Y == 0) continue;
                    double d = Math.Sqrt((pt.X - bCx) * (pt.X - bCx) + (pt.Y - bCy) * (pt.Y - bCy));
                    if (d > bestD) { bestD = d; best = pt; }
                }
            }
            return best;
        }

        private (double x, double y) ComputeOutwardDir(double px, double py, double bCx, double bCy)
        {
            double dx = px - bCx;
            double dy = py - bCy;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-9) return (1.0, 0.0);
            return (dx / d, dy / d);
        }

        private void TryAddLine(List<Curve> list, XYZ a, XYZ b)
        {
            if (a == null || b == null) return;
            if (a.DistanceTo(b) < 0.001) return;
            try { list.Add(Line.CreateBound(a, b)); } catch { }
        }

        /// <summary>
        /// Revit 모델의 횡방향 Rebar(Mark = 구조도(N)_M단_(inner|outer)_K)를 분석해서
        /// 1단·2단의 Z 차이로 stride = CTC/2 추정 → CTC = stride × 2.
        /// </summary>
        private Dictionary<string, double> ExtractTransverseCtcFromModel(List<Rebar> allRebars)
        {
            var map = new Dictionary<string, double>();
            var transRegex = new Regex(@"^(구조도\(\d+\))_(\d+)단_(inner|outer)_(\d+)$");
            var zByKey = new Dictionary<string, Dictionary<int, List<double>>>();

            foreach (var r in allRebars)
            {
                string mk = r.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                var m = transRegex.Match(mk);
                if (!m.Success) continue;
                string sk = m.Groups[1].Value;
                int dan = int.Parse(m.Groups[2].Value);
                if (!TryGetRebarLine(r, out XYZ s, out XYZ _)) continue;
                // 좌표 규약: Civil3D Y → Revit Z, 종방향 오프셋 → Revit Y
                // 횡방향 단 위치(CTC 배수) = Revit Y
                double yVal = s.Y;
                if (!zByKey.TryGetValue(sk, out var zMap))
                {
                    zMap = new Dictionary<int, List<double>>();
                    zByKey[sk] = zMap;
                }
                if (!zMap.TryGetValue(dan, out var list))
                {
                    list = new List<double>();
                    zMap[dan] = list;
                }
                list.Add(yVal);  // Revit Y = 종방향 오프셋(단 위치)
            }

            foreach (var kv in zByKey)
            {
                var zMap = kv.Value;
                if (!zMap.ContainsKey(1) || !zMap.ContainsKey(2)) continue;
                double z1 = zMap[1].Average();
                double z2 = zMap[2].Average();
                double strideFt = Math.Abs(z2 - z1);
                if (strideFt <= 0) continue;
                double strideMm = strideFt * 304.8;
                map[kv.Key] = strideMm * 2.0;
            }
            return map;
        }

        private Dictionary<string, double> ExtractTransverseDiameterFromModel(List<Rebar> allRebars, Document doc)
        {
            var map = new Dictionary<string, double>();
            var transRegex = new Regex(@"^(구조도\(\d+\))_(\d+)단_(inner|outer)_(\d+)$");
            foreach (var r in allRebars)
            {
                string mk = r.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                var m = transRegex.Match(mk);
                if (!m.Success) continue;
                string sk = m.Groups[1].Value;
                
                if (!map.ContainsKey(sk))
                {
                    try
                    {
                        var barType = doc.GetElement(r.GetTypeId()) as RebarBarType;
                        if (barType != null)
                        {
                            map[sk] = GetBarDiameterFt(barType);
                        }
                    }
                    catch { }
                }
            }
            return map;
        }

        /// <summary>Revit에 배치된 종방향 Rebar의 첫 번째 centerline curve로부터 시작/끝점 추출.</summary>
        private bool TryGetRebarLine(Rebar rebar, out XYZ start, out XYZ end)
        {
            start = end = null;
            try
            {
                var curves = rebar.GetCenterlineCurves(false, false, false,
                    MultiplanarOption.IncludeOnlyPlanarCurves, 0);
                if (curves == null || curves.Count == 0) return false;
                var c = curves[0];
                start = c.GetEndPoint(0);
                end = c.GetEndPoint(1);
                return start != null && end != null;
            }
            catch
            {
                return false;
            }
        }

        private class LongiRebarRef
        {
            public string Side;   // "outer" or "inner"
            public int Dan;       // 1-based
            public XYZ Start;     // Z=0 끝점
            public XYZ End;       // Z=depth 끝점
            public double DiameterFt;
        }

        // ============================================================
        // 시각화: DirectShape(Line) 으로 곡선 그리기 — 빨간 ModelCurve 스타일
        // ============================================================
        private bool TryCreateLineDirectShape(Document doc, List<Curve> curves,
            ElementId categoryId, ElementId graphicsStyleId,
            string mark, out string errorDetail)
        {
            errorDetail = null;
            if (curves == null || curves.Count == 0)
            {
                errorDetail = "curves 비어 있음";
                return false;
            }

            try
            {
                var ds = DirectShape.CreateElement(doc, categoryId);
                ds.ApplicationId = "RevitRebarModeler";
                ds.ApplicationDataId = mark;
                try { ds.Name = mark; } catch { }

                var geo = new List<GeometryObject>();
                foreach (var c in curves) geo.Add(c);
                ds.SetShape(geo);

                if (graphicsStyleId != ElementId.InvalidElementId)
                {
                    try
                    {
                        var sset = ds.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                        // (DirectShape는 GraphicsStyle을 직접 못 받음 — 색상은 Override 또는 카테고리 라인 색상)
                    }
                    catch { }
                }
                ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(mark);
                return true;
            }
            catch (Exception ex)
            {
                errorDetail = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 빨간색 Line Style 을 가져오거나 생성.
        /// (Line Style은 별도 카테고리(OST_Lines 의 서브카테고리)에 속함)
        /// </summary>
        private ElementId GetOrCreateRedLineStyle(Document doc)
        {
            const string styleName = "전단철근_빨강";
            var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            if (lines == null) return ElementId.InvalidElementId;

            Category sub = null;
            foreach (Category c in lines.SubCategories)
            {
                if (c.Name == styleName) { sub = c; break; }
            }
            if (sub == null)
            {
                try { sub = doc.Settings.Categories.NewSubcategory(lines, styleName); }
                catch { return ElementId.InvalidElementId; }
            }
            try
            {
                sub.LineColor = new Color(255, 0, 0);
                sub.SetLineWeight(5, GraphicsStyleType.Projection);
            }
            catch { }
            var gs = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
            return gs?.Id ?? ElementId.InvalidElementId;
        }

        // ============================================================
        // Rebar 생성 — 3선 U자 + Revit 후크 API
        // 커브 순서: pSI → pSO → pEO → pEI
        // 양끝에 RebarHookType(주로 90도) 적용 → 폴백으로 FreeForm
        // ============================================================
        private bool TryCreateShearRebar(Document doc, List<Curve> curves, RebarBarType barType,
            RebarHookType hookType, Element hostElement, string mark,
            out string createMethod, out string errorDetail)
        {
            createMethod = null;
            errorDetail = null;
            Rebar rebar = null;
            string stdErr = null, ffErr = null;
            RebarFreeFormValidationResult validation = RebarFreeFormValidationResult.Success;

            // 커브의 normal 가산 (Standard CreateFromCurves에 필요)
            XYZ normal = XYZ.BasisY; // 폴백값; 이하에서 정확한 값으로 교체
            if (TryComputePlane(curves, out _, out _, out XYZ computedNormal))
                normal = computedNormal;

            // ── 1) CreateFromCurves (StirrupTie + 후크 타입) ──
            try
            {
                rebar = Rebar.CreateFromCurves(
                    doc,
                    RebarStyle.Standard,
                    barType,
                    hookType,  // 시작점 후크
                    hookType,  // 끝점 후크
                    hostElement,
                    normal,
                    curves,
                    RebarHookOrientation.Left,
                    RebarHookOrientation.Right,
                    true, false);
                if (rebar != null) createMethod = $"StirrupTie+Hook({hookType?.Name ?? "none"})";
            }
            catch (Exception ex)
            {
                stdErr = $"{ex.GetType().Name}: {ex.Message}";
                rebar = null;
            }

            // ── 2) 후크 없이 다시 시도 ──
            if (rebar == null)
            {
                try
                {
                    rebar = Rebar.CreateFromCurves(
                        doc, RebarStyle.Standard, barType,
                        null, null,
                        hostElement, normal,
                        curves,
                        RebarHookOrientation.Left, RebarHookOrientation.Right,
                        true, false);
                    if (rebar != null) createMethod = "StirrupTie(NoHook)";
                }
                catch (Exception ex)
                {
                    stdErr += $" | NoHook: {ex.GetType().Name}: {ex.Message}";
                    rebar = null;
                }
            }

            // ── 3) FreeForm 폴백 ──
            if (rebar == null)
            {
                try
                {
                    var sets = new List<IList<Curve>> { curves };
                    rebar = Rebar.CreateFreeForm(doc, barType, hostElement, sets, out validation);
                    if (rebar != null) createMethod = $"FreeForm({validation})";
                    else ffErr = $"null (validation={validation})";
                }
                catch (Exception ex)
                {
                    ffErr = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            if (rebar == null)
            {
                errorDetail = $"Std:{stdErr ?? "skip"} | FF:{ffErr ?? "skip"}";
                return false;
            }

            rebar.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(mark);
            rebar.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"{mark}|{createMethod}");
            return true;
        }

        /// <summary>
        /// 90도 + 지정 길이(기본 100mm) RebarHookType 확보. 없으면 생성.
        /// tangentLengthMultiplier = targetLengthFt / barDiameterFt (Revit RebarHookType은 절대 길이가
        /// 아니라 bar 직경 배수로 길이를 정의하기 때문). 따라서 다른 bar 직경에는 다른 hook type 필요.
        /// </summary>
        private RebarHookType EnsureHookType90(Document doc, double barDiameterFt, double targetTangentLengthMm = 100.0)
        {
            double targetTangentFt = targetTangentLengthMm * MmToFt;
            double multiplier = (barDiameterFt > 1e-9) ? targetTangentFt / barDiameterFt : 7.6923;
            string targetName = $"Hook_90_{targetTangentLengthMm:F0}mm_D{Math.Round(barDiameterFt / MmToFt)}";

            var all = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarHookType))
                .Cast<RebarHookType>()
                .ToList();

            // 1) 이름으로 정확 매칭
            var byName = all.FirstOrDefault(h => h.Name == targetName);
            if (byName != null) return byName;

            // 2) 신규 생성
            try
            {
                var created = RebarHookType.Create(doc, Math.PI / 2.0, multiplier);
                try { created.Name = targetName; } catch { }
                return created;
            }
            catch
            {
                // 생성 실패 시 기존 90도 hook 중 하나 fallback
                return all.FirstOrDefault(h => Math.Abs(h.HookAngle - Math.PI / 2.0) < 0.01)
                    ?? all.FirstOrDefault();
            }
        }

        /// <summary>
        /// curves 의 처음 점 + 첫 직선 방향(xDir) + curves 가 놓인 평면 normal 추정.
        /// 모든 curve 가 한 평면 위에 있다고 가정.
        /// </summary>
        private bool TryComputePlane(List<Curve> curves, out XYZ origin, out XYZ xDir, out XYZ normal)
        {
            origin = xDir = normal = null;
            if (curves == null || curves.Count < 2) return false;

            origin = curves[0].GetEndPoint(0);
            XYZ pEnd = curves[0].GetEndPoint(1);
            XYZ x = (pEnd - origin);
            if (x.GetLength() < 1e-6) return false;
            x = x.Normalize();

            // 두 번째 curve 의 진행 방향과 x 의 외적 → normal
            XYZ pNext = curves[1].GetEndPoint(1);
            XYZ y = (pNext - pEnd);
            if (y.GetLength() < 1e-6) return false;
            y = y.Normalize();

            XYZ n = x.CrossProduct(y);
            if (n.GetLength() < 1e-6) return false;

            xDir = x;
            normal = n.Normalize();
            return true;
        }

        // ============================================================
        // Helpers (CreateLongitudinalRebarCommand에서 복사 — 자체 완결성 확보)
        // ============================================================
        private Dictionary<string, Element> BuildHostMap(Document doc)
        {
            var map = new Dictionary<string, Element>();
            var hosts = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .WhereElementIsNotElementType()
                .ToList();

            if (hosts.Count == 0)
            {
                hosts = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_GenericModel)
                    .WhereElementIsNotElementType()
                    .ToList();
            }

            foreach (var elem in hosts)
            {
                string comments = elem.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
                string key = ExtractStructureKey(comments);
                if (!string.IsNullOrEmpty(key) && !map.ContainsKey(key))
                    map[key] = elem;
            }
            return map;
        }

        private double ParseDepthFromHost(Element hostElement)
        {
            string comments = hostElement.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
            var match = Regex.Match(comments, @"depth=(\d+\.?\d*)");
            return match.Success ? double.Parse(match.Groups[1].Value) : 0;
        }

        private RebarBarType FindRebarBarType(Document doc, double diameterMm, string diameterLabel = null)
        {
            int d = (int)Math.Round(diameterMm);
            double targetFt = diameterMm * MmToFt;

            var all = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
            if (all.Count == 0) return null;

            // 1순위: DiameterLabel 직접 이름 매칭 ("D13", "D16" 등)
            if (!string.IsNullOrEmpty(diameterLabel))
            {
                var direct = all.FirstOrDefault(r => r.Name == diameterLabel);
                if (direct != null) return direct;
                // "D13" 형식에서 숫자만 추출
                var labelNum = new System.Text.RegularExpressions.Regex(@"\d+").Match(diameterLabel);
                if (labelNum.Success && int.TryParse(labelNum.Value, out int ld))
                {
                    string[] lblCandidates = { $"D{ld}", $"{ld} 400S", $"D{ld} 400S", $"{ld}" };
                    foreach (var name in lblCandidates)
                    {
                        var hit = all.FirstOrDefault(r => r.Name == name);
                        if (hit != null) return hit;
                    }
                }
            }

            // 2순위: DiameterMm 기반 이름 매칭
            string[] nameCandidates = { $"D{d}", $"{d} 400S", $"D{d} 400S", $"{d}" };
            foreach (var name in nameCandidates)
            {
                var hit = all.FirstOrDefault(r => r.Name == name);
                if (hit != null) return hit;
            }

            // 3순위: 직경 수치 근사치 (ft 단위)
            return all
                .Where(r => Math.Abs(GetBarDiameterFt(r) - targetFt) < 0.001)
                .OrderBy(r => (r.Name.Contains("스트럽") || r.Name.Contains("타이") || r.Name.Contains("Stirrup") || r.Name.Contains("Tie")) ? 0 : 1)
                .FirstOrDefault();
        }

        private double GetBarDiameterFt(RebarBarType barType)
        {
            var param = barType.get_Parameter(BuiltInParameter.REBAR_BAR_DIAMETER);
            return param != null ? param.AsDouble() : 0.0;
        }

        private string ExtractStructureKey(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var m = Regex.Match(text, @"구조도\(\d+\)");
            return m.Success ? m.Value : "";
        }

        private void GetBoundaryCenter(CivilExportData data, string structureKey,
            out double cx, out double cy, out bool found)
        {
            cx = cy = 0; found = false;
            if (data?.StructureRegions == null) return;
            var keyRegex = new Regex(@"구조도\((\d+)\)");
            foreach (var cd in data.StructureRegions)
            {
                var m = keyRegex.Match(cd.CycleKey ?? "");
                if (!m.Success) continue;
                if ($"구조도({m.Groups[1].Value})" != structureKey) continue;
                if (cd.BoundaryCenterX == 0 && cd.BoundaryCenterY == 0) continue;
                cx = cd.BoundaryCenterX;
                cy = cd.BoundaryCenterY;
                found = true;
                return;
            }
        }

        /// <summary>
        /// 사각형 4선을 ModelCurve(기본 선)으로 생성.
        /// 네 점이 동일 평면 위에 있다고 가정하고 SketchPlane을 한 번만 생성.
        /// </summary>
        private bool TryCreateModelLines(Document doc, List<Curve> curves, out string error)
        {
            error = "";
            try
            {
                if (curves == null || curves.Count == 0)
                {
                    error = "커브 없음";
                    return false;
                }

                // 평면 계산: 첫 커브의 시작점 + 두 방향벡터의 외적
                XYZ origin = curves[0].GetEndPoint(0);
                XYZ p1     = curves[0].GetEndPoint(1);
                XYZ v1 = (p1 - origin);
                if (v1.GetLength() < 1e-6) { error = "커브 진행 방향 0"; return false; }
                v1 = v1.Normalize();

                // 두 번째 커브에서 다른 방향벡터 탐색
                XYZ normal = null;
                foreach (var c in curves)
                {
                    XYZ candidate = (c.GetEndPoint(1) - origin);
                    if (candidate.GetLength() < 1e-6) continue;
                    candidate = candidate.Normalize();
                    XYZ n = v1.CrossProduct(candidate);
                    if (n.GetLength() > 1e-6) { normal = n.Normalize(); break; }
                }
                if (normal == null) { error = "평면 normal 계산 실패 (모든 커브가 평행)"; return false; }

                Plane plane = Plane.CreateByNormalAndOrigin(normal, origin);
                SketchPlane sp = SketchPlane.Create(doc, plane);

                foreach (var curve in curves)
                    doc.Create.NewModelCurve(curve, sp);

                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

    }
}
