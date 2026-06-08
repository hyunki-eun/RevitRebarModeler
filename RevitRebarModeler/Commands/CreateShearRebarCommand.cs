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
using static RevitRebarModeler.Models.RebarHelpers;

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
    public class CreateShearRebarCommand : CommandBase
    {
        // MmToFt 상수는 RebarHelpers.MmToFt 사용 (using static)

        protected override Result Run(ExternalCommandData commandData, ref string message, ElementSet elements)
        {

            var doc = commandData.Application.ActiveUIDocument.Document;

            if (SessionCache.LoadedJson == null)
            {
                TaskDialog.Show("Civil3D JSON 필요",
                    "먼저 리본의 [Civil3D JSON 불러오기]를 실행하세요.");
                return Result.Cancelled;
            }

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

            // 구조도별 내측 횡방향 패널(좌=1·중=2·우=3…) Revit XZ 점군.
            //   전단철근 T 마크를 "가로 위치(좌/중/우)"로 부여하기 위한 판정 기준.
            //   (AutoSetGlobalOrigin 이후라야 ToRevitWorld 좌표가 유효)
            var innerPanelMap = BuildInnerPanelMap(loadedData);

            int created = 0;
            int createdStdHook = 0;     // Standard + RebarHookType 부착
            int createdStdNoHook = 0;   // Standard, hook 없이 (1차 실패 후 후크 빼고 재시도)
            int createdFreeForm = 0;    // FreeForm 폴백
            int failed = 0;
            // 형상 검증 카운터 — 시각 대신 로그로 hook 방향 / 평면 정합 자동 체크
            int vfPlaneOK = 0, vfPlaneBad = 0;
            int vfHookInward = 0, vfHookSwap = 0, vfHookMixed = 0, vfHookUnknown = 0;
            var debugLog = new List<string>();
            var sheetStats = new Dictionary<string, int>();
            var fallbackLog = new List<string>();   // Standard 실패 → NoHook/FreeForm 폴백 발생 위치
            var verifyAnomalies = new List<string>(); // hook swap / plane bad / mixed 인 rebar 목록
            var verifyDetail = new List<string>();    // 구조도별 첫 rebar 4코너 좌표 dump
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
                // (_SD\d+)? — 봉강 등급 suffix (SD400/500). SD300은 suffix 없음 → 호환.
                var markRegex = new Regex(@"^(구조도\(\d+\))_longi_(outer|inner)_(\d+)단(_SD\d+)?$");
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
                // 세션 캐시를 직접 변형하지 않도록 복사본을 사용 (자동추출값이 세션을 오염시키는 것 방지)
                var transCtcMap = new Dictionary<string, double>(
                    SessionCache.TransverseCtcMap ?? new Dictionary<string, double>());
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
                    RebarBarType barType = FindRebarBarType(doc, shear.DiameterMm, preferStirrup: true, diameterLabel: shear.DiameterLabel);
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
                    bool verifyDumpedForThisSheet = false;
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

                        // 가로 위치(좌/중/우) 판정 → T 마크 인덱스. 내측 종철근 위치를
                        // 가장 가까운 내측 횡방향 패널에 매칭. (깊이 묶음과 무관하게 쌍당 1회)
                        var panelsForKey = innerPanelMap.TryGetValue(structureKey, out var pnls) ? pnls : null;
                        int panelK = FindPanelK(panelsForKey, inXY);

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

                            // 전단철근이 케이지를 바깥에서 감싸며 생기는 겹침은 다리(b·d) 쪽에서 보정.
                            // 다리 양 끝에 D_전단/2씩 → 다리 길이 b=d가 D_전단(두께)만큼 늘어남. (c는 미적용)
                            double shearRadiusFt = GetBarDiameterFt(barType) / 2.0;

                            // 레그 길이 연장량: 횡철근 두께 + 종철근 두께 + 전단 반지름
                            double extOuter = transDiamFt + pair.Outer.DiameterFt + shearRadiusFt;
                            double extInner = transDiamFt + pair.Inner.DiameterFt + shearRadiusFt;

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
                            // (c는 전단 두께를 더하지 않음 — 겹침 보정은 다리 b·d 쪽에서만)
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

                            // _P{K} = 가로 위치(좌=1·중=2·우=3). 일람표에서 T{K}로 묶임.
                            string mark = $"{structureKey}_shear_종{dan}_횡{sDan}-{eDan}_{(isGroupA ? "A" : "B")}_P{panelK}";

                            // 입력 normal 미리 산출 (검증에서 재사용)
                            XYZ inputNormal = XYZ.BasisY;
                            if (TryComputePlane(curves, out _, out _, out XYZ cn))
                                inputNormal = cn;

                            bool ok = TryCreateShearRebar(doc, curves, barType, hookType,
                                hostElement, mark, out string createMethod, out string err, out Rebar createdRebar);
                            if (ok)
                            {
                                created++; sheetCreated++;
                                // createMethod prefix 로 분류 — TryCreateShearRebar 의 3개 경로와 매칭
                                if (createMethod != null && createMethod.StartsWith("StirrupTie+Hook"))
                                    createdStdHook++;
                                else if (createMethod != null && createMethod.StartsWith("StirrupTie(NoHook"))
                                {
                                    createdStdNoHook++;
                                    if (fallbackLog.Count < 30)
                                        fallbackLog.Add($"  NoHook  {mark}");
                                }
                                else if (createMethod != null && createMethod.StartsWith("FreeForm"))
                                {
                                    createdFreeForm++;
                                    if (fallbackLog.Count < 30)
                                        fallbackLog.Add($"  FreeForm {mark} ({createMethod})");
                                }

                                // ── 형상 검증 ──
                                List<string> curveDump = !verifyDumpedForThisSheet ? new List<string>() : null;
                                var vf = VerifyShearRebar(createdRebar, pSI, pSO, pEO, pEI, inputNormal, ndx, ndz, curveDump);
                                if (vf.PlaneOK) vfPlaneOK++; else vfPlaneBad++;
                                if (!vf.HookKnown) vfHookUnknown++;
                                else if (vf.HookInwardOK) vfHookInward++;
                                else if (vf.HookOutwardSwap) vfHookSwap++;
                                else if (vf.HookMixed) vfHookMixed++;

                                // anomaly 만 모음
                                if (!vf.PlaneOK || vf.HookOutwardSwap || vf.HookMixed)
                                {
                                    if (verifyAnomalies.Count < 30)
                                        verifyAnomalies.Add(
                                            $"  {mark}  plane(|nY|={vf.NormalY:F3}, |n·rad|={vf.NormalDotRadial:F3}) " +
                                            $"hook(s.dy={vf.HookStartDY:F3}, e.dy={vf.HookEndDY:F3})");
                                }

                                // 구조도별 첫 rebar 4코너 dump (PASS/FAIL과 무관하게 한 번)
                                if (!verifyDumpedForThisSheet)
                                {
                                    verifyDumpedForThisSheet = true;
                                    verifyDetail.Add($"[{structureKey}] 첫 rebar = {mark}");
                                    verifyDetail.Add($"  pSI=({pSI.X:F3},{pSI.Y:F3},{pSI.Z:F3})  pSO=({pSO.X:F3},{pSO.Y:F3},{pSO.Z:F3})");
                                    verifyDetail.Add($"  pEO=({pEO.X:F3},{pEO.Y:F3},{pEO.Z:F3})  pEI=({pEI.X:F3},{pEI.Y:F3},{pEI.Z:F3})  (ft)");
                                    verifyDetail.Add($"  inputNormal=({inputNormal.X:F4},{inputNormal.Y:F4},{inputNormal.Z:F4}) " +
                                                     $"|nY|={vf.NormalY:F4} |n·rad|={vf.NormalDotRadial:F4}  " +
                                                     $"PlaneOK={vf.PlaneOK}");
                                    verifyDetail.Add($"  hookStartDY={vf.HookStartDY:F4}ft  hookEndDY={vf.HookEndDY:F4}ft  " +
                                                     $"InwardOK={vf.HookInwardOK}  Swap={vf.HookOutwardSwap}  Mixed={vf.HookMixed}  " +
                                                     $"Known={vf.HookKnown}");
                                    if (curveDump != null && curveDump.Count > 0)
                                    {
                                        verifyDetail.Add($"  Revit centerline curves ({curveDump.Count}개, suppressBendRadius=true):");
                                        verifyDetail.AddRange(curveDump);
                                    }
                                }
                            }
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

            // 배치 직후 A/B/T 마크 라벨 즉시 기록 (수량 일람표 없이도 라벨 표시)
            try { RebarSchedulePopulator.StampLabels(commandData.Application.Application, doc); } catch { }

            // ── 로그 파일용 verbose 내용 (전부 보존) ──
            string verboseLog = "═══════════════════════════════════\n" +
                                "  전단철근 배치\n" +
                                "═══════════════════════════════════\n" +
                                $"── 총 생성: {created}개 | 실패: {failed}개\n" +
                                $"  Standard+Hook: {createdStdHook} | Standard(NoHook): {createdStdNoHook} | FreeForm: {createdFreeForm}\n" +
                                $"  형상 검증: Plane OK/Bad = {vfPlaneOK}/{vfPlaneBad} | " +
                                $"Hook In={vfHookInward} Swap={vfHookSwap} Mixed={vfHookMixed} Unknown={vfHookUnknown}\n";
            if (sheetStats.Count > 0)
            {
                verboseLog += "\n── 구조도별 ──\n";
                foreach (var kv in sheetStats.OrderBy(k => k.Key))
                    verboseLog += $"  {kv.Key}: {kv.Value}개\n";
            }
            if (verifyDetail.Count > 0)
                verboseLog += "\n── 검증 상세 (구조도별 첫 rebar) ──\n" + string.Join("\n", verifyDetail) + "\n";
            if (verifyAnomalies.Count > 0)
                verboseLog += $"\n── 검증 이상 ({verifyAnomalies.Count}건) ──\n" + string.Join("\n", verifyAnomalies) + "\n";
            if (fallbackLog.Count > 0)
                verboseLog += $"\n── Standard 실패 → 폴백 위치 ({fallbackLog.Count}건) ──\n" + string.Join("\n", fallbackLog) + "\n";
            if (errors.Count > 0)
                verboseLog += "\n오류:\n" + string.Join("\n", errors.Take(20));

            string logPath = null;
            try
            {
                string logDir = Path.Combine(Path.GetTempPath(), "RevitRebarModeler", "Logs");
                Directory.CreateDirectory(logDir);
                logPath = Path.Combine(logDir, $"ShearRebar_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                File.WriteAllText(logPath, verboseLog + "\n\n[디버그]\n" + string.Join("\n", debugLog), System.Text.Encoding.UTF8);
            }
            catch { }

            // ── 사용자 다이얼로그: 핵심 결과만 (검증·폴백 디테일은 모두 로그로) ──
            string msg = $"전단철근 배치 완료\n\n" +
                         $"  생성: {created}개  /  실패: {failed}개";

            if (sheetStats.Count > 0)
            {
                msg += "\n\n  구조도별";
                foreach (var kv in sheetStats.OrderBy(k => k.Key))
                    msg += $"\n    {kv.Key}: {kv.Value}개";
            }

            int anomalyCount = vfPlaneBad + vfHookSwap + vfHookMixed;
            if (anomalyCount > 0)
                msg += $"\n\n검증 이상: {anomalyCount}건 — 자세한 내용은 로그 참고";

            if (errors.Count > 0)
            {
                msg += "\n\n오류:";
                foreach (var err in errors.Take(8))
                    msg += $"\n  · {err}";
                if (errors.Count > 8)
                    msg += $"\n  · ... 외 {errors.Count - 8}건";
            }

            if (!string.IsNullOrEmpty(logPath))
                msg += $"\n\n자세한 로그: {logPath}";

            TaskDialog.Show("전단철근 배치", msg);
            return Result.Succeeded;
        }

        private void TryAddLine(List<Curve> list, XYZ a, XYZ b)
        {
            if (a == null || b == null) return;
            if (a.DistanceTo(b) < 0.001) return;
            try { list.Add(Line.CreateBound(a, b)); } catch { }
        }

        /// <summary>내측 패널 1개: 매칭용 점군 + 폴리라인 양 끝점(체인 정렬용).</summary>
        private class InnerPanel
        {
            public List<XYZ> Points = new List<XYZ>();
            public XYZ End0;   // 첫 세그 시작
            public XYZ End1;   // 마지막 세그 끝
        }

        /// <summary>
        /// 구조도별 "내측 횡방향 패널"(좌·중·우 …)을 Revit XZ 점군으로 구성.
        /// 앞 절반 = 내측. 좌→우 순서는 JSON 저장 순서나 평균 X로는 보장 안 됨
        /// (아치형은 패널 X 구간이 겹침). 대신 내측 철근이 좌→중→우로 한 줄로
        /// 이어지는 성질을 이용해 끝점 연결(체인)을 따라 순서를 매김 → 가운데 패널은
        /// 항상 가운데 순번. 좌/우 방향은 체인 양 끝(벽체 끝, X로 확실히 갈림)으로 정함.
        /// 반환: 좌→우로 정렬된 패널 점군 목록 (index 0=좌 … = K1).
        /// </summary>
        private Dictionary<string, List<List<XYZ>>> BuildInnerPanelMap(CivilExportData data)
        {
            var map = new Dictionary<string, List<List<XYZ>>>();
            if (data?.TransverseRebars == null) return map;

            var byStruct = data.TransverseRebars
                .Where(r => r.CycleNumber == 1)
                .GroupBy(r => ExtractStructureKey(r.SheetId))
                .Where(g => !string.IsNullOrEmpty(g.Key));

            foreach (var g in byStruct)
            {
                var list = g.ToList();          // JSON 저장 순서
                int half = list.Count / 2;       // 앞 절반 = 내측
                if (half == 0) continue;

                var panels = new List<InnerPanel>();
                for (int i = 0; i < half; i++)
                {
                    var segs = list[i].Segments;
                    if (segs == null || segs.Count == 0) continue;

                    var panel = new InnerPanel();
                    foreach (var seg in segs)
                    {
                        if (seg == null) continue;
                        if (seg.StartPoint != null) panel.Points.Add(Civil3DCoordinate.ToRevitWorld(seg.StartPoint, 0));
                        if (seg.MidPoint != null)   panel.Points.Add(Civil3DCoordinate.ToRevitWorld(seg.MidPoint, 0));
                        if (seg.EndPoint != null)   panel.Points.Add(Civil3DCoordinate.ToRevitWorld(seg.EndPoint, 0));
                    }
                    if (panel.Points.Count == 0) continue;

                    var sp = segs.Select(s => s?.StartPoint).FirstOrDefault(p => p != null);
                    var ep = segs.Select(s => s?.EndPoint).LastOrDefault(p => p != null);
                    panel.End0 = sp != null ? Civil3DCoordinate.ToRevitWorld(sp, 0) : panel.Points[0];
                    panel.End1 = ep != null ? Civil3DCoordinate.ToRevitWorld(ep, 0) : panel.Points[panel.Points.Count - 1];
                    panels.Add(panel);
                }
                if (panels.Count == 0) continue;

                var ordered = OrderPanelsAlongChain(panels);
                map[g.Key] = ordered.Select(p => p.Points).ToList();
            }
            return map;
        }

        /// <summary>
        /// 내측 패널들을 끝점 연결(체인)을 따라 좌→우 순서로 정렬.
        /// ① 다른 패널과 가장 멀리 떨어진 끝점 2개 = 체인 양 끝(벽체 끝).
        /// ② X가 작은 쪽을 좌측 시작으로 잡고 최근접 끝점으로 체이닝.
        /// 패널이 1개면 그대로 반환.
        /// </summary>
        private List<InnerPanel> OrderPanelsAlongChain(List<InnerPanel> panels)
        {
            int n = panels.Count;
            if (n <= 1) return panels;

            var ends = new List<(int pi, int we, XYZ p)>();
            for (int i = 0; i < n; i++)
            {
                ends.Add((i, 0, panels[i].End0));
                ends.Add((i, 1, panels[i].End1));
            }

            // 각 끝점의 "다른 패널 끝점까지 최소거리" — 클수록 자유단(체인 끝)
            double FreeScore(XYZ p, int ownPi)
            {
                double best = double.MaxValue;
                foreach (var o in ends)
                {
                    if (o.pi == ownPi) continue;
                    double d = Dist2XZ(o.p, p);
                    if (d < best) best = d;
                }
                return best;
            }

            var freeEnds = ends.OrderByDescending(e => FreeScore(e.p, e.pi)).Take(2).ToList();
            var startEnd = freeEnds.OrderBy(e => e.p.X).First(); // 좌 = X 작은 자유단

            var result = new List<InnerPanel>();
            var visited = new bool[n];
            int cur = startEnd.pi;
            int entryWe = startEnd.we;
            while (cur >= 0 && !visited[cur])
            {
                visited[cur] = true;
                result.Add(panels[cur]);
                XYZ exitPt = (entryWe == 0) ? panels[cur].End1 : panels[cur].End0;

                int nextPi = -1, nextWe = 0;
                double best = double.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (visited[i]) continue;
                    double d0 = Dist2XZ(panels[i].End0, exitPt);
                    double d1 = Dist2XZ(panels[i].End1, exitPt);
                    if (d0 < best) { best = d0; nextPi = i; nextWe = 0; }
                    if (d1 < best) { best = d1; nextPi = i; nextWe = 1; }
                }
                cur = nextPi;
                entryWe = nextWe;
            }
            // 혹시 연결 안 된 패널은 뒤에 붙임 (안전망)
            for (int i = 0; i < n; i++) if (!visited[i]) result.Add(panels[i]);
            return result;
        }

        private static double Dist2XZ(XYZ a, XYZ b)
        {
            if (a == null || b == null) return double.MaxValue;
            double dx = a.X - b.X, dz = a.Z - b.Z;
            return dx * dx + dz * dz;
        }

        /// <summary>
        /// 주어진 점(Revit XYZ)을 가장 가까운 내측 패널에 매칭 → 1-based K 반환.
        /// 가로 위치만 비교하므로 X·Z 평면 거리만 사용(Y=종방향 깊이는 무시).
        /// 패널 정보가 없으면 1로 폴백.
        /// </summary>
        private int FindPanelK(List<List<XYZ>> panels, XYZ p)
        {
            if (panels == null || panels.Count == 0 || p == null) return 1;
            int bestK = 1;
            double best = double.MaxValue;
            for (int i = 0; i < panels.Count; i++)
            {
                foreach (var q in panels[i])
                {
                    double dx = q.X - p.X;
                    double dz = q.Z - p.Z;
                    double d = dx * dx + dz * dz;
                    if (d < best) { best = d; bestK = i + 1; }
                }
            }
            return bestK;
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
        // Rebar 생성 — 3선 U자 + Revit 후크 API
        // 커브 순서: pSI → pSO → pEO → pEI
        // 양끝에 RebarHookType(주로 90도) 적용 → 폴백으로 FreeForm
        // ============================================================
        private bool TryCreateShearRebar(Document doc, List<Curve> curves, RebarBarType barType,
            RebarHookType hookType, Element hostElement, string mark,
            out string createMethod, out string errorDetail, out Rebar createdRebar)
        {
            createMethod = null;
            errorDetail = null;
            createdRebar = null;
            Rebar rebar = null;
            string stdErr = null, ffErr = null;
            RebarFreeFormValidationResult validation = RebarFreeFormValidationResult.Success;

            // 커브의 normal 가산 (Standard CreateFromCurves에 필요)
            XYZ normal = XYZ.BasisY; // 폴백값; 이하에서 정확한 값으로 교체
            if (TryComputePlane(curves, out _, out _, out XYZ computedNormal))
                normal = computedNormal;
            else
                // 평면 법선 산출 실패(커브 퇴화) → BasisY 사용 시 스터럽이 틀어질 수 있어 기록
                System.Diagnostics.Debug.WriteLine(
                    $"[CreateShearRebar] 평면 법선 계산 실패 — BasisY 폴백 사용 (mark={mark})");

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
            createdRebar = rebar;
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

        // ============================================================
        // 시각 대신 로그로 형상 검증
        //   - PlaneOK : 입력 normal Y성분이 ~0 (U자 평면이 종축 Y를 포함)
        //   - HookDir : 시작 hook 끝의 ΔY > 0 AND 끝 hook 끝의 ΔY < 0 → 양쪽 모두 U 안쪽으로 굽음
        //               반대면 Left/Right swap 필요
        //   - 4코너 좌표는 첫 rebar 한정으로 풀 dump
        // ============================================================
        private struct ShearVerifyResult
        {
            public bool HookKnown;
            public double HookStartDY;   // hook[0] 자유단 - pSI.Y
            public double HookEndDY;     // hook[1] 자유단 - pEI.Y
            public bool HookInwardOK;    // 둘 다 U자 안쪽으로 굽음
            public bool HookOutwardSwap; // 둘 다 바깥쪽 (Left/Right swap 필요)
            public bool HookMixed;       // 한쪽만 안쪽 (드물지만 가능)

            public double NormalY;       // 입력 normal의 Y 성분 절대값
            public double NormalDotRadial;
            public bool PlaneOK;
        }

        private ShearVerifyResult VerifyShearRebar(Rebar rebar,
            XYZ pSI, XYZ pSO, XYZ pEO, XYZ pEI,
            XYZ inputNormal, double ndx, double ndz,
            List<string> curveDumpOut)
        {
            var r = new ShearVerifyResult();

            // 평면 검증 — U자 평면은 Y축을 포함해야 하므로 |normal · Y| ≈ 0
            r.NormalY = inputNormal != null ? Math.Abs(inputNormal.Y) : 1.0;
            r.NormalDotRadial = inputNormal != null
                ? Math.Abs(inputNormal.X * ndx + inputNormal.Z * ndz)
                : 1.0;
            r.PlaneOK = (r.NormalY < 0.01) && (r.NormalDotRadial < 0.01);

            // Hook 자유단 위치 — Revit이 만들어낸 centerline curves 에서 직접 읽음.
            // suppressBendRadius=true 로 호출해야 코너가 샤프해서 pSI/pEI가 단일 curve의
            // endpoint로 그대로 나타남. false 면 코너에 bend arc가 끼어 매칭 실패.
            try
            {
                var rebarCurves = rebar.GetCenterlineCurves(false, false, true,
                    MultiplanarOption.IncludeOnlyPlanarCurves, 0);
                if (rebarCurves == null || rebarCurves.Count == 0) return r;

                // 디버그 — 첫 rebar의 curve 토폴로지를 확인할 수 있도록 호출자에 그대로 전달
                if (curveDumpOut != null)
                {
                    for (int i = 0; i < rebarCurves.Count; i++)
                    {
                        var c = rebarCurves[i];
                        XYZ a = c.GetEndPoint(0), b = c.GetEndPoint(1);
                        double len = c.Length;
                        curveDumpOut.Add($"    curve[{i}] {c.GetType().Name} len={len:F4}ft  " +
                                         $"({a.X:F3},{a.Y:F3},{a.Z:F3})→({b.X:F3},{b.Y:F3},{b.Z:F3})");
                    }
                }

                // Revit이 리턴하는 centerline은 한 끝(=시작 hook 자유단)부터 다른 끝(=끝 hook 자유단)까지
                // 연속된 polyline 형태이므로, 첫·마지막 curve의 외곽 endpoint가 곧 hook 자유단.
                // (※ pSI/pEI 근방 매칭은 Revit이 hook 부착하느라 leg를 안쪽으로 단축시켜 실패함)
                XYZ startFreeEnd = rebarCurves[0].GetEndPoint(0);
                XYZ endFreeEnd = rebarCurves[rebarCurves.Count - 1].GetEndPoint(1);

                r.HookKnown = true;
                r.HookStartDY = startFreeEnd.Y - pSI.Y;
                r.HookEndDY = endFreeEnd.Y - pEI.Y;

                // pSI는 Y_start, pEI는 Y_end (Y_end > Y_start). U 안쪽 = pSI 기준 +Y, pEI 기준 -Y.
                bool sIn = r.HookStartDY > 0;
                bool eIn = r.HookEndDY < 0;
                if (sIn && eIn) r.HookInwardOK = true;
                else if (!sIn && !eIn) r.HookOutwardSwap = true;
                else r.HookMixed = true;
            }
            catch { /* HookKnown stays false */ }

            return r;
        }

        /// <summary>
        /// curves 의 처음 점 + 첫 직선 방향(xDir) + curves 가 놓인 평면 normal 추정.
        /// 모든 curve 가 한 평면 위에 있다고 가정.
        /// </summary>
        /// <summary>
        /// U자 커브들이 놓인 평면의 법선을 계산.
        /// 처음 두 커브가 거의 일직선(예: 시작 다리와 상단바가 평행)이면 법선이 퇴화하므로,
        /// 모든 커브 방향벡터 쌍 중 외적 크기가 가장 큰(=가장 비평행) 쌍을 골라 안정적으로 산출한다.
        /// 모든 커브가 평행(완전 퇴화)이면 false.
        /// </summary>
        private bool TryComputePlane(List<Curve> curves, out XYZ origin, out XYZ xDir, out XYZ normal)
        {
            origin = xDir = normal = null;
            if (curves == null || curves.Count < 2) return false;

            origin = curves[0].GetEndPoint(0);

            // 각 커브의 정규화 방향벡터 수집 (degenerate 제외)
            var dirs = new List<XYZ>();
            foreach (var c in curves)
            {
                XYZ d = c.GetEndPoint(1) - c.GetEndPoint(0);
                if (d.GetLength() < 1e-6) continue;
                dirs.Add(d.Normalize());
            }
            if (dirs.Count < 2) return false;

            // 가장 비평행한 두 방향(외적 크기 최대)을 선택 → 퇴화에 강함
            XYZ bestX = null, bestN = null;
            double bestLen = 0;
            for (int i = 0; i < dirs.Count; i++)
            {
                for (int j = i + 1; j < dirs.Count; j++)
                {
                    XYZ n = dirs[i].CrossProduct(dirs[j]);
                    double len = n.GetLength();
                    if (len > bestLen) { bestLen = len; bestX = dirs[i]; bestN = n; }
                }
            }

            if (bestN == null || bestLen < 1e-6) return false; // 모든 커브 평행 → 퇴화

            xDir = bestX;
            normal = bestN.Normalize();
            return true;
        }

        // [통합] BuildHostMap / ParseDepthFromHost / FindRebarBarType / GetBarDiameterFt /
        //   ExtractStructureKey / GetBoundaryCenter 는 Models.RebarHelpers 로 이동.
    }
}
