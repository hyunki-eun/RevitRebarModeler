using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.DB;

namespace RevitRebarModeler.Models
{
    /// <summary>
    /// 세션 전역 Civil3D → Revit 좌표 변환 규약.
    /// 구조물/철근이 동일한 GlobalOrigin을 공유해 DWG 절대좌표 기준으로 배치된다.
    ///
    /// 좌표 매핑:
    ///   Civil3D X (mm) → Revit X (ft)
    ///   Civil3D Y (mm) → Revit Z (ft)
    ///   종방향 오프셋  → Revit Y (ft)
    ///
    /// GlobalOrigin은 Revit Internal Origin에서 geometry가 너무 멀리 떨어지는 것을 방지하기 위한
    /// 글로벌 오프셋 역할만 한다. 모든 좌표는 GlobalOrigin을 뺀 뒤 ft 단위로 변환된다.
    /// </summary>
    public static class Civil3DCoordinate
    {
        private const double MmToFt = 1.0 / 304.8;

        public static double GlobalOriginXMm { get; private set; }
        public static double GlobalOriginYMm { get; private set; }
        public static bool IsSet { get; private set; }

        /// <summary>커맨드 시작 시점에 호출해 세션 상태 초기화.</summary>
        public static void ResetGlobalOrigin()
        {
            GlobalOriginXMm = 0;
            GlobalOriginYMm = 0;
            IsSet = false;
        }

        /// <summary>
        /// JSON 로드 결과로부터 GlobalOrigin을 결정론적으로 계산.
        /// 1순위: 모든 사이클 BoundaryCenter의 평균
        /// 2순위: 사이클 정점 전체의 BBox 중심
        /// 3순위: 횡방향 철근 정점 전체의 BBox 중심
        /// </summary>
        public static void AutoSetGlobalOrigin(CivilExportData data)
        {
            if (data == null)
            {
                IsSet = false;
                return;
            }

            if (data.StructureRegions != null && data.StructureRegions.Count > 0)
            {
                var withCenter = data.StructureRegions
                    .Where(c => c.BoundaryCenterX != 0 || c.BoundaryCenterY != 0)
                    .ToList();

                if (withCenter.Count > 0)
                {
                    GlobalOriginXMm = withCenter.Average(c => c.BoundaryCenterX);
                    GlobalOriginYMm = withCenter.Average(c => c.BoundaryCenterY);
                    IsSet = true;
                    return;
                }

                double minX = double.MaxValue, maxX = double.MinValue;
                double minY = double.MaxValue, maxY = double.MinValue;
                bool hasVertex = false;
                foreach (var cycle in data.StructureRegions)
                    foreach (var region in cycle.Regions)
                        foreach (var v in region.Vertices)
                        {
                            minX = System.Math.Min(minX, v.X); maxX = System.Math.Max(maxX, v.X);
                            minY = System.Math.Min(minY, v.Y); maxY = System.Math.Max(maxY, v.Y);
                            hasVertex = true;
                        }

                if (hasVertex)
                {
                    GlobalOriginXMm = (minX + maxX) / 2.0;
                    GlobalOriginYMm = (minY + maxY) / 2.0;
                    IsSet = true;
                    return;
                }
            }

            if (data.TransverseRebars != null && data.TransverseRebars.Count > 0)
            {
                double minX = double.MaxValue, maxX = double.MinValue;
                double minY = double.MaxValue, maxY = double.MinValue;
                bool hasPt = false;
                foreach (var rebar in data.TransverseRebars)
                    foreach (var seg in rebar.Segments)
                    {
                        foreach (var pt in new[] { seg.StartPoint, seg.EndPoint, seg.MidPoint })
                        {
                            if (pt == null) continue;
                            minX = System.Math.Min(minX, pt.X); maxX = System.Math.Max(maxX, pt.X);
                            minY = System.Math.Min(minY, pt.Y); maxY = System.Math.Max(maxY, pt.Y);
                            hasPt = true;
                        }
                    }

                if (hasPt)
                {
                    GlobalOriginXMm = (minX + maxX) / 2.0;
                    GlobalOriginYMm = (minY + maxY) / 2.0;
                    IsSet = true;
                    return;
                }
            }

            IsSet = false;
        }

