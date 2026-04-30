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
    public class CreateTransverseRebarCommand : IExternalCommand
    {
        private const double MmToFt = 1.0 / 304.8;

        private bool _verboseDebug = false;   // 개발 시 true, 사용자 배포 시 false
        private bool _debugLogged = false;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument.Document;

            var window = new UI.TransverseRebarWindow(doc);
            if (window.ShowDialog() != true)
                return Result.Cancelled;

            var selectedRebars = window.SelectedRebars;
            var sheetCtcMap = window.SheetCtcMap;

            // 세션 GlobalOrigin 초기화 + JSON 기반 자동 설정 (구조물 생성 없이 철근만 돌리는 경우 대비)
            Civil3DCoordinate.ResetGlobalOrigin();
            Civil3DCoordinate.AutoSetGlobalOrigin(window.LoadedData);

            // sheet별 Cycle2→Cycle1 rigid transform (중심선 start/end 기반)
            var sheetTransforms = Civil3DCoordinate.BuildSheetTransforms(window.LoadedData);

            var hostMap = BuildHostMap(doc);
            if (hostMap.Count == 0)
            {
                TaskDialog.Show("오류", "프로젝트에 구조 프레임 요소가 없습니다.\n먼저 '구조물 생성'을 실행하세요.");
                return Result.Failed;
            }

            var supportedRebars = selectedRebars.ToList();

            int created = 0;
            int createdStandard = 0;
            int createdFreeForm = 0;
            int failed = 0;
            int hostMissing = 0;
            var errors = new List<string>();
            var debugLog = new List<string>();
            var diameterStats = new Dictionary<int, int>();
            var failureStats = new Dictionary<int, int>();        // 직경별 실패 카운트
            var failureReasons = new Dictionary<int, string>();   // 직경별 첫 실패 사유
            var failureDetails = new List<string>();              // rebar-id별 상세 실패 기록

            var rebarsBySheet = supportedRebars
                .GroupBy(r => ExtractStructureKey(r.SheetId))
                .ToList();

            using (var tr = new Transaction(doc, "횡방향 철근 배치"))
            {
                tr.Start();

                if (new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).FirstOrDefault() == null)
                {
                    tr.RollBack();
                    TaskDialog.Show("오류", "RebarBarType이 없습니다.\n구조 템플릿에서 실행해주세요.");
                    return Result.Failed;
                }

                // 시작/끝 후크 없음 (null 전달)
                RebarHookType hookType = null;

                foreach (var sheetGroup in rebarsBySheet)
                {
                    string structureKey = sheetGroup.Key;

                    if (!hostMap.TryGetValue(structureKey, out Element hostElement))
                    {
                        hostMissing += sheetGroup.Count();
                        errors.Add($"[{structureKey}] Host 매칭 실패 → {sheetGroup.Count()}개 스킵");
                        foreach (var r in sheetGroup)
                            failureDetails.Add($"[{structureKey}] Id={r.Id} cycle={r.CycleNumber} D{(int)Math.Round(r.DiameterMm)} → Host 매칭 실패");
                        continue;
                    }

                    double depthMm = ParseDepthFromHost(hostElement);
                    if (depthMm <= 0) depthMm = 1000;

                    double ctcMm = sheetCtcMap.ContainsKey(structureKey)
                        ? sheetCtcMap[structureKey]
                        : 200;

                    var sheetRebars = sheetGroup.ToList();
                    var sheetCycle1 = sheetRebars.Where(r => r.CycleNumber == 1).ToList();
                    var sheetCycle2 = sheetRebars.Where(r => r.CycleNumber == 2).ToList();

                    // Cycle2 → Cycle1 rigid transform (중심선 기반, JSON에서 사전 계산됨)
                    Civil3DCoordinate.CenterlineTransform cycle2Tx =
                        sheetTransforms.TryGetValue(structureKey, out var t)
                            ? t : Civil3DCoordinate.CenterlineTransform.Identity;

                    // CTC 의미: 같은 cycle끼리의 간격 (c1→c1, c2→c2 = CTC)
                    //           인접 cycle 간격 (c1→c2) = CTC/2
                    // 따라서 복사 stride = CTC/2
                    double strideMm = ctcMm / 2.0;

                    // stride 배수 위치 + 끝단 보정 (남는 공간 > stride/2이면 depth 끝단에 한 단 추가)
                    var yOffsets = new List<double>();
                    int nFull = (int)Math.Floor(depthMm / strideMm);
                    for (int i = 0; i <= nFull; i++)
                        yOffsets.Add(i * strideMm);
                    double remainderMm = depthMm - nFull * strideMm;
                    if (remainderMm > strideMm / 2.0)
                        yOffsets.Add(depthMm);
                    int copies = yOffsets.Count;

                    debugLog.Add($"[{structureKey}] Cycle2Tx: {(cycle2Tx.IsIdentity ? "Identity(중심선 없음)" : $"rigid θ=atan2({cycle2Tx.Sin:F4},{cycle2Tx.Cos:F4})")}");

                    if (_verboseDebug && !_debugLogged)
                    {
                        debugLog.Add($"[DEBUG] === {structureKey} ===");
                        debugLog.Add($"[DEBUG] GlobalOrigin: ({Civil3DCoordinate.GlobalOriginXMm:F1},{Civil3DCoordinate.GlobalOriginYMm:F1}) mm");
                        debugLog.Add($"[DEBUG] depth={depthMm}mm, CTC={ctcMm}mm, stride={strideMm}mm, remainder={remainderMm:F1}mm, copies={copies}");
                    }

                    for (int copy = 0; copy < copies; copy++)
                    {
                        double yOffsetMm = yOffsets[copy];
                        double yOffsetFt = yOffsetMm * MmToFt;

                        bool isCycle1Turn = (copy % 2 == 0);
                        var currentRebars = isCycle1Turn ? sheetCycle1 : sheetCycle2;
                        if (currentRebars.Count == 0) continue;

                        // Cycle1: Identity, Cycle2: 중심선 기반 rigid transform
                        var activeTx = isCycle1Turn
                            ? Civil3DCoordinate.CenterlineTransform.Identity
                            : cycle2Tx;

                        // 단(段) 번호 = copy + 1 (1-based, 1Cycle/2Cycle 무관)
                        int dan = copy + 1;
                        // 내/외측 분류: JSON 저장 순서로 앞 절반 = 내측, 뒤 절반 = 외측
                        int halfCount = currentRebars.Count / 2;

                        // 같은 측(내측/외측) 내에서 X centroid 작은 순 → 좌→우 인덱스 부여
                        var innerList = currentRebars.Take(halfCount).ToList();
                        var outerList = currentRebars.Skip(halfCount).ToList();
                        var innerOrdered = innerList
                            .Select((r, i) => new { Rebar = r, OrigIdx = i, Cx = ComputeCentroidX(r) })
                            .OrderBy(x => x.Cx).ToList();
                        var outerOrdered = outerList
                            .Select((r, i) => new { Rebar = r, OrigIdx = i + halfCount, Cx = ComputeCentroidX(r) })
                            .OrderBy(x => x.Cx).ToList();
                        // OrigIdx → sideIndex(좌→우 1-based) 매핑
                        var sideIndexMap = new Dictionary<int, int>();
                        for (int i = 0; i < innerOrdered.Count; i++) sideIndexMap[innerOrdered[i].OrigIdx] = i + 1;
                        for (int i = 0; i < outerOrdered.Count; i++) sideIndexMap[outerOrdered[i].OrigIdx] = i + 1;

                        for (int rebarIdx = 0; rebarIdx < currentRebars.Count; rebarIdx++)
                        {
                            var rebar = currentRebars[rebarIdx];
                            bool isOuterSide = (rebarIdx >= halfCount);
                            string sideLabel = isOuterSide ? "outer" : "inner";
                            int sideIndex = sideIndexMap.TryGetValue(rebarIdx, out int si) ? si : 0;
                            {
                            int dKey0 = (int)Math.Round(rebar.DiameterMm);
                            try
                            {
                                RebarBarType barType = FindRebarBarType(doc, rebar.DiameterMm);
                                if (barType == null)
                                {
                                    failed++;
                                    failureStats[dKey0] = failureStats.ContainsKey(dKey0) ? failureStats[dKey0] + 1 : 1;
                                    if (!failureReasons.ContainsKey(dKey0))
                                        failureReasons[dKey0] = "RebarBarType 매칭 실패";
                                    failureDetails.Add($"[{structureKey}] Id={rebar.Id} copy={copy} cycle={rebar.CycleNumber} D{dKey0} → RebarBarType 매칭 실패");
                                    continue;
                                }

                                var curves = BuildCurves(rebar.Segments, yOffsetFt, activeTx);
                                if (curves == null || curves.Count == 0)
                                {
                                    failed++;
                                    failureStats[dKey0] = failureStats.ContainsKey(dKey0) ? failureStats[dKey0] + 1 : 1;
                                    if (!failureReasons.ContainsKey(dKey0))
                                        failureReasons[dKey0] = "Curve 생성 실패";
                                    failureDetails.Add($"[{structureKey}] Id={rebar.Id} copy={copy} cycle={rebar.CycleNumber} D{dKey0} → Curve 생성 실패 (segments={rebar.Segments?.Count ?? 0})");
                                    continue;
                                }

                                if (_verboseDebug && !_debugLogged)
                                {
                                    debugLog.Add($"[DEBUG] ── 첫 Rebar (copy={copy}, yOffset={yOffsetMm}mm) ──");
                                    debugLog.Add($"[DEBUG] Id: {rebar.Id}, BarType: {barType.Name}, Curves: {curves.Count}개");
                                    for (int ci = 0; ci < Math.Min(curves.Count, 3); ci++)
                                    {
                                        var c = curves[ci];
                                        var sp = c.GetEndPoint(0);
                                        var ep = c.GetEndPoint(1);
                                        debugLog.Add($"[DEBUG]   [{ci}] {c.GetType().Name} " +
                                            $"({sp.X:F2},{sp.Y:F2},{sp.Z:F2})→({ep.X:F2},{ep.Y:F2},{ep.Z:F2})");
                                    }
                                }

                                Rebar rebarElem = null;
                                string createMethod = "";
                                string standardError = null;
                                string freeformError = null;
                                RebarFreeFormValidationResult validationResult = RebarFreeFormValidationResult.Success;

                                // ── 1차: Standard 방식 시도 ──
                                try
                                {
                                    rebarElem = Rebar.CreateFromCurves(
                                        doc, RebarStyle.Standard, barType,
                                        null, null,
                                        hostElement, XYZ.BasisY,
                                        curves,
                                        RebarHookOrientation.Left,
                                        RebarHookOrientation.Left,
                                        true, true);
                                    if (rebarElem != null) createMethod = "Standard";
                                }
                                catch (Exception ex)
                                {
                                    standardError = $"{ex.GetType().Name}: {ex.Message}";
                                    rebarElem = null;
                                }

                                // ── 2차: FreeForm 폴백 ──
                                if (rebarElem == null)
                                {
                                    try
                                    {
                                        var curveSets = new List<IList<Curve>> { curves };
                                        rebarElem = Rebar.CreateFreeForm(
                                            doc, barType, hostElement,
                                            curveSets, out validationResult);
                                        if (rebarElem != null)
                                            createMethod = $"FreeForm({validationResult})";
                                        else
                                            freeformError = $"null (validation={validationResult})";
                                    }
                                    catch (Exception ex)
                                    {
                                        freeformError = $"{ex.GetType().Name}: {ex.Message}";
                                    }
                                }

                                if (rebarElem != null)
                                {
                                    // 시작/끝 후크 강제 제거 (RebarBarType/Shape 기본 후크 override)
                                    try { rebarElem.SetHookTypeId(0, ElementId.InvalidElementId); } catch { }
                                    try { rebarElem.SetHookTypeId(1, ElementId.InvalidElementId); } catch { }

                                    string cycleLabel = isCycle1Turn ? "1cycle" : "2cycle";
                                    string markText = $"{structureKey}_{dan}단_{sideLabel}_{sideIndex}";
                                    rebarElem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(markText);
                                    rebarElem.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                                        ?.Set($"{structureKey}|{dan}단|{sideLabel}#{sideIndex}|CycleNumber={rebar.CycleNumber}|{cycleLabel}|{createMethod}");

                                    created++;
                                    if (createMethod.StartsWith("Standard")) createdStandard++;
                                    else if (createMethod.StartsWith("FreeForm")) createdFreeForm++;

                                    int dKey = (int)Math.Round(rebar.DiameterMm);
                                    diameterStats[dKey] = diameterStats.ContainsKey(dKey) ? diameterStats[dKey] + 1 : 1;

                                    if (_verboseDebug && !_debugLogged)
                                    {
                                        debugLog.Add($"[DEBUG] OK Rebar Id={rebarElem.Id.Value} via {createMethod}");
                                        _debugLogged = true;
                                    }
                                }
                                else
                                {
                                    failed++;
                                    failureStats[dKey0] = failureStats.ContainsKey(dKey0) ? failureStats[dKey0] + 1 : 1;
                                    string reason = $"BarType={barType?.Name ?? "null"} | " +
                                                    $"Std: {standardError ?? "OK"} | " +
                                                    $"FF: {freeformError ?? "OK"}";
                                    if (!failureReasons.ContainsKey(dKey0))
                                        failureReasons[dKey0] = reason;
                                    failureDetails.Add($"[{structureKey}] Id={rebar.Id} copy={copy} cycle={rebar.CycleNumber} D{dKey0} → {reason}");
                                }
                            }
                            catch (Exception ex)
                            {
                                failed++;
                                failureStats[dKey0] = failureStats.ContainsKey(dKey0) ? failureStats[dKey0] + 1 : 1;
                                if (!failureReasons.ContainsKey(dKey0))
                                    failureReasons[dKey0] = $"Outer: {ex.GetType().Name}: {ex.Message}";
                                failureDetails.Add($"[{structureKey}] Id={rebar.Id} copy={copy} cycle={rebar.CycleNumber} D{dKey0} → Outer: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                            }
                            } // inner side block
                        }
                    }
                }

                try { Models.RebarColorHelper.ApplyToAll3DViews(doc); } catch { }
                tr.Commit();
            }

            // 세션 캐시에 CTC 맵 저장 → 전단철근 배치 시 재사용 (파일 재오픈 후 Revit 역파싱으로도 복원 가능)
            SessionCache.TransverseCtcMap = new System.Collections.Generic.Dictionary<string, double>(sheetCtcMap);

            string msg = "═══════════════════════════════════\n" +
                         "  횡방향 철근 배치 완료\n" +
                         "═══════════════════════════════════\n" +
                         $"── 총 배치: {created}개 | 실패: {failed}개\n" +
                         $"│  ── Standard: {createdStandard}개\n" +
                         $"│  ── FreeForm: {createdFreeForm}개\n";

            foreach (var kv in diameterStats.OrderBy(k => k.Key))
                msg += $"│  ── H{kv.Key}: {kv.Value}개\n";

            if (failureStats.Count > 0)
            {
                msg += "\n── 직경별 실패\n";
                foreach (var kv in failureStats.OrderBy(k => k.Key))
                {
                    string reason = failureReasons.ContainsKey(kv.Key) ? failureReasons[kv.Key] : "unknown";
                    msg += $"│  ── H{kv.Key}: {kv.Value}개 — {reason}\n";
                }
            }

            if (hostMissing > 0)
                msg += $"── Host 매칭 실패: {hostMissing}개\n";

            msg += $"\n── 구조도별 설정\n";
            foreach (var kv in sheetCtcMap)
                msg += $"│  ── {kv.Key}: CTC={kv.Value}mm\n";

            if (_verboseDebug && debugLog.Count > 0)
                msg += "\n═══ 디버그 ═══\n" + string.Join("\n", debugLog);

            if (errors.Count > 0)
                msg += "\n\n오류:\n" + string.Join("\n", errors.Take(20));

            string logPath = WriteFailureLog(created, createdStandard, createdFreeForm, failed, hostMissing,
                diameterStats, failureStats, failureReasons, failureDetails, errors, debugLog, sheetCtcMap);
            if (!string.IsNullOrEmpty(logPath))
                msg += $"\n\n로그: {logPath}";

            TaskDialog.Show("횡방향 철근 배치", msg);
            return Result.Succeeded;
        }

        private string WriteFailureLog(int created, int createdStandard, int createdFreeForm, int failed, int hostMissing,
            Dictionary<int, int> diameterStats, Dictionary<int, int> failureStats, Dictionary<int, string> failureReasons,
            List<string> failureDetails, List<string> errors, List<string> debugLog, Dictionary<string, double> sheetCtcMap)
        {
            try
            {
                string logDir = Path.Combine(Path.GetTempPath(), "RevitRebarModeler", "Logs");
                Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, $"TransverseRebar_{DateTime.Now:yyyyMMdd_HHmmss}.log");

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("═══════════════════════════════════");
                sb.AppendLine($"  횡방향 철근 배치 로그 — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("═══════════════════════════════════");
                sb.AppendLine($"총 배치: {created} | 실패: {failed} | Host missing: {hostMissing}");
                sb.AppendLine($"  Standard: {createdStandard} | FreeForm: {createdFreeForm}");
                sb.AppendLine();

                sb.AppendLine("── 직경별 성공 ──");
                foreach (var kv in diameterStats.OrderBy(k => k.Key))
                    sb.AppendLine($"  H{kv.Key}: {kv.Value}개");

                sb.AppendLine();
                sb.AppendLine("── 직경별 실패 요약 ──");
                foreach (var kv in failureStats.OrderBy(k => k.Key))
                {
                    string reason = failureReasons.ContainsKey(kv.Key) ? failureReasons[kv.Key] : "unknown";
                    sb.AppendLine($"  H{kv.Key}: {kv.Value}개 — {reason}");
                }

                sb.AppendLine();
                sb.AppendLine("── 구조도별 CTC 설정 ──");
                foreach (var kv in sheetCtcMap)
                    sb.AppendLine($"  {kv.Key}: CTC={kv.Value}mm");

                if (failureDetails.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"═══ 실패 상세 ({failureDetails.Count}건) ═══");
                    foreach (var line in failureDetails)
                        sb.AppendLine(line);
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
        /// 횡철근 폴리라인의 모든 Start/End 점 X 좌표 평균(centroid X) 반환.
        /// 좌→우 정렬 시 인덱스 부여 기준으로 사용.
        /// </summary>
        private double ComputeCentroidX(TransverseRebarData rebar)
        {
            if (rebar?.Segments == null || rebar.Segments.Count == 0) return 0;
            double sx = 0; int n = 0;
            foreach (var seg in rebar.Segments)
            {
                if (seg.StartPoint != null) { sx += seg.StartPoint.X; n++; }
                if (seg.EndPoint != null) { sx += seg.EndPoint.X; n++; }
            }
            return n > 0 ? sx / n : 0;
        }

        private double ParseDepthFromHost(Element hostElement)
        {
            string comments = hostElement.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                ?.AsString() ?? "";
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

        private List<Curve> BuildCurves(List<RebarSegment> segments, double yOffsetFt,
            Civil3DCoordinate.CenterlineTransform tx)
        {
            if (segments == null || segments.Count == 0) return null;
            // Civil3D에서 같은 호가 0-length Line으로 분할되어 들어오는 경우가 있어,
            // Revit Rebar 생성 전에 degenerate 제거 + 같은 원 위 인접 Arc 병합.
            var cleaned = RebarSegmentCleaner.Clean(segments);
            var curves = new List<Curve>();
            foreach (var seg in cleaned)
            {
                Curve curve = SegmentToCurve(seg, yOffsetFt, tx);
                if (curve != null) curves.Add(curve);
            }
            return curves.Count > 0 ? curves : null;
        }

        private Curve SegmentToCurve(RebarSegment seg, double yOffsetFt,
            Civil3DCoordinate.CenterlineTransform tx)
        {
            if (seg.StartPoint == null || seg.EndPoint == null) return null;

            XYZ p1 = Civil3DCoordinate.ToRevitWorld(seg.StartPoint, yOffsetFt, tx);
            XYZ p2 = Civil3DCoordinate.ToRevitWorld(seg.EndPoint, yOffsetFt, tx);

            if (p1.DistanceTo(p2) < 0.001) return null;

            if (seg.SegmentType == "Arc" && seg.MidPoint != null)
            {
                XYZ midPt = Civil3DCoordinate.ToRevitWorld(seg.MidPoint, yOffsetFt, tx);
                try { return Arc.Create(p1, p2, midPt); }
                catch { return Line.CreateBound(p1, p2); }
            }

            return Line.CreateBound(p1, p2);
        }

        private RebarBarType FindRebarBarType(Document doc, double diameterMm)
        {
            int d = (int)Math.Round(diameterMm);
            double targetFt = diameterMm * MmToFt;

            var allTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarBarType))
                .Cast<RebarBarType>()
                .ToList();

            // 1. 이름 정확 일치 (D29, 29 400S 등 여러 규칙 허용)
            string[] nameCandidates = { $"D{d}", $"{d} 400S", $"D{d} 400S", $"{d}" };
            foreach (var name in nameCandidates)
            {
                var hit = allTypes.FirstOrDefault(r => r.Name == name);
                if (hit != null) return hit;
            }

            // 2. 직경 값으로 매칭 (스터럽/타이는 후순위)
            // Revit 2024+: BarDiameter 제거 → BarModelDiameter / BarNominalDiameter 또는 Parameter로 조회
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

        private RebarHookType FindHookType(Document doc, string name = "표준 - 90도")
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(RebarHookType))
                .Cast<RebarHookType>()
                .FirstOrDefault(h => h.Name.Contains(name) || h.Name.Contains("90"));
        }
    }

}
