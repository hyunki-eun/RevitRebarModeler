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
    /// 종방향 철근 미리보기 — 외곽선(내측/외측), 옵셋 arc, 샘플 포인트, 가상 선을
    /// DirectShape로 Revit 뷰에 렌더링하여 배치 위치를 사전 검증.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class PreviewLongitudinalCommand : IExternalCommand
    {
        private const double MmToFt = 1.0 / 304.8;

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

            int created = 0;
            var debugLog = new List<string>();

            ElementId dsCategory = new ElementId(BuiltInCategory.OST_Lines);

            using (var tr = new Transaction(doc, "종방향 외곽선 미리보기"))
            {
                tr.Start();

                foreach (var kvp in sheetSettings)
                {
                    string structureKey = kvp.Key;
                    var setting = kvp.Value;

                    // cycle1 polyline 수집
                    var sheetRebars = loadedData.TransverseRebars
                        .Where(r => r.CycleNumber == 1 && ExtractStructureKey(r.SheetId) == structureKey)
                        .ToList();
                    if (sheetRebars.Count == 0) continue;

                    GetBoundaryCenter(loadedData, structureKey, out double bCx, out double bCy, out bool hasBoundaryCenter);

                    // 내측/외측 분류
                    var classification = ClassifyInnerOuter(sheetRebars, bCx, bCy, hasBoundaryCenter);
                    var outerPolys = sheetRebars.Where(r => { bool v; return classification.TryGetValue(r.Id, out v) && v; }).ToList();
                    var innerPolys = sheetRebars.Where(r => { bool v; return !classification.TryGetValue(r.Id, out v) || !v; }).ToList();

                    debugLog.Add($"[{structureKey}] outer={outerPolys.Count}, inner={innerPolys.Count}");
                    if (outerPolys.Count == 0 || innerPolys.Count == 0) continue;

                    // Pos1/Pos2 선택은 실제 배치(Create)에서 사용되며,
                    // 미리보기는 단순히 내측/외측 concatenated polyline만 시각화한다.

                    // ===== 1. 모든 외측 polyline 하나로 연결 =====
                    var outerSegLists = outerPolys.Select(p => p.Segments).Where(s => s != null && s.Count > 0).ToList();
                    var concatOuter = LongiCurveSampler.ConcatenatePolylines(outerSegLists);

                    // ===== 2. 모든 내측 polyline 하나로 연결 =====
                    var innerSegLists = innerPolys.Select(p => p.Segments).Where(s => s != null && s.Count > 0).ToList();
                    var concatInner = LongiCurveSampler.ConcatenatePolylines(innerSegLists);

                    // ===== 3. 연결된 두 개의 폴리라인만 단순 외형 선으로 생성 =====
                    created += CreatePolylineDS(doc, dsCategory, concatOuter, 0,
                        $"{structureKey}_preview_concat_outer", "outer_merged");

                    created += CreatePolylineDS(doc, dsCategory, concatInner, 0,
                        $"{structureKey}_preview_concat_inner", "inner_merged");

                    // ===== 3. 연결된 타겟 + 옵셋 arc (요청에 따라 생략) =====
                    /*
                    var targetSegLists = targetPolys
                        .Select(p => p.Segments).Where(s => s != null && s.Count > 0).ToList();
                    var concatenatedTarget = LongiCurveSampler.ConcatenatePolylines(targetSegLists);
                    if (concatenatedTarget.Count == 0) continue;

                    var offsetSegs = LongiCurveSampler.OffsetPolyline(
                        concatenatedTarget, setting.OffsetMm, bCx, bCy, offsetAwayFromBC);

                    // 옵셋 arc DirectShape (노란 점선 — Line segment 체인)
                    created += CreatePolylineDS(doc, dsCategory, offsetSegs, 0,
                        $"{structureKey}_preview_offset", "offset");

                    // ===== 4. 샘플 포인트 + 가상 선 + 교차점 (요청에 따라 생략) =====
                    var samples = LongiCurveSampler.SampleFromCenterWithChordNormal(offsetSegs, setting.CtcMm, setting.Count);

                    int validCount = 0;
                    for (int si = 0; si < samples.Count; si++)
                    {
                        var (arcLen, ptOnOffset, nx, ny) = samples[si];

                        bool hitInner = LongiCurveSampler.IntersectRayWithPolyline(
                            allInnerSegs, ptOnOffset.X, ptOnOffset.Y, nx, ny, false, out var fInner);
                        bool hitOuter = LongiCurveSampler.IntersectRayWithPolyline(
                            allOuterSegs, ptOnOffset.X, ptOnOffset.Y, nx, ny, false, out var fOuter);

                        if (!hitInner || !hitOuter) continue;
                        validCount++;

                        // D/2 보정
                        {
                            double dx = ptOnOffset.X - fOuter.X, dy = ptOnOffset.Y - fOuter.Y;
                            double d = Math.Sqrt(dx * dx + dy * dy);
                            if (d > 1e-9)
                                fOuter = new RebarPoint { X = fOuter.X + dx / d * halfDiam, Y = fOuter.Y + dy / d * halfDiam };
                        }
                        {
                            double dx = ptOnOffset.X - fInner.X, dy = ptOnOffset.Y - fInner.Y;
                            double d = Math.Sqrt(dx * dx + dy * dy);
                            if (d > 1e-9)
                                fInner = new RebarPoint { X = fInner.X + dx / d * halfDiam, Y = fInner.Y + dy / d * halfDiam };
                        }

                        // 가상 선 (교차점끼리 연결)
                        try
                        {
                            XYZ pOut = Civil3DCoordinate.ToRevitWorld(fOuter.X, fOuter.Y, 0);
                            XYZ pIn = Civil3DCoordinate.ToRevitWorld(fInner.X, fInner.Y, 0);
                            if (pOut.DistanceTo(pIn) > 0.05)
                            {
                                var ds = DirectShape.CreateElement(doc, dsCategory);
                                ds.ApplicationId = "RevitRebarModeler";
                                ds.ApplicationDataId = $"{structureKey}_preview_vline_{si}";
                                try { ds.Name = $"{structureKey}_vline_{si}"; } catch { }
                                ds.SetShape(new List<GeometryObject> { Line.CreateBound(pOut, pIn) });
                                created++;
                            }
                        }
                        catch { }

                        // 교차점 마커 (외측 — 짧은 십자선)
                        CreateCrossMark(doc, dsCategory, fOuter, $"{structureKey}_fOut_{si}", 30);
                        // 교차점 마커 (내측)
                        CreateCrossMark(doc, dsCategory, fInner, $"{structureKey}_fIn_{si}", 30);
                        created += 2;
                    }

                    debugLog.Add($"  Pos1={setting.Pos1}, CTC={setting.CtcMm}, offset={setting.OffsetMm:F1}, " +
                                 $"D/2={halfDiam:F1}, 샘플={samples.Count}, 유효={validCount}");
                    */
                }

                tr.Commit();
            }

            string msg = $"종방향 외곽선 미리보기\n" +
                         $"─────────────────────\n" +
                         $"생성된 미리보기 선: {created}개\n\n" +
                         string.Join("\n", debugLog.Take(30));
            TaskDialog.Show("종방향 미리보기", msg);
            return Result.Succeeded;
        }

        // ============================================================
        // DirectShape 생성 헬퍼
        // ============================================================

        private int CreatePolylineDS(Document doc, ElementId categoryId,
            List<RebarSegment> segments, double yOffsetFt, string appDataId, string label)
        {
            if (segments == null || segments.Count == 0) return 0;

            var curves = new List<GeometryObject>();
            foreach (var seg in segments)
            {
                if (seg.StartPoint == null || seg.EndPoint == null) continue;

                XYZ p1 = Civil3DCoordinate.ToRevitWorld(seg.StartPoint.X, seg.StartPoint.Y, yOffsetFt);
                XYZ p2 = Civil3DCoordinate.ToRevitWorld(seg.EndPoint.X, seg.EndPoint.Y, yOffsetFt);
                if (p1.DistanceTo(p2) < 0.01) continue;

                if (seg.SegmentType == "Arc" && seg.MidPoint != null)
                {
                    XYZ mid = Civil3DCoordinate.ToRevitWorld(seg.MidPoint.X, seg.MidPoint.Y, yOffsetFt);
                    try { curves.Add(Arc.Create(p1, p2, mid)); continue; }
                    catch { }
                }
                curves.Add(Line.CreateBound(p1, p2));
            }

            if (curves.Count == 0) return 0;

            try
            {
                var ds = DirectShape.CreateElement(doc, categoryId); // OST_Lines 가 넘어옵니다.
                ds.ApplicationId = "RevitRebarModeler";
                ds.ApplicationDataId = appDataId;
                try { ds.Name = $"{appDataId}_{label}"; } catch { }
                ds.SetShape(curves);
                return 1;
            }
            catch { return 0; }
        }

        private void CreateCrossMark(Document doc, ElementId categoryId, RebarPoint pt, string id, double sizeMm)
        {
            try
            {
                double halfSize = sizeMm / 2.0;
                XYZ center = Civil3DCoordinate.ToRevitWorld(pt.X, pt.Y, 0);
                double hs = halfSize * MmToFt;

                var shapes = new List<GeometryObject>
                {
                    Line.CreateBound(new XYZ(center.X - hs, center.Y, center.Z), new XYZ(center.X + hs, center.Y, center.Z)),
                    Line.CreateBound(new XYZ(center.X, center.Y, center.Z - hs), new XYZ(center.X, center.Y, center.Z + hs))
                };

                var ds = DirectShape.CreateElement(doc, categoryId);
                ds.ApplicationId = "RevitRebarModeler";
                ds.ApplicationDataId = id;
                ds.SetShape(shapes);
            }
            catch { }
        }

        // ============================================================
        // 공통 헬퍼 (CreateLongitudinalRebarCommand와 동일)
        // ============================================================

        private string ExtractStructureKey(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var match = Regex.Match(text, @"구조도\(\d+\)");
            return match.Success ? match.Value : "";
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
        /// Civil3D 원본 저장 순서 기준 분류: 앞 절반 = 내측, 뒤 절반 = 외측.
        /// BC 인자는 시그니처 유지 목적으로 남겨두며 현재는 사용하지 않는다.
        /// </summary>
        private Dictionary<string, bool> ClassifyInnerOuter(
            List<TransverseRebarData> rebars, double cx, double cy, bool hasCenter)
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
