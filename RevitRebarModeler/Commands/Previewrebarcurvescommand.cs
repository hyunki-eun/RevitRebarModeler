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

                    var sheetCycle1 = sheetRebars.Where(r => r.CycleNumber == 1).ToList();
                    var sheetCycle2 = sheetRebars.Where(r => r.CycleNumber == 2).ToList();

                    Civil3DCoordinate.CenterlineTransform cycle2Tx =
                        sheetTransforms.TryGetValue(structureKey, out var t)
                            ? t : Civil3DCoordinate.CenterlineTransform.Identity;

                    debugLog.Add($"[{structureKey}] GlobalOrigin=({Civil3DCoordinate.GlobalOriginXMm:F1},{Civil3DCoordinate.GlobalOriginYMm:F1})mm, " +
                        $"Cycle2Tx={(cycle2Tx.IsIdentity ? "Identity" : "rigid")}, " +
                        $"depth={depthMm}mm, CTC={ctcMm}mm, stride={strideMm}mm, remainder={remainderMm:F1}mm, copies={copies}, " +
                        $"1C={sheetCycle1.Count}, 2C={sheetCycle2.Count}");

                    for (int copy = 0; copy < copies; copy++)
                    {
                        double yOffsetMm = yOffsets[copy];
                        double yOffsetFt = yOffsetMm * MmToFt;

                        bool isCycle1Turn = (copy % 2 == 0);
                        var currentRebars = isCycle1Turn ? sheetCycle1 : sheetCycle2;
                        if (currentRebars.Count == 0) continue;

                        var activeTx = isCycle1Turn
                            ? Civil3DCoordinate.CenterlineTransform.Identity
                            : cycle2Tx;

                        // 내측/외측 분류를 위한 BoundaryCenter 추출
                        double bCx, bCy; bool hasCenter;
                        GetBoundaryCenter(window.LoadedData, structureKey, out bCx, out bCy, out hasCenter);
                        
                        var classification = ClassifyInnerOuter(currentRebars, bCx, bCy, hasCenter);

                        var outerPolys = currentRebars.Where(r => { bool v; return classification.TryGetValue(r.Id, out v) && v; }).ToList();
                        var innerPolys = currentRebars.Where(r => { bool v; return !classification.TryGetValue(r.Id, out v) || !v; }).ToList();

                        // 외측 3가닥 → 겹침 구간을 10mm 임계거리로 완전 제거 후 병합 (단일 궤도)
                        var outerSegLists = outerPolys.Select(r => r.Segments).Where(s => s != null && s.Count > 0).ToList();

                        // 디버그: 외측 polyline별 세그먼트 덤프 (cycle1 첫 copy만)
                        if (copy == 0 && isCycle1Turn)
                        {
                            for (int pi = 0; pi < outerSegLists.Count; pi++)
                            {
                                var segs = outerSegLists[pi];
                                debugLog.Add($"[{structureKey}] OUTER poly#{pi} id={outerPolys[pi].Id} segCount={segs.Count}");
                                for (int si = 0; si < segs.Count; si++)
                                {
                                    var s = segs[si];
                                    string sp = s.StartPoint != null ? $"({s.StartPoint.X:F1},{s.StartPoint.Y:F1})" : "null";
                                    string ep = s.EndPoint != null ? $"({s.EndPoint.X:F1},{s.EndPoint.Y:F1})" : "null";
                                    string mp = s.MidPoint != null ? $"({s.MidPoint.X:F1},{s.MidPoint.Y:F1})" : "-";
                                    debugLog.Add($"  seg{si} {s.SegmentType} S{sp} E{ep} M{mp}");
                                }
                            }
                        }

                        var concatOuter = LongiCurveSampler.MergeWithoutOverlap(outerSegLists, 30.0);

                        // 내측 3가닥 → 동일 방식
                        var innerSegLists = innerPolys.Select(r => r.Segments).Where(s => s != null && s.Count > 0).ToList();
                        var concatInner = LongiCurveSampler.MergeWithoutOverlap(innerSegLists, 30.0);

                        var combinedPolys = new List<RebarSegment>();
                        combinedPolys.AddRange(concatOuter);
                        combinedPolys.AddRange(concatInner);

                        if (combinedPolys.Count == 0)
                        {
                            failed += currentRebars.Count;
                            continue;
                        }

                        try
                        {
                            var curves = BuildCurves(combinedPolys, yOffsetFt, activeTx);
                            if (curves == null || curves.Count == 0)
                            {
                                failed += currentRebars.Count;
                                continue;
                            }

                            string cycleLabel = isCycle1Turn ? "1cycle" : "2cycle";

                            // 세그먼트마다 별도 DirectShape로 생성 (어느 세그가 살았는지 육안 확인용)
                            for (int ci = 0; ci < curves.Count; ci++)
                            {
                                try
                                {
                                    DirectShape ds = DirectShape.CreateElement(doc, dsCategoryId);
                                    ds.ApplicationId = "RevitRebarModeler";
                                    ds.ApplicationDataId = $"{structureKey}_c{copy}_{cycleLabel}_seg{ci}";
                                    try { ds.Name = $"{structureKey}_{cycleLabel}_seg{ci}"; } catch { }
                                    ds.SetShape(new List<GeometryObject> { curves[ci] });
                                    created++;
                                }
                                catch (Exception exSeg)
                                {
                                    if (failed < 5)
                                        debugLog.Add($"[ERROR] seg{ci}: {exSeg.Message}");
                                    failed++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (failed < 5)
                                debugLog.Add($"[ERROR] Merged curve failed: {ex.Message}");
                            failed += currentRebars.Count;
                        }
                    }
                }

                tr.Commit();
            }

            // 전체 디버그 로그 파일 저장
            string logPath = null;
            try
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RevitRebarModeler", "Logs");
                System.IO.Directory.CreateDirectory(dir);
                logPath = System.IO.Path.Combine(dir, $"PreviewCurves_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                System.IO.File.WriteAllText(logPath, string.Join("\r\n", debugLog), System.Text.Encoding.UTF8);
            }
            catch { }

            string msg = $"철근 커브 미리보기\n" +
                         $"─────────────────────\n" +
                         $"총 생성: {created}개 | 실패: {failed}개\n\n" +
                         string.Join("\n", debugLog.Take(30)) +
                         (logPath != null ? $"\n\n전체 로그: {logPath}" : "");

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

        private void GetBoundaryCenter(CivilExportData data, string structureKey, out double cx, out double cy, out bool hasCenter)
        {
            cx = 0; cy = 0; hasCenter = false;
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
                hasCenter = true;
                return;
            }
        }

        /// <summary>
        /// 6개(혹은 N개)의 polyline을 BC 거리 순위로 절반씩 가른다.
        /// BC에 더 가까운 절반 = outer, 나머지 = inner.
        /// (기존 greedy pairing은 외측끼리 가까이 붙어있는 Lap 구성에서 오분류가 발생해서 버림)
        /// </summary>
        /// <summary>
        /// Civil3D 원본 저장 순서 기준 분류: 앞 절반 = 내측, 뒤 절반 = 외측.
        /// BC 인자는 시그니처 유지 목적으로 남겨두며 현재는 사용하지 않는다.
        /// </summary>
        private Dictionary<string, bool> ClassifyInnerOuter(List<TransverseRebarData> rebars, double cx, double cy, bool hasCenter)
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
    }
}
