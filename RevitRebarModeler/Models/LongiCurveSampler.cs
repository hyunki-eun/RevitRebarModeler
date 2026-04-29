using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitRebarModeler.Models
{
    /// <summary>
    /// 종철근 전용 curve 헬퍼:
    ///  - Polyline 옵셋, 중앙 기준 샘플링
    ///  - 중심선(직선) vs polyline 교차점 계산
    ///  - 특정 arc-length 위치의 점 + outward normal 계산
    ///  - 내측/외측 polyline으로부터 "중앙 curve" 파생
    /// </summary>
    public static class LongiCurveSampler
    {
        // ============================================================
        // Polyline 연결: 여러 polyline을 끝점 매칭으로 하나의 연속 curve로
        // ============================================================

        /// <summary>
        /// 여러 polyline을 끝점 매칭 순서대로 연결하여 하나의 연속 curve 생성.
        /// 각 polyline의 방향은 자동으로 결정됨 (필요 시 reverse).
        /// </summary>
        public static List<RebarSegment> ConcatenatePolylines(List<List<RebarSegment>> polylines, bool connectGaps = true)
        {
            if (polylines == null || polylines.Count == 0) return new List<RebarSegment>();
            if (polylines.Count == 1) return new List<RebarSegment>(polylines[0]);

            var used = new bool[polylines.Count];
            used[0] = true;

            // LinkedList로 양방향 확장
            var chain = new System.Collections.Generic.LinkedList<List<RebarSegment>>();
            chain.AddFirst(polylines[0]);

            for (int step = 1; step < polylines.Count; step++)
            {
                // 현재 체인의 시작/끝점
                var firstSegs = chain.First.Value;
                var lastSegs = chain.Last.Value;

                double csX = firstSegs[0].StartPoint.X, csY = firstSegs[0].StartPoint.Y;
                double ceX = lastSegs[lastSegs.Count - 1].EndPoint.X, ceY = lastSegs[lastSegs.Count - 1].EndPoint.Y;

                int bestIdx = -1;
                bool bestReverse = false, bestPrepend = false;
                double bestDist = double.MaxValue;

                for (int i = 0; i < polylines.Count; i++)
                {
                    if (used[i]) continue;
                    var segs = polylines[i];
                    if (segs == null || segs.Count == 0) continue;

                    double sx = segs[0].StartPoint.X, sy = segs[0].StartPoint.Y;
                    double ex = segs[segs.Count - 1].EndPoint.X, ey = segs[segs.Count - 1].EndPoint.Y;

                    // 체인 끝에 연결: chain_end → poly_start
                    double d1 = Dist(ceX, ceY, sx, sy);
                    if (d1 < bestDist) { bestDist = d1; bestIdx = i; bestReverse = false; bestPrepend = false; }

                    // 체인 끝에 역방향 연결: chain_end → poly_end (reverse)
                    double d2 = Dist(ceX, ceY, ex, ey);
                    if (d2 < bestDist) { bestDist = d2; bestIdx = i; bestReverse = true; bestPrepend = false; }

                    // 체인 앞에 연결: poly_end → chain_start
                    double d3 = Dist(ex, ey, csX, csY);
                    if (d3 < bestDist) { bestDist = d3; bestIdx = i; bestReverse = false; bestPrepend = true; }

                    // 체인 앞에 역방향 연결: poly_start(reversed end) → chain_start
                    double d4 = Dist(sx, sy, csX, csY);
                    if (d4 < bestDist) { bestDist = d4; bestIdx = i; bestReverse = true; bestPrepend = true; }
                }

                if (bestIdx < 0) break;
                used[bestIdx] = true;

                var toAdd = bestReverse ? ReverseSegments(polylines[bestIdx]) : new List<RebarSegment>(polylines[bestIdx]);

                if (bestPrepend) chain.AddFirst(toAdd);
                else chain.AddLast(toAdd);
            }

            var result = new List<RebarSegment>();
            bool firstStep = true;
            foreach (var segs in chain)
            {
                if (!firstStep && result.Count > 0 && segs.Count > 0)
                {
                    var lastSeg = result[result.Count - 1];
                    var lastPt = lastSeg.EndPoint ?? lastSeg.StartPoint;

                    // 다음 폴리라인(segs) 중에서 현재 체인 끝점과 가장 가까운 지점을 찾아 겹침(Overlapping/Lap) 구간을 잘라낸다.
                    int closestIdx = 0;
                    double minDist = double.MaxValue;
                    for (int j = 0; j < segs.Count; j++)
                    {
                        var pt = segs[j].StartPoint ?? segs[j].EndPoint;
                        if (pt != null && lastPt != null)
                        {
                            double d = Dist(lastPt.X, lastPt.Y, pt.X, pt.Y);
                            if (d < minDist)
                            {
                                minDist = d;
                                closestIdx = j;
                            }
                        }
                    }

                    // 가장 가까운 지점(closestIdx) 이전의 세그먼트들은 "이미 앞쪽 곡선과 겹쳐진 중복 구간"이므로 버린다(Trim).
                    var trimmedSegs = new List<RebarSegment>();
                    for (int j = closestIdx; j < segs.Count; j++)
                    {
                        trimmedSegs.Add(segs[j]);
                    }

                    if (trimmedSegs.Count > 0)
                    {
                        var nextSeg = trimmedSegs[0];
                        if (lastSeg.EndPoint != null && nextSeg.StartPoint != null)
                        {
                            double d = Dist(lastSeg.EndPoint.X, lastSeg.EndPoint.Y, nextSeg.StartPoint.X, nextSeg.StartPoint.Y);
                            if (connectGaps && d > 1.0) // 1mm 이상 떨어져 있는 빈 틈(Gap)인 경우만 연결 선분 추가 (옵션)
                            {
                                result.Add(new RebarSegment
                                {
                                    SegmentType = "Line",
                                    StartPoint = new RebarPoint { X = lastSeg.EndPoint.X, Y = lastSeg.EndPoint.Y },
                                    EndPoint = new RebarPoint { X = nextSeg.StartPoint.X, Y = nextSeg.StartPoint.Y }
                                });
                            }
                        }
                        result.AddRange(trimmedSegs);
                    }
                }
                else
                {
                    result.AddRange(segs);
                }
                firstStep = false;
            }
            return result;
        }

        /// <summary>
        /// 여러 polyline을 병합. 서로 맞닿은 polyline끼리 그룹으로 묶고,
        /// 각 그룹 안에서 중복 세그먼트(Start/End가 1mm 이내, 방향 반대도 동일 취급)는 한쪽만 남기고 버림.
        /// 남은 세그먼트들은 끝점 매칭 순서로 이어붙여 단일 체인 구성.
        /// </summary>
        public static List<RebarSegment> MergeWithoutOverlap(
            List<List<RebarSegment>> polylines,
            double touchThresholdMm = 1.0)
        {
            if (polylines == null || polylines.Count == 0) return new List<RebarSegment>();

            var valid = polylines.Where(p => p != null && p.Count > 0).ToList();
            if (valid.Count == 0) return new List<RebarSegment>();
            if (valid.Count == 1) return new List<RebarSegment>(valid[0]);

            // ===== 1) Union-Find 그룹핑: 한 polyline의 세그먼트 끝/중점이 다른 polyline과 touchThresholdMm 이내면 union =====
            int n = valid.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

            double touch2 = touchThresholdMm * touchThresholdMm;

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (Find(i) == Find(j)) continue;
                    if (PolylinesTouch(valid[i], valid[j], touch2)) Union(i, j);
                }
            }

            // ===== 2) 그룹별 세그먼트 수집 =====
            var groups = new Dictionary<int, List<RebarSegment>>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(i);
                if (!groups.TryGetValue(root, out var list)) { list = new List<RebarSegment>(); groups[root] = list; }
                list.AddRange(valid[i]);
            }

            // ===== 3) 각 그룹 내 부분 겹침 자르기 + 끝점 체인 정렬 → 결과에 추가 =====
            var merged = new List<RebarSegment>();
            foreach (var kv in groups)
            {
                var trimmed = TrimPartialOverlaps(kv.Value, touchThresholdMm);
                var chained = ChainByEndpoints(trimmed, touchThresholdMm);
                merged.AddRange(chained);
            }

            return merged;
        }

        /// <summary>
        /// 같은 직선/같은 원호 위에 있는 세그먼트끼리의 부분 겹침 구간을 제거.
        /// 뒤에 등장하는 세그먼트를 자른다. 완전 포함되면 해당 세그먼트 통째 삭제.
        /// </summary>
        private static List<RebarSegment> TrimPartialOverlaps(List<RebarSegment> segs, double thrMm)
        {
            var result = new List<RebarSegment>();
            foreach (var raw in segs)
            {
                if (raw?.StartPoint == null || raw.EndPoint == null) continue;
                var cur = CloneSeg(raw);

                // 기존 결과 대비 겹침 자르기
                bool dropped = false;
                for (int i = 0; i < result.Count && !dropped; i++)
                {
                    var other = result[i];
                    if (cur.SegmentType == "Line" && other.SegmentType == "Line")
                    {
                        // 교차점 케이스: 양쪽 모두 P로 수렴 (other와 cur 모두 트림)
                        if (TryIntersectLines(other, cur, thrMm,
                            out var otherNew, out var curNew, out bool dropOther, out bool dropCur))
                        {
                            if (dropOther) { result.RemoveAt(i); i--; }
                            else if (otherNew != null) result[i] = otherNew;
                            if (dropCur) { dropped = true; break; }
                            if (curNew != null) cur = curNew;
                            continue;
                        }
                        // 평행+같은 직선 위 부분 겹침 케이스
                        var res = TrimLineOverlap(other, cur, thrMm);
                        if (res.dropNew) { dropped = true; break; }
                        if (res.newSeg != null) cur = res.newSeg;
                    }
                    else if (cur.SegmentType == "Arc" && other.SegmentType == "Arc")
                    {
                        var res = TrimArcOverlap(other, cur, thrMm);
                        if (res.dropNew) { dropped = true; break; }
                        if (res.newSeg != null) cur = res.newSeg;
                    }
                    else if (cur.SegmentType == "Line" && other.SegmentType == "Arc")
                    {
                        // cur(Line)이 other(Arc) 위에 놓여있으면 (각 끝점이 원 위 + 각도 범위 안) → 트림
                        if (LineLiesOnArc(cur, other, thrMm)) { dropped = true; break; }
                    }
                    else if (cur.SegmentType == "Arc" && other.SegmentType == "Line")
                    {
                        // other(Line)이 cur(Arc) 위에 놓여있으면 other를 이미 편입한 상태 → cur에서 해당 구간 잘라야 함
                        if (LineLiesOnArc(other, cur, thrMm))
                        {
                            var res = TrimArcByLine(cur, other, thrMm);
                            if (res.dropNew) { dropped = true; break; }
                            if (res.newSeg != null) cur = res.newSeg;
                        }
                    }
                }
                if (!dropped) result.Add(cur);
            }
            return result;
        }

        /// <summary>
        /// 두 Line이 내부에서 교차(X자)하면 교차점 P 기준으로 양쪽 모두 트림.
        /// 각 선에서 P에 가까운 끝점 쪽 조각을 버리고, P에 먼 끝점 쪽을 살림.
        /// 결과: 두 선이 P에서 정확히 만나는 형태.
        /// 반환 true = 처리됨(trim 적용), false = 교차점이 내부에 없거나 평행이라 pass.
        /// </summary>
        private static bool TryIntersectLines(RebarSegment a, RebarSegment b, double thrMm,
            out RebarSegment aNew, out RebarSegment bNew, out bool dropA, out bool dropB)
        {
            aNew = bNew = null; dropA = dropB = false;

            double aDx = a.EndPoint.X - a.StartPoint.X, aDy = a.EndPoint.Y - a.StartPoint.Y;
            double aLen = Math.Sqrt(aDx * aDx + aDy * aDy);
            if (aLen < 1e-9) return false;

            double bDx = b.EndPoint.X - b.StartPoint.X, bDy = b.EndPoint.Y - b.StartPoint.Y;
            double bLen = Math.Sqrt(bDx * bDx + bDy * bDy);
            if (bLen < 1e-9) return false;

            double aUx = aDx / aLen, aUy = aDy / aLen;
            double bUx = bDx / bLen, bUy = bDy / bLen;

            double sinTheta = Math.Abs(aUx * bUy - aUy * bUx);
            if (sinTheta < 0.02) return false; // 평행

            double denom = aDx * bDy - aDy * bDx;
            if (Math.Abs(denom) < 1e-9) return false;
            double dx = b.StartPoint.X - a.StartPoint.X, dy = b.StartPoint.Y - a.StartPoint.Y;
            double t = (dx * bDy - dy * bDx) / denom;
            double s = (dx * aDy - dy * aDx) / denom;

            double tolT = thrMm / aLen;
            double tolS = thrMm / bLen;
            // 교차점이 양쪽 모두의 내부(양 끝에서 충분히 떨어짐)에 있어야 함
            if (t < tolT || t > 1 - tolT) return false;
            if (s < tolS || s > 1 - tolS) return false;

            double px = a.StartPoint.X + aDx * t, py = a.StartPoint.Y + aDy * t;
            var P = new RebarPoint { X = px, Y = py };

            // a 처리: P가 A의 시작/끝 중 어느 쪽에 가까운가 → 가까운 쪽을 버리고 먼 쪽을 살림
            // P의 a 파라미터 = t (0=Start, 1=End)
            if (t < 0.5)
            {
                // P가 A.Start 쪽에 더 가까움 → [A.Start, P]는 버리고 [P, A.End] 살림
                aNew = new RebarSegment
                {
                    SegmentType = "Line",
                    StartPoint = P,
                    EndPoint = new RebarPoint { X = a.EndPoint.X, Y = a.EndPoint.Y }
                };
            }
            else
            {
                // P가 A.End 쪽에 더 가까움 → [P, A.End]는 버리고 [A.Start, P] 살림
                aNew = new RebarSegment
                {
                    SegmentType = "Line",
                    StartPoint = new RebarPoint { X = a.StartPoint.X, Y = a.StartPoint.Y },
                    EndPoint = P
                };
            }

            // b 처리: 동일 규칙, s 파라미터로 판단
            if (s < 0.5)
            {
                bNew = new RebarSegment
                {
                    SegmentType = "Line",
                    StartPoint = new RebarPoint { X = P.X, Y = P.Y },
                    EndPoint = new RebarPoint { X = b.EndPoint.X, Y = b.EndPoint.Y }
                };
            }
            else
            {
                bNew = new RebarSegment
                {
                    SegmentType = "Line",
                    StartPoint = new RebarPoint { X = b.StartPoint.X, Y = b.StartPoint.Y },
                    EndPoint = new RebarPoint { X = P.X, Y = P.Y }
                };
            }

            return true;
        }

        private static RebarSegment CloneSeg(RebarSegment s)
        {
            return new RebarSegment
            {
                SegmentType = s.SegmentType,
                StartPoint = s.StartPoint != null ? new RebarPoint { X = s.StartPoint.X, Y = s.StartPoint.Y } : null,
                EndPoint = s.EndPoint != null ? new RebarPoint { X = s.EndPoint.X, Y = s.EndPoint.Y } : null,
                MidPoint = s.MidPoint != null ? new RebarPoint { X = s.MidPoint.X, Y = s.MidPoint.Y } : null
            };
        }

        /// <summary>
        /// 같은 직선 위에 있는 두 Line의 부분 겹침 트림 (평행 케이스 전용).
        /// 교차점 케이스는 TryIntersectLines에서 별도 처리.
        /// </summary>
        private static (RebarSegment newSeg, bool dropNew) TrimLineOverlap(RebarSegment a, RebarSegment b, double thrMm)
        {
            double aDx = a.EndPoint.X - a.StartPoint.X, aDy = a.EndPoint.Y - a.StartPoint.Y;
            double aLen = Math.Sqrt(aDx * aDx + aDy * aDy);
            if (aLen < 1e-9) return (b, false);
            double aUx = aDx / aLen, aUy = aDy / aLen;

            double bDx = b.EndPoint.X - b.StartPoint.X, bDy = b.EndPoint.Y - b.StartPoint.Y;
            double bLen = Math.Sqrt(bDx * bDx + bDy * bDy);
            if (bLen < 1e-9) return (b, false);

            // 평행 아니면 스킵 (교차점 케이스는 호출자가 먼저 처리했음)
            double sinTheta = Math.Abs(aUx * (bDy / bLen) - aUy * (bDx / bLen));
            if (sinTheta >= 0.02) return (b, false);

            // b의 양 끝점이 a의 직선 위에 있는지 (수직거리 thrMm 이내)
            double perp1 = PerpDist(a.StartPoint, aUx, aUy, b.StartPoint);
            double perp2 = PerpDist(a.StartPoint, aUx, aUy, b.EndPoint);
            if (perp1 > thrMm || perp2 > thrMm) return (b, false); // 같은 직선 위 아님

            // a의 파라미터 기준으로 b 구간을 [t1, t2]로 표현 (a의 길이 단위)
            double t0 = 0, t1 = aLen; // a의 구간
            double s1 = ((b.StartPoint.X - a.StartPoint.X) * aUx + (b.StartPoint.Y - a.StartPoint.Y) * aUy);
            double s2 = ((b.EndPoint.X - a.StartPoint.X) * aUx + (b.EndPoint.Y - a.StartPoint.Y) * aUy);
            double bLo = Math.Min(s1, s2), bHi = Math.Max(s1, s2);

            // 겹침 구간
            double oLo = Math.Max(t0, bLo), oHi = Math.Min(t1, bHi);
            if (oHi - oLo <= thrMm) return (b, false); // 실질 겹침 없음

            // b가 a 안에 완전 포함 → drop
            if (bLo >= t0 - thrMm && bHi <= t1 + thrMm) return (null, true);

            // b를 겹치지 않는 쪽으로 잘라냄
            double keepLo, keepHi;
            if (bLo < t0 - thrMm && bHi > t1 + thrMm)
            {
                // a가 b 내부에 완전 포함 → b를 두 조각으로 나눠야 하지만 일단 "앞쪽 잘린 부분"만 살림 (뒤쪽은 Civil3D lap 특성상 드묾)
                keepLo = bLo; keepHi = t0;
            }
            else if (bLo < t0 - thrMm)
            {
                // b 앞부분이 a 밖 → 그 부분만 남김
                keepLo = bLo; keepHi = t0;
            }
            else
            {
                // b 뒷부분이 a 밖 → 그 부분만 남김
                keepLo = t1; keepHi = bHi;
            }

            if (keepHi - keepLo <= thrMm) return (null, true);

            // keep 구간을 a의 좌표계에서 실제 점으로 복원. b의 원래 방향 유지 위해 s1 < s2인지 확인.
            RebarPoint pLo = new RebarPoint { X = a.StartPoint.X + aUx * keepLo, Y = a.StartPoint.Y + aUy * keepLo };
            RebarPoint pHi = new RebarPoint { X = a.StartPoint.X + aUx * keepHi, Y = a.StartPoint.Y + aUy * keepHi };
            bool bForward = (s1 <= s2); // b의 원래 방향이 a와 같은지
            var newSeg = new RebarSegment
            {
                SegmentType = "Line",
                StartPoint = bForward ? pLo : pHi,
                EndPoint = bForward ? pHi : pLo
            };
            return (newSeg, false);
        }

        private static double PerpDist(RebarPoint origin, double ux, double uy, RebarPoint p)
        {
            double dx = p.X - origin.X, dy = p.Y - origin.Y;
            double cross = ux * dy - uy * dx;
            return Math.Abs(cross);
        }

        /// <summary>두 Arc가 같은 원(center, radius) 위에 있고 각도 겹치면 b를 잘라 반환.</summary>
        private static (RebarSegment newSeg, bool dropNew) TrimArcOverlap(RebarSegment a, RebarSegment b, double thrMm)
        {
            if (!ComputeCircle(a.StartPoint, a.MidPoint, a.EndPoint, out double cxA, out double cyA, out double rA)) return (b, false);
            if (!ComputeCircle(b.StartPoint, b.MidPoint, b.EndPoint, out double cxB, out double cyB, out double rB)) return (b, false);

            // 같은 원 위인지 — center 일치 + 반지름 일치
            double dc = Math.Sqrt((cxA - cxB) * (cxA - cxB) + (cyA - cyB) * (cyA - cyB));
            if (dc > thrMm) return (b, false);
            if (Math.Abs(rA - rB) > thrMm) return (b, false);

            double cx = (cxA + cxB) / 2.0, cy = (cyA + cyB) / 2.0, r = (rA + rB) / 2.0;

            // 각 arc를 CCW 기준 [angStart, angEnd] 구간으로 표준화 (angStart: [0, 2π), sweep > 0)
            GetArcCcwRange(a, cx, cy, out double aStart, out double aSweep);
            GetArcCcwRange(b, cx, cy, out double bStart, out double bSweep);

            // 각도 겹침 구간 계산 (원환에서 교집합은 최대 2개 조각)
            var overlap = AngleOverlap(aStart, aSweep, bStart, bSweep);
            if (overlap.Count == 0) return (b, false);

            double totalOverlap = 0;
            foreach (var seg in overlap) totalOverlap += seg.sweep;
            double angTol = thrMm / Math.Max(r, 1.0);
            if (totalOverlap <= angTol) return (b, false);

            // b가 a에 완전 포함?
            if (totalOverlap >= bSweep - angTol) return (null, true);

            // b에서 겹침을 제외한 남은 구간 찾기 (최대 2개). 첫 번째만 취함 (간단화).
            var remain = AngleSubtract(bStart, bSweep, overlap);
            if (remain.Count == 0) return (null, true);

            var pick = remain[0];
            if (pick.sweep <= angTol) return (null, true);

            // 원래 b 방향 (CW or CCW) 복원
            bool bWasCcw = IsArcCcw(b, cx, cy);
            double ps = pick.start, pe = pick.start + pick.sweep;
            double startAng = bWasCcw ? ps : pe;
            double endAng = bWasCcw ? pe : ps;
            double midAng = (ps + pe) / 2.0;

            var newSeg = new RebarSegment
            {
                SegmentType = "Arc",
                StartPoint = new RebarPoint { X = cx + r * Math.Cos(startAng), Y = cy + r * Math.Sin(startAng) },
                EndPoint = new RebarPoint { X = cx + r * Math.Cos(endAng), Y = cy + r * Math.Sin(endAng) },
                MidPoint = new RebarPoint { X = cx + r * Math.Cos(midAng), Y = cy + r * Math.Sin(midAng) }
            };
            return (newSeg, false);
        }

        /// <summary>
        /// Line의 양 끝점이 Arc의 원(center, radius) 위에 있고 Arc의 각도 범위 안에 들어가는지.
        /// Civil3D가 Arc의 일부를 Line으로 근사해서 lap 세그먼트로 내보낸 경우에 해당.
        /// </summary>
        private static bool LineLiesOnArc(RebarSegment line, RebarSegment arc, double thrMm)
        {
            if (line?.StartPoint == null || line.EndPoint == null) return false;
            if (arc?.StartPoint == null || arc.MidPoint == null || arc.EndPoint == null) return false;
            if (!ComputeCircle(arc.StartPoint, arc.MidPoint, arc.EndPoint, out double cx, out double cy, out double r)) return false;

            double d1 = Math.Sqrt((line.StartPoint.X - cx) * (line.StartPoint.X - cx) + (line.StartPoint.Y - cy) * (line.StartPoint.Y - cy));
            double d2 = Math.Sqrt((line.EndPoint.X - cx) * (line.EndPoint.X - cx) + (line.EndPoint.Y - cy) * (line.EndPoint.Y - cy));
            if (Math.Abs(d1 - r) > thrMm) return false;
            if (Math.Abs(d2 - r) > thrMm) return false;

            // 각도 범위 포함 판정
            GetArcCcwRange(arc, cx, cy, out double aStart, out double aSweep);
            double angS = NormalizeAngle(Math.Atan2(line.StartPoint.Y - cy, line.StartPoint.X - cx));
            double angE = NormalizeAngle(Math.Atan2(line.EndPoint.Y - cy, line.EndPoint.X - cx));
            double angTol = thrMm / Math.Max(r, 1.0);
            return AngleInRange(angS, aStart, aSweep, angTol) && AngleInRange(angE, aStart, aSweep, angTol);
        }

        private static bool AngleInRange(double ang, double start, double sweep, double tol)
        {
            double a = NormalizeAngle(ang - start);
            return a <= sweep + tol || a >= 2 * Math.PI - tol;
        }

        /// <summary>Arc에서 Line(원 위에 놓여있는)과 겹치는 각도 구간을 잘라냄.</summary>
        private static (RebarSegment newSeg, bool dropNew) TrimArcByLine(RebarSegment arc, RebarSegment line, double thrMm)
        {
            if (!ComputeCircle(arc.StartPoint, arc.MidPoint, arc.EndPoint, out double cx, out double cy, out double r))
                return (arc, false);

            // Line이 걸친 각도 구간 (CCW, line 방향 무관)
            double lS = NormalizeAngle(Math.Atan2(line.StartPoint.Y - cy, line.StartPoint.X - cx));
            double lE = NormalizeAngle(Math.Atan2(line.EndPoint.Y - cy, line.EndPoint.X - cx));
            // 두 각 사이 "작은 호"를 overlap으로 간주
            double sweepCcw = NormalizeAngle(lE - lS);
            double sweepCw = 2 * Math.PI - sweepCcw;
            double oStart, oSweep;
            if (sweepCcw <= sweepCw) { oStart = lS; oSweep = sweepCcw; }
            else { oStart = lE; oSweep = sweepCw; }

            GetArcCcwRange(arc, cx, cy, out double aStart, out double aSweep);

            var overlap = AngleOverlap(aStart, aSweep, oStart, oSweep);
            if (overlap.Count == 0) return (arc, false);
            double totalOverlap = 0;
            foreach (var s in overlap) totalOverlap += s.sweep;

            double angTol = thrMm / Math.Max(r, 1.0);
            if (totalOverlap <= angTol) return (arc, false);
            if (totalOverlap >= aSweep - angTol) return (null, true);

            var remain = AngleSubtract(aStart, aSweep, overlap);
            if (remain.Count == 0) return (null, true);

            var pick = remain[0];
            if (pick.sweep <= angTol) return (null, true);

            bool ccw = IsArcCcw(arc, cx, cy);
            double ps = pick.start, pe = pick.start + pick.sweep;
            double startAng = ccw ? ps : pe;
            double endAng = ccw ? pe : ps;
            double midAng = (ps + pe) / 2.0;

            var newSeg = new RebarSegment
            {
                SegmentType = "Arc",
                StartPoint = new RebarPoint { X = cx + r * Math.Cos(startAng), Y = cy + r * Math.Sin(startAng) },
                EndPoint = new RebarPoint { X = cx + r * Math.Cos(endAng), Y = cy + r * Math.Sin(endAng) },
                MidPoint = new RebarPoint { X = cx + r * Math.Cos(midAng), Y = cy + r * Math.Sin(midAng) }
            };
            return (newSeg, false);
        }

        private static bool IsArcCcw(RebarSegment seg, double cx, double cy)
        {
            double aS = Math.Atan2(seg.StartPoint.Y - cy, seg.StartPoint.X - cx);
            double aM = Math.Atan2(seg.MidPoint.Y - cy, seg.MidPoint.X - cx);
            double aE = Math.Atan2(seg.EndPoint.Y - cy, seg.EndPoint.X - cx);
            double toEndCcw = NormalizeAngle(aE - aS);
            double toMidCcw = NormalizeAngle(aM - aS);
            return toMidCcw > 1e-9 && toMidCcw < toEndCcw;
        }

        private static void GetArcCcwRange(RebarSegment seg, double cx, double cy, out double angStart, out double sweepCcw)
        {
            double aS = Math.Atan2(seg.StartPoint.Y - cy, seg.StartPoint.X - cx);
            double aE = Math.Atan2(seg.EndPoint.Y - cy, seg.EndPoint.X - cx);
            bool ccw = IsArcCcw(seg, cx, cy);
            if (ccw) { angStart = NormalizeAngle(aS); sweepCcw = NormalizeAngle(aE - aS); }
            else { angStart = NormalizeAngle(aE); sweepCcw = NormalizeAngle(aS - aE); }
            if (sweepCcw < 1e-9) sweepCcw = 2 * Math.PI; // 전체 원
        }

        /// <summary>두 각도 구간(시작, CCW sweep)의 교집합. [0, 2π) 원환 기준.</summary>
        private static List<(double start, double sweep)> AngleOverlap(double s1, double sw1, double s2, double sw2)
        {
            var a = new List<(double, double)> { (NormalizeAngle(s1), sw1) };
            var b = new List<(double, double)> { (NormalizeAngle(s2), sw2) };
            // 2π 걸치는 경우 쪼갬
            a = SplitAt2Pi(a);
            b = SplitAt2Pi(b);

            var result = new List<(double start, double sweep)>();
            foreach (var ai in a)
                foreach (var bi in b)
                {
                    double lo = Math.Max(ai.Item1, bi.Item1);
                    double hi = Math.Min(ai.Item1 + ai.Item2, bi.Item1 + bi.Item2);
                    if (hi - lo > 1e-9) result.Add((lo, hi - lo));
                }
            return result;
        }

        private static List<(double, double)> SplitAt2Pi(List<(double start, double sweep)> segs)
        {
            var r = new List<(double, double)>();
            foreach (var s in segs)
            {
                double e = s.start + s.sweep;
                if (e <= 2 * Math.PI + 1e-9) r.Add((s.start, s.sweep));
                else
                {
                    r.Add((s.start, 2 * Math.PI - s.start));
                    r.Add((0, e - 2 * Math.PI));
                }
            }
            return r;
        }

        /// <summary>구간 [start, start+sweep]에서 exclude 구간들을 뺀 나머지.</summary>
        private static List<(double start, double sweep)> AngleSubtract(
            double start, double sweep, List<(double start, double sweep)> excludes)
        {
            var pieces = SplitAt2Pi(new List<(double, double)> { (NormalizeAngle(start), sweep) });
            foreach (var ex in excludes)
            {
                var exParts = SplitAt2Pi(new List<(double, double)> { (NormalizeAngle(ex.start), ex.sweep) });
                foreach (var exPart in exParts)
                {
                    var next = new List<(double, double)>();
                    foreach (var p in pieces)
                    {
                        double pLo = p.Item1, pHi = p.Item1 + p.Item2;
                        double eLo = exPart.Item1, eHi = exPart.Item1 + exPart.Item2;
                        // exclude가 p 바깥이면 그대로
                        if (eHi <= pLo + 1e-9 || eLo >= pHi - 1e-9) { next.Add(p); continue; }
                        // 왼쪽 잔여
                        if (pLo < eLo - 1e-9) next.Add((pLo, eLo - pLo));
                        // 오른쪽 잔여
                        if (eHi < pHi - 1e-9) next.Add((eHi, pHi - eHi));
                    }
                    pieces = next;
                }
            }
            return pieces.Select(p => (p.Item1, p.Item2)).ToList();
        }

        /// <summary>두 polyline이 어느 한 점에서라도 touch2 이내로 맞닿아 있는지 판정.</summary>
        private static bool PolylinesTouch(List<RebarSegment> a, List<RebarSegment> b, double touch2)
        {
            // a의 주요 점(Start/End/Mid)을 b의 세그먼트에서 가장 가까운 점과 비교
            foreach (var segA in a)
            {
                if (segA == null) continue;
                if (PointNearPolyline(segA.StartPoint, b, touch2)) return true;
                if (PointNearPolyline(segA.EndPoint, b, touch2)) return true;
                if (PointNearPolyline(segA.MidPoint, b, touch2)) return true;
            }
            // 반대도 체크 (대칭)
            foreach (var segB in b)
            {
                if (segB == null) continue;
                if (PointNearPolyline(segB.StartPoint, a, touch2)) return true;
                if (PointNearPolyline(segB.EndPoint, a, touch2)) return true;
                if (PointNearPolyline(segB.MidPoint, a, touch2)) return true;
            }
            return false;
        }

        private static bool PointNearPolyline(RebarPoint p, List<RebarSegment> poly, double touch2)
        {
            if (p == null) return false;
            var q = NearestPointOnPolyline(poly, p);
            if (q == null) return false;
            double dx = q.X - p.X, dy = q.Y - p.Y;
            return dx * dx + dy * dy <= touch2;
        }

        /// <summary>끝점 매칭으로 세그먼트들을 연속 체인으로 정렬. 방향 필요 시 reverse.</summary>
        private static List<RebarSegment> ChainByEndpoints(List<RebarSegment> segs, double thrMm)
        {
            if (segs == null || segs.Count == 0) return new List<RebarSegment>();
            var remaining = new List<RebarSegment>(segs);
            var chain = new List<RebarSegment>();
            double thr2 = thrMm * thrMm;

            chain.Add(remaining[0]);
            remaining.RemoveAt(0);

            while (remaining.Count > 0)
            {
                var tail = chain[chain.Count - 1].EndPoint;
                var head = chain[0].StartPoint;

                int bestIdx = -1;
                bool appendToTail = true;
                bool reverseIt = false;
                double bestDist = double.MaxValue;

                for (int i = 0; i < remaining.Count; i++)
                {
                    var s = remaining[i];
                    // tail ↔ s.Start
                    double d1 = Sq(tail, s.StartPoint);
                    if (d1 < bestDist) { bestDist = d1; bestIdx = i; appendToTail = true; reverseIt = false; }
                    // tail ↔ s.End (reverse)
                    double d2 = Sq(tail, s.EndPoint);
                    if (d2 < bestDist) { bestDist = d2; bestIdx = i; appendToTail = true; reverseIt = true; }
                    // head ↔ s.End
                    double d3 = Sq(head, s.EndPoint);
                    if (d3 < bestDist) { bestDist = d3; bestIdx = i; appendToTail = false; reverseIt = false; }
                    // head ↔ s.Start (reverse)
                    double d4 = Sq(head, s.StartPoint);
                    if (d4 < bestDist) { bestDist = d4; bestIdx = i; appendToTail = false; reverseIt = true; }
                }

                if (bestIdx < 0) break;
                var pick = remaining[bestIdx];
                remaining.RemoveAt(bestIdx);
                if (reverseIt) pick = ReverseSegment(pick);
                if (appendToTail) chain.Add(pick);
                else chain.Insert(0, pick);
            }

            return chain;
        }

        private static double Sq(RebarPoint a, RebarPoint b)
        {
            if (a == null || b == null) return double.MaxValue;
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        private static RebarSegment ReverseSegment(RebarSegment s)
        {
            return new RebarSegment
            {
                SegmentType = s.SegmentType,
                StartPoint = s.EndPoint != null ? new RebarPoint { X = s.EndPoint.X, Y = s.EndPoint.Y } : null,
                EndPoint = s.StartPoint != null ? new RebarPoint { X = s.StartPoint.X, Y = s.StartPoint.Y } : null,
                MidPoint = s.MidPoint != null ? new RebarPoint { X = s.MidPoint.X, Y = s.MidPoint.Y } : null
            };
        }

        private static double CentroidX(List<RebarSegment> segs)
        {
            double sx = 0; int n = 0;
            foreach (var s in segs)
            {
                if (s.StartPoint != null) { sx += s.StartPoint.X; n++; }
                if (s.EndPoint != null) { sx += s.EndPoint.X; n++; }
            }
            return n == 0 ? 0 : sx / n;
        }

        /// <summary>
        /// Polyline segments의 순서와 방향을 반전.
        /// </summary>
        public static List<RebarSegment> ReverseSegments(List<RebarSegment> segments)
        {
            var result = new List<RebarSegment>();
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                var seg = segments[i];
                result.Add(new RebarSegment
                {
                    SegmentType = seg.SegmentType,
                    StartPoint = seg.EndPoint != null ? new RebarPoint { X = seg.EndPoint.X, Y = seg.EndPoint.Y } : null,
                    EndPoint = seg.StartPoint != null ? new RebarPoint { X = seg.StartPoint.X, Y = seg.StartPoint.Y } : null,
                    MidPoint = seg.MidPoint != null ? new RebarPoint { X = seg.MidPoint.X, Y = seg.MidPoint.Y } : null
                });
            }
            return result;
        }

        private static double Dist(double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1, dy = y2 - y1;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // ============================================================
        // Step 1: Polyline 옵셋
        // ============================================================

        /// <summary>
        /// Polyline을 offsetMm만큼 옵셋하여 새 polyline(Line segment 체인) 생성.
        /// offsetAwayFromBC=true → BC에서 멀어지는 방향 (outer→콘크리트 안쪽)
        /// offsetAwayFromBC=false → BC 쪽으로 (inner→콘크리트 안쪽)
        /// </summary>
        public static List<RebarSegment> OffsetPolyline(
            List<RebarSegment> segments, double offsetMm,
            double bcX, double bcY, bool offsetAwayFromBC)
        {
            if (segments == null || segments.Count == 0) return new List<RebarSegment>();
            if (Math.Abs(offsetMm) < 1e-9) return segments;

            double totalLen = TotalLength(segments);
            if (totalLen <= 0) return segments;

            // 촘촘하게 샘플링 (최소 200포인트 or 5mm 간격)
            double step = Math.Min(5.0, totalLen / 200);
            if (step < 1) step = 1;

            var srcPts = new List<RebarPoint>();
            for (double d = 0; d <= totalLen + 1e-6; d += step)
            {
                if (SamplePointAt(segments, Math.Min(d, totalLen), out var pt))
                    srcPts.Add(pt);
            }
            if (srcPts.Count < 2) return segments;

            // 각 점에서 chord perpendicular → BC 방향 기준으로 일관된 법선 → 옵셋
            var offsetPts = new List<RebarPoint>();
            for (int i = 0; i < srcPts.Count; i++)
            {
                var pA = i > 0 ? srcPts[i - 1] : null;
                var pB = i < srcPts.Count - 1 ? srcPts[i + 1] : null;

                double chordX, chordY;
                if (pA != null && pB != null) { chordX = pB.X - pA.X; chordY = pB.Y - pA.Y; }
                else if (pB != null) { chordX = pB.X - srcPts[i].X; chordY = pB.Y - srcPts[i].Y; }
                else { chordX = srcPts[i].X - pA.X; chordY = srcPts[i].Y - pA.Y; }

                double len = Math.Sqrt(chordX * chordX + chordY * chordY);
                if (len < 1e-9) { offsetPts.Add(new RebarPoint { X = srcPts[i].X, Y = srcPts[i].Y }); continue; }

                double nx = -chordY / len, ny = chordX / len;

                // 법선이 BC 쪽을 향하도록 정규화
                double toBcX = bcX - srcPts[i].X;
                double toBcY = bcY - srcPts[i].Y;
                if (nx * toBcX + ny * toBcY < 0) { nx = -nx; ny = -ny; }

                // offsetAwayFromBC이면 BC 반대 방향
                double dir = offsetAwayFromBC ? -1.0 : 1.0;

                offsetPts.Add(new RebarPoint
                {
                    X = srcPts[i].X + nx * offsetMm * dir,
                    Y = srcPts[i].Y + ny * offsetMm * dir
                });
            }

            // Line segment 체인으로 변환
            var result = new List<RebarSegment>();
            for (int i = 0; i < offsetPts.Count - 1; i++)
            {
                result.Add(new RebarSegment
                {
                    SegmentType = "Line",
                    StartPoint = new RebarPoint { X = offsetPts[i].X, Y = offsetPts[i].Y },
                    EndPoint = new RebarPoint { X = offsetPts[i + 1].X, Y = offsetPts[i + 1].Y }
                });
            }
            return result;
        }

        // ============================================================
        // Step 2-3: 중앙 기준 양방향 CTC 샘플링 + chord perpendicular 법선
        // ============================================================

        /// <summary>
        /// 임의 anchor arc length를 중심으로 양방향 CTC 등분 + chord perpendicular 법선 추출.
        /// SampleFromCenterWithChordNormal의 anchor 버전 — 구조도 중심선이 base curve와 만나는 위치를 anchor로 사용.
        /// anchor가 [0, totalLen] 범위 밖이면 totalLen/2로 fallback.
        /// </summary>
        public static List<(double arcLen, RebarPoint point, double nx, double ny)> SampleFromAnchorWithChordNormal(
            List<RebarSegment> segments, double ctcMm, int totalRebarCount, double anchorArcLen)
        {
            var results = new List<(double, RebarPoint, double, double)>();
            if (segments == null || segments.Count == 0 || ctcMm <= 0 || totalRebarCount <= 0) return results;

            double totalLen = TotalLength(segments);
            if (totalLen <= 0) return results;

            double center = anchorArcLen;
            if (center < 0 || center > totalLen) center = totalLen / 2.0;

            int sets = totalRebarCount / 2;
            if (sets <= 0) return results;

            var arcLens = new List<double>();
            bool hasCenterPoint = (sets % 2 != 0);

            if (hasCenterPoint)
            {
                if (center >= -1e-6 && center <= totalLen + 1e-6) arcLens.Add(center);
                int kMax = (sets - 1) / 2;
                for (int k = 1; k <= kMax; k++)
                {
                    double rPos = center + k * ctcMm;
                    double lPos = center - k * ctcMm;
                    if (rPos <= totalLen + 1e-4) arcLens.Add(rPos);
                    if (lPos >= -1e-4) arcLens.Add(lPos);
                }
            }
            else
            {
                int kMax = sets / 2;
                for (int k = 1; k <= kMax; k++)
                {
                    double offset = (k - 0.5) * ctcMm;
                    double rPos = center + offset;
                    double lPos = center - offset;
                    if (rPos <= totalLen + 1e-4) arcLens.Add(rPos);
                    if (lPos >= -1e-4) arcLens.Add(lPos);
                }
            }
            arcLens.Sort();

            var points = new List<RebarPoint>();
            foreach (var al in arcLens)
            {
                SamplePointAt(segments, al, out var pt);
                points.Add(pt);
            }

            for (int i = 0; i < points.Count; i++)
            {
                if (points[i] == null) continue;
                RebarPoint pA = (i > 0) ? points[i - 1] : null;
                RebarPoint pB = (i < points.Count - 1) ? points[i + 1] : null;

                double chordX, chordY;
                if (pA != null && pB != null) { chordX = pB.X - pA.X; chordY = pB.Y - pA.Y; }
                else if (pB != null) { chordX = pB.X - points[i].X; chordY = pB.Y - points[i].Y; }
                else if (pA != null) { chordX = points[i].X - pA.X; chordY = points[i].Y - pA.Y; }
                else continue;

                double len = Math.Sqrt(chordX * chordX + chordY * chordY);
                if (len < 1e-9) continue;

                double nx = -chordY / len;
                double ny = chordX / len;
                results.Add((arcLens[i], points[i], nx, ny));
            }
            return results;
        }

        /// <summary>
        /// arc 중앙에서 양방향으로 CTC 등분하여 점 + chord perpendicular 법선 추출.
        /// </summary>
        public static List<(double arcLen, RebarPoint point, double nx, double ny)> SampleFromCenterWithChordNormal(
            List<RebarSegment> segments, double ctcMm, int totalRebarCount)
        {
            var results = new List<(double, RebarPoint, double, double)>();
            if (segments == null || segments.Count == 0 || ctcMm <= 0 || totalRebarCount <= 0) return results;

            double totalLen = TotalLength(segments);
            if (totalLen <= 0) return results;

            double center = totalLen / 2.0;

            int sets = totalRebarCount / 2; // 총 철근 개수의 절반 = 내측/외측 각각의 철근 수 (샘플 점 개수)
            if (sets <= 0) return results;

            var arcLens = new List<double>();
            bool hasCenterPoint = (sets % 2 != 0);

            if (hasCenterPoint)
            {
                // 홀수 개의 점: 정중앙 1개 + 좌우 (sets-1)/2 쌍
                arcLens.Add(center);
                int kMax = (sets - 1) / 2;
                for (int k = 1; k <= kMax; k++)
                {
                    double rPos = center + k * ctcMm;
                    double lPos = center - k * ctcMm;
                    if (rPos <= totalLen + 1e-4) arcLens.Add(rPos);
                    if (lPos >= -1e-4) arcLens.Add(lPos);
                }
            }
            else
            {
                // 짝수 개의 점: 정중앙 생략, ±0.5*CTC 위치부터 시작
                int kMax = sets / 2;
                for (int k = 1; k <= kMax; k++)
                {
                    double offset = (k - 0.5) * ctcMm;
                    double rPos = center + offset;
                    double lPos = center - offset;
                    if (rPos <= totalLen + 1e-4) arcLens.Add(rPos);
                    if (lPos >= -1e-4) arcLens.Add(lPos);
                }
            }
            arcLens.Sort();

            // 각 위치의 점 추출
            var points = new List<RebarPoint>();
            foreach (var al in arcLens)
            {
                SamplePointAt(segments, al, out var pt);
                points.Add(pt);
            }

            // chord perpendicular 법선 계산
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i] == null) continue;

                RebarPoint pA = (i > 0) ? points[i - 1] : null;
                RebarPoint pB = (i < points.Count - 1) ? points[i + 1] : null;

                double chordX, chordY;
                if (pA != null && pB != null) { chordX = pB.X - pA.X; chordY = pB.Y - pA.Y; }
                else if (pB != null) { chordX = pB.X - points[i].X; chordY = pB.Y - points[i].Y; }
                else if (pA != null) { chordX = points[i].X - pA.X; chordY = points[i].Y - pA.Y; }
                else continue;

                double len = Math.Sqrt(chordX * chordX + chordY * chordY);
                if (len < 1e-9) continue;

                double nx = -chordY / len;
                double ny = chordX / len;

                results.Add((arcLens[i], points[i], nx, ny));
            }

            return results;
        }

        // ============================================================
        // 기존 순차 샘플링 (유지)
        // ============================================================

        /// <summary>
        /// 순차 CTC 샘플링 + chord perpendicular 법선.
        /// polyline을 arcLen=0부터 CTC 간격으로 순차 샘플링하고,
        /// 인접 샘플점 간의 chord에서 수직 벡터를 법선으로 사용.
        /// </summary>
        public static List<(double arcLen, RebarPoint point, double nx, double ny)> SampleSequentialWithChordNormal(
            List<RebarSegment> segments, double ctcMm, double startOffset = 0)
        {
            var results = new List<(double, RebarPoint, double, double)>();
            if (segments == null || segments.Count == 0 || ctcMm <= 0) return results;

            double totalLen = TotalLength(segments);
            if (totalLen <= 0) return results;

            // 1. CTC 간격으로 arc-length 위치 생성
            var arcLens = new List<double>();
            for (double pos = startOffset; pos <= totalLen + 1e-6; pos += ctcMm)
                arcLens.Add(Math.Min(pos, totalLen));

            // 2. 각 위치의 점 추출
            var points = new List<RebarPoint>();
            foreach (var al in arcLens)
            {
                SamplePointAt(segments, al, out var pt);
                points.Add(pt);
            }

            // 3. chord perpendicular 법선 계산
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i] == null) continue;

                // 인접 두 점으로 chord 구함
                RebarPoint pA = (i > 0) ? points[i - 1] : null;
                RebarPoint pB = (i < points.Count - 1) ? points[i + 1] : null;

                double chordX, chordY;
                if (pA != null && pB != null)
                {
                    // 양쪽 다 있으면 pA→pB chord (중앙 차분)
                    chordX = pB.X - pA.X;
                    chordY = pB.Y - pA.Y;
                }
                else if (pB != null)
                {
                    // 첫 번째 점: 현재→다음
                    chordX = pB.X - points[i].X;
                    chordY = pB.Y - points[i].Y;
                }
                else if (pA != null)
                {
                    // 마지막 점: 이전→현재
                    chordX = points[i].X - pA.X;
                    chordY = points[i].Y - pA.Y;
                }
                else continue;

                double len = Math.Sqrt(chordX * chordX + chordY * chordY);
                if (len < 1e-9) continue;

                // 수직 벡터 (chord를 90° 회전)
                double nx = -chordY / len;
                double ny = chordX / len;

                results.Add((arcLens[i], points[i], nx, ny));
            }

            return results;
        }

        /// <summary>arc-length 위치의 점만 반환 (법선 계산 없음).</summary>
        public static bool SamplePointAt(List<RebarSegment> segments, double targetArcLen, out RebarPoint point)
        {
            point = null;
            if (segments == null || segments.Count == 0) return false;

            double cumLen = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                double segLen = SegmentLength(seg);
                bool last = (i == segments.Count - 1);

                if (targetArcLen <= cumLen + segLen + 1e-6 || last)
                {
                    double localLen = targetArcLen - cumLen;
                    if (localLen < 0) localLen = 0;
                    if (localLen > segLen) localLen = segLen;
                    SampleOnSegment(seg, localLen, segLen, out point, out _, out _);
                    return point != null;
                }
                cumLen += segLen;
            }
            return false;
        }

        /// <summary>
        /// 중심선(직선)이 polyline과 만나는 점과 그 지점의 polyline 상 arc-length 위치를 반환.
        /// 여러 교차가 있으면 centerline end 점에 더 가까운 것을 우선.
        /// </summary>
        public static bool FindCenterlineIntersection(
            List<RebarSegment> segments,
            double lineStartX, double lineStartY,
            double lineEndX, double lineEndY,
            out RebarPoint hit, out double arcLenAtHit)
        {
            hit = null;
            arcLenAtHit = 0;
            if (segments == null || segments.Count == 0) return false;

            double cumLen = 0;
            double bestDistToEnd = double.MaxValue;
            bool found = false;

            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                double segLen = SegmentLength(seg);

                var candidates = new List<(RebarPoint pt, double localLen)>();
                if (seg.SegmentType == "Arc" && seg.MidPoint != null)
                    candidates = IntersectLineWithArc(seg, lineStartX, lineStartY, lineEndX, lineEndY);
                else
                    candidates = IntersectLineWithLine(seg, lineStartX, lineStartY, lineEndX, lineEndY);

                foreach (var (pt, localLen) in candidates)
                {
                    double dx = pt.X - lineEndX;
                    double dy = pt.Y - lineEndY;
                    double dd = dx * dx + dy * dy;
                    if (dd < bestDistToEnd)
                    {
                        bestDistToEnd = dd;
                        hit = pt;
                        arcLenAtHit = cumLen + localLen;
                        found = true;
                    }
                }

                cumLen += segLen;
            }
            return found;
        }

        /// <summary>arc-length 위치의 점과 outward normal을 계산. boundaryCenter 제공 시 normal 방향을 "중심에서 멀어지는 쪽"으로 정규화.</summary>
        public static bool SampleWithNormal(
            List<RebarSegment> segments,
            double targetArcLen,
            double boundaryCx, double boundaryCy, bool hasCenter,
            out RebarPoint point, out double normalX, out double normalY)
        {
            point = null;
            normalX = normalY = 0;
            if (segments == null || segments.Count == 0) return false;

            double cumLen = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                double segLen = SegmentLength(seg);
                bool last = (i == segments.Count - 1);

                if (targetArcLen <= cumLen + segLen + 1e-6 || last)
                {
                    double localLen = targetArcLen - cumLen;
                    if (localLen < 0) localLen = 0;
                    if (localLen > segLen) localLen = segLen;

                    SampleOnSegment(seg, localLen, segLen, out point, out double nx, out double ny);
                    if (hasCenter && point != null)
                    {
                        double outX = point.X - boundaryCx;
                        double outY = point.Y - boundaryCy;
                        if (nx * outX + ny * outY < 0) { nx = -nx; ny = -ny; }
                    }
                    normalX = nx; normalY = ny;
                    return point != null;
                }
                cumLen += segLen;
            }
            return false;
        }

        /// <summary>
        /// 내측과 외측 polyline 쌍으로부터 "중앙 curve"를 파생.
        /// 내측을 촘촘히 샘플링하고 각 점에서 외측의 최근점을 찾아 평균 → Line 세그먼트 체인 구성.
        /// </summary>
        public static List<RebarSegment> BuildCenterCurve(
            List<RebarSegment> innerSegments,
            List<RebarSegment> outerSegments,
            double sampleStepMm = 20.0)
        {
            var result = new List<RebarSegment>();
            if (innerSegments == null || outerSegments == null || innerSegments.Count == 0 || outerSegments.Count == 0)
                return result;

            double innerTotalLen = TotalLength(innerSegments);
            if (innerTotalLen <= 0) return result;

            var midpoints = new List<RebarPoint>();
            double pos = 0;
            while (pos <= innerTotalLen + 1e-6)
            {
                if (SampleWithNormal(innerSegments, Math.Min(pos, innerTotalLen), 0, 0, false,
                    out var innerPt, out _, out _))
                {
                    var outerPt = NearestPointOnPolyline(outerSegments, innerPt);
                    if (outerPt != null)
                    {
                        midpoints.Add(new RebarPoint
                        {
                            X = (innerPt.X + outerPt.X) / 2.0,
                            Y = (innerPt.Y + outerPt.Y) / 2.0
                        });
                    }
                }
                pos += sampleStepMm;
            }

            for (int i = 0; i < midpoints.Count - 1; i++)
            {
                result.Add(new RebarSegment
                {
                    SegmentType = "Line",
                    StartPoint = new RebarPoint { X = midpoints[i].X, Y = midpoints[i].Y },
                    EndPoint = new RebarPoint { X = midpoints[i + 1].X, Y = midpoints[i + 1].Y }
                });
            }
            return result;
        }

        /// <summary>
        /// 한 점 (ox, oy) 에서 방향 (dx, dy) 로 뻗은 반직선(양쪽 방향 모두 허용)이
        /// polyline 과 만나는 점들 중 시작점에 가장 가까운 교차점 반환.
        /// forwardOnly=true 이면 양의 방향만 (t ≥ 0) 허용.
        /// </summary>
        public static bool IntersectRayWithPolyline(
            List<RebarSegment> segments,
            double ox, double oy, double dx, double dy,
            bool forwardOnly,
            out RebarPoint hit)
        {
            hit = null;
            if (segments == null || segments.Count == 0) return false;

            // ray를 충분히 긴 선분 두 개로 표현 (forward + backward)
            double farLen = 1e7;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) return false;
            double ux = dx / len, uy = dy / len;

            // 교차 후보 수집
            RebarPoint best = null;
            double bestDist = double.MaxValue;

            foreach (var seg in segments)
            {
                List<(RebarPoint pt, double localLen)> candidates;
                double fx1 = ox - ux * (forwardOnly ? 0 : farLen);
                double fy1 = oy - uy * (forwardOnly ? 0 : farLen);
                double fx2 = ox + ux * farLen;
                double fy2 = oy + uy * farLen;

                if (seg.SegmentType == "Arc" && seg.MidPoint != null)
                    candidates = IntersectLineWithArc(seg, fx1, fy1, fx2, fy2);
                else
                    candidates = IntersectLineWithLine(seg, fx1, fy1, fx2, fy2);

                foreach (var (pt, _) in candidates)
                {
                    double ddx = pt.X - ox, ddy = pt.Y - oy;
                    double along = ddx * ux + ddy * uy;
                    if (forwardOnly && along < -1e-6) continue;
                    double d = Math.Sqrt(ddx * ddx + ddy * ddy);
                    if (d < bestDist) { bestDist = d; best = pt; }
                }
            }

            hit = best;
            return best != null;
        }

        // ============================================================
        // 내부 유틸
        // ============================================================

        public static double SegmentLength(RebarSegment seg)
        {
            if (seg?.StartPoint == null || seg.EndPoint == null) return 0;
            if (seg.SegmentType == "Arc" && seg.MidPoint != null)
                return ArcLenFromThreePoints(seg.StartPoint, seg.MidPoint, seg.EndPoint);
            return Distance(seg.StartPoint, seg.EndPoint);
        }

        public static double TotalLength(List<RebarSegment> segments)
        {
            if (segments == null) return 0;
            double s = 0;
            foreach (var seg in segments) s += SegmentLength(seg);
            return s;
        }

        private static void SampleOnSegment(RebarSegment seg, double localLen, double segLen,
            out RebarPoint point, out double normalX, out double normalY)
        {
            point = null;
            normalX = 0; normalY = 0;

            if (segLen < 1e-9)
            {
                point = new RebarPoint { X = seg.StartPoint.X, Y = seg.StartPoint.Y };
                return;
            }

            double t = localLen / segLen;

            if (seg.SegmentType != "Arc" || seg.MidPoint == null)
            {
                double dx = seg.EndPoint.X - seg.StartPoint.X;
                double dy = seg.EndPoint.Y - seg.StartPoint.Y;
                point = new RebarPoint
                {
                    X = seg.StartPoint.X + dx * t,
                    Y = seg.StartPoint.Y + dy * t
                };
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d > 1e-9)
                {
                    // 수직 normal (+90° 회전)
                    normalX = -dy / d;
                    normalY = dx / d;
                }
                return;
            }

            if (!ComputeCircle(seg.StartPoint, seg.MidPoint, seg.EndPoint,
                out double cx, out double cy, out double r))
            {
                double dx = seg.EndPoint.X - seg.StartPoint.X;
                double dy = seg.EndPoint.Y - seg.StartPoint.Y;
                point = new RebarPoint
                {
                    X = seg.StartPoint.X + dx * t,
                    Y = seg.StartPoint.Y + dy * t
                };
                return;
            }

            double aS = Math.Atan2(seg.StartPoint.Y - cy, seg.StartPoint.X - cx);
            double aM = Math.Atan2(seg.MidPoint.Y - cy, seg.MidPoint.X - cx);
            double aE = Math.Atan2(seg.EndPoint.Y - cy, seg.EndPoint.X - cx);

            double toEndCcw = NormalizeAngle(aE - aS);
            double toMidCcw = NormalizeAngle(aM - aS);
            bool isCcw = toMidCcw > 1e-9 && toMidCcw < toEndCcw;

            double sweep = isCcw ? toEndCcw : -(2 * Math.PI - toEndCcw);
            double angle = aS + sweep * t;

            point = new RebarPoint
            {
                X = cx + r * Math.Cos(angle),
                Y = cy + r * Math.Sin(angle)
            };

            // Radial normal (cx, cy → point)
            normalX = Math.Cos(angle);
            normalY = Math.Sin(angle);
        }

        private static List<(RebarPoint, double)> IntersectLineWithLine(RebarSegment seg,
            double lx1, double ly1, double lx2, double ly2)
        {
            var result = new List<(RebarPoint, double)>();
            double ax = seg.StartPoint.X, ay = seg.StartPoint.Y;
            double bx = seg.EndPoint.X, by = seg.EndPoint.Y;

            double d1x = bx - ax, d1y = by - ay;
            double d2x = lx2 - lx1, d2y = ly2 - ly1;

            double denom = d1x * d2y - d1y * d2x;
            if (Math.Abs(denom) < 1e-9) return result;

            double t = ((lx1 - ax) * d2y - (ly1 - ay) * d2x) / denom;
            if (t < -1e-6 || t > 1 + 1e-6) return result;
            if (t < 0) t = 0;
            if (t > 1) t = 1;

            double segLen = Math.Sqrt(d1x * d1x + d1y * d1y);
            double localLen = t * segLen;
            var pt = new RebarPoint { X = ax + d1x * t, Y = ay + d1y * t };
            result.Add((pt, localLen));
            return result;
        }

        private static List<(RebarPoint, double)> IntersectLineWithArc(RebarSegment seg,
            double lx1, double ly1, double lx2, double ly2)
        {
            var result = new List<(RebarPoint, double)>();
            if (!ComputeCircle(seg.StartPoint, seg.MidPoint, seg.EndPoint,
                out double cx, out double cy, out double r))
                return result;

            double dx = lx2 - lx1, dy = ly2 - ly1;
            double fx = lx1 - cx, fy = ly1 - cy;

            double a = dx * dx + dy * dy;
            double b = 2 * (fx * dx + fy * dy);
            double c = fx * fx + fy * fy - r * r;
            double disc = b * b - 4 * a * c;
            if (disc < 0) return result;

            double sqrtD = Math.Sqrt(disc);
            double[] ts = { (-b - sqrtD) / (2 * a), (-b + sqrtD) / (2 * a) };

            double aS = Math.Atan2(seg.StartPoint.Y - cy, seg.StartPoint.X - cx);
            double aM = Math.Atan2(seg.MidPoint.Y - cy, seg.MidPoint.X - cx);
            double aE = Math.Atan2(seg.EndPoint.Y - cy, seg.EndPoint.X - cx);

            double toEndCcw = NormalizeAngle(aE - aS);
            double toMidCcw = NormalizeAngle(aM - aS);
            bool isCcw = toMidCcw > 1e-9 && toMidCcw < toEndCcw;

            double sweep = isCcw ? toEndCcw : -(2 * Math.PI - toEndCcw);
            double totalArcLen = Math.Abs(sweep) * r;

            foreach (var t in ts)
            {
                double px = lx1 + dx * t;
                double py = ly1 + dy * t;
                double angle = Math.Atan2(py - cy, px - cx);

                double angleFromStart = NormalizeAngle(angle - aS);
                double localLen;
                if (isCcw)
                {
                    if (angleFromStart > toEndCcw + 1e-6) continue;
                    localLen = angleFromStart * r;
                }
                else
                {
                    double cwFromStart = 2 * Math.PI - angleFromStart;
                    double cwToEnd = 2 * Math.PI - toEndCcw;
                    if (angleFromStart < 1e-9)
                    {
                        localLen = 0;
                    }
                    else
                    {
                        if (cwFromStart > cwToEnd + 1e-6) continue;
                        localLen = cwFromStart * r;
                    }
                }

                result.Add((new RebarPoint { X = px, Y = py }, localLen));
            }
            return result;
        }

        public static RebarPoint NearestPointOnPolyline(List<RebarSegment> segments, RebarPoint p)
        {
            RebarPoint best = null;
            double bestD = double.MaxValue;
            foreach (var seg in segments)
            {
                var q = NearestPointOnSegment(seg, p);
                if (q == null) continue;
                double d = Distance(q, p);
                if (d < bestD) { bestD = d; best = q; }
            }
            return best;
        }

        private static RebarPoint NearestPointOnSegment(RebarSegment seg, RebarPoint p)
        {
            if (seg?.StartPoint == null || seg.EndPoint == null) return null;

            if (seg.SegmentType == "Arc" && seg.MidPoint != null &&
                ComputeCircle(seg.StartPoint, seg.MidPoint, seg.EndPoint, out double cx, out double cy, out double r))
            {
                double dx = p.X - cx, dy = p.Y - cy;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < 1e-9) return new RebarPoint { X = seg.StartPoint.X, Y = seg.StartPoint.Y };

                double px = cx + dx / d * r;
                double py = cy + dy / d * r;

                double aS = Math.Atan2(seg.StartPoint.Y - cy, seg.StartPoint.X - cx);
                double aM = Math.Atan2(seg.MidPoint.Y - cy, seg.MidPoint.X - cx);
                double aE = Math.Atan2(seg.EndPoint.Y - cy, seg.EndPoint.X - cx);
                double angle = Math.Atan2(py - cy, px - cx);

                double toEndCcw = NormalizeAngle(aE - aS);
                double toMidCcw = NormalizeAngle(aM - aS);
                bool isCcw = toMidCcw > 1e-9 && toMidCcw < toEndCcw;
                double afs = NormalizeAngle(angle - aS);

                bool onArc = isCcw ? (afs <= toEndCcw + 1e-6) : (afs < 1e-9 || (2 * Math.PI - afs) <= (2 * Math.PI - toEndCcw) + 1e-6);
                if (onArc) return new RebarPoint { X = px, Y = py };

                double ds = Distance(seg.StartPoint, p);
                double de = Distance(seg.EndPoint, p);
                return ds < de
                    ? new RebarPoint { X = seg.StartPoint.X, Y = seg.StartPoint.Y }
                    : new RebarPoint { X = seg.EndPoint.X, Y = seg.EndPoint.Y };
            }
            else
            {
                double ax = seg.StartPoint.X, ay = seg.StartPoint.Y;
                double bx = seg.EndPoint.X, by = seg.EndPoint.Y;
                double ex = bx - ax, ey = by - ay;
                double l2 = ex * ex + ey * ey;
                if (l2 < 1e-9) return new RebarPoint { X = ax, Y = ay };
                double t = ((p.X - ax) * ex + (p.Y - ay) * ey) / l2;
                t = Math.Max(0, Math.Min(1, t));
                return new RebarPoint { X = ax + t * ex, Y = ay + t * ey };
            }
        }

        private static bool ComputeCircle(RebarPoint a, RebarPoint b, RebarPoint c,
            out double cx, out double cy, out double r)
        {
            double ax = a.X, ay = a.Y, bx = b.X, by = b.Y, cxP = c.X, cyP = c.Y;
            double d = 2 * (ax * (by - cyP) + bx * (cyP - ay) + cxP * (ay - by));
            if (Math.Abs(d) < 1e-9) { cx = cy = r = 0; return false; }
            cx = ((ax * ax + ay * ay) * (by - cyP) + (bx * bx + by * by) * (cyP - ay) + (cxP * cxP + cyP * cyP) * (ay - by)) / d;
            cy = ((ax * ax + ay * ay) * (cxP - bx) + (bx * bx + by * by) * (ax - cxP) + (cxP * cxP + cyP * cyP) * (bx - ax)) / d;
            r = Math.Sqrt((ax - cx) * (ax - cx) + (ay - cy) * (ay - cy));
            return r > 1e-6;
        }

        private static double ArcLenFromThreePoints(RebarPoint s, RebarPoint m, RebarPoint e)
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

        // ============================================================
        // TrimmedChain: 세그먼트 원본은 보존하고 per-segment skip만 메타데이터로 기록하여
        //               공간 중복 구간을 "논리적으로" 제외한 채 arc-length 연산.
        // ============================================================

        /// <summary>
        /// 원본 세그먼트 + 유효 구간 (localStart, localEnd) 메타. 세그먼트 자체는 변경하지 않음.
        /// </summary>
        public class TrimmedSeg
        {
            public RebarSegment Seg;
            public double LocalStartMm; // 세그먼트의 내부 arc-length 시작 위치 (0 이상)
            public double LocalEndMm;   // 세그먼트의 내부 arc-length 끝 위치 (SegmentLength 이하)
            public double EffectiveLength => Math.Max(0, LocalEndMm - LocalStartMm);
        }

        /// <summary>디버그 로그 수집용. null이면 수집 안 함.</summary>
        public static List<string> TrimmedChainDebugLog = null;

        private static void DbgLog(string msg)
        {
            if (TrimmedChainDebugLog != null) TrimmedChainDebugLog.Add(msg);
        }

        /// <summary>
        /// 여러 polyline을 끝점 매칭 순으로 이어붙이되, 새 polyline 앞부분이 기존 체인 끝점과 겹치면
        /// 그 겹침만큼 새 polyline의 시작 arc-length를 skip하도록 LocalStart 조정.
        /// 원본 세그먼트는 건드리지 않음.
        /// </summary>
        public static List<TrimmedSeg> ConcatenatePolylinesTrimmed(List<List<RebarSegment>> polylines)
        {
            var result = new List<TrimmedSeg>();
            if (polylines == null || polylines.Count == 0) return result;

            var valid = polylines.Where(p => p != null && p.Count > 0).ToList();
            if (valid.Count == 0) return result;

            // 각 polyline 정보 덤프
            for (int i = 0; i < valid.Count; i++)
            {
                var segs = valid[i];
                double totalL = TotalLength(segs);
                var sp = segs[0].StartPoint;
                var ep = segs[segs.Count - 1].EndPoint;
                DbgLog($"  poly#{i}: segCount={segs.Count}, totalLen={totalL:F1}, " +
                       $"Start=({sp.X:F1},{sp.Y:F1}), End=({ep.X:F1},{ep.Y:F1})");
            }

            // 첫 polyline 시드
            var used = new bool[valid.Count];
            used[0] = true;
            foreach (var s in valid[0])
                result.Add(new TrimmedSeg { Seg = s, LocalStartMm = 0, LocalEndMm = SegmentLength(s) });
            DbgLog($"  [seed] poly#0 편입 (skip=0)");

            // 남은 polyline들: 체인 head/tail 양쪽에서 가장 가까운 후보를 찾아 확장
            for (int step = 1; step < valid.Count; step++)
            {
                RebarPoint tail = GetChainTailPoint(result);
                RebarPoint head = GetChainHeadPoint(result);
                if (tail == null && head == null) break;

                int bestIdx = -1;
                bool bestReverse = false;
                bool bestAppend = true; // true = tail에 append, false = head에 prepend
                double bestDist = double.MaxValue;
                for (int i = 0; i < valid.Count; i++)
                {
                    if (used[i]) continue;
                    var segs = valid[i];
                    var sp = segs[0].StartPoint;
                    var ep = segs[segs.Count - 1].EndPoint;

                    if (tail != null)
                    {
                        // tail ↔ sp : append, reverse=false
                        double d = Sq(tail, sp);
                        if (d < bestDist) { bestDist = d; bestIdx = i; bestReverse = false; bestAppend = true; }
                        // tail ↔ ep : append, reverse=true
                        d = Sq(tail, ep);
                        if (d < bestDist) { bestDist = d; bestIdx = i; bestReverse = true; bestAppend = true; }
                    }
                    if (head != null)
                    {
                        // ep ↔ head : prepend, reverse=false
                        double d = Sq(head, ep);
                        if (d < bestDist) { bestDist = d; bestIdx = i; bestReverse = false; bestAppend = false; }
                        // sp ↔ head : prepend, reverse=true (뒤집으면 끝점이 sp가 됨)
                        d = Sq(head, sp);
                        if (d < bestDist) { bestDist = d; bestIdx = i; bestReverse = true; bestAppend = false; }
                    }
                }
                if (bestIdx < 0) break;
                used[bestIdx] = true;

                var next = bestReverse ? ReverseSegments(valid[bestIdx]) : new List<RebarSegment>(valid[bestIdx]);
                double nextTotalL = TotalLength(next);

                // 접합부 점 P = append이면 tail, prepend이면 head
                RebarPoint junction = bestAppend ? tail : head;

                // P가 next 내부 어느 위치에 있는지 찾음
                bool found = FindPointInsidePolyline(next, junction, out _, out double enterArcLen);

                DbgLog($"  [step{step}] pick poly#{bestIdx} (reverse={bestReverse}, append={bestAppend}) " +
                       $"junction=({junction.X:F1},{junction.Y:F1}) bestDist={Math.Sqrt(bestDist):F1}mm, " +
                       $"nextLen={nextTotalL:F1}, found={found}, enterArcLen={enterArcLen:F1}");

                if (!found)
                {
                    // 접합부 skip 계산 실패 → skip=0으로 그냥 이어붙임
                    var toAdd = new List<TrimmedSeg>();
                    foreach (var s in next)
                        toAdd.Add(new TrimmedSeg { Seg = s, LocalStartMm = 0, LocalEndMm = SegmentLength(s) });
                    if (bestAppend) result.AddRange(toAdd);
                    else result.InsertRange(0, toAdd);
                    DbgLog($"    -> found=false: skip=0으로 그냥 이어붙임 (중복 가능!)");
                    continue;
                }

                // append 인 경우: next의 [0, enterArcLen] 구간 skip, 이후만 체인 끝에 추가
                // prepend 인 경우: next의 [enterArcLen, total] 구간 skip, 이전만 체인 앞에 추가
                if (bestAppend)
                {
                    double cum = 0;
                    var toAdd = new List<TrimmedSeg>();
                    for (int i = 0; i < next.Count; i++)
                    {
                        var s = next[i];
                        double L = SegmentLength(s);
                        double segStart = cum, segEnd = cum + L;

                        if (segEnd <= enterArcLen + 1e-6)
                        {
                            // 완전히 skip 구간 → 버림
                        }
                        else if (segStart >= enterArcLen - 1e-6)
                        {
                            toAdd.Add(new TrimmedSeg { Seg = s, LocalStartMm = 0, LocalEndMm = L });
                        }
                        else
                        {
                            double localStart = enterArcLen - segStart;
                            toAdd.Add(new TrimmedSeg { Seg = s, LocalStartMm = localStart, LocalEndMm = L });
                        }
                        cum = segEnd;
                    }
                    result.AddRange(toAdd);
                }
                else
                {
                    // prepend: next의 [0, enterArcLen] 만 유효 (enterArcLen 이후는 skip)
                    double cum = 0;
                    var toAdd = new List<TrimmedSeg>();
                    for (int i = 0; i < next.Count; i++)
                    {
                        var s = next[i];
                        double L = SegmentLength(s);
                        double segStart = cum, segEnd = cum + L;

                        if (segStart >= enterArcLen - 1e-6)
                        {
                            // 완전히 skip 구간 → 버림
                        }
                        else if (segEnd <= enterArcLen + 1e-6)
                        {
                            toAdd.Add(new TrimmedSeg { Seg = s, LocalStartMm = 0, LocalEndMm = L });
                        }
                        else
                        {
                            double localEnd = enterArcLen - segStart;
                            toAdd.Add(new TrimmedSeg { Seg = s, LocalStartMm = 0, LocalEndMm = localEnd });
                        }
                        cum = segEnd;
                    }
                    result.InsertRange(0, toAdd);
                }
            }

            return result;
        }

        private static RebarPoint GetChainHeadPoint(List<TrimmedSeg> chain)
        {
            for (int i = 0; i < chain.Count; i++)
            {
                var ts = chain[i];
                if (ts.EffectiveLength <= 1e-9) continue;
                if (!SamplePointAt(new List<RebarSegment> { ts.Seg }, ts.LocalStartMm, out var pt)) continue;
                return pt;
            }
            return null;
        }

        private static RebarPoint GetChainTailPoint(List<TrimmedSeg> chain)
        {
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var ts = chain[i];
                if (ts.EffectiveLength <= 1e-9) continue;
                if (!SamplePointAt(new List<RebarSegment> { ts.Seg }, ts.LocalEndMm, out var pt)) continue;
                return pt;
            }
            return null;
        }

        /// <summary>polyline 내부에서 target 점이 어느 세그먼트의 어느 localLen에 해당하는지. 못 찾으면 false.</summary>
        private static bool FindPointInsidePolyline(List<RebarSegment> polyline, RebarPoint target,
            out int segIdx, out double arcLenAtPoint)
        {
            segIdx = -1; arcLenAtPoint = 0;
            if (polyline == null || polyline.Count == 0 || target == null) return false;

            double cum = 0;
            double bestDist2 = double.MaxValue;
            int bestIdx = -1;
            double bestLocal = 0, bestCum = 0;
            double tol2 = 9.0; // 3mm^2 이내면 "매치"로 간주 (여유)

            for (int i = 0; i < polyline.Count; i++)
            {
                var s = polyline[i];
                double L = SegmentLength(s);
                double localLen;
                if (!FindNearestLocalLenOnSegment(s, target, out localLen)) { cum += L; continue; }

                RebarPoint q;
                if (!SamplePointAt(new List<RebarSegment> { s }, localLen, out q)) { cum += L; continue; }
                double dx = q.X - target.X, dy = q.Y - target.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestDist2)
                {
                    bestDist2 = d2;
                    bestIdx = i; bestLocal = localLen; bestCum = cum;
                }
                cum += L;
            }

            if (bestIdx < 0 || bestDist2 > 1e6) return false; // 1000mm 이내 근사도 없음 = 실패
            // 너무 먼 매칭은 실패로 취급 (예: gap이 너무 커서 의미 없음)
            if (bestDist2 > Math.Max(tol2, 100 * 100)) return false;

            segIdx = bestIdx;
            arcLenAtPoint = bestCum + bestLocal;
            return true;
        }

        /// <summary>세그먼트 위에서 target과 가장 가까운 점의 localLen(0~segLen)을 반환.</summary>
        private static bool FindNearestLocalLenOnSegment(RebarSegment s, RebarPoint target, out double localLen)
        {
            localLen = 0;
            if (s?.StartPoint == null || s.EndPoint == null) return false;
            double L = SegmentLength(s);
            if (L < 1e-9) return false;

            if (s.SegmentType != "Arc" || s.MidPoint == null)
            {
                // Line: target을 선에 수직 투영
                double ex = s.EndPoint.X - s.StartPoint.X, ey = s.EndPoint.Y - s.StartPoint.Y;
                double l2 = ex * ex + ey * ey;
                if (l2 < 1e-9) return false;
                double t = ((target.X - s.StartPoint.X) * ex + (target.Y - s.StartPoint.Y) * ey) / l2;
                t = Math.Max(0, Math.Min(1, t));
                localLen = t * L;
                return true;
            }

            // Arc: target을 중심 방향으로 원 위에 투영
            if (!ComputeCircle(s.StartPoint, s.MidPoint, s.EndPoint, out double cx, out double cy, out double r))
                return false;
            double dx = target.X - cx, dy = target.Y - cy;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-9) return false;

            double aS = Math.Atan2(s.StartPoint.Y - cy, s.StartPoint.X - cx);
            double aM = Math.Atan2(s.MidPoint.Y - cy, s.MidPoint.X - cx);
            double aE = Math.Atan2(s.EndPoint.Y - cy, s.EndPoint.X - cx);
            double toEndCcw = NormalizeAngle(aE - aS);
            double toMidCcw = NormalizeAngle(aM - aS);
            bool isCcw = toMidCcw > 1e-9 && toMidCcw < toEndCcw;

            double aT = Math.Atan2(dy, dx);
            double fromStart = NormalizeAngle(aT - aS);
            double sweep = isCcw ? toEndCcw : (2 * Math.PI - toEndCcw);

            double along;
            if (isCcw)
            {
                if (fromStart > toEndCcw + 1e-6) along = (fromStart < Math.PI + toEndCcw / 2) ? toEndCcw : 0;
                else along = fromStart;
            }
            else
            {
                double cw = 2 * Math.PI - fromStart;
                double cwEnd = 2 * Math.PI - toEndCcw;
                if (fromStart < 1e-9) along = 0;
                else if (cw > cwEnd + 1e-6) along = (cw < Math.PI + cwEnd / 2) ? cwEnd : 0;
                else along = cw;
            }

            localLen = along * r;
            localLen = Math.Max(0, Math.Min(L, localLen));
            return true;
        }

        // ----- TrimmedChain 기반 arc-length 연산 -----

        public static double TotalLengthTrimmed(List<TrimmedSeg> chain)
        {
            if (chain == null) return 0;
            double sum = 0;
            foreach (var ts in chain) sum += ts.EffectiveLength;
            return sum;
        }

        /// <summary>trimmed 체인에서 공간 arc-length 위치의 점 + chord perpendicular 법선 샘플링.</summary>
        public static bool SamplePointAtTrimmed(List<TrimmedSeg> chain, double targetArcLen,
            out RebarPoint point)
        {
            point = null;
            if (chain == null || chain.Count == 0) return false;

            double cum = 0;
            foreach (var ts in chain)
            {
                double eff = ts.EffectiveLength;
                if (eff <= 1e-9) continue;
                bool last = (ts == chain[chain.Count - 1]);

                if (targetArcLen <= cum + eff + 1e-6 || last)
                {
                    double localOffset = targetArcLen - cum;
                    if (localOffset < 0) localOffset = 0;
                    if (localOffset > eff) localOffset = eff;
                    double absLocal = ts.LocalStartMm + localOffset;
                    return SamplePointAt(new List<RebarSegment> { ts.Seg }, absLocal, out point);
                }
                cum += eff;
            }
            return false;
        }

        /// <summary>TrimmedChain 기반 중앙 양방향 CTC 샘플링 + chord perpendicular 법선.</summary>
        public static List<(double arcLen, RebarPoint point, double nx, double ny)> SampleFromCenterTrimmed(
            List<TrimmedSeg> chain, double ctcMm, int totalRebarCount)
        {
            var results = new List<(double, RebarPoint, double, double)>();
            if (chain == null || chain.Count == 0 || ctcMm <= 0 || totalRebarCount <= 0) return results;

            double totalLen = TotalLengthTrimmed(chain);
            if (totalLen <= 0) return results;

            double center = totalLen / 2.0;
            int sets = totalRebarCount / 2;
            if (sets <= 0) return results;

            var arcLens = new List<double>();
            bool hasCenterPoint = (sets % 2 != 0);

            if (hasCenterPoint)
            {
                arcLens.Add(center);
                int kMax = (sets - 1) / 2;
                for (int k = 1; k <= kMax; k++)
                {
                    double rPos = center + k * ctcMm;
                    double lPos = center - k * ctcMm;
                    if (rPos <= totalLen + 1e-4) arcLens.Add(rPos);
                    if (lPos >= -1e-4) arcLens.Add(lPos);
                }
            }
            else
            {
                int kMax = sets / 2;
                for (int k = 1; k <= kMax; k++)
                {
                    double offset = (k - 0.5) * ctcMm;
                    double rPos = center + offset;
                    double lPos = center - offset;
                    if (rPos <= totalLen + 1e-4) arcLens.Add(rPos);
                    if (lPos >= -1e-4) arcLens.Add(lPos);
                }
            }
            arcLens.Sort();

            var points = new List<RebarPoint>();
            foreach (var al in arcLens)
            {
                SamplePointAtTrimmed(chain, al, out var pt);
                points.Add(pt);
            }

            for (int i = 0; i < points.Count; i++)
            {
                if (points[i] == null) continue;
                RebarPoint pA = (i > 0) ? points[i - 1] : null;
                RebarPoint pB = (i < points.Count - 1) ? points[i + 1] : null;

                double chordX, chordY;
                if (pA != null && pB != null) { chordX = pB.X - pA.X; chordY = pB.Y - pA.Y; }
                else if (pB != null) { chordX = pB.X - points[i].X; chordY = pB.Y - points[i].Y; }
                else if (pA != null) { chordX = points[i].X - pA.X; chordY = points[i].Y - pA.Y; }
                else continue;

                double len = Math.Sqrt(chordX * chordX + chordY * chordY);
                if (len < 1e-9) continue;
                double nx = -chordY / len, ny = chordX / len;
                results.Add((arcLens[i], points[i], nx, ny));
            }

            return results;
        }

        /// <summary>TrimmedChain을 일반 세그먼트 리스트로 "실현". LocalStart/End가 잘린 세그는 잘라낸 버전으로 변환 (Line/Arc).</summary>
        public static List<RebarSegment> MaterializeTrimmed(List<TrimmedSeg> chain)
        {
            var result = new List<RebarSegment>();
            if (chain == null) return result;
            foreach (var ts in chain)
            {
                if (ts.EffectiveLength <= 1e-9) continue;
                var s = ts.Seg;
                double L = SegmentLength(s);
                if (ts.LocalStartMm <= 1e-6 && ts.LocalEndMm >= L - 1e-6)
                {
                    result.Add(s);
                    continue;
                }
                // 부분 세그먼트 만들기
                if (!SamplePointAt(new List<RebarSegment> { s }, ts.LocalStartMm, out var pS)) continue;
                if (!SamplePointAt(new List<RebarSegment> { s }, ts.LocalEndMm, out var pE)) continue;

                if (s.SegmentType == "Arc" && s.MidPoint != null)
                {
                    double mid = (ts.LocalStartMm + ts.LocalEndMm) / 2.0;
                    if (!SamplePointAt(new List<RebarSegment> { s }, mid, out var pM)) continue;
                    result.Add(new RebarSegment
                    {
                        SegmentType = "Arc",
                        StartPoint = pS,
                        MidPoint = pM,
                        EndPoint = pE
                    });
                }
                else
                {
                    result.Add(new RebarSegment
                    {
                        SegmentType = "Line",
                        StartPoint = pS,
                        EndPoint = pE
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// 주어진 점에 가장 가까운 centerline 위 점에서의 chord-perpendicular을 계산.
        /// 외측-내측 페어 연결선이 centerline에 수직이 되도록 ray 방향을 보정하는 용도.
        /// </summary>
        public static bool ComputeChordPerpendicularNearPoint(
            List<RebarSegment> centerSegs, RebarPoint nearPoint,
            out double nx, out double ny)
        {
            nx = ny = 0;
            if (centerSegs == null || centerSegs.Count == 0 || nearPoint == null) return false;

            double totalLen = TotalLength(centerSegs);
            if (totalLen <= 0) return false;

            double bestD2 = double.MaxValue;
            double bestArcLen = 0;
            double cumLen = 0;

            foreach (var seg in centerSegs)
            {
                double segLen = SegmentLength(seg);
                if (segLen < 1e-9) { cumLen += segLen; continue; }

                var nearest = NearestPointOnSegment(seg, nearPoint);
                if (nearest == null) { cumLen += segLen; continue; }

                double dx = nearest.X - nearPoint.X;
                double dy = nearest.Y - nearPoint.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    double localLen;
                    if (seg.SegmentType == "Arc" && seg.MidPoint != null)
                    {
                        localLen = ArcLenFromThreePoints(seg.StartPoint, seg.MidPoint, nearest);
                    }
                    else
                    {
                        double sx = seg.StartPoint.X, sy = seg.StartPoint.Y;
                        double ex = seg.EndPoint.X - sx, ey = seg.EndPoint.Y - sy;
                        double l2 = ex * ex + ey * ey;
                        double t = l2 < 1e-9 ? 0 : ((nearest.X - sx) * ex + (nearest.Y - sy) * ey) / l2;
                        if (t < 0) t = 0; else if (t > 1) t = 1;
                        localLen = t * segLen;
                    }
                    bestArcLen = cumLen + localLen;
                }
                cumLen += segLen;
            }

            // 양옆 delta 위치를 샘플링해서 chord 방향 → perpendicular
            double delta = Math.Min(50.0, totalLen * 0.05);
            if (delta < 1.0) delta = 1.0;
            double posA = bestArcLen - delta;
            double posB = bestArcLen + delta;
            if (posA < 0) { posB = Math.Min(totalLen, posB - posA); posA = 0; }
            if (posB > totalLen) { posA = Math.Max(0, posA - (posB - totalLen)); posB = totalLen; }
            if (Math.Abs(posB - posA) < 1e-9) return false;

            if (!SamplePointAt(centerSegs, posA, out var pA)) return false;
            if (!SamplePointAt(centerSegs, posB, out var pB)) return false;
            if (pA == null || pB == null) return false;

            double cx = pB.X - pA.X, cy = pB.Y - pA.Y;
            double len = Math.Sqrt(cx * cx + cy * cy);
            if (len < 1e-9) return false;

            nx = -cy / len;
            ny = cx / len;
            return true;
        }
    }
}
