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
    [Transaction(TransactionMode.Manual)]
    public class CreateLongitudinalRebarCommand : IExternalCommand
    {
        private const double MmToFt = 1.0 / 304.8;

        private bool _verboseDebug = true;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument.Document;

            var window = new UI.LongitudinalRebarWindow(doc);
            if (window.ShowDialog() != true)
                return Result.Cancelled;

            var loadedData = window.LoadedData;
            var sheetSettings = window.SheetSettings;

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
            var errors = new List<string>();
            var debugLog = new List<string>();
            var sheetStats = new Dictionary<string, int>();
            var diameterStats = new Dictionary<int, int>();
            var failureDetails = new List<string>();

            using (var tr = new Transaction(doc, "종방향 철근 배치"))
            {
                tr.Start();

                if (new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).FirstOrDefault() == null)
                {
                    tr.RollBack();
                    TaskDialog.Show("오류", "RebarBarType이 없습니다.\n구조 템플릿에서 실행해주세요.");
                    return Result.Failed;
                }

                foreach (var kvp in sheetSettings)
                {
                    string structureKey = kvp.Key;
                    var setting = kvp.Value;

                    if (!hostMap.TryGetValue(structureKey, out Element hostElement))
                    {
                        errors.Add($"[{structureKey}] Host 매칭 실패");
                        continue;
                    }

                    double depthMm = ParseDepthFromHost(hostElement);
                    if (depthMm <= 0) depthMm = 1000;
                    double depthFt = depthMm * MmToFt;

                    RebarBarType barType = FindRebarBarType(doc, setting.DiameterMm);
                    if (barType == null)
                    {
                        errors.Add($"[{structureKey}] RebarBarType 매칭 실패 (D{setting.DiameterMm})");
                        continue;
                    }

                    // ====== 사용자 7 단계 방식 ======
                    // 1. 타겟 arc(a) → 옵셋 → 옵셋 arc(b)
                    // 2. 옵셋 arc(b) 중앙에서 양방향 CTC 등분 → 포인트(c)
                    // 3. 포인트(c)의 법선 벡터(d)
                    // 4. 법선 + 역법선 → 가상 선(e)
                    // 5. 가상 선(e)와 횡철근 내부/외부 교차점(f_inner, f_outer)
                    // 6. 교차점에서 종철근 D/2 만큼 안쪽으로 보정
                    // 7. 보정된 점에서 depth 방향으로 철근 생성

                    // --- 준비: cycle1 polyline 수집 및 내측/외측 분류 ---
                    var sheetRebars = loadedData.TransverseRebars
                        .Where(r => r.CycleNumber == 1 && ExtractStructureKey(r.SheetId) == structureKey)
                        .ToList();
                    if (sheetRebars.Count == 0)
                    {
                        errors.Add($"[{structureKey}] cycle1 polyline 없음");
                        continue;
                    }

                    GetBoundaryCenter(loadedData, structureKey, out double bCx, out double bCy, out bool hasBoundaryCenter);

                    var classification = ClassifyInnerOuter(sheetRebars, bCx, bCy, hasBoundaryCenter);
                    var outerPolys = sheetRebars.Where(r => classification.TryGetValue(r.Id, out var isOuter) && isOuter).ToList();
                    var innerPolys = sheetRebars.Where(r => !classification.TryGetValue(r.Id, out var isOuter2) || !isOuter2).ToList();

                    // 전체 내측/외측 segments 합치기 (이 끊어지지 않은 완전한 곡선들을 교차 탐색용으로 사용)
                    var innerSegLists = innerPolys.Select(p => p.Segments).Where(s => s != null && s.Count > 0).ToList();
                    var concatInner = LongiCurveSampler.ConcatenatePolylines(innerSegLists);

                    var outerSegLists = outerPolys.Select(p => p.Segments).Where(s => s != null && s.Count > 0).ToList();
                    var concatOuter = LongiCurveSampler.ConcatenatePolylines(outerSegLists);

                    debugLog.Add($"[{structureKey}] polyline={sheetRebars.Count}, " +
                                 $"outer={outerPolys.Count}({string.Join(",", outerPolys.Select(p => p.Id))}), " +
                                 $"inner={innerPolys.Count}({string.Join(",", innerPolys.Select(p => p.Id))})");

                    if (outerPolys.Count == 0 || innerPolys.Count == 0)
                    {
                        errors.Add($"[{structureKey}] 내측({innerPolys.Count}) 또는 외측({outerPolys.Count}) polyline 부족");
                        continue;
                    }

                    int sheetCreated = 0;

                    // --- Pos1 기준 곡선 + Pos2 shift → arc(b) 생성 ---
                    // Pos1: 어느 횡철근 곡선을 CTC 측정 기준선으로 쓸지 (Inner/Center/Outer)
                    // Pos2: 기준 곡선에서 부호 있는 offset (+offset/2 = BC반대 방향, -offset/2 = BC쪽)
                    // 두 조합으로 결정되는 arc(b) 위에서 CTC 200이 정확히 맞도록 샘플링.
                    // 최종 철근 원 위치는 arc(b)가 아니라 가상 법선선 × 횡철근 곡선 교차 결과에서 계산됨.

                    double halfDiam = setting.DiameterMm / 2.0;
                    double pos2ShiftMm = GetPos2Shift(setting.Pos2Shift, setting.OffsetMm);

                    // Pos1별 기준 polyline 리스트 + 기준선 offset 부호 결정
                    List<TransverseRebarData> basePolys;
                    // baseSignFromBC: 기준 곡선에서 "BC 반대 방향(= outer 방향, 지반 쪽)"이 +인지 결정
                    //  - Inner 기준선 → inner 곡선에서 양수 shift면 BC 반대(벽체 중심) 쪽으로
                    //  - Outer 기준선 → outer 곡선에서 양수 shift면 BC 반대(지반) 쪽으로
                    //  - Center 기준선 → outer 곡선에서 -offset/2 shift 후 추가로 pos2ShiftMm 적용
                    double baseOffsetMm; // 기준선에서 최종 arc(b)까지의 부호 있는 거리 (+ = BC 반대)
                    switch (setting.Pos1)
                    {
                        case UI.Pos1Kind.Inner:
                            basePolys = innerPolys;
                            baseOffsetMm = pos2ShiftMm; // inner + pos2shift
                            break;
                        case UI.Pos1Kind.Center:
                            // 중심 = outer에서 -offset/2 → 다시 pos2ShiftMm 적용
                            basePolys = outerPolys;
                            baseOffsetMm = -(setting.OffsetMm / 2.0) + pos2ShiftMm;
                            break;
                        case UI.Pos1Kind.Outer:
                        default:
                            basePolys = outerPolys;
                            baseOffsetMm = pos2ShiftMm; // outer + pos2shift
                            break;
                    }

                    // --- 기준 polyline들을 TrimmedChain으로 연결 ---
                    var baseSegLists = basePolys.Select(p => p.Segments).Where(s => s != null && s.Count > 0).ToList();

                    var chainDebug = new List<string>();
                    LongiCurveSampler.TrimmedChainDebugLog = chainDebug;
                    debugLog.Add($"[{structureKey}] TrimmedChain 구성:");
                    var trimmedChain = LongiCurveSampler.ConcatenatePolylinesTrimmed(baseSegLists);
                    LongiCurveSampler.TrimmedChainDebugLog = null;
                    debugLog.AddRange(chainDebug);

                    for (int ti = 0; ti < trimmedChain.Count; ti++)
                    {
                        var ts = trimmedChain[ti];
                        double segL = LongiCurveSampler.SegmentLength(ts.Seg);
                        debugLog.Add($"    trim#{ti} {ts.Seg.SegmentType} segL={segL:F1} " +
                                     $"LocalStart={ts.LocalStartMm:F1} LocalEnd={ts.LocalEndMm:F1} eff={ts.EffectiveLength:F1}");
                    }

                    if (trimmedChain.Count == 0)
                    {
                        errors.Add($"[{structureKey}] 기준 polyline 연결 실패");
                        continue;
                    }

                    var concatenatedBase = LongiCurveSampler.MaterializeTrimmed(trimmedChain);
                    double baseArcLen = LongiCurveSampler.TotalLengthTrimmed(trimmedChain);

                    // Step 1: 기준 arc(a)를 baseOffsetMm 만큼 옵셋 → arc(b)
                    // offsetAwayFromBC = (baseOffsetMm >= 0) — 부호에 따라 방향 지정, 거리는 절대값
                    var offsetSegs = LongiCurveSampler.OffsetPolyline(
                        concatenatedBase, Math.Abs(baseOffsetMm), bCx, bCy, baseOffsetMm >= 0);
                    if (Math.Abs(baseOffsetMm) < 1e-9)
                    {
                        // 0 offset이면 기준 곡선 그대로 사용
                        offsetSegs = concatenatedBase;
                    }

                    double offsetArcLen = LongiCurveSampler.TotalLength(offsetSegs);

                    // Step 2-3: 옵셋 arc(b) 중앙에서 지정된 개수만큼 양방향 CTC 등분 → 포인트(c) + 법선(d)
                    var samples = LongiCurveSampler.SampleFromCenterWithChordNormal(
                        offsetSegs, setting.CtcMm, setting.Count);

                    debugLog.Add($"  기준 polyline {basePolys.Count}개 (Pos1={setting.Pos1}) → trimmed arcLen={baseArcLen:N0}, " +
                                 $"baseOffset={baseOffsetMm:+#;-#;0}mm (Pos2={setting.Pos2Shift}), " +
                                 $"offsetArcLen={offsetArcLen:N0}, offset={setting.OffsetMm:F1}, 샘플={samples.Count}");

                    // Step 6: 수집 단계 (다 배치될 후보군 모으기)
                    var candidates = new List<(int OriginalIndex, double ArcLen, RebarPoint OutPt, RebarPoint InPt)>();

                    // 단계별 개수 추적
                    int expectedCount = setting.Count;
                    int samplesGenerated = samples.Count * 2; // 쌍(외측+내측)이므로 ×2
                    int innerMissCount = 0, outerMissCount = 0, bothMissCount = 0;

                    for (int si = 0; si < samples.Count; si++)
                    {
                        var (arcLen, ptOnOffset, nx, ny) = samples[si];

                        // Step 4-5: 법선 + 역법선 가상 선(e) → 횡철근 내부/외부와 교차
                        RebarPoint fInner = null, fOuter = null;

                        bool hitInner = LongiCurveSampler.IntersectRayWithPolyline(
                            concatInner, ptOnOffset.X, ptOnOffset.Y, nx, ny, false, out fInner);
                        bool hitOuter = LongiCurveSampler.IntersectRayWithPolyline(
                            concatOuter, ptOnOffset.X, ptOnOffset.Y, nx, ny, false, out fOuter);

                        // ★ 내측·외측 모두 교차해야만 후보군에 편입
                        if (!hitInner || !hitOuter)
                        {
                            if (!hitInner && !hitOuter) bothMissCount++;
                            else if (!hitInner) innerMissCount++;
                            else outerMissCount++;

                            if (si < 3 || si >= samples.Count - 3)
                                debugLog.Add($"    [{si}] arc={arcLen:F0} 막힘/스킵 (inner={hitInner}, outer={hitOuter})");
                            continue;
                        }

                        // 외측 원: 외측 횡방향 철근 선에서 offset/2 만큼 안쪽(내측 방향)으로 이동
                        {
                            double dx = ptOnOffset.X - fOuter.X;
                            double dy = ptOnOffset.Y - fOuter.Y;
                            double d = Math.Sqrt(dx * dx + dy * dy);
                            if (d > 1e-9)
                            {
                                double mv = setting.OffsetMm / 2.0;
                                fOuter = new RebarPoint
                                {
                                    X = fOuter.X + dx / d * mv,
                                    Y = fOuter.Y + dy / d * mv
                                };
                            }
                        }
                        // 내측 원: 내측 횡방향 철근 선에서 offset/2 만큼 안쪽(외측 방향)으로 이동
                        {
                            double dx = ptOnOffset.X - fInner.X;
                            double dy = ptOnOffset.Y - fInner.Y;
                            double d = Math.Sqrt(dx * dx + dy * dy);
                            if (d > 1e-9)
                            {
                                double mv = setting.OffsetMm / 2.0;
                                fInner = new RebarPoint
                                {
                                    X = fInner.X + dx / d * mv,
                                    Y = fInner.Y + dy / d * mv
                                };
                            }
                        }

                        if (si < 3 || si >= samples.Count - 3)
                            debugLog.Add($"    [{si}] arc={arcLen:F0} offset=({ptOnOffset.X:F0},{ptOnOffset.Y:F0}) " +
                                         $"n=({nx:F3},{ny:F3}) fIn=({fInner.X:F0},{fInner.Y:F0}) fOut=({fOuter.X:F0},{fOuter.Y:F0})");

                        candidates.Add((si, arcLen, fOuter, fInner));
                    }

                    // Step 7: 실제 생성 진행 (노이즈 필터 제거됨 — 모든 후보 그대로 배치)
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        var cand = candidates[i];

                        // 실제 외측 철근 배치
                        if (TryCreateRebar(doc, cand.OutPt, depthFt, barType, hostElement,
                            $"{structureKey}_longi_outer", out string mOut, out string eOut))
                        {
                            created++; sheetCreated++;
                            if (mOut.StartsWith("Standard")) createdStandard++;
                            else if (mOut.StartsWith("FreeForm")) createdFreeForm++;
                            int dKey = (int)Math.Round(setting.DiameterMm);
                            diameterStats[dKey] = diameterStats.ContainsKey(dKey) ? diameterStats[dKey] + 1 : 1;
                        }
                        else
                        {
                            failed++;
                            failureDetails.Add($"[{structureKey}] 외측 arc={cand.ArcLen:F1} → {eOut}");
                        }

                        // 실제 내측 철근 배치
                        if (TryCreateRebar(doc, cand.InPt, depthFt, barType, hostElement,
                            $"{structureKey}_longi_inner", out string mIn, out string eIn))
                        {
                            created++; sheetCreated++;
                            if (mIn.StartsWith("Standard")) createdStandard++;
                            else if (mIn.StartsWith("FreeForm")) createdFreeForm++;
                            int dKey = (int)Math.Round(setting.DiameterMm);
                            diameterStats[dKey] = diameterStats.ContainsKey(dKey) ? diameterStats[dKey] + 1 : 1;
                        }
                        else
                        {
                            failed++;
                            failureDetails.Add($"[{structureKey}] 내측 arc={cand.ArcLen:F1} → {eIn}");
                        }
                    }

                    sheetStats[structureKey] = sheetCreated;
                    debugLog.Add($"[{structureKey}] 전체: Pos1={setting.Pos1}, Pos2={setting.Pos2Shift}, CTC={setting.CtcMm}, " +
                                 $"offset={setting.OffsetMm:F1}, D/2={halfDiam:F1}, 기준연결={basePolys.Count}개, created={sheetCreated}");

                    // 목표 개수와 다르면 이유 분석 기록
                    if (sheetCreated != expectedCount)
                    {
                        int diff = expectedCount - sheetCreated;
                        var reasons = new List<string>();
                        if (samplesGenerated < expectedCount)
                            reasons.Add($"샘플 부족 {expectedCount - samplesGenerated}개 (chain 공간={baseArcLen:F0}mm, CTC={setting.CtcMm}mm에 맞춰 {samples.Count}쌍만 생성됨)");
                        int missTotal = (innerMissCount + outerMissCount + bothMissCount) * 2;
                        if (missTotal > 0)
                            reasons.Add($"교차 실패 {missTotal}개 (내측만 실패 {innerMissCount*2}, 외측만 실패 {outerMissCount*2}, 둘 다 실패 {bothMissCount*2})");

                        string reasonText = reasons.Count > 0
                            ? string.Join(" / ", reasons)
                            : "원인 불명 (생성/교차/필터 단계 모두 설명 안됨)";

                        string deltaInfo = $"[{structureKey}] 안내: 설정 개수 {expectedCount}개 → 실제 곡선에 맞춰 {sheetCreated}개 배치 ({(diff > 0 ? "-" : "+")}{Math.Abs(diff)}): {reasonText}";
                        debugLog.Add(deltaInfo);
                        
                        // 자연스러운 공간 부족(샘플 부족)이면 에러로 간주하지 않고 안내로만 끝냄
                        if (missTotal > 0 || reasons.Count == 0)
                        {
                            errors.Add($"[{structureKey}] ⚠ 철근 교차 실패 또는 알 수 없는 원인으로 일부 누락됨: {reasonText}");
                        }
                    }
                }

                tr.Commit();
            }

            string msg = "═══════════════════════════════════\n" +
                         "  종방향 철근 배치 완료\n" +
                         "═══════════════════════════════════\n" +
                         $"── 총 배치: {created}개 | 실패: {failed}개\n" +
                         $"│  ── Standard: {createdStandard}개\n" +
                         $"│  ── FreeForm: {createdFreeForm}개\n";

            foreach (var kv in diameterStats.OrderBy(k => k.Key))
                msg += $"│  ── D{kv.Key}: {kv.Value}개\n";

            if (sheetStats.Count > 0)
            {
                msg += "\n── 구조도별\n";
                foreach (var kv in sheetStats.OrderBy(k => k.Key))
                    msg += $"│  ── {kv.Key}: {kv.Value}개\n";
            }

            if (_verboseDebug && debugLog.Count > 0)
                msg += "\n═══ 디버그 ═══\n" + string.Join("\n", debugLog);

            if (errors.Count > 0)
                msg += "\n\n오류:\n" + string.Join("\n", errors.Take(20));

            string logPath = WriteFailureLog(created, createdStandard, createdFreeForm, failed,
                diameterStats, failureDetails, errors, debugLog, sheetSettings, sheetStats);
            if (!string.IsNullOrEmpty(logPath))
                msg += $"\n\n로그: {logPath}";

            TaskDialog.Show("종방향 철근 배치", msg);
            return Result.Succeeded;
        }

        // ============================================================
        // 샘플 위치 생성: anchor 기준 양쪽 확장. 한쪽이 boundary 도달하면 반대쪽으로 비대칭 확장.
        // ============================================================
        private List<double> GenerateSamplePositions(double anchorArcLen, double ctcMm, int sets, double totalArcLen)
        {
            var result = new List<double>();
            if (sets <= 0 || ctcMm <= 0 || totalArcLen <= 0) return result;

            bool setsOdd = (sets % 2 == 1);

            // 후보 생성: anchor 기준으로 거리순 sort된 위치들
            var candidates = new List<double>();
            if (setsOdd)
            {
                if (anchorArcLen >= -1e-6 && anchorArcLen <= totalArcLen + 1e-6)
                    candidates.Add(anchorArcLen);
            }

            // setsOdd면 k=1 → anchor±CTC, k=2 → anchor±2CTC, ...
            // !setsOdd면 k=1 → anchor±CTC/2, k=2 → anchor±3CTC/2, ...
            int k = 1;
            int safetyLimit = sets * 4 + 10;
            while (candidates.Count < sets && k < safetyLimit)
            {
                double offset = setsOdd ? k * ctcMm : (k - 0.5) * ctcMm;
                double rPos = anchorArcLen + offset;
                double lPos = anchorArcLen - offset;

                bool rOk = rPos >= -1e-6 && rPos <= totalArcLen + 1e-6;
                bool lOk = lPos >= -1e-6 && lPos <= totalArcLen + 1e-6;

                if (rOk) candidates.Add(rPos);
                if (candidates.Count >= sets) break;
                if (lOk) candidates.Add(lPos);

                // 양쪽 모두 범위 밖이면 종료
                if (!rOk && !lOk) break;

                k++;
            }

            candidates.Sort();
            return candidates;
        }

        /// <summary>
        /// 구조도당 총 갯수를 쌍 수로 분배.
        /// 각 쌍에 동일 수 배정 + 짝수 보정 (4N+2 형태 우선).
        /// 예: count=10, pairs=3 → 각 쌍 4개 (총 12개, 실제 의도에 가까움).
        /// </summary>
        private int DistributeCountToPair(int totalCount, int pairs)
        {
            if (pairs <= 0) return 0;
            if (totalCount <= 0) return 0;

            double perPair = (double)totalCount / pairs;
            int perPairInt = (int)Math.Round(perPair);

            // 짝수로 보정 (홀수면 위로 올림)
            if (perPairInt % 2 != 0) perPairInt += 1;

            // 4N+2 우선: 4N이면 +2 해서 4N+2로 조정
            // (단, 사용자가 4, 8, 12 같은 값을 명시했으면 경고만 띄우고 진행 — 여기선 분배 후 결과가 4N이면 UI warning은 이미 통과했다고 가정)
            if (perPairInt < 2) perPairInt = 2;

            return perPairInt;
        }

        /// <summary>
        /// Pos2 선택값을 부호 있는 shift 거리(mm)로 변환.
        /// +offset/2 = 양수 (BC 반대 방향, 지반 쪽)
        /// -offset/2 = 음수 (BC 쪽, 터널 공간 쪽)
        /// 0        = 0
        /// </summary>
        private static double GetPos2Shift(UI.Pos2ShiftKind shift, double offset)
        {
            switch (shift)
            {
                case UI.Pos2ShiftKind.PlusHalf: return +offset / 2.0;
                case UI.Pos2ShiftKind.MinusHalf: return -offset / 2.0;
                default: return 0;
            }
        }

        // ============================================================
        // Rebar 생성 (Standard → FreeForm 폴백)
        // ============================================================
        private bool TryCreateRebar(Document doc, RebarPoint pt, double depthFt, RebarBarType barType,
            Element hostElement, string mark, out string createMethod, out string errorDetail)
        {
            createMethod = null;
            errorDetail = null;

            XYZ p1 = Civil3DCoordinate.ToRevitWorld(pt.X, pt.Y, 0);
            XYZ p2 = Civil3DCoordinate.ToRevitWorld(pt.X, pt.Y, depthFt);
            if (p1.DistanceTo(p2) < 0.001)
            {
                errorDetail = "길이 0";
                return false;
            }

            Line line = Line.CreateBound(p1, p2);
            var curves = new List<Curve> { line };

            Rebar rebarElem = null;
            string standardError = null;
            string freeformError = null;
            RebarFreeFormValidationResult validationResult = RebarFreeFormValidationResult.Success;

            try
            {
                rebarElem = Rebar.CreateFromCurves(
                    doc, RebarStyle.Standard, barType,
                    null, null,
                    hostElement, XYZ.BasisX,
                    curves,
                    RebarHookOrientation.Left, RebarHookOrientation.Left,
                    true, true);
                if (rebarElem != null) createMethod = "Standard";
            }
            catch (Exception ex)
            {
                standardError = $"{ex.GetType().Name}: {ex.Message}";
                rebarElem = null;
            }

            if (rebarElem == null)
            {
                try
                {
                    var curveSets = new List<IList<Curve>> { curves };
                    rebarElem = Rebar.CreateFreeForm(doc, barType, hostElement, curveSets, out validationResult);
                    if (rebarElem != null) createMethod = $"FreeForm({validationResult})";
                    else freeformError = $"null (validation={validationResult})";
                }
                catch (Exception ex)
                {
                    freeformError = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            if (rebarElem == null)
            {
                errorDetail = $"Std: {standardError ?? "OK"} | FF: {freeformError ?? "OK"}";
                return false;
            }

            try { rebarElem.SetHookTypeId(0, ElementId.InvalidElementId); } catch { }
            try { rebarElem.SetHookTypeId(1, ElementId.InvalidElementId); } catch { }

            rebarElem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(mark);
            rebarElem.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"{mark}|{createMethod}");

            return true;
        }

        // ============================================================
        // Boundary/Centerline helpers
        // ============================================================
        private void GetBoundaryCenter(CivilExportData data, string structureKey,
            out double cx, out double cy, out bool found)
        {
            cx = cy = 0;
            found = false;
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

        private void GetCenterline(CivilExportData data, string structureKey,
            out double sx, out double sy, out double ex, out double ey, out bool found)
        {
            sx = sy = ex = ey = 0;
            found = false;
            if (data?.StructureRegions == null) return;
            var keyRegex = new Regex(@"구조도\((\d+)\)");
            foreach (var cd in data.StructureRegions)
            {
                var m = keyRegex.Match(cd.CycleKey ?? "");
                if (!m.Success) continue;
                if ($"구조도({m.Groups[1].Value})" != structureKey) continue;
                if (!cd.HasCenterlines) continue;
                sx = cd.Cycle1CenterlineStartX;
                sy = cd.Cycle1CenterlineStartY;
                ex = cd.Cycle1CenterlineEndX;
                ey = cd.Cycle1CenterlineEndY;
                found = true;
                return;
            }
        }

        /// <summary>
        /// Civil3D 원본 저장 순서 기준 쌍 매칭: 앞 i번째(내측) ↔ 뒤 i번째(외측).
        /// BC 인자는 시그니처 유지 목적으로 남겨두며 현재는 사용하지 않는다.
        /// </summary>
        private List<(TransverseRebarData inner, TransverseRebarData outer)> MatchInnerOuterPairs(
            List<TransverseRebarData> rebars, double bCx, double bCy, bool hasCenter)
        {
            var result = new List<(TransverseRebarData, TransverseRebarData)>();
            if (rebars == null || rebars.Count < 2) return result;

            int half = rebars.Count / 2;
            for (int i = 0; i < half; i++)
            {
                var inner = rebars[i];
                var outer = rebars[i + half];
                if (inner?.Segments == null || inner.Segments.Count == 0) continue;
                if (outer?.Segments == null || outer.Segments.Count == 0) continue;
                result.Add((inner, outer));
            }
            return result;
        }

        /// <summary>
        /// Civil3D 원본 저장 순서 기준 분류: 앞 절반 = 내측, 뒤 절반 = 외측.
        /// BC 인자는 시그니처 유지 목적으로 남겨두며 현재는 사용하지 않는다.
        /// </summary>
        private Dictionary<string, bool> ClassifyInnerOuter(List<TransverseRebarData> rebars,
            double cx, double cy, bool hasCenter)
        {
            var result = new Dictionary<string, bool>();
            if (rebars == null || rebars.Count < 2) return result;

            int half = rebars.Count / 2;
            for (int i = 0; i < rebars.Count; i++)
            {
                var r = rebars[i];
                if (r?.Segments == null || r.Segments.Count == 0) continue;
                result[r.Id] = (i >= half); // true = outer (뒤 절반)
            }
            return result;
        }

        // ============================================================
        // 공통 헬퍼
        // ============================================================
        private string WriteFailureLog(int created, int createdStandard, int createdFreeForm, int failed,
            Dictionary<int, int> diameterStats, List<string> failureDetails, List<string> errors, List<string> debugLog,
            Dictionary<string, UI.LongitudinalSheetSetting> sheetSettings, Dictionary<string, int> sheetStats)
        {
            try
            {
                string logDir = Path.Combine(Path.GetTempPath(), "RevitRebarModeler", "Logs");
                Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, $"LongitudinalRebar_{DateTime.Now:yyyyMMdd_HHmmss}.log");

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("═══════════════════════════════════");
                sb.AppendLine($"  종방향 철근 배치 로그 — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("═══════════════════════════════════");
                sb.AppendLine($"총 배치: {created} | 실패: {failed}");
                sb.AppendLine($"  Standard: {createdStandard} | FreeForm: {createdFreeForm}");
                sb.AppendLine();

                sb.AppendLine("── 직경별 성공 ──");
                foreach (var kv in diameterStats.OrderBy(k => k.Key))
                    sb.AppendLine($"  D{kv.Key}: {kv.Value}개");

                sb.AppendLine();
                sb.AppendLine("── 구조도별 ──");
                foreach (var kv in sheetStats.OrderBy(k => k.Key))
                    sb.AppendLine($"  {kv.Key}: {kv.Value}개");

                sb.AppendLine();
                sb.AppendLine("── 구조도별 설정 ──");
                foreach (var kv in sheetSettings)
                    sb.AppendLine($"  {kv.Key}: Pos1={kv.Value.Pos1}, Pos2Shift={kv.Value.Pos2Shift}, " +
                                  $"CTC={kv.Value.CtcMm}mm, cnt={kv.Value.Count}, D{kv.Value.DiameterMm}, offset={kv.Value.OffsetMm}mm");

                if (failureDetails.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"═══ 실패 상세 ({failureDetails.Count}건) ═══");
                    foreach (var line in failureDetails) sb.AppendLine(line);
                }

                if (errors.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"═══ 그룹 오류 ({errors.Count}건) ═══");
                    foreach (var e in errors) sb.AppendLine(e);
                }

                if (debugLog.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("═══ 디버그 ═══");
                    foreach (var line in debugLog) sb.AppendLine(line);
                }

                File.WriteAllText(logPath, sb.ToString(), System.Text.Encoding.UTF8);
                return logPath;
            }
            catch
            {
                return null;
            }
        }

        private double ParseDepthFromHost(Element hostElement)
        {
            string comments = hostElement.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
            var match = Regex.Match(comments, @"depth=(\d+\.?\d*)");
            return match.Success ? double.Parse(match.Groups[1].Value) : 0;
        }

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

        private string ExtractStructureKey(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var match = Regex.Match(text, @"구조도\(\d+\)");
            return match.Success ? match.Value : "";
        }

        private RebarBarType FindRebarBarType(Document doc, double diameterMm)
        {
            int d = (int)Math.Round(diameterMm);
            double targetFt = diameterMm * MmToFt;

            var allTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarBarType))
                .Cast<RebarBarType>()
                .ToList();

            string[] nameCandidates = { $"D{d}", $"{d} 400S", $"D{d} 400S", $"{d}" };
            foreach (var name in nameCandidates)
            {
                var hit = allTypes.FirstOrDefault(r => r.Name == name);
                if (hit != null) return hit;
            }

            return allTypes
                .Where(r => Math.Abs(GetBarDiameterFt(r) - targetFt) < 0.001)
                .OrderBy(r => (r.Name.Contains("스트럽") || r.Name.Contains("타이") || r.Name.Contains("Stirrup") || r.Name.Contains("Tie")) ? 1 : 0)
                .FirstOrDefault();
        }

        private double GetBarDiameterFt(RebarBarType barType)
        {
            var param = barType.get_Parameter(BuiltInParameter.REBAR_BAR_DIAMETER);
            return param != null ? param.AsDouble() : 0.0;
        }
    }

}
