using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.DB;

namespace RevitRebarModeler.Models
{
    /// <summary>Civil3D Vertex+Bulge → Revit CurveLoop 변환</summary>
    public static class GeometryConverter
    {
        private const double MmToFeet = 1.0 / 304.8;
        private const double MinDistMm = 2.0;          // 2mm 미만 꼭짓점 병합
        private const double MinCurveLenFt = 0.001;     // ~0.3mm, Revit 최소 커브 길이
        private const int ArcSubdivisions = 8;          // Arc→Line 분할 수

        /// <summary>
        /// Civil3D 좌표 변환:
        /// Civil3D X (수평) → Revit X
        /// Civil3D Y (수직) → Revit Z
        /// 단위: mm → feet
        /// </summary>
        public static XYZ ToRevitPoint(double cadX, double cadY)
        {
            return new XYZ(cadX * MmToFeet, 0, cadY * MmToFeet);
        }

        /// <summary>닫힌 Vertex 배열 → CurveLoop (Arc→Line 분할 방식)</summary>
        public static CurveLoop ToCurveLoop(List<StructureVertex> vertices)
        {
            if (vertices == null || vertices.Count < 3)
                return null;

            // 1단계: 근접 꼭짓점 병합 (mm 단위에서 판별)
            var merged = MergeCloseVertices(vertices, MinDistMm);
            if (merged.Count < 3)
                return null;

            // 2단계: Vertex → Revit 점으로 변환 (Arc는 Line 분할)
            //   - Bulge가 있는 세그먼트는 여러 개의 중간점을 생성하여 직선 분할
            //   - 이렇게 하면 모든 커브가 Line이므로 endpoint 불일치 문제가 없음
            var allPoints = new List<XYZ>();
            int n = merged.Count;

            for (int i = 0; i < n; i++)
            {
                var v1 = merged[i];
                var v2 = merged[(i + 1) % n];

                XYZ p1 = ToRevitPoint(v1.X, v1.Y);
                XYZ p2 = ToRevitPoint(v2.X, v2.Y);

                // 시작점 추가 (중복 방지)
                if (allPoints.Count == 0 || allPoints.Last().DistanceTo(p1) > MinCurveLenFt)
                    allPoints.Add(p1);

                if (Math.Abs(v1.Bulge) > 1e-8)
                {
                    // Arc → 여러 개의 중간점 생성
                    var arcPoints = SubdivideArc(v1.X, v1.Y, v2.X, v2.Y, v1.Bulge, ArcSubdivisions);
                    // arcPoints[0]=시작점, arcPoints[last]=끝점 → 중간점만 추가
                    for (int j = 1; j < arcPoints.Count - 1; j++)
                    {
                        XYZ pt = ToRevitPoint(arcPoints[j].Item1, arcPoints[j].Item2);
                        if (allPoints.Last().DistanceTo(pt) > MinCurveLenFt)
                            allPoints.Add(pt);
                    }
                }
                // 끝점은 다음 반복의 시작점으로 추가되므로 여기서는 추가하지 않음
            }

            // 마지막 점과 첫 점이 너무 가까우면 제거
            if (allPoints.Count > 1 && allPoints.Last().DistanceTo(allPoints[0]) < MinCurveLenFt)
                allPoints.RemoveAt(allPoints.Count - 1);

            if (allPoints.Count < 3)
                return null;

            // 3단계: 점열 → Line 커브 리스트
            var curves = new List<Curve>();
            for (int i = 0; i < allPoints.Count; i++)
            {
                XYZ p1 = allPoints[i];
                XYZ p2 = allPoints[(i + 1) % allPoints.Count];

                double dist = p1.DistanceTo(p2);
                if (dist < MinCurveLenFt)
                    continue;

                curves.Add(Line.CreateBound(p1, p2));
            }

            if (curves.Count < 3)
                return null;

            // 4단계: CurveLoop 생성
            try
            {
                return CurveLoop.Create(curves);
            }
            catch
            {
                // 폴백: 수동 Append
                try
                {
                    var loop = new CurveLoop();
                    foreach (var c in curves)
                        loop.Append(c);
                    return loop;
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>CurveLoop의 점 목록을 XZ 평면 2D 점으로 반환 (TessellatedShapeBuilder 용)</summary>
        public static List<XYZ> GetLoopPoints(List<StructureVertex> vertices)
        {
            if (vertices == null || vertices.Count < 3)
                return null;

            var merged = MergeCloseVertices(vertices, MinDistMm);
            if (merged.Count < 3)
                return null;

            var allPoints = new List<XYZ>();
            int n = merged.Count;

            for (int i = 0; i < n; i++)
            {
                var v1 = merged[i];
                var v2 = merged[(i + 1) % n];

                XYZ p1 = ToRevitPoint(v1.X, v1.Y);

                if (allPoints.Count == 0 || allPoints.Last().DistanceTo(p1) > MinCurveLenFt)
                    allPoints.Add(p1);

                if (Math.Abs(v1.Bulge) > 1e-8)
                {
                    var arcPoints = SubdivideArc(v1.X, v1.Y, v2.X, v2.Y, v1.Bulge, ArcSubdivisions);
                    for (int j = 1; j < arcPoints.Count - 1; j++)
                    {
                        XYZ pt = ToRevitPoint(arcPoints[j].Item1, arcPoints[j].Item2);
                        if (allPoints.Last().DistanceTo(pt) > MinCurveLenFt)
                            allPoints.Add(pt);
                    }
                }
            }

            if (allPoints.Count > 1 && allPoints.Last().DistanceTo(allPoints[0]) < MinCurveLenFt)
                allPoints.RemoveAt(allPoints.Count - 1);

            return allPoints.Count >= 3 ? allPoints : null;
        }

        /// <summary>
        /// Bulge가 있는 세그먼트를 N개의 점으로 분할
        /// 반환: (X, Y) 점 목록 (시작점 + 중간점 + 끝점)
        /// </summary>
        public static List<Tuple<double, double>> SubdivideArc(
            double x1, double y1, double x2, double y2, double bulge, int subdivisions)
        {
            var result = new List<Tuple<double, double>>();

            // Arc 파라미터 계산 (원래 mm 좌표계에서)
            double dx = x2 - x1;
            double dy = y2 - y1;
            double chordLen = Math.Sqrt(dx * dx + dy * dy);

            if (chordLen < 1e-6)
            {
                result.Add(Tuple.Create(x1, y1));
                result.Add(Tuple.Create(x2, y2));
                return result;
            }

            // 호 각도: θ = 4 * atan(|bulge|)
            double theta = 4.0 * Math.Atan(Math.Abs(bulge));

            // 반지름: r = (chord/2) / sin(θ/2)
            double sinHalfTheta = Math.Sin(theta / 2.0);
            if (Math.Abs(sinHalfTheta) < 1e-10)
            {
                result.Add(Tuple.Create(x1, y1));
                result.Add(Tuple.Create(x2, y2));
                return result;
            }
            double radius = (chordLen / 2.0) / sinHalfTheta;

            // 호 중심 계산
            double mx = (x1 + x2) / 2.0;
            double my = (y1 + y2) / 2.0;

            // 현에 수직인 단위벡터
            double nx = -dy / chordLen;
            double ny = dx / chordLen;

            // 중심까지의 거리: d = r * cos(θ/2)
            double d = radius * Math.Cos(theta / 2.0);

            // bulge 부호에 따라 중심 방향 결정
            double sign = bulge > 0 ? 1.0 : -1.0;
            double cx = mx + sign * nx * d;
            double cy = my + sign * ny * d;

            // 시작/끝 각도
            double startAngle = Math.Atan2(y1 - cy, x1 - cx);
            double endAngle = Math.Atan2(y2 - cy, x2 - cx);

            // 각도 차이 정규화 (bulge 방향에 따라)
            double angleDiff;
            if (bulge > 0)
            {
                // 반시계 방향
                angleDiff = endAngle - startAngle;
                if (angleDiff <= 0) angleDiff += 2.0 * Math.PI;
            }
            else
            {
                // 시계 방향
                angleDiff = endAngle - startAngle;
                if (angleDiff >= 0) angleDiff -= 2.0 * Math.PI;
            }

            // 분할 수 결정 (호 각도에 비례)
            int numSeg = Math.Max(2, (int)Math.Ceiling(Math.Abs(angleDiff) / (Math.PI / 18.0))); // 10도당 1분할
            numSeg = Math.Min(numSeg, subdivisions * 2); // 상한

            // 점 생성
            for (int i = 0; i <= numSeg; i++)
            {
                double t = (double)i / numSeg;
                double angle = startAngle + t * angleDiff;
                double px = cx + radius * Math.Cos(angle);
                double py = cy + radius * Math.Sin(angle);
                result.Add(Tuple.Create(px, py));
            }

            return result;
        }

        /// <summary>거리가 minDist(mm) 미만인 연속 꼭짓점 병합</summary>
        public static List<StructureVertex> MergeCloseVertices(List<StructureVertex> verts, double minDistMm = 2.0)
        {
            var result = new List<StructureVertex>();
            result.Add(verts[0]);

            for (int i = 1; i < verts.Count; i++)
            {
                var prev = result.Last();
                var cur = verts[i];

                double dx = cur.X - prev.X;
                double dy = cur.Y - prev.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist < minDistMm)
                {
                    if (Math.Abs(cur.Bulge) > 1e-8)
                    {
                        result[result.Count - 1] = new StructureVertex
                        {
                            X = prev.X,
                            Y = prev.Y,
                            Bulge = cur.Bulge
                        };
                    }
                    continue;
                }

                result.Add(cur);
            }

            // 마지막 점과 첫 점 사이도 체크
            if (result.Count > 1)
            {
                var first = result[0];
                var last = result.Last();
                double dx = first.X - last.X;
                double dy = first.Y - last.Y;
                if (Math.Sqrt(dx * dx + dy * dy) < minDistMm)
                {
                    result.RemoveAt(result.Count - 1);
                }
            }

            return result;
        }

        /// <summary>mm 값을 feet로 변환</summary>
        public static double MmToFt(double mm) => mm * MmToFeet;
    }
}
