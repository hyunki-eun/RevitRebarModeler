namespace RevitRebarModeler.Models
{
    public enum RebarKind
    {
        Transverse, // 횡방향
        Shear,      // 전단
        Longitudinal // 종방향
    }

    /// <summary>
    /// 보강철근 수량 일람표의 한 행. (TYPE × 종류 × 직경) 단위로 집계.
    /// </summary>
    public class RebarScheduleRow
    {
        public string StructureKey { get; set; }   // 예: "구조도(1)"
        public RebarKind Kind { get; set; }
        public int DiameterMm { get; set; }        // 호칭 직경 (예: 13, 16, 22)

        public int Count { get; set; }             // D = 본수 (실제 배치된 Rebar 수)
        public double UnitLengthMm { get; set; }   // 한 본 평균 길이 (mm, 내부 보존용)
        public double SetCount { get; set; }       // E = 1m당 철근개수 (횡: 1000/CTC, 전단/종: 0)
        public double TotalLengthMm { get; set; }  // C = 전체 철근 길이 (mm) = UnitLengthMm × Count

        public double UnitWeightKgPerM { get; set; }   // G = 단위중량 (kg/m, 룩업)
        public double TotalWeightKg { get; set; }      // H = 철근 총길이 × G
                                                       //   = (TotalLengthMm/1000 × SetCount) × UnitWeightKgPerM

        public double SurchargePercent { get; set; }   // I = 할증 (%)
        public double WeightWithSurchargeKg { get; set; } // J = H × (1 + I/100)

        // F = 철근 총길이 (m, 1m 라이닝당) = 전체 철근 길이(m) × 1m당 철근개수
        public double RebarTotalLengthM => (TotalLengthMm / 1000.0) * SetCount;

        // 표시용
        public string KindLabel
        {
            get
            {
                switch (Kind)
                {
                    case RebarKind.Transverse: return "횡방향";
                    case RebarKind.Shear: return "전단";
                    case RebarKind.Longitudinal: return "종방향";
                    default: return "";
                }
            }
        }

        public string DiameterLabel => $"D{DiameterMm}";
        public string DiameterLabelWithSuffix =>
            Kind == RebarKind.Shear ? $"D{DiameterMm}_전단" : $"D{DiameterMm}";

        // 표시용: "구조도(1)" → "TYPE1"
        public string TypeLabel
        {
            get
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    StructureKey ?? "", @"구조도\((\d+)\)");
                return m.Success ? $"TYPE{m.Groups[1].Value}" : StructureKey;
            }
        }

        // 미리보기 그리드용 m 단위 변환
        public double TotalLengthM => TotalLengthMm / 1000.0;
    }
}
