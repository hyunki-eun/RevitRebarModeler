namespace RevitRebarModeler.Models
{
    /// <summary>
    /// 일람표 2/3 (마크 단위 세분화 양식) 한 행.
    /// 그룹화 단위:
    ///   - 횡: (TYPE × Cycle × sideLabel × K) → 마크 "A{i}" (CY1) / "A{i}-1" (CY2)
    ///   - 종: (TYPE)                          → 마크 "B1"
    ///   - 전단: (TYPE × 묶음)                  → 마크 "T{i}"
    /// 수량 컬럼은 사용자 요청으로 출력 시 제거 (내부 계산용 Count만 유지).
    /// </summary>
    public class RebarScheduleRowDetailed
    {
        public string StructureKey { get; set; }  // 예: "구조도(1)"
        public RebarKind Kind { get; set; }
        public int? CycleNumber { get; set; }     // 횡만 1/2, 그 외 null
        public int MarkIndex { get; set; }        // 1-based 인덱스
        public string MarkLabel { get; set; }     // "A1" / "A1-1" / "B1" / "T1"
        public string SubGroupLabel { get; set; } // "TYPE1_CY1" / "배력철근" / "전단철근"

        // Populator가 Rebar → Row 룩업에 사용하는 원천 키.
        //   횡: "T:{sk}|{cycle}|{side}|{k}"  /  종: "L:{sk}|{dia}"  /  전단: "S:{sk}|{bundle}"
        public string SourceKey { get; set; }

        public int DiameterMm { get; set; }
        public string DiameterLabel { get; set; } // "H16" / "H13" / "H13_전단철근"

        public int Count { get; set; }            // 내부 계산용 (출력 시 제거)
        public double UnitLengthMm { get; set; }  // E = 철근 길이 (한 본 평균)
        public double TotalLengthMm { get; set; } // G = 전체 철근 길이 = UnitLengthMm × Count

        public double OneMPerCount { get; set; }      // H = 1m당 철근개수 = 1000/CTC (횡만, 그 외 0)
        public double TotalLengthPerMm { get; set; }  // I = 철근 총길이 (mm) = G × H (횡만)

        public double UnitWeightKgM { get; set; }    // J = kg/m (룩업)
        public double TotalWeightKg { get; set; }    // K (t로 변환) = I/1000 × J

        public double SurchargePercent { get; set; } // L = 할증 %
        public double SurchargeTotalKg { get; set; } // M (t로 변환) = K × (1+L/100)

        // 표시용 톤 단위
        public double TotalWeightT => TotalWeightKg / 1000.0;
        public double SurchargeTotalT => SurchargeTotalKg / 1000.0;

        // 미리보기 그리드용 m 단위 변환
        public double TotalLengthM => TotalLengthMm / 1000.0;
        public double TotalLengthPerMmInM => TotalLengthPerMm / 1000.0;

        // "TYPE1" 라벨 (구조도(N) → TYPEN)
        public string TypeLabel
        {
            get
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    StructureKey ?? "", @"구조도\((\d+)\)");
                return m.Success ? $"TYPE{m.Groups[1].Value}" : StructureKey;
            }
        }
    }
}
