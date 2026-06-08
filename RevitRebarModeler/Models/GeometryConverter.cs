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

        // [미사용 제거 2026-06-08] ToCurveLoop / GetLoopPoints / ToRevitPoint 는 호출처가 없어 삭제.
        //   ※ 재추가 시 주의: 옛 ToRevitPoint 는 GlobalOrigin을 빼지 않는 절대 mm→ft 변환이었음.
        //     절대좌표가 Revit 내부원점에서 멀면 배치 오류가 나므로 Civil3DCoordinate.ToRevitWorld
        //     처럼 GlobalOrigin 보정을 반드시 포함할 것.

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
            if (verts == null || verts.Count == 0) return result;   // 빈/널 입력 방어
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
