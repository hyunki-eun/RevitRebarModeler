using System;
using System.Collections.Generic;

namespace RevitRebarModeler.Models
{
    /// <summary>
    /// 횡철근 polyline(Line+Arc 세그먼트 혼합)을 호 길이 기준으로 샘플링한다.
    /// 종철근 배치에 사용되며, CTC 간격으로 원주 방향 좌표를 생성한다.
    /// </summary>
    public static class PolylineSampler
    {
        /// <summary>세그먼트의 호 길이 (Line=직선거리, Arc=3점 원 복원 후 sweep*R)</summary>
        public static double SegmentLength(RebarSegment seg)
        {
            if (seg?.StartPoint == null || seg.EndPoint == null) return 0;
            if (seg.SegmentType == "Arc" && seg.MidPoint != null)
                return ArcLengthFromThreePoints(seg.StartPoint, seg.MidPoint, seg.EndPoint);
            return Distance(seg.StartPoint, seg.EndPoint);
        }

        /// <summary>polyline 전체 호 길이</summary>
        public static double TotalLength(List<RebarSegment> segments)
        {
            if (segments == null) return 0;
            double sum = 0;
            foreach (var s in segments) sum += SegmentLength(s);
            return sum;
        }

        /// <summary>
        /// startOffset (mm)에서 시작해 ctc (mm) 간격으로 polyline 위 점을 샘플링.
        /// 끝단 보정: 마지막 샘플 이후 남는 거리가 ctc/2보다 크면 폴리라인 끝점에 추가.
        /// </summary>
        public static List<RebarPoint> SamplePoints(List<RebarSegment> segments, double startOffsetMm, double ctcMm)
        {
            var result = new List<RebarPoint>();
            if (segments == null || segments.Count == 0 || ctcMm <= 0) return result;

            var segLens = new List<double>(segments.Count);
            foreach (var s in segments) segLens.Add(SegmentLength(s));

            double totalLen = 0;
            foreach (var l in segLens) totalLen += l;

            if (totalLen <= 0) return result;

            var targets = new List<double>();
            double pos = Math.Max(0, startOffsetMm);
            while (pos <= totalLen + 1e-6)
            {
                targets.Add(Math.Min(pos, totalLen));
                pos += ctcMm;
            }

            double lastTarget = targets.Count > 0 ? targets[targets.Count - 1] : 0;
            double remainder = totalLen - lastTarget;
            if (targets.Count == 0 || remainder > ctcMm / 2.0)
                targets.Add(totalLen);

            foreach (var t in targets)
                result.Add(PointAtArcLength(segments, segLens, t));

            return result;
        }

        private static RebarPoint PointAtArcLength(List<RebarSegment> segments, List<double> segLens, double target)
        {
            double cumSum = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                double segLen = segLens[i];
                bool last = (i == segments.Count - 1);
                if (target <= cumSum + segLen + 1e-6 || last)
                {
                    double localLen = target - cumSum;
                    if (localLen < 0) localLen = 0;
                    if (localLen > segLen) localLen = segLen;
                    return SampleOnSegment(segments[i], localLen, segLen);
                }
                cumSum += segLen;
            }
            return new RebarPoint { X = segments[0].StartPoint.X, Y = segments[0].StartPoint.Y };
        }

        private static RebarPoint SampleOnSegment(RebarSegment seg, double localLen, double segLen)
        {
            if (segLen < 1e-9)
                return new RebarPoint { X = seg.StartPoint.X, Y = seg.StartPoint.Y };

            double t = localLen / segLen;

            if (seg.SegmentType != "Arc" || seg.MidPoint == null)
            {
                return new RebarPoint
                {
                    X = seg.StartPoint.X + (seg.EndPoint.X - seg.StartPoint.X) * t,
                    Y = seg.StartPoint.Y + (seg.EndPoint.Y - seg.StartPoint.Y) * t
                };
            }

            if (!ComputeCircle(seg.StartPoint, seg.MidPoint, seg.EndPoint, out double cx, out double cy, out double r))
            {
                return new RebarPoint
                {
                    X = seg.StartPoint.X + (seg.EndPoint.X - seg.StartPoint.X) * t,
                    Y = seg.StartPoint.Y + (seg.EndPoint.Y - seg.StartPoint.Y) * t
                };
            }

            double aS = Math.Atan2(seg.StartPoint.Y - cy, seg.StartPoint.X - cx);
            double aM = Math.Atan2(seg.MidPoint.Y - cy, seg.MidPoint.X - cx);
            double aE = Math.Atan2(seg.EndPoint.Y - cy, seg.EndPoint.X - cx);

            double toEndCcw = NormalizeAngle(aE - aS);
            double toMidCcw = NormalizeAngle(aM - aS);
            bool isCcw = toMidCcw > 1e-9 && toMidCcw < toEndCcw;

            double sweep = isCcw ? toEndCcw : -(2 * Math.PI - toEndCcw);
            double angle = aS + sweep * t;

            return new RebarPoint
            {
                X = cx + r * Math.Cos(angle),
                Y = cy + r * Math.Sin(angle)
            };
        }

        public static double ArcLengthFromThreePoints(RebarPoint s, RebarPoint m, RebarPoint e)
        {
            if (!ComputeCircle(s, m, e, out double cx, out double cy, out double r))
                return Distance(s, e);

            double aS = Math.Atan2(s.Y - cy, s.X - cx);
            double aM = Math.Atan2(m.Y - cy, m.X - cx);
            double aE = Math.Atan2(e.Y - cy, e.X - cx);

            double toEndCcw = NormalizeAngle(aE - aS);
            double toMidCcw = NormalizeAngle(aM - aS);
            bool isCcw = toMidCcw > 1e-9 && toMidCcw < toEndCcw;

            double sweep = isCcw ? toEndCcw : (2 * Math.PI - toEndCcw);
            return Math.Abs(sweep) * r;
        }

        private static bool ComputeCircle(RebarPoint a, RebarPoint b, RebarPoint c,
            out double cx, out double cy, out double r)
        {
            double ax = a.X, ay = a.Y, bx = b.X, by = b.Y, cxP = c.X, cyP = c.Y;
            double d = 2 * (ax * (by - cyP) + bx * (cyP - ay) + cxP * (ay - by));
            if (Math.Abs(d) < 1e-9)
            {
                cx = cy = r = 0;
                return false;
            }
            cx = ((ax * ax + ay * ay) * (by - cyP) + (bx * bx + by * by) * (cyP - ay) + (cxP * cxP + cyP * cyP) * (ay - by)) / d;
            cy = ((ax * ax + ay * ay) * (cxP - bx) + (bx * bx + by * by) * (ax - cxP) + (cxP * cxP + cyP * cyP) * (bx - ax)) / d;
            r = Math.Sqrt((ax - cx) * (ax - cx) + (ay - cy) * (ay - cy));
            return r > 1e-6;
        }

        private static double Distance(RebarPoint a, RebarPoint b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double NormalizeAngle(double a)
        {
            while (a < 0) a += 2 * Math.PI;
            while (a >= 2 * Math.PI) a -= 2 * Math.PI;
            return a;
        }
    }
}
