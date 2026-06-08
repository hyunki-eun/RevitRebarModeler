using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitRebarModeler.Models
{
    /// <summary>
    /// 일람표 데이터(RebarScheduleRow)를 각 Rebar 요소의 Shared Parameter에 채워 넣음.
    /// 같은 (구조도 × 종류 × 직경) 그룹의 Rebar들은 동일한 값을 받아,
    /// Revit Schedule의 "Itemize every instance = false" 설정으로 1행으로 합쳐진다.
    /// </summary>
    public static class RebarSchedulePopulator
    {
        private const double FtToMm = 304.8;

        private static readonly Regex TransRegex =
            new Regex(@"^(구조도\(\d+\))_(\d+)단_(inner|outer)_(\d+)$");
        // (_SD\d+)? — 봉강 등급 suffix (SD400/500). SD300은 suffix 없음 → 호환.
        private static readonly Regex LongiRegex =
            new Regex(@"^(구조도\(\d+\))_longi_(outer|inner)_(\d+)단(_SD\d+)?$");
        private static readonly Regex ShearRegex =
            new Regex(@"^(구조도\(\d+\))_shear_종(\d+)_횡(\d+)-(\d+)_(A|B)(?:_P(\d+))?$");
        private static readonly Regex StructureNumRegex = new Regex(@"구조도\((\d+)\)");

        /// <summary>
        /// 트랜잭션 내부에서 호출. 각 Rebar의 RBR_* 파라미터를 그룹값으로 채운다.
        /// 미분류 Rebar는 건드리지 않는다.
        /// </summary>
        /// <returns>채워진 Rebar 개수</returns>
        public static int Populate(Document doc, List<RebarScheduleRow> rows)
        {
            if (rows == null || rows.Count == 0) return 0;

            // 그룹 키 → row
            var rowMap = new Dictionary<GroupKey, RebarScheduleRow>();
            foreach (var r in rows)
                rowMap[new GroupKey(r.StructureKey, r.Kind, r.DiameterMm)] = r;

            var allRebars = new FilteredElementCollector(doc)
                .OfClass(typeof(Rebar))
                .Cast<Rebar>()
                .ToList();

            int written = 0;
            foreach (var rebar in allRebars)
            {
                if (!TryClassify(rebar, doc, out string structureKey, out RebarKind kind, out int diameterMm))
                    continue;

                var key = new GroupKey(structureKey, kind, diameterMm);
                if (!rowMap.TryGetValue(key, out var row)) continue;

                string typeLabel = ToTypeLabel(structureKey);

                SetString(rebar, RebarScheduleParameters.Names.Type, typeLabel);
                SetString(rebar, RebarScheduleParameters.Names.DiameterLabel, row.DiameterLabelWithSuffix);
                SetDouble(rebar, RebarScheduleParameters.Names.UnitLengthMm, row.UnitLengthMm);
                SetInt(rebar, RebarScheduleParameters.Names.Count, row.Count);
                SetDouble(rebar, RebarScheduleParameters.Names.SetCount, row.SetCount);
                SetDouble(rebar, RebarScheduleParameters.Names.TotalLengthM, row.TotalLengthMm / 1000.0);
                SetDouble(rebar, RebarScheduleParameters.Names.UnitWeightKgM, row.UnitWeightKgPerM);
                SetDouble(rebar, RebarScheduleParameters.Names.TotalWeightKg, row.TotalWeightKg);
                SetDouble(rebar, RebarScheduleParameters.Names.SurchargeWeightKg, row.WeightWithSurchargeKg);

                written++;
            }
            return written;
        }

        /// <summary>
        /// 일람표2/3용 Detailed 행을 각 Rebar의 RBR_M_* 파라미터에 채워 넣음.
        /// Detailed Row의 SourceKey와 Rebar의 Mark/Comments에서 재구성한 키를 일치시켜 룩업.
        /// </summary>
        /// <summary>
        /// SourceKey → row 맵 구성. 같은 SourceKey가 둘 이상이면 첫 행만 채택하되,
        /// 충돌을 조용히 버리지 않고 진단 로그로 남긴다(데이터 손실 가시화).
        /// </summary>
        private static Dictionary<string, RebarScheduleRowDetailed> BuildSourceMap(
            List<RebarScheduleRowDetailed> rowsDetailed, string caller)
        {
            var map = new Dictionary<string, RebarScheduleRowDetailed>();
            foreach (var r in rowsDetailed)
            {
                if (string.IsNullOrEmpty(r.SourceKey)) continue;
                if (map.ContainsKey(r.SourceKey))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[RebarSchedulePopulator.{caller}] SourceKey 중복 — 무시됨: '{r.SourceKey}' (마크 {r.MarkLabel})");
                    continue;
                }
                map[r.SourceKey] = r;
            }
            return map;
        }

        public static int PopulateDetailed(Document doc, List<RebarScheduleRowDetailed> rowsDetailed)
        {
            if (rowsDetailed == null || rowsDetailed.Count == 0) return 0;

            // SourceKey → row
            var bySource = BuildSourceMap(rowsDetailed, nameof(PopulateDetailed));

            var allRebars = new FilteredElementCollector(doc)
                .OfClass(typeof(Rebar)).Cast<Rebar>().ToList();

            int written = 0;
            foreach (var rebar in allRebars)
            {
                string srcKey = BuildSourceKeyFromRebar(rebar, doc);
                if (srcKey == null) continue;
                if (!bySource.TryGetValue(srcKey, out var row)) continue;

                string structureKey = row.StructureKey;
                string typeLabel = ToTypeLabel(structureKey);

                SetString(rebar, RebarScheduleParameters.Names.Type, typeLabel);
                SetString(rebar, RebarScheduleParameters.Names.M_MarkLabel, row.MarkLabel);
                SetString(rebar, RebarScheduleParameters.Names.M_SubGroup, row.SubGroupLabel);
                SetString(rebar, RebarScheduleParameters.Names.M_DiameterLabel, row.DiameterLabel);
                SetString(rebar, RebarScheduleParameters.Names.M_SortKey2, BuildSortKey2(row));
                SetInt(rebar, RebarScheduleParameters.Names.M_MarkIndex, row.MarkIndex);
                SetInt(rebar, RebarScheduleParameters.Names.M_Count, row.Count);
                SetDouble(rebar, RebarScheduleParameters.Names.M_UnitLengthMm, row.UnitLengthMm);
                SetString(rebar, RebarScheduleParameters.Names.M_UnitLengthText, row.UnitLengthText);
                SetDouble(rebar, RebarScheduleParameters.Names.M_TotalLengthM, row.TotalLengthMm / 1000.0);
                SetDouble(rebar, RebarScheduleParameters.Names.M_OneMPerCount, row.OneMPerCount);
                SetDouble(rebar, RebarScheduleParameters.Names.M_TotalLengthPerM, row.TotalLengthPerMm / 1000.0);
                SetDouble(rebar, RebarScheduleParameters.Names.M_UnitWeightKgM, row.UnitWeightKgM);
                SetDouble(rebar, RebarScheduleParameters.Names.M_TotalWeightT, row.TotalWeightKg / 1000.0);
                SetDouble(rebar, RebarScheduleParameters.Names.M_SurchargePercent, row.SurchargePercent);
                SetDouble(rebar, RebarScheduleParameters.Names.M_SurchargeTotalT, row.SurchargeTotalKg / 1000.0);

                written++;
            }
            return written;
        }

        /// <summary>
        /// 라벨 전용 기록: 배치 직후 호출용. 수량/중량은 건드리지 않고
        /// Type · 마크라벨(A/B/T) · 서브그룹 · 정렬키 · 마크인덱스만 채운다.
        /// (수량·중량 집계는 수량 일람표 생성 시 PopulateDetailed 가 채움)
        /// </summary>
        public static int PopulateLabelsOnly(Document doc, List<RebarScheduleRowDetailed> rowsDetailed)
        {
            if (rowsDetailed == null || rowsDetailed.Count == 0) return 0;

            var bySource = BuildSourceMap(rowsDetailed, nameof(PopulateLabelsOnly));

            var allRebars = new FilteredElementCollector(doc)
                .OfClass(typeof(Rebar)).Cast<Rebar>().ToList();

            int written = 0;
            foreach (var rebar in allRebars)
            {
                string srcKey = BuildSourceKeyFromRebar(rebar, doc);
                if (srcKey == null) continue;
                if (!bySource.TryGetValue(srcKey, out var row)) continue;

                SetString(rebar, RebarScheduleParameters.Names.Type, ToTypeLabel(row.StructureKey));
                SetString(rebar, RebarScheduleParameters.Names.M_MarkLabel, row.MarkLabel);
                SetString(rebar, RebarScheduleParameters.Names.M_SubGroup, row.SubGroupLabel);
                SetString(rebar, RebarScheduleParameters.Names.M_SortKey2, BuildSortKey2(row));
                SetInt(rebar, RebarScheduleParameters.Names.M_MarkIndex, row.MarkIndex);
                written++;
            }
            return written;
        }

        /// <summary>
        /// 배치 명령 끝에서 호출. 현재 모델의 모든 Rebar에 A/B/T 마크 라벨을 즉시 기록.
        /// 자체 트랜잭션으로 Shared Parameter 바인딩까지 처리(없으면 생성). 실패는 조용히 무시.
        /// 수량·중량 집계는 별도 [수량 일람표] 명령에서 생성된다.
        /// </summary>
        public static void StampLabels(Autodesk.Revit.ApplicationServices.Application app, Document doc)
        {
            if (app == null || doc == null) return;
            using (var tr = new Transaction(doc, "마크 라벨 기록"))
            {
                try
                {
                    tr.Start();
                    RebarScheduleParameters.EnsureBound(app, doc);
                    // 라벨은 CTC·할증과 무관 → ctcMap=null, surcharge=0 으로 충분.
                    // 해설은 일람표와 동일하게 PatternName을 결합하기 위해 패턴 맵 전달.
                    var patternMap = RebarScheduleCollector.BuildPatternMap(SessionCache.LoadedJson);
                    var rows = RebarScheduleCollector.CollectDetailed(doc, 0, null, out _, patternMap);
                    PopulateLabelsOnly(doc, rows);
                    tr.Commit();
                }
                catch (Exception ex)
                {
                    // 라벨 기록 실패는 비치명적(배치 자체는 이미 완료)이나, 원인 파악용으로 로그
                    System.Diagnostics.Debug.WriteLine($"[StampLabels] 라벨 기록 실패: {ex.Message}");
                    if (tr.HasStarted() && !tr.HasEnded()) tr.RollBack();
                }
            }
        }

        // 일람표2 마크 인터리브 정렬용 키.
        // 1차: Kind(횡<종<전단), 2차: MarkIndex, 3차: CycleNumber(횡만 1<2)
        private static string BuildSortKey2(RebarScheduleRowDetailed row)
        {
            int kindOrd = row.Kind == RebarKind.Transverse ? 1
                        : row.Kind == RebarKind.Longitudinal ? 2 : 3;
            int cycle = row.CycleNumber ?? 0;
            return $"{kindOrd}_{row.MarkIndex:D3}_{cycle}";
        }

        // Rebar → Detailed Row 룩업 키. Collector의 SourceKey와 동일 포맷이어야 함.
        private static readonly Regex TransRegexExtract =
            new Regex(@"^(구조도\(\d+\))_(\d+)단_(inner|outer)_(\d+)$");
        // (_SD\d+)? — 봉강 등급 suffix (SD400/500). SD300은 suffix 없음 → 호환.
        private static readonly Regex LongiRegexExtract =
            new Regex(@"^(구조도\(\d+\))_longi_(outer|inner)_(\d+)단(_SD\d+)?$");
        private static readonly Regex ShearRegexExtract =
            new Regex(@"^(구조도\(\d+\))_shear_종(\d+)_횡(\d+)-(\d+)_(A|B)(?:_P(\d+))?$");

        private static string BuildSourceKeyFromRebar(Rebar rebar, Document doc)
        {
            string mark = rebar.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
            if (string.IsNullOrEmpty(mark)) return null;

            Match m;
            if ((m = TransRegexExtract.Match(mark)).Success)
            {
                string sk = m.Groups[1].Value;
                string side = m.Groups[3].Value;
                if (!int.TryParse(m.Groups[4].Value, out int k)) return null;
                int cycle = ParseCycleFromComments(rebar);
                return $"T:{sk}|{cycle}|{side}|{k}";
            }
            if ((m = LongiRegexExtract.Match(mark)).Success)
            {
                string sk = m.Groups[1].Value;
                var barType = doc.GetElement(rebar.GetTypeId()) as RebarBarType;
                int dia = ParseDiameter(barType);
                if (dia <= 0) return null;
                return $"L:{sk}|{dia}";
            }
            if ((m = ShearRegexExtract.Match(mark)).Success)
            {
                string sk = m.Groups[1].Value;
                // 가로 위치 K = _P{K} (없으면 구버전 호환: 묶음 시작 k) — 수집기와 동일 규칙
                string panelRaw = m.Groups[6].Success ? m.Groups[6].Value : m.Groups[3].Value;
                if (!int.TryParse(panelRaw, out int panelK)) return null;
                return $"S:{sk}|P{panelK}";
            }
            return null;
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

        private static bool TryClassify(Rebar rebar, Document doc,
            out string structureKey, out RebarKind kind, out int diameterMm)
        {
            structureKey = null;
            kind = RebarKind.Transverse;
            diameterMm = 0;

            string mark = rebar.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
            if (string.IsNullOrEmpty(mark)) return false;

            Match m = TransRegex.Match(mark);
            if (m.Success) { kind = RebarKind.Transverse; structureKey = m.Groups[1].Value; }
            else if ((m = LongiRegex.Match(mark)).Success)
            { kind = RebarKind.Longitudinal; structureKey = m.Groups[1].Value; }
            else if ((m = ShearRegex.Match(mark)).Success)
            { kind = RebarKind.Shear; structureKey = m.Groups[1].Value; }
            else return false;

            var barType = doc.GetElement(rebar.GetTypeId()) as RebarBarType;
            diameterMm = ParseDiameter(barType);
            return diameterMm > 0;
        }

        private static int ParseDiameter(RebarBarType barType)
        {
            if (barType == null) return 0;
            string name = barType.Name ?? "";
            var m = Regex.Match(name, @"D?(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int d) && d > 0)
                return d;
            var p = barType.get_Parameter(BuiltInParameter.REBAR_BAR_DIAMETER);
            if (p != null)
                return (int)Math.Round(p.AsDouble() * FtToMm);
            return 0;
        }

        private static string ToTypeLabel(string structureKey)
        {
            var m = StructureNumRegex.Match(structureKey ?? "");
            return m.Success ? $"TYPE{m.Groups[1].Value}" : structureKey;
        }

        private static void SetString(Element el, string paramName, string val)
        {
            try { el.LookupParameter(paramName)?.Set(val ?? ""); } catch { }
        }
        private static void SetInt(Element el, string paramName, int val)
        {
            try { el.LookupParameter(paramName)?.Set(val); } catch { }
        }
        private static void SetDouble(Element el, string paramName, double val)
        {
            try { el.LookupParameter(paramName)?.Set(val); } catch { }
        }

        private struct GroupKey : IEquatable<GroupKey>
        {
            public readonly string StructureKey;
            public readonly RebarKind Kind;
            public readonly int DiameterMm;
            public GroupKey(string sk, RebarKind k, int d) { StructureKey = sk; Kind = k; DiameterMm = d; }
            public bool Equals(GroupKey o) =>
                StructureKey == o.StructureKey && Kind == o.Kind && DiameterMm == o.DiameterMm;
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
