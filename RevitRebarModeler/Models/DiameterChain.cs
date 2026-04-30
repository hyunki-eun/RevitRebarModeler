using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitRebarModeler.Models
{
    /// <summary>
    /// 한 횡철근 폴리라인이 chain 호장 위에서 차지하는 (Start, End) 범위 + 직경 메타.
    /// </summary>
    public class DiameterPolyline
    {
        public TransverseRebarData Source;
        public double StartArcMm;
        public double EndArcMm;
        public double DiameterMm => Source != null ? Source.DiameterMm : 0;
    }

    /// <summary>
    /// "큰 직경 우선" 점유 결과의 sub-span 1개. 이 호장 구간에서 dominant 한 폴리라인 1개로 표현.
    /// </summary>
    public struct DiameterSpan
    {
        public double StartArcMm;
        public double EndArcMm;
        public DiameterPolyline Dominant;
    }

    /// <summary>
    /// inner / outer 각각에 대해 "큰 직경 우선" 규칙으로 분할된 호장 chain.
    /// 종방향 sample 위치(arcMm) → DominantAt(arcMm) 으로 그 위치를 지배하는 폴리라인·직경을 lookup.
    /// </summary>
    public class DiameterChain
    {
        public List<DiameterPolyline> All = new List<DiameterPolyline>();
        public List<DiameterSpan> Spans = new List<DiameterSpan>();
        public double TotalLengthMm;

        /// <summary>호장 좌표계 기준이 되는 통합 chain — DominantAtPoint 에서 점→호장 변환에 사용.</summary>
        public List<RebarSegment> UnifiedChain;

        public bool IsEmpty => Spans == null || Spans.Count == 0;

        /// <summary>실제 좌표 점을 chain에 투영하여 그 위치의 dominant 폴리라인을 반환.</summary>
        public DiameterPolyline DominantAtPoint(RebarPoint pt)
        {
            if (pt == null || UnifiedChain == null || UnifiedChain.Count == 0) return null;
            double arcMm = ProjectPointToArcLength(UnifiedChain, TotalLengthMm, pt);
            return DominantAt(arcMm);
        }

        /// <summary>실제 좌표 점을 chain에 투영하여 호장 위치를 반환 — 검증/디버그 로그용.</summary>
        public double ArcLengthAtPoint(RebarPoint pt)
        {
            if (pt == null || UnifiedChain == null || UnifiedChain.Count == 0) return 0;
            return ProjectPointToArcLength(UnifiedChain, TotalLengthMm, pt);
        }

        /// <summary>주어진 호장 위치를 지배하는 폴리라인. 범위 밖이면 가장 가까운 끝 span 반환.</summary>
        public DiameterPolyline DominantAt(double arcMm)
        {
            if (Spans == null || Spans.Count == 0) return null;
            if (arcMm <= Spans[0].StartArcMm) return Spans[0].Dominant;
            if (arcMm >= Spans[Spans.Count - 1].EndArcMm) return Spans[Spans.Count - 1].Dominant;

            // 이진탐색
            int lo = 0, hi = Spans.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var s = Spans[mid];
                if (arcMm < s.StartArcMm) hi = mid - 1;
                else if (arcMm > s.EndArcMm) lo = mid + 1;
                else return s.Dominant;
            }
            return Spans[Math.Max(0, Math.Min(lo, Spans.Count - 1))].Dominant;
        }

        /// <summary>
        /// Spans를 (StartMm, EndMm, Diameter, SourceId) 형식으로 한 줄씩 출력 — 검증 로그용.
        /// </summary>
        public IEnumerable<string> DescribeSpans()
        {
            foreach (var s in Spans)
            {
                string id = s.Dominant?.Source?.Id ?? "(null)";
                yield return $"[{s.StartArcMm,8:F0}-{s.EndArcMm,8:F0}] D{s.Dominant?.DiameterMm:F0} (src={id})";
            }
        }

        /// <summary>호장 가중 평균 직경 — 통계/UI 표시용.</summary>
        public double WeightedAverageDiameterMm
        {
            get
            {
                if (Spans == null || Spans.Count == 0) return 0;
                double w = 0, ws = 0;
                foreach (var s in Spans)
                {
                    double len = Math.Max(0, s.EndArcMm - s.StartArcMm);
                    if (len <= 0 || s.Dominant == null) continue;
                    ws += s.Dominant.DiameterMm * len;
                    w += len;
                }
                return w > 0 ? ws / w : 0;
            }
        }

        // ============================================================
        // 빌드
        // ============================================================

        /// <summary>
        /// 큰 직경 우선 규칙으로 polyline들을 분할.
        ///
        /// primaryPolylines: 호장 좌표계의 base 가 되는 폴리라인 묶음 (예: Cycle 1 inner).
        ///   chain 길이는 이 묶음으로만 결정 — overlay 와 정확히 같은 위치에 중첩될 경우
        ///   둘 다 합치면 ConcatenatePolylines 가 같은 위치를 두 번 traverse 하여 chain 길이가
        ///   배로 부풀려지는 문제가 있어 분리.
        ///
        /// overlayPolylines (옵션): 같은 frame에서 primary 위에 덧씌워진 폴리라인 (예: Cycle 2 inner).
        ///   chain 길이에는 영향 안 주고, 자기 끝점을 primary chain에 투영해 (Start, End) ArcMm
        ///   범위만 산출 → big-first 점유 경쟁에 참여.
        ///
        /// 절차:
        ///  1) primary 만으로 통합 chain 생성 (호장 좌표계 정의)
        ///  2) primary + overlay 의 양 끝점을 chain에 투영 → 각자 (StartArcMm, EndArcMm)
        ///  3) 직경 큰 순으로 chain 위에 점유
        ///  4) sub-span 들을 호장 오름차순 정렬해 반환
        /// </summary>
        public static DiameterChain BuildBigDiameterFirst(
            List<TransverseRebarData> primaryPolylines,
            List<TransverseRebarData> overlayPolylines = null)
        {
            var chain = new DiameterChain();
            if (primaryPolylines == null || primaryPolylines.Count == 0) return chain;

            var validPrimary = primaryPolylines.Where(p => p?.Segments != null && p.Segments.Count > 0).ToList();
            if (validPrimary.Count == 0) return chain;

            // 1) primary 만으로 통합 chain (호장 좌표계)
            var segLists = validPrimary.Select(p => p.Segments).ToList();
            var unified = LongiCurveSampler.ConcatenatePolylines(segLists);
            chain.UnifiedChain = unified;
            chain.TotalLengthMm = LongiCurveSampler.TotalLength(unified);
            if (chain.TotalLengthMm <= 0) return chain;

            // 2) primary 폴리라인의 (StartArcMm, EndArcMm)
            foreach (var p in validPrimary)
            {
                AddPolylineToChain(chain, unified, p);
            }

            // 2b) overlay 폴리라인 (있으면) — primary chain에 투영
            if (overlayPolylines != null)
            {
                foreach (var p in overlayPolylines)
                {
                    if (p?.Segments == null || p.Segments.Count == 0) continue;
                    AddPolylineToChain(chain, unified, p);
                }
            }

            // 3) 큰 직경 우선 점유
            var gaps = new List<(double s, double e)> { (0, chain.TotalLengthMm) };
            var spans = new List<DiameterSpan>();

            foreach (var dp in chain.All.OrderByDescending(x => x.DiameterMm))
            {
                var newGaps = new List<(double s, double e)>();
                foreach (var g in gaps)
                {
                    double iS = Math.Max(g.s, dp.StartArcMm);
                    double iE = Math.Min(g.e, dp.EndArcMm);
                    if (iE > iS + 1e-6)
                    {
                        spans.Add(new DiameterSpan { StartArcMm = iS, EndArcMm = iE, Dominant = dp });
                        if (g.s < iS - 1e-6) newGaps.Add((g.s, iS));
                        if (iE < g.e - 1e-6) newGaps.Add((iE, g.e));
                    }
                    else
                    {
                        newGaps.Add(g);
                    }
                }
                gaps = newGaps;
            }

            spans.Sort((a, b) => a.StartArcMm.CompareTo(b.StartArcMm));
            chain.Spans = spans;
            return chain;
        }

        private static void AddPolylineToChain(DiameterChain chain, List<RebarSegment> unified, TransverseRebarData p)
        {
            var startPt = p.Segments[0].StartPoint;
            var endPt = p.Segments[p.Segments.Count - 1].EndPoint;
            if (startPt == null || endPt == null) return;

            double aS = ProjectPointToArcLength(unified, chain.TotalLengthMm, startPt);
            double aE = ProjectPointToArcLength(unified, chain.TotalLengthMm, endPt);
            if (aS > aE) { var t = aS; aS = aE; aE = t; }

            chain.All.Add(new DiameterPolyline
            {
                Source = p,
                StartArcMm = aS,
                EndArcMm = aE
            });
        }

        /// <summary>
        /// 점 pt를 chain에 투영해 가장 가까운 위치의 호장 좌표(mm) 반환.
        /// 균일 샘플링(약 10mm 간격) 후 nearest pick — chain 길이가 매우 크지 않으면 수ms 이내.
        /// </summary>
        private static double ProjectPointToArcLength(List<RebarSegment> chain, double totalLen, RebarPoint pt)
        {
            if (chain == null || chain.Count == 0 || pt == null || totalLen <= 0) return 0;

            const double STEP_MM = 10.0;
            int N = Math.Max(50, (int)Math.Ceiling(totalLen / STEP_MM));
            double bestD2 = double.MaxValue;
            double bestArc = 0;

            for (int i = 0; i <= N; i++)
            {
                double s = totalLen * i / N;
                if (LongiCurveSampler.SamplePointAt(chain, s, out var p) && p != null)
                {
                    double dx = p.X - pt.X, dy = p.Y - pt.Y;
                    double d2 = dx * dx + dy * dy;
                    if (d2 < bestD2) { bestD2 = d2; bestArc = s; }
                }
            }
            return bestArc;
        }
    }
}
