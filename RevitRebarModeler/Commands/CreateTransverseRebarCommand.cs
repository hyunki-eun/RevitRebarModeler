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

        private static readonly string[] RebarFamilyPaths = new[]
        {
            @"C:\ProgramData\Autodesk\RVT 2024\Libraries\Korean\구조 프리캐스트\Revit\00.rfa",
            @"C:\ProgramData\Autodesk\RVT 2025\Libraries\Korean\구조 프리캐스트\Revit\00.rfa",
            @"C:\ProgramData\Autodesk\RVT 2023\Libraries\Korean\구조 프리캐스트\Revit\00.rfa",
        };

        private bool _verboseDebug = true;
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

            var rebarsBySheet = supportedRebars
                .GroupBy(r => ExtractStructureKey(r.SheetId))
                .ToList();

            using (var tr = new Transaction(doc, "횡방향 철근 배치"))
            {
                tr.Start();

                EnsureRebarFamilyLoaded(doc, errors);

                if (new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).FirstOrDefault() == null)
                {
                    tr.RollBack();
                    TaskDialog.Show("오류", "Rebar 패밀리 로드 실패.");
                    return Result.Failed;
                }

                RebarHookType hookType = FindHookType(doc);

                foreach (var sheetGroup in rebarsBySheet)
                {
                    string structureKey = sheetGroup.Key;

                    if (!hostMap.TryGetValue(structureKey, out Element hostElement))
                    {
                        hostMissing += sheetGroup.Count();
                        errors.Add($"[{structureKey}] Host 매칭 실패 → {sheetGroup.Count()}개 스킵");
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

                    int copies = (int)Math.Floor(depthMm / ctcMm) + 1;

                    debugLog.Add($"[{structureKey}] Cycle2Tx: {(cycle2Tx.IsIdentity ? "Identity(중심선 없음)" : $"rigid θ=atan2({cycle2Tx.Sin:F4},{cycle2Tx.Cos:F4})")}");

                    if (_verboseDebug && !_debugLogged)
                    {
                        debugLog.Add($"[DEBUG] === {structureKey} ===");
                        debugLog.Add($"[DEBUG] GlobalOrigin: ({Civil3DCoordinate.GlobalOriginXMm:F1},{Civil3DCoordinate.GlobalOriginYMm:F1}) mm");
                        debugLog.Add($"[DEBUG] depth={depthMm}mm, CTC={ctcMm}mm, copies={copies}");
                    }

                    for (int copy = 0; copy < copies; copy++)
                    {
                        double yOffsetMm = copy * ctcMm;
                        if (yOffsetMm > depthMm) break;

                        double yOffsetFt = yOffsetMm * MmToFt;

                        bool isCycle1Turn = (copy % 2 == 0);
                        var currentRebars = isCycle1Turn ? sheetCycle1 : sheetCycle2;
                        if (currentRebars.Count == 0) continue;

                        // Cycle1: Identity, Cycle2: 중심선 기반 rigid transform
                        var activeTx = isCycle1Turn
                            ? Civil3DCoordinate.CenterlineTransform.Identity
                            : cycle2Tx;

                        foreach (var rebar in currentRebars)
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
                                    continue;
                                }

                                var curves = BuildCurves(rebar.Segments, yOffsetFt, activeTx);
                                if (curves == null || curves.Count == 0)
                                {
                                    failed++;
                                    failureStats[dKey0] = failureStats.ContainsKey(dKey0) ? failureStats[dKey0] + 1 : 1;
                                    if (!failureReasons.ContainsKey(dKey0))
                                        failureReasons[dKey0] = "Curve 생성 실패";
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
                                        hookType, hookType,
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
                                    string cycleLabel = isCycle1Turn ? "1cycle" : "2cycle";
                                    rebarElem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)
                                        ?.Set($"{structureKey}_{cycleLabel}");
                                    rebarElem.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                                        ?.Set($"{structureKey}|CycleNumber={rebar.CycleNumber}|{createMethod}");

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
                                    if (!failureReasons.ContainsKey(dKey0))
                                    {
                                        string reason = $"BarType={barType?.Name ?? "null"} | " +
                                                        $"Std: {standardError ?? "OK"} | " +
                                                        $"FF: {freeformError ?? "OK"}";
                                        failureReasons[dKey0] = reason;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                failed++;
                                failureStats[dKey0] = failureStats.ContainsKey(dKey0) ? failureStats[dKey0] + 1 : 1;
                                if (!failureReasons.ContainsKey(dKey0))
                                    failureReasons[dKey0] = $"Outer: {ex.GetType().Name}: {ex.Message}";
                            }
                        }
                    }
                }

                tr.Commit();
            }

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

            if (debugLog.Count > 0)
                msg += "\n═══ 디버그 ═══\n" + string.Join("\n", debugLog);

            if (errors.Count > 0)
                msg += "\n\n오류:\n" + string.Join("\n", errors.Take(20));

            TaskDialog.Show("횡방향 철근 배치", msg);
            return Result.Succeeded;
        }

        private double ParseDepthFromHost(Element hostElement)
        {
            string comments = hostElement.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                ?.AsString() ?? "";
            var match = Regex.Match(comments, @"depth=(\d+\.?\d*)");
            return match.Success ? double.Parse(match.Groups[1].Value) : 0;
        }

        private string EnsureRebarFamilyLoaded(Document doc, List<string> errors)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).FirstOrDefault();
            if (existing != null) return null;

            foreach (var path in RebarFamilyPaths)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    Family family;
                    if (doc.LoadFamily(path, new RebarFamilyLoadOptions(), out family)) return path;
                }
                catch { }
            }
            return null;
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
            var curves = new List<Curve>();
            foreach (var seg in segments)
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

    internal class RebarFamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
            out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
}