        /// <summary>
        /// DWG 절대좌표(mm)를 Revit 월드 좌표(ft)로 변환.
        /// revitYFt는 종방향 오프셋(돌출/CTC 위치).
        /// </summary>
        public static XYZ ToRevitWorld(double xMm, double yMm, double revitYFt)
        {
            return new XYZ(
                (xMm - GlobalOriginXMm) * MmToFt,
                revitYFt,
                (yMm - GlobalOriginYMm) * MmToFt);
        }

        /// <summary>RebarPoint 편의 오버로드.</summary>
        public static XYZ ToRevitWorld(RebarPoint p, double revitYFt)
        {
            return ToRevitWorld(p.X, p.Y, revitYFt);
        }

        /// <summary>
        /// 사이클 BoundaryCenter 위치에 패밀리를 배치할 좌표.
        /// 패밀리 내부 프로파일은 BoundaryCenter 기준 상대좌표로 그려져 있으므로,
        /// 패밀리 배치점 = BoundaryCenter - GlobalOrigin (Y=0 고정).
        /// </summary>
        public static XYZ FamilyPlacementPoint(double xMm, double yMm)
        {
            return new XYZ(
                (xMm - GlobalOriginXMm) * MmToFt,
                0,
                (yMm - GlobalOriginYMm) * MmToFt);
        }

        /// <summary>
        /// Cycle2 좌표계에서 Cycle1 좌표계로의 rigid 2D 변환 (회전 + 이동).
        /// 두 사이클의 중심선 start/end 점을 1:1 대응시키는 변환:
        ///   T(P) = Rotate(P − C2_start, θ) + C1_start
        ///   θ = atan2(C1_end − C1_start) − atan2(C2_end − C2_start)
        /// 중심선 정보가 없으면 Identity (변환 없음).
        /// </summary>
        public struct CenterlineTransform
        {
            public bool IsIdentity;
            public double C2StartX;
            public double C2StartY;
            public double C1StartX;
            public double C1StartY;
            public double Cos;
            public double Sin;

            public static CenterlineTransform Identity => new CenterlineTransform { IsIdentity = true, Cos = 1, Sin = 0 };

            public static CenterlineTransform FromCycleData(StructureCycleData cd)
            {
                if (cd == null || !cd.HasCenterlines)
                    return Identity;

                double v1x = cd.Cycle1CenterlineEndX - cd.Cycle1CenterlineStartX;
                double v1y = cd.Cycle1CenterlineEndY - cd.Cycle1CenterlineStartY;
                double v2x = cd.Cycle2CenterlineEndX - cd.Cycle2CenterlineStartX;
                double v2y = cd.Cycle2CenterlineEndY - cd.Cycle2CenterlineStartY;

                // 중심선 길이가 0이면 identity (분기: 퇴화 케이스 방지)
                if ((v1x * v1x + v1y * v1y) < 1e-6 || (v2x * v2x + v2y * v2y) < 1e-6)
                    return Identity;

                double theta = System.Math.Atan2(v1y, v1x) - System.Math.Atan2(v2y, v2x);

                return new CenterlineTransform
                {
                    IsIdentity = false,
                    C2StartX = cd.Cycle2CenterlineStartX,
                    C2StartY = cd.Cycle2CenterlineStartY,
                    C1StartX = cd.Cycle1CenterlineStartX,
                    C1StartY = cd.Cycle1CenterlineStartY,
                    Cos = System.Math.Cos(theta),
                    Sin = System.Math.Sin(theta)
                };
            }

            public void Apply(double xMm, double yMm, out double outXMm, out double outYMm)
            {
                if (IsIdentity)
                {
                    outXMm = xMm; outYMm = yMm;
                    return;
                }
                double dx = xMm - C2StartX;
                double dy = yMm - C2StartY;
                outXMm = dx * Cos - dy * Sin + C1StartX;
                outYMm = dx * Sin + dy * Cos + C1StartY;
            }
        }

