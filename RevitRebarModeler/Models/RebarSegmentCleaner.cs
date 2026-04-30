using System;
using System.Collections.Generic;

namespace RevitRebarModeler.Models
{
    /// <summary>
    /// JSON에서 들어온 RebarSegment 리스트를 Revit Rebar 생성에 적합한 형태로 정제.
    /// - 길이 0 (또는 1mm 미만) degenerate 세그먼트 제거
    /// - 같은 원 위에 있고 끝점이 연속된 인접 Arc를 하나로 병합
    ///
    /// Civil3D 추출 단계에서 같은 호가 0-length Line으로 분할되어 출력되는 경우가 있고,
    /// 그대로 Revit Rebar.CreateFromCurves에 넘기면 Standard 생성이 깨지거나
    /// FreeForm으로 폴백되며 host 바깥에 박혀 시각적으로 누락됨.
    /// </summary>
    public static class RebarSegmentCleaner
    {
        /// <summary>두 점이 사실상 같다고 볼 직선거리 임계 (mm).</summary>
        public const double PositionTolMm = 1.0;

        /// <summary>같은 반경/중심으로 볼 임계 (mm).</summary>
        public const double GeometryTolMm = 1.0;

        /// <summary>
        /// segments를 정제한 새 리스트를 반환. 입력은 변경하지 않음.
        /// </summary>
        public static List<RebarSegment> Clean(List<RebarSegment> segments)
        {
            if (segments == null || segments.Count == 0)
                return new List<RebarSegment>();

            // 1. degenerate 제거
            var filtered = new List<RebarSegment>(segments.Count);
            foreach (var s in segments)
            {
                if (s?.StartPoint == null || s.EndPoint == null) continue;
                if (Distance(s.StartPoint, s.EndPoint) < PositionTolMm) continue;
                filtered.Add(s);
            }

            // 2. 같은 원 위 인접 Arc 병합
            var merged = new List<RebarSegment>(filtered.Count);
            foreach (var seg in filtered)
            {
                if (merged.Count > 0 && CanMergeArc(merged[merged.Count - 1], seg))
                {
                    merged[merged.Count - 1] = MergeArcs(merged[merged.Count - 1], seg);
                }
                else
                {
                    merged.Add(seg);
                }
            }

            return merged;
        }

        private static bool CanMergeArc(RebarSegment a, RebarSegment b)
        {
            if (a.SegmentType != "Arc" || b.SegmentType != "Arc") return false;
            if (a.Radius <= 0 || b.Radius <= 0) return false;

            // 끝점 연속성
            if (Distance(a.EndPoint, b.StartPoint) > PositionTolMm) return false;

            // 같은 반경
            if (Math.Abs(a.Radius - b.Radius) > GeometryTolMm) return false;

            // 같은 중심
            double dcx = a.CenterX - b.CenterX;
            double dcy = a.CenterY - b.CenterY;
            if (Math.Sqrt(dcx * dcx + dcy * dcy) > GeometryTolMm) return false;

            return true;
        }

        /// <summary>
        /// 두 인접 Arc를 하나로 병합. Start=a.Start, End=b.End,
        /// MidPoint는 합쳐진 호의 각도 중앙 위치를 원에서 재계산.
        /// </summary>
        private static RebarSegment MergeArcs(RebarSegment a, RebarSegment b)
        {
            double cx = (a.CenterX + b.CenterX) / 2.0;
            double cy = (a.CenterY + b.CenterY) / 2.0;
            double r = (a.Radius + b.Radius) / 2.0;

            double angStart = Math.Atan2(a.StartPoint.Y - cy, a.StartPoint.X - cx);
            double angEnd = Math.Atan2(b.EndPoint.Y - cy, b.EndPoint.X - cx);

            // a→b 진행 방향 결정용 기준점: a.MidPoint가 angStart 기준 어느 쪽에 있는지로 판단
            double angAMid = Math.Atan2(a.MidPoint.Y - cy, a.MidPoint.X - cx);
            bool ccw = SweepCcw(angStart, angAMid, angEnd);

            double angMid = ccw
                ? NormalizeAngle(angStart + SweepLength(angStart, angEnd, true) / 2.0)
                : NormalizeAngle(angStart - SweepLength(angStart, angEnd, false) / 2.0);

            var midPt = new RebarPoint
            {
                X = cx + r * Math.Cos(angMid),
                Y = cy + r * Math.Sin(angMid)
            };

            return new RebarSegment
            {
                SegmentType = "Arc",
                StartPoint = a.StartPoint,
                EndPoint = b.EndPoint,
                MidPoint = midPt,
                CenterX = cx,
                CenterY = cy,
                Radius = r
            };
        }

        private static bool SweepCcw(double start, double mid, double end)
        {
            double ccwSweep = SweepLength(start, end, true);
            double midOffsetCcw = NormalizeAngle(mid - start);
            return midOffsetCcw <= ccwSweep + 1e-6;
        }

        private static double SweepLength(double start, double end, bool ccw)
        {
            double diff = NormalizeAngle(end - start);
            if (!ccw) diff = 2 * Math.PI - diff;
            return diff;
        }

        private static double NormalizeAngle(double a)
        {
            while (a < 0) a += 2 * Math.PI;
            while (a >= 2 * Math.PI) a -= 2 * Math.PI;
            return a;
        }

        private static double Distance(RebarPoint a, RebarPoint b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
