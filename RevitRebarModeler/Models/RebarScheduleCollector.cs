using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitRebarModeler.Models
{
    /// <summary>
    /// Revit Document에 배치된 Rebar 요소를 Mark 정규식으로 분류해
    /// (구조도 × 종류 × 직경) 단위로 집계하여 RebarScheduleRow 목록 생성.
    ///
    /// Mark 패턴:
    ///   횡: 구조도(N)_M단_(inner|outer)_K
    ///   종: 구조도(N)_longi_(outer|inner)_M단
    ///   전단: 구조도(N)_shear_종M_횡S-E_(A|B)
    /// </summary>
    public static class RebarScheduleCollector
    {
        private const double FtToMm = 304.8;

        private static readonly Regex TransRegex =
            new Regex(@"^(구조도\(\d+\))_(\d+)단_(inner|outer)_(\d+)$");
        private static readonly Regex LongiRegex =
            new Regex(@"^(구조도\(\d+\))_longi_(outer|inner)_(\d+)단$");
        private static readonly Regex ShearRegex =
            new Regex(@"^(구조도\(\d+\))_shear_종(\d+)_횡(\d+)-(\d+)_(A|B)$");

        public class CollectStats
        {
            public int TotalRebars;        // 문서 내 Rebar 총 개수
            public int Matched;             // Mark 패턴 매칭된 개수
            public int Unmatched;           // Mark 패턴 매칭 실패 (스킵)
            public int DiameterUnknown;     // BarType 직경 추출 실패
            public int LengthZero;          // Centerline 길이 0
            public List<string> UnknownDiameters = new List<string>(); // 룩업표 미수록 직경
        }

        public static List<RebarScheduleRow> Collect(
            Document doc, double surchargePercent,
            Dictionary<string, double> transverseCtcMap,
            out CollectStats stats)
        {
            stats = new CollectStats();
            var rows = new List<RebarScheduleRow>();
            if (doc == null) return rows;

            var allRebars = new FilteredElementCollector(doc)
                .OfClass(typeof(Rebar))
                .Cast<Rebar>()
                .ToList();
            stats.TotalRebars = allRebars.Count;

            // 그룹 키: (structureKey, Kind, diameterMm)
            // 그룹 값: 본별 길이(mm) 리스트
            var groups = new Dictionary<GroupKey, List<double>>();

            foreach (var rebar in allRebars)
            {
                string mark = rebar.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                if (string.IsNullOrEmpty(mark)) { stats.Unmatched++; continue; }

                RebarKind kind;
                string structureKey;

                Match m = TransRegex.Match(mark);
                if (m.Success)
                {
                    kind = RebarKind.Transverse;
                    structureKey = m.Groups[1].Value;
                }
                else if ((m = LongiRegex.Match(mark)).Success)
                {
                    kind = RebarKind.Longitudinal;
                    structureKey = m.Groups[1].Value;
                }
                else if ((m = ShearRegex.Match(mark)).Success)
                {
                    kind = RebarKind.Shear;
                    structureKey = m.Groups[1].Value;
                }
                else
                {
                    stats.Unmatched++;
                    continue;
                }
                stats.Matched++;

                var barType = doc.GetElement(rebar.GetTypeId()) as RebarBarType;
                int diameterMm = ParseDiameterFromBarType(barType);
                if (diameterMm <= 0) { stats.DiameterUnknown++; continue; }

                double lengthMm = ComputeRebarLengthMm(rebar);
                if (lengthMm <= 0) { stats.LengthZero++; continue; }

                var key = new GroupKey(structureKey, kind, diameterMm);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<double>();
                    groups[key] = list;
                }
                list.Add(lengthMm);
            }

            foreach (var kv in groups)
            {
                var key = kv.Key;
                var lengths = kv.Value;
                int count = lengths.Count;
                double avgLength = lengths.Average();
                double totalLength = lengths.Sum();
                double unitWeight = RebarUnitWeights.GetKgPerMeter(key.DiameterMm);
                if (!RebarUnitWeights.Contains(key.DiameterMm))
                {
                    string label = $"D{key.DiameterMm}";
                    if (!stats.UnknownDiameters.Contains(label))
                        stats.UnknownDiameters.Add(label);
                }
                // E = 1m당 철근개수 = 1000/CTC (횡만, 전단/종은 0)
                // ※ 이전엔 D × 1000/CTC 였으나 사용자 수식 (총중량=철근총길이×단위중량) 에
                //   맞춰 D를 빼고 1m당 본수만 산출.
                double setCount = 0;
                if (key.Kind == RebarKind.Transverse &&
                    transverseCtcMap != null &&
                    transverseCtcMap.TryGetValue(key.StructureKey, out double ctcMm) &&
                    ctcMm > 0)
                {
                    setCount = 1000.0 / ctcMm;
                }

                // 철근 총길이 (1m 라이닝당, m) = 전체 길이(m) × 1m당 본수
                double rebarTotalLenM = (totalLength / 1000.0) * setCount;
                // 총중량 (kg) = 철근 총길이 × 단위중량(kg/m)
                double totalWeightKg = rebarTotalLenM * unitWeight;
                double surcharge = totalWeightKg * (1.0 + surchargePercent / 100.0);

                rows.Add(new RebarScheduleRow
                {
                    StructureKey = key.StructureKey,
                    Kind = key.Kind,
                    DiameterMm = key.DiameterMm,
                    Count = count,
                    UnitLengthMm = avgLength,
                    SetCount = setCount,
                    TotalLengthMm = totalLength,
                    UnitWeightKgPerM = unitWeight,
                    TotalWeightKg = totalWeightKg,
                    SurchargePercent = surchargePercent,
                    WeightWithSurchargeKg = surcharge,
                });
            }

            return rows
                .OrderBy(r => StructureSortKey(r.StructureKey))
                .ThenBy(r => KindSortOrder(r.Kind))
                .ThenByDescending(r => r.DiameterMm)
                .ToList();
        }

        private static int KindSortOrder(RebarKind k)
        {
            switch (k)
            {
                case RebarKind.Transverse: return 0;
                case RebarKind.Shear: return 1;
                case RebarKind.Longitudinal: return 2;
                default: return 99;
            }
        }

        // "구조도(1)" → 1, 정렬용
        private static int StructureSortKey(string key)
        {
            var m = Regex.Match(key ?? "", @"\((\d+)\)");
            return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : int.MaxValue;
        }

        /// <summary>
        /// 일람표 2/3용 세분화 행 수집.
        /// 그룹화:
        ///   - 횡: (구조도 × Cycle × sideLabel × K) → 마크 "A{i}" (CY1) / "A{i}-1" (CY2)
        ///   - 종: (구조도) → 마크 "B1"
        ///   - 전단: (구조도 × 횡묶음 S-E) → 마크 "T{i}"
        /// </summary>
        public static List<RebarScheduleRowDetailed> CollectDetailed(
            Document doc, double surchargePercent,
            Dictionary<string, double> transverseCtcMap,
            out CollectStats stats)
        {
            stats = new CollectStats();
            var result = new List<RebarScheduleRowDetailed>();
            if (doc == null) return result;

            var allRebars = new FilteredElementCollector(doc)
                .OfClass(typeof(Rebar)).Cast<Rebar>().ToList();
            stats.TotalRebars = allRebars.Count;

            // 그룹: (sk, cycle, side, k) → 길이 리스트
            var transGroups = new Dictionary<(string sk, int cycle, string side, int k), List<double>>();
            var transBarTypes = new Dictionary<(string sk, int cycle, string side, int k), RebarBarType>();
            // 종: (sk, dia) → 길이 리스트
            var longiGroups = new Dictionary<(string sk, int dia), List<double>>();
            var longiBarTypes = new Dictionary<(string sk, int dia), RebarBarType>();
            // 전단: (sk, bundle) → 길이 리스트
            var shearGroups = new Dictionary<(string sk, string bundle), List<double>>();
            var shearBarTypes = new Dictionary<(string sk, string bundle), RebarBarType>();

            foreach (var rebar in allRebars)
            {
                string mark = rebar.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                if (string.IsNullOrEmpty(mark)) { stats.Unmatched++; continue; }

                var barType = doc.GetElement(rebar.GetTypeId()) as RebarBarType;
                int dia = ParseDiameterFromBarType(barType);
                if (dia <= 0) { stats.DiameterUnknown++; continue; }
                double lenMm = ComputeRebarLengthMm(rebar);
                if (lenMm <= 0) { stats.LengthZero++; continue; }

                Match m = TransRegex.Match(mark);
                if (m.Success)
                {
                    string sk = m.Groups[1].Value;
                    string side = m.Groups[3].Value;
                    int kIdx = int.Parse(m.Groups[4].Value);
                    int cycle = ParseCycleFromComments(rebar);
                    var key = (sk, cycle, side, kIdx);
                    if (!transGroups.ContainsKey(key)) transGroups[key] = new List<double>();
                    transGroups[key].Add(lenMm);
                    transBarTypes[key] = barType;
                    stats.Matched++;
                    continue;
                }
                m = LongiRegex.Match(mark);
                if (m.Success)
                {
                    string sk = m.Groups[1].Value;
                    var key = (sk, dia);
                    if (!longiGroups.ContainsKey(key)) longiGroups[key] = new List<double>();
                    longiGroups[key].Add(lenMm);
                    longiBarTypes[key] = barType;
                    stats.Matched++;
                    continue;
                }
                m = ShearRegex.Match(mark);
                if (m.Success)
                {
                    string sk = m.Groups[1].Value;
                    string bundle = $"{m.Groups[3].Value}-{m.Groups[4].Value}";
                    var key = (sk, bundle);
                    if (!shearGroups.ContainsKey(key)) shearGroups[key] = new List<double>();
                    shearGroups[key].Add(lenMm);
                    shearBarTypes[key] = barType;
                    stats.Matched++;
                    continue;
                }
                stats.Unmatched++;
            }

            // ── 횡 ──: 구조도 + Cycle 단위로 MarkIndex 부여
            var transByStructCycle = transGroups
                .GroupBy(g => new { g.Key.sk, g.Key.cycle })
                .OrderBy(g => StructureSortKey(g.Key.sk))
                .ThenBy(g => g.Key.cycle);
            foreach (var sc in transByStructCycle)
            {
                var ordered = sc.OrderBy(x => x.Key.side == "inner" ? 0 : 1)
                                .ThenBy(x => x.Key.k)
                                .ToList();
                int idx = 1;
                foreach (var entry in ordered)
                {
                    var (sk, cycle, side, k) = entry.Key;
                    var lens = entry.Value;
                    var barType = transBarTypes[entry.Key];
                    int dia = ParseDiameterFromBarType(barType);
                    if (!RebarUnitWeights.Contains(dia))
                    {
                        string lbl = $"D{dia}";
                        if (!stats.UnknownDiameters.Contains(lbl)) stats.UnknownDiameters.Add(lbl);
                    }
                    result.Add(BuildTransRow(sk, cycle, idx, side, k, dia, lens,
                        transverseCtcMap, surchargePercent));
                    idx++;
                }
            }

            // ── 종 ──: 구조도당 직경별로 B1, B2... (보통 1개)
            var longiByStruct = longiGroups
                .GroupBy(g => g.Key.sk)
                .OrderBy(g => StructureSortKey(g.Key));
            foreach (var sg in longiByStruct)
            {
                var ordered = sg.OrderByDescending(x => x.Key.dia).ToList();
                int idx = 1;
                foreach (var entry in ordered)
                {
                    var (sk, dia) = entry.Key;
                    var lens = entry.Value;
                    if (!RebarUnitWeights.Contains(dia))
                    {
                        string lbl = $"D{dia}";
                        if (!stats.UnknownDiameters.Contains(lbl)) stats.UnknownDiameters.Add(lbl);
                    }
                    result.Add(BuildLongiRow(sk, idx, dia, lens, surchargePercent));
                    idx++;
                }
            }

            // ── 전단 ──: 구조도당 묶음별로 T1, T2, T3...
            var shearByStruct = shearGroups
                .GroupBy(g => g.Key.sk)
                .OrderBy(g => StructureSortKey(g.Key));
            foreach (var sg in shearByStruct)
            {
                var ordered = sg.OrderBy(x => x.Key.bundle).ToList();
                int idx = 1;
                foreach (var entry in ordered)
                {
                    var (sk, bundle) = entry.Key;
                    var lens = entry.Value;
                    var barType = shearBarTypes[entry.Key];
                    int dia = ParseDiameterFromBarType(barType);
                    if (!RebarUnitWeights.Contains(dia))
                    {
                        string lbl = $"D{dia}";
                        if (!stats.UnknownDiameters.Contains(lbl)) stats.UnknownDiameters.Add(lbl);
                    }
                    result.Add(BuildShearRow(sk, idx, dia, bundle, lens, surchargePercent));
                    idx++;
                }
            }

            return result;
        }

        private static RebarScheduleRowDetailed BuildTransRow(
            string sk, int cycle, int markIdx, string side, int k, int dia, List<double> lens,
            Dictionary<string, double> transverseCtcMap, double surchargePercent)
        {
            double avgLen = lens.Average();
            int count = lens.Count;
            double oneMPerCount = 0;
            if (transverseCtcMap != null && transverseCtcMap.TryGetValue(sk, out double ctc) && ctc > 0)
                oneMPerCount = 1000.0 / ctc;
            double totalLenMm = avgLen * count;
            double totalLenPerMm = totalLenMm * oneMPerCount;  // mm of rebar per 1m of lining
            double unitW = RebarUnitWeights.GetKgPerMeter(dia);
            double totalKg = (totalLenPerMm / 1000.0) * unitW;
            double sur = totalKg * (1.0 + surchargePercent / 100.0);

            string typeLabel = ToTypeLabelStatic(sk);
            return new RebarScheduleRowDetailed
            {
                StructureKey = sk,
                Kind = RebarKind.Transverse,
                CycleNumber = cycle,
                MarkIndex = markIdx,
                MarkLabel = cycle == 1 ? $"A{markIdx}" : $"A{markIdx}-1",
                SubGroupLabel = $"{typeLabel}_CY{cycle}",
                SourceKey = $"T:{sk}|{cycle}|{side}|{k}",
                DiameterMm = dia,
                DiameterLabel = $"H{dia}",
                Count = count,
                UnitLengthMm = avgLen,
                TotalLengthMm = totalLenMm,
                OneMPerCount = oneMPerCount,
                TotalLengthPerMm = totalLenPerMm,
                UnitWeightKgM = unitW,
                TotalWeightKg = totalKg,
                SurchargePercent = surchargePercent,
                SurchargeTotalKg = sur,
            };
        }

        private static RebarScheduleRowDetailed BuildLongiRow(
            string sk, int markIdx, int dia, List<double> lens, double surchargePercent)
        {
            double avgLen = lens.Average();
            int count = lens.Count;
            double totalLenMm = avgLen * count;
            double unitW = RebarUnitWeights.GetKgPerMeter(dia);
            // 종/전단은 OneMPerCount = 0 → 1m당 환산 안 함 → K, M = 0
            return new RebarScheduleRowDetailed
            {
                StructureKey = sk,
                Kind = RebarKind.Longitudinal,
                CycleNumber = null,
                MarkIndex = markIdx,
                MarkLabel = $"B{markIdx}",
                SubGroupLabel = "배력철근",
                SourceKey = $"L:{sk}|{dia}",
                DiameterMm = dia,
                DiameterLabel = $"H{dia}",
                Count = count,
                UnitLengthMm = avgLen,
                TotalLengthMm = totalLenMm,
                OneMPerCount = 0,
                TotalLengthPerMm = 0,
                UnitWeightKgM = unitW,
                TotalWeightKg = 0,
                SurchargePercent = surchargePercent,
                SurchargeTotalKg = 0,
            };
        }

        private static RebarScheduleRowDetailed BuildShearRow(
            string sk, int markIdx, int dia, string bundle, List<double> lens, double surchargePercent)
        {
            double avgLen = lens.Average();
            int count = lens.Count;
            double totalLenMm = avgLen * count;
            double unitW = RebarUnitWeights.GetKgPerMeter(dia);
            return new RebarScheduleRowDetailed
            {
                StructureKey = sk,
                Kind = RebarKind.Shear,
                CycleNumber = null,
                MarkIndex = markIdx,
                MarkLabel = $"T{markIdx}",
                SubGroupLabel = "전단철근",
                SourceKey = $"S:{sk}|{bundle}",
                DiameterMm = dia,
                DiameterLabel = $"H{dia}_전단철근",
                Count = count,
                UnitLengthMm = avgLen,
                TotalLengthMm = totalLenMm,
                OneMPerCount = 0,
                TotalLengthPerMm = 0,
                UnitWeightKgM = unitW,
                TotalWeightKg = 0,
                SurchargePercent = surchargePercent,
                SurchargeTotalKg = 0,
            };
        }

        private static int ParseCycleFromComments(Element el)
        {
            string c = el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
            var m = Regex.Match(c, @"CycleNumber=(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n)) return n;
            if (c.Contains("1cycle")) return 1;
            if (c.Contains("2cycle")) return 2;
            return 0;
        }

        private static string ToTypeLabelStatic(string structureKey)
        {
            var m = Regex.Match(structureKey ?? "", @"구조도\((\d+)\)");
            return m.Success ? $"TYPE{m.Groups[1].Value}" : structureKey;
        }

        private static int ParseDiameterFromBarType(RebarBarType barType)
        {
            if (barType == null) return 0;
            string name = barType.Name ?? "";
            // "D13", "D16", "13 400S", "D29 400S" 등에서 첫 정수 추출
            var m = Regex.Match(name, @"D?(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int d) && d > 0)
                return d;

            // 폴백: REBAR_BAR_DIAMETER 파라미터에서 직경 환산
            var param = barType.get_Parameter(BuiltInParameter.REBAR_BAR_DIAMETER);
            if (param != null)
            {
                double diamFt = param.AsDouble();
                return (int)Math.Round(diamFt * FtToMm);
            }
            return 0;
        }

        // 횡/종은 후크 없음 (배치 시 SetHookTypeId(0, InvalidElementId) 처리됨).
        // 전단은 후크 포함 → suppressHooks=false 로 통일 (없는 경우엔 무영향).
        private static double ComputeRebarLengthMm(Rebar rebar)
        {
            try
            {
                var curves = rebar.GetCenterlineCurves(
                    adjustForSelfIntersection: false,
                    suppressHooks: false,
                    suppressBendRadius: false,
                    multiplanarOption: MultiplanarOption.IncludeOnlyPlanarCurves,
                    barPositionIndex: 0);
                if (curves == null || curves.Count == 0) return 0;
                double totalFt = curves.Sum(c => c.Length);
                return totalFt * FtToMm;
            }
            catch
            {
                return 0;
            }
        }

        private struct GroupKey : IEquatable<GroupKey>
        {
            public string StructureKey;
            public RebarKind Kind;
            public int DiameterMm;

            public GroupKey(string sk, RebarKind k, int d) { StructureKey = sk; Kind = k; DiameterMm = d; }

            public bool Equals(GroupKey other) =>
                StructureKey == other.StructureKey && Kind == other.Kind && DiameterMm == other.DiameterMm;
            public override bool Equals(object obj) => obj is GroupKey g && Equals(g);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = StructureKey?.GetHashCode() ?? 0;
                    h = h * 31 + (int)Kind;
                    h = h * 31 + DiameterMm;
                    return h;
                }
            }
        }
    }
}
