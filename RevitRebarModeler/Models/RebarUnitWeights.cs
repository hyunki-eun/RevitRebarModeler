using System.Collections.Generic;

namespace RevitRebarModeler.Models
{
    /// <summary>
    /// 이형철근 호칭별 단위중량 (KS D 3504, kg/m).
    /// 일람표 G 컬럼에 사용.
    /// </summary>
    public static class RebarUnitWeights
    {
        // key = 호칭 직경 (mm), value = kg/m
        private static readonly Dictionary<int, double> _table = new Dictionary<int, double>
        {
            { 10, 0.560 },
            { 13, 0.995 },
            { 16, 1.560 },
            { 19, 2.250 },
            { 22, 3.040 },
            { 25, 3.980 },
            { 29, 5.040 },
            { 32, 6.230 },
            { 35, 7.510 },
            { 38, 8.950 },
        };

        /// <summary>
        /// 직경(mm)에 대한 단위중량(kg/m). 표에 없으면 0.
        /// </summary>
        public static double GetKgPerMeter(int diameterMm)
        {
            return _table.TryGetValue(diameterMm, out double w) ? w : 0.0;
        }

        public static bool Contains(int diameterMm) => _table.ContainsKey(diameterMm);
    }
}