        /// <summary>
        /// Sheet별 CenterlineTransform 사전 구축.
        /// 키: "구조도(N)" (StructureCycleData.CycleKey에서 sheet 식별자 추출)
        /// </summary>
        public static Dictionary<string, CenterlineTransform> BuildSheetTransforms(CivilExportData data)
        {
            var result = new Dictionary<string, CenterlineTransform>();
            if (data?.StructureRegions == null) return result;

            var keyRegex = new System.Text.RegularExpressions.Regex(@"구조도\((\d+)\)");

            foreach (var cd in data.StructureRegions)
            {
                if (cd == null) continue;
                var m = keyRegex.Match(cd.CycleKey ?? "");
                if (!m.Success) continue;
                string sheetKey = $"구조도({m.Groups[1].Value})";
                if (result.ContainsKey(sheetKey)) continue;
                result[sheetKey] = CenterlineTransform.FromCycleData(cd);
            }
            return result;
        }

        /// <summary>
        /// DWG 점에 CenterlineTransform을 먼저 적용한 뒤 Revit 월드로 변환.
        /// Cycle1 철근은 Identity transform을 넘기면 되고, Cycle2 철근은 해당 sheet의 transform을 넘긴다.
        /// </summary>
        public static XYZ ToRevitWorld(double xMm, double yMm, double revitYFt, CenterlineTransform tx)
        {
            tx.Apply(xMm, yMm, out double tXMm, out double tYMm);
            return ToRevitWorld(tXMm, tYMm, revitYFt);
        }

        public static XYZ ToRevitWorld(RebarPoint p, double revitYFt, CenterlineTransform tx)
        {
            return ToRevitWorld(p.X, p.Y, revitYFt, tx);
        }

        // ============================================================
        // TransverseRebarData / RebarSegment / RebarPoint 의 좌표를 통째 변환.
        // 직경별 분리 큰 거 우선 규칙에서 Cycle 2 폴리라인을 Cycle 1 frame 으로 옮길 때 사용.
        // ============================================================

        private static RebarPoint TransformPoint(RebarPoint p, CenterlineTransform tx)
        {
            if (p == null) return null;
            tx.Apply(p.X, p.Y, out double x, out double y);
            return new RebarPoint { X = x, Y = y };
        }

        public static RebarSegment TransformSegment(RebarSegment src, CenterlineTransform tx)
        {
            if (src == null) return null;
            if (tx.IsIdentity) return src;
            tx.Apply(src.CenterX, src.CenterY, out double cx, out double cy);
            return new RebarSegment
            {
                SegmentType = src.SegmentType,
                StartPoint = TransformPoint(src.StartPoint, tx),
                EndPoint = TransformPoint(src.EndPoint, tx),
                MidPoint = TransformPoint(src.MidPoint, tx),
                CenterX = cx,
                CenterY = cy,
                Radius = src.Radius
            };
        }

        public static TransverseRebarData TransformRebar(TransverseRebarData src, CenterlineTransform tx)
        {
            if (src == null) return null;
            if (tx.IsIdentity) return src;

            tx.Apply(src.CenterX, src.CenterY, out double cx, out double cy);

            var copy = new TransverseRebarData
            {
                Id = src.Id,
                SheetId = src.SheetId,
                CycleNumber = src.CycleNumber,
                DiameterMm = src.DiameterMm,
                MatchedText = src.MatchedText,
                Layer = src.Layer,
                CenterX = cx,
                CenterY = cy
            };
            // bounds: 정확히 transform 된 segments 끝점에서 재계산 (회전이 있으면 bbox가 바뀜)
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var s in src.Segments ?? new List<RebarSegment>())
            {
                var ts = TransformSegment(s, tx);
                copy.Segments.Add(ts);
                foreach (var pt in new[] { ts.StartPoint, ts.EndPoint, ts.MidPoint })
                {
                    if (pt == null) continue;
                    if (pt.X < minX) minX = pt.X;
                    if (pt.X > maxX) maxX = pt.X;
                    if (pt.Y < minY) minY = pt.Y;
                    if (pt.Y > maxY) maxY = pt.Y;
                }
            }
            if (minX < double.MaxValue)
            {
                copy.MinX = minX; copy.MinY = minY; copy.MaxX = maxX; copy.MaxY = maxY;
            }
            return copy;
        }
    }
}
