using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using RevitRebarModeler.Models;

namespace RevitRebarModeler.Commands
{
    /// <summary>
    /// 철근 커브 미리보기 — Rebar 없이 DirectShape 선으로 좌표 검증
    /// GlobalOrigin 기준 DWG 절대좌표로 배치 (구조물과 동일 공간)
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class PreviewRebarCurvesCommand : IExternalCommand
    {
        private const double MmToFt = 1.0 / 304.8;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument.Document;

            var window = new UI.TransverseRebarWindow(doc);
            if (window.ShowDialog() != true)
                return Result.Cancelled;

            var selectedRebars = window.SelectedRebars;
            var sheetCtcMap = window.SheetCtcMap;

            // 세션 GlobalOrigin 초기화 + JSON 기반 자동 설정
            Civil3DCoordinate.ResetGlobalOrigin();
            Civil3DCoordinate.AutoSetGlobalOrigin(window.LoadedData);

            // sheet별 Cycle2→Cycle1 rigid transform (중심선 start/end 기반)
            var sheetTransforms = Civil3DCoordinate.BuildSheetTransforms(window.LoadedData);

            var supportedRebars = selectedRebars.ToList();

            var hostMap = BuildHostMap(doc);

            int created = 0;
            int failed = 0;
            var debugLog = new List<string>();

            var rebarsBySheet = supportedRebars
                .GroupBy(r => ExtractStructureKey(r.SheetId))
                .ToList();

            ElementId dsCategoryId = new ElementId(BuiltInCategory.OST_GenericModel);

            using (var tr = new Transaction(doc, "철근 커브 미리보기"))
            {
                tr.Start();

                foreach (var sheetGroup in rebarsBySheet)
                {
                    string structureKey = sheetGroup.Key;
                    var sheetRebars = sheetGroup.ToList();

                    double depthMm = 1000;
                    if (hostMap.TryGetValue(structureKey, out Element hostElement))
                    {
                        depthMm = ParseDepthFromHost(hostElement);
                        if (depthMm <= 0) depthMm = 1000;
                    }

                    double ctcMm = sheetCtcMap.ContainsKey(structureKey)
                        ? sheetCtcMap[structureKey]
                        : 200;

                    int copies = (int)Math.Floor(depthMm / ctcMm) + 1;

                    var sheetCycle1 = sheetRebars.Where(r => r.CycleNumber == 1).ToList();
                    var sheetCycle2 = sheetRebars.Where(r => r.CycleNumber == 2).ToList();

                    Civil3DCoordinate.CenterlineTransform cycle2Tx =
                        sheetTransforms.TryGetValue(structureKey, out var t)
                            ? t : Civil3DCoordinate.CenterlineTransform.Identity;

                    debugLog.Add($"[{structureKey}] GlobalOrigin=({Civil3DCoordinate.GlobalOriginXMm:F1},{Civil3DCoordinate.GlobalOriginYMm:F1})mm, " +
                        $"Cycle2Tx={(cycle2Tx.IsIdentity ? "Identity" : "rigid")}, " +
                        $"depth={depthMm}mm, CTC={ctcMm}mm, copies={copies}, " +
                        $"1C={sheetCycle1.Count}, 2C={sheetCycle2.Count}");

                    for (int copy = 0; copy < copies; copy++)
                    {
                        double yOffsetMm = copy * ctcMm;
                        if (yOffsetMm > depthMm) break;

                        double yOffsetFt = yOffsetMm * MmToFt;

                        bool isCycle1Turn = (copy % 2 == 0);
                        var currentRebars = isCycle1Turn ? sheetCycle1 : sheetCycle2;
                        if (currentRebars.Count == 0) continue;

                        var activeTx = isCycle1Turn
                            ? Civil3DCoordinate.CenterlineTransform.Identity
                            : cycle2Tx;

                        foreach (var rebar in currentRebars)
                        {
                            try
                            {
                                var curves = BuildCurves(rebar.Segments, yOffsetFt, activeTx);
                                if (curves == null || curves.Count == 0) { failed++; continue; }

                                DirectShape ds = DirectShape.CreateElement(doc, dsCategoryId);
                                ds.ApplicationId = "RevitRebarModeler";
                                ds.ApplicationDataId = $"{structureKey}_c{copy}_id{rebar.Id}";

                                string cycleLabel = isCycle1Turn ? "1cycle" : "2cycle";
                                string dsName = $"{structureKey}_{cycleLabel}_H{(int)Math.Round(rebar.DiameterMm)}_id{rebar.Id}";
                                try { ds.Name = dsName; } catch { }

                                ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                                    ?.Set($"{structureKey}|CycleNumber={rebar.CycleNumber}");

                                var geometryObjects = new List<GeometryObject>();
                                foreach (var c in curves)
                                    geometryObjects.Add(c);

                                ds.SetShape(geometryObjects);
                                created++;
                            }
                            catch (Exception ex)
                            {
                                if (failed < 5)
                                    debugLog.Add($"[ERROR] Id={rebar.Id}: {ex.Message}");
                                failed++;
                            }
                        }
                    }
                }

                tr.Commit();
            }

            string msg = $"철근 커브 미리보기\n" +
                         $"─────────────────────\n" +
                         $"총 생성: {created}개 | 실패: {failed}개\n\n" +
                         string.Join("\n", debugLog.Take(30));

            TaskDialog.Show("커브 미리보기", msg);
            return Result.Succeeded;
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
    }
}
