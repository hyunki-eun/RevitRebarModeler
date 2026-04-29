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
            var csvRows = new List<string>();
            csvRows.Add("structure,dan,placed_outer_x,placed_outer_y,placed_inner_x,placed_inner_y," +
                        "outer_inner_dist,outer_ctc,inner_ctc," +
                        "normal_deg,u_deg," +
                        "raw_outer_x,raw_outer_y,raw_inner_x,raw_inner_y,raw_dist");

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

                    // ★ ray 방향(외측-내측 페어 연결선)을 centerline에 수직으로 강제하기 위한 기준 곡선
                    //   inner/outer trimmed chain → materialize → BuildCenterCurve.
                    //   이 곡선은 ray 방향 계산 전용 (sampling 위치는 기존 Pos1/Pos2 로직 그대로).
                    List<RebarSegment> centerForPerp = null;
                    try
                    {
                        var innerChainForPerp = LongiCurveSampler.ConcatenatePolylinesTrimmed(innerSegLists);
                        var outerChainForPerp = LongiCurveSampler.ConcatenatePolylinesTrimmed(outerSegLists);
                        if (innerChainForPerp.Count > 0 && outerChainForPerp.Count > 0)
                        {
                            var innerMatForPerp = LongiCurveSampler.MaterializeTrimmed(innerChainForPerp);
                            var outerMatForPerp = LongiCurveSampler.MaterializeTrimmed(outerChainForPerp);
                            centerForPerp = LongiCurveSampler.BuildCenterCurve(innerMatForPerp, outerMatForPerp);
                        }
                    }
                    catch { centerForPerp = null; }
                    if (centerForPerp == null || centerForPerp.Count == 0)
                        debugLog.Add($"[{structureKey}] perpendicular용 centerline 빌드 실패 → chord normal fallback 사용");

                    int sheetCreated = 0;

                    // --- Pos1 기준 곡선 + Pos2 shift → arc(b) 생성 ---
                    // Pos1: 어느 횡철근 곡선을 CTC 측정 기준선으로 쓸지 (Inner/Center/Outer)
                    // Pos2: 기준 곡선에서 부호 있는 offset (+offset/2 = BC반대 방향, -offset/2 = BC쪽)
                    // 두 조합으로 결정되는 arc(b) 위에서 CTC 200이 정확히 맞도록 샘플링.
                    // 최종 철근 원 위치는 arc(b)가 아니라 가상 법선선 × 횡철근 곡선 교차 결과에서 계산됨.

                    double halfDiam = setting.DiameterMm / 2.0;
                    double pos2ShiftMm = GetPos2Shift(setting.Pos2Shift, setting.OffsetMm);

                    // Pos1별 기준 곡선 결정 (CTC 측정 기준선)
                    //  - Inner → 내측 횡철근 선
                    //  - Outer → 외측 횡철근 선
                    //  - Center → 내측과 외측의 중점을 이은 중심 곡선 (BuildCenterCurve)
                    List<RebarSegment> concatenatedBase;
                    double baseArcLen;
                    switch (setting.Pos1)
                    {
                        case UI.Pos1Kind.Inner:
                        {
                            var innerSegListsForBase = innerPolys.Select(p => p.Segments)
                                .Where(s => s != null && s.Count > 0).ToList();
                            var chain = LongiCurveSampler.ConcatenatePolylinesTrimmed(innerSegListsForBase);
                            if (chain.Count == 0)
                            {
                                errors.Add($"[{structureKey}] 내측 기준 polyline 연결 실패");
                                continue;
                            }
                            concatenatedBase = LongiCurveSampler.MaterializeTrimmed(chain);
                            baseArcLen = LongiCurveSampler.TotalLengthTrimmed(chain);
                            break;
                        }
                        case UI.Pos1Kind.Center:
                        {
                            // 내측 곡선 위의 각 점 ↔ 외측 최근점의 중점을 이은 곡선
                            var innerSegListsForBase = innerPolys.Select(p => p.Segments)
                                .Where(s => s != null && s.Count > 0).ToList();
                            var innerChain = LongiCurveSampler.ConcatenatePolylinesTrimmed(innerSegListsForBase);
                            var outerSegListsForBase = outerPolys.Select(p => p.Segments)
                                .Where(s => s != null && s.Count > 0).ToList();
                            var outerChain = LongiCurveSampler.ConcatenatePolylinesTrimmed(outerSegListsForBase);
                            if (innerChain.Count == 0 || outerChain.Count == 0)
                            {
                                errors.Add($"[{structureKey}] 중심 곡선 생성을 위한 내/외측 체인 부족");
                                continue;
                            }
                            var innerMat = LongiCurveSampler.MaterializeTrimmed(innerChain);
                            var outerMat = LongiCurveSampler.MaterializeTrimmed(outerChain);
                            concatenatedBase = LongiCurveSampler.BuildCenterCurve(innerMat, outerMat);
                            if (concatenatedBase == null || concatenatedBase.Count == 0)
                            {
                                errors.Add($"[{structureKey}] 중심 곡선 생성 실패");
                                continue;
                            }
                            baseArcLen = LongiCurveSampler.TotalLength(concatenatedBase);
                            break;
                        }
                        case UI.Pos1Kind.Outer:
                        default:
                        {
                            var outerSegListsForBase = outerPolys.Select(p => p.Segments)
                                .Where(s => s != null && s.Count > 0).ToList();
                            var chain = LongiCurveSampler.ConcatenatePolylinesTrimmed(outerSegListsForBase);
                            if (chain.Count == 0)
                            {
                                errors.Add($"[{structureKey}] 외측 기준 polyline 연결 실패");
                                continue;
                            }
                            concatenatedBase = LongiCurveSampler.MaterializeTrimmed(chain);
                            baseArcLen = LongiCurveSampler.TotalLengthTrimmed(chain);
                            break;
                        }
                    }

                    debugLog.Add($"[{structureKey}] 기준 곡선(Pos1={setting.Pos1}) arcLen={baseArcLen:F0}mm, segs={concatenatedBase.Count}");

                    // Pos2 shift는 추후 기준 arc에 추가 offset 적용 시 사용 (현재는 0만 의미있게 처리)
                    double baseOffsetMm = pos2ShiftMm;

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

                    // Step 2-3: 구조도 중심선이 offsetSegs와 만나는 위치를 anchor로 사용 → 양방향 CTC 등분
                    // 중심선이 없거나 교차 못 찾으면 arc 절반(midpoint)으로 fallback
                    GetCenterline(loadedData, structureKey, out double clSx, out double clSy,
                        out double clEx, out double clEy, out bool hasCenterline);
                    double anchorArcLen = LongiCurveSampler.TotalLength(offsetSegs) / 2.0;
                    string anchorSrc = "midpoint";
                    if (hasCenterline)
                    {
                        if (LongiCurveSampler.FindCenterlineIntersection(
                                offsetSegs, clSx, clSy, clEx, clEy,
                                out RebarPoint clHit, out double clArcLen))
                        {
                            anchorArcLen = clArcLen;
                            anchorSrc = $"centerline ({clHit.X:F0},{clHit.Y:F0})";
                        }

                        // 구조도 중심선을 노란색 DirectShape로 표시 (배치 결과 검증용)
                        try
                        {
                            CreateCenterlineDirectShape(doc, structureKey, clSx, clSy, clEx, clEy);
                        }
                        catch { }
                    }
                    var samples = LongiCurveSampler.SampleFromAnchorWithChordNormal(
                        offsetSegs, setting.CtcMm, setting.Count, anchorArcLen);

                    debugLog.Add($"  기준 Pos1={setting.Pos1} → arcLen={baseArcLen:N0}, " +
                                 $"baseOffset={baseOffsetMm:+#;-#;0}mm (Pos2={setting.Pos2Shift}), " +
                                 $"offsetArcLen={offsetArcLen:N0}, offset={setting.OffsetMm:F1}, 샘플={samples.Count}, " +
                                 $"anchor={anchorArcLen:F1}mm ({anchorSrc})");

                    // Step 6: 수집 단계 (다 배치될 후보군 모으기)
                    var candidates = new List<(int OriginalIndex, double ArcLen,
                        RebarPoint OutPt, RebarPoint InPt,           // 배치된 점 (offset/2 적용 후)
                        RebarPoint RawOut, RebarPoint RawIn,         // raw 교차점 (offset/2 적용 전)
                        double Nx, double Ny,                        // chord normal (ray 방향)
                        double Ux, double Uy)>();                    // fOuter→fInner 방향 (정규화)

                    // 단계별 개수 추적
                    int expectedCount = setting.Count;
                    int samplesGenerated = samples.Count * 2; // 쌍(외측+내측)이므로 ×2
                    int innerMissCount = 0, outerMissCount = 0, bothMissCount = 0;

                    for (int si = 0; si < samples.Count; si++)
                    {
                        var (arcLen, ptOnOffset, nx, ny) = samples[si];

                        // ★ ray 방향을 centerline-perpendicular로 교체 → 외측-내측 페어 연결선이 centerline에 수직
                        if (centerForPerp != null && centerForPerp.Count > 0 &&
                            LongiCurveSampler.ComputeChordPerpendicularNearPoint(
                                centerForPerp, ptOnOffset, out double pnx, out double pny))
                        {
                            // 기존 nx,ny와 부호 일치시키기 (반대 방향 뒤집힘 방지)
                            if (pnx * nx + pny * ny < 0) { pnx = -pnx; pny = -pny; }
                            nx = pnx; ny = pny;
                        }

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

                        // raw 교차점 보존 (offset/2 적용 전)
                        var rawOut = new RebarPoint { X = fOuter.X, Y = fOuter.Y };
                        var rawIn  = new RebarPoint { X = fInner.X, Y = fInner.Y };

                        // 내측 방향 단위벡터 = fOuter → fInner.
                        // 외측/내측 모두 "반대쪽 교차점을 향해" offset/2 이동 → 벽체 두께 내부로 들어감.
                        double ux = 0, uy = 0;
                        {
                            double dx = fInner.X - fOuter.X;
                            double dy = fInner.Y - fOuter.Y;
                            double d = Math.Sqrt(dx * dx + dy * dy);
                            if (d > 1e-9)
                            {
                                double mv = setting.OffsetMm / 2.0;
                                ux = dx / d; uy = dy / d;

                                // 외측 원: fOuter 에서 내측 방향(+u) 으로 offset/2
                                fOuter = new RebarPoint
                                {
                                    X = fOuter.X + ux * mv,
                                    Y = fOuter.Y + uy * mv
                                };
                                // 내측 원: fInner 에서 외측 방향(-u) 으로 offset/2
                                fInner = new RebarPoint
                                {
                                    X = fInner.X - ux * mv,
                                    Y = fInner.Y - uy * mv
                                };
                            }
                        }

                        if (si < 3 || si >= samples.Count - 3)
                            debugLog.Add($"    [{si}] arc={arcLen:F0} offset=({ptOnOffset.X:F0},{ptOnOffset.Y:F0}) " +
                                         $"n=({nx:F3},{ny:F3}) fIn=({fInner.X:F0},{fInner.Y:F0}) fOut=({fOuter.X:F0},{fOuter.Y:F0})");

                        candidates.Add((si, arcLen, fOuter, fInner, rawOut, rawIn, nx, ny, ux, uy));
                    }

                    // Step 7: 실제 생성 진행 (노이즈 필터 제거됨 — 모든 후보 그대로 배치)
                    // 단(段) 번호 부여: 호길이 시작점부터의 거리(ArcLen) 작은 순으로 1단, 2단, ...
                    var orderedCandidates = candidates.OrderBy(c => c.ArcLen).ToList();
                    for (int i = 0; i < orderedCandidates.Count; i++)
                    {
                        var cand = orderedCandidates[i];
                        int dan = i + 1; // 1-based 단 번호

                        // 실제 외측 철근 배치
                        if (TryCreateRebar(doc, cand.OutPt, depthFt, barType, hostElement,
                            $"{structureKey}_longi_outer_{dan}단", out string mOut, out string eOut))
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
                            $"{structureKey}_longi_inner_{dan}단", out string mIn, out string eIn))
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
                                 $"offset={setting.OffsetMm:F1}, D/2={halfDiam:F1}, 기준arcLen={baseArcLen:F0}mm, created={sheetCreated}");

                    // ── 종철근 배치 검증 표 (단별 좌표/외-내 거리/연속 단 CTC) ──
                    debugLog.Add($"");
                    debugLog.Add($"[{structureKey}] === 종철근 배치 검증 (Civil3D X=동/서, Y=입면) ===");
                    debugLog.Add($"  단 |       외측(X,Y)        |       내측(X,Y)        | 외-내거리 |  외측CTC  |  내측CTC");
                    debugLog.Add($"  ---+------------------------+------------------------+-----------+-----------+----------");
                    for (int i = 0; i < orderedCandidates.Count; i++)
                    {
                        var c = orderedCandidates[i];
                        int dan = i + 1;
                        double outInDist = Math.Sqrt(
                            Math.Pow(c.OutPt.X - c.InPt.X, 2) +
                            Math.Pow(c.OutPt.Y - c.InPt.Y, 2));
                        string outerCtc = "        -", innerCtc = "        -";
                        double dOuterCtc = double.NaN, dInnerCtc = double.NaN;
                        if (i + 1 < orderedCandidates.Count)
                        {
                            var c2 = orderedCandidates[i + 1];
                            dOuterCtc = Math.Sqrt(
                                Math.Pow(c.OutPt.X - c2.OutPt.X, 2) +
                                Math.Pow(c.OutPt.Y - c2.OutPt.Y, 2));
                            dInnerCtc = Math.Sqrt(
                                Math.Pow(c.InPt.X - c2.InPt.X, 2) +
                                Math.Pow(c.InPt.Y - c2.InPt.Y, 2));
                            outerCtc = $"{dOuterCtc,9:F2}";
                            innerCtc = $"{dInnerCtc,9:F2}";
                        }
                        debugLog.Add($"  {dan,3}| ({c.OutPt.X,10:F2},{c.OutPt.Y,8:F2}) | ({c.InPt.X,10:F2},{c.InPt.Y,8:F2}) | {outInDist,9:F2} | {outerCtc} | {innerCtc}");

                        // CSV 행 기록
                        double normalDeg = Math.Atan2(c.Ny, c.Nx) * 180.0 / Math.PI;
                        if (normalDeg < 0) normalDeg += 360.0;
                        double uDeg = Math.Atan2(c.Uy, c.Ux) * 180.0 / Math.PI;
                        if (uDeg < 0) uDeg += 360.0;
                        double rawDist = Math.Sqrt(
                            Math.Pow(c.RawOut.X - c.RawIn.X, 2) +
                            Math.Pow(c.RawOut.Y - c.RawIn.Y, 2));
                        string outerCtcCsv = double.IsNaN(dOuterCtc) ? "" : dOuterCtc.ToString("F4");
                        string innerCtcCsv = double.IsNaN(dInnerCtc) ? "" : dInnerCtc.ToString("F4");
                        csvRows.Add($"{structureKey},{dan}," +
                                    $"{c.OutPt.X:F4},{c.OutPt.Y:F4},{c.InPt.X:F4},{c.InPt.Y:F4}," +
                                    $"{outInDist:F4},{outerCtcCsv},{innerCtcCsv}," +
                                    $"{normalDeg:F4},{uDeg:F4}," +
                                    $"{c.RawOut.X:F4},{c.RawOut.Y:F4},{c.RawIn.X:F4},{c.RawIn.Y:F4},{rawDist:F4}");

                        // 벡터 검증선 (외측↔내측 페어 잇기, 흰색)
                        try { CreateVectorVerificationLine(doc, structureKey, dan, c.OutPt, c.InPt); }
                        catch { }
                    }
                    debugLog.Add($"");

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

                try { Models.RebarColorHelper.ApplyToAll3DViews(doc); } catch { }
                tr.Commit();
            }

            // 세션 캐시에 종방향 설정 저장 → 전단철근 배치 시 재사용 (파일 재오픈 후 Revit 역파싱으로도 복원 가능)
            SessionCache.LongitudinalSettings = new System.Collections.Generic.Dictionary<string, UI.LongitudinalSheetSetting>(sheetSettings);

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

            // CSV 파일 저장 (벡터/CTC 검증용)
            string csvPath = WriteVerificationCsv(csvRows);
            if (!string.IsNullOrEmpty(csvPath))
                msg += $"\nCSV: {csvPath}";

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

        /// <summary>
        /// 외측↔내측 종철근 페어를 흰색 DirectShape로 잇기. 벡터 방향 시각 검증용.
        /// </summary>
        private void CreateVectorVerificationLine(Document doc, string structureKey, int dan,
            RebarPoint outPt, RebarPoint inPt)
        {
            var p1 = Civil3DCoordinate.ToRevitWorld(outPt.X, outPt.Y, 0);
            var p2 = Civil3DCoordinate.ToRevitWorld(inPt.X, inPt.Y, 0);
            if (p1.DistanceTo(p2) < 0.001) return;

            var line = Line.CreateBound(p1, p2);
            var ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
            ds.ApplicationId = "RevitRebarModeler";
            string mark = $"{structureKey}_벡터검증_{dan}단";
            ds.ApplicationDataId = mark;
            try { ds.Name = mark; } catch { }
            ds.SetShape(new List<GeometryObject> { line });
            ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(mark);

            var activeView = doc.ActiveView;
            if (activeView != null)
            {
                try
                {
                    var ogs = new OverrideGraphicSettings();
                    var white = new Color(255, 255, 255);
                    ogs.SetProjectionLineColor(white);
                    ogs.SetCutLineColor(white);
                    activeView.SetElementOverrides(ds.Id, ogs);
                }
                catch { }
            }
        }

        /// <summary>
        /// 구조도 중심선(Cycle1Centerline)을 노란색 DirectShape로 활성 뷰에 표시.
        /// 배치 결과와 anchor 위치 검증용.
        /// </summary>
        private void CreateCenterlineDirectShape(Document doc, string structureKey,
            double sx, double sy, double ex, double ey)
        {
            var p1 = Civil3DCoordinate.ToRevitWorld(sx, sy, 0);
            var p2 = Civil3DCoordinate.ToRevitWorld(ex, ey, 0);
            if (p1.DistanceTo(p2) < 0.001) return;

            var line = Line.CreateBound(p1, p2);
            var ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
            ds.ApplicationId = "RevitRebarModeler";
            string mark = $"구조도중심선_{structureKey}";
            ds.ApplicationDataId = mark;
            try { ds.Name = mark; } catch { }
            ds.SetShape(new List<GeometryObject> { line });
            ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(mark);

            var activeView = doc.ActiveView;
            if (activeView != null)
            {
                try
                {
                    var ogs = new OverrideGraphicSettings();
                    var yellow = new Color(250, 204, 21);
                    ogs.SetProjectionLineColor(yellow);
                    ogs.SetCutLineColor(yellow);
                    activeView.SetElementOverrides(ds.Id, ogs);
                }
                catch { }
            }
        }

        private string WriteVerificationCsv(List<string> rows)
        {
            if (rows == null || rows.Count <= 1) return null;
            try
            {
                string logDir = Path.Combine(Path.GetTempPath(), "RevitRebarModeler", "Logs");
                Directory.CreateDirectory(logDir);
                string csvPath = Path.Combine(logDir, $"LongitudinalPlacement_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                File.WriteAllLines(csvPath, rows, new System.Text.UTF8Encoding(true)); // BOM 포함 → Excel 한글 호환
                return csvPath;
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
