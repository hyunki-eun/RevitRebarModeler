using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitRebarModeler.Models
{
    /// <summary>
    /// 여러 명령/창에 복사돼 있던 공통 헬퍼를 한 곳으로 통합한 정적 클래스.
    /// (이전: 각 Command/Window 파일에 ExtractStructureKey/ParseDepthFromHost/BuildHostMap/
    ///  FindRebarBarType/GetBoundaryCenter/ClassifyInnerOuter 가 복붙되어 분기 위험이 있었음)
    /// </summary>
    public static class RebarHelpers
    {
        /// <summary>mm → Revit 내부 단위(feet).</summary>
        public const double MmToFt = 1.0 / 304.8;
        /// <summary>feet → mm.</summary>
        public const double FtToMm = 304.8;

        // 구조도 키 추출용 정규식 (재컴파일 방지)
        private static readonly Regex StructureKeyRegex = new Regex(@"구조도\(\d+\)", RegexOptions.Compiled);
        private static readonly Regex StructureKeyNumRegex = new Regex(@"구조도\((\d+)\)", RegexOptions.Compiled);
        private static readonly Regex DepthRegex = new Regex(@"depth=(\d+\.?\d*)", RegexOptions.Compiled);

        /// <summary>"… 구조도(3) …" → "구조도(3)". 못 찾으면 빈 문자열.</summary>
        public static string ExtractStructureKey(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var m = StructureKeyRegex.Match(text);
            return m.Success ? m.Value : "";
        }

        /// <summary>호스트(구조 프레임)의 Comments에서 'depth=N'(mm) 추출. 없으면 0.</summary>
        public static double ParseDepthFromHost(Element hostElement)
        {
            string comments = hostElement?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
            var m = DepthRegex.Match(comments);
            return m.Success
                ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
                : 0;
        }

        /// <summary>
        /// 구조도 키 → 호스트 Element 맵. StructuralFraming 우선, 없으면 GenericModel.
        /// 같은 키는 먼저 발견한 것 우선(first-wins).
        /// </summary>
        public static Dictionary<string, Element> BuildHostMap(Document doc)
        {
            var map = new Dictionary<string, Element>();
            var hosts = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .WhereElementIsNotElementType()
                .ToList();

            if (hosts.Count == 0)
            {
                hosts = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_GenericModel)
                    .WhereElementIsNotElementType()
                    .ToList();
            }

            foreach (var elem in hosts)
            {
                string comments = elem.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
                string key = ExtractStructureKey(comments);
                if (!string.IsNullOrEmpty(key) && !map.ContainsKey(key))
                    map[key] = elem;
            }
            return map;
        }

        /// <summary>BarType의 직경(ft).</summary>
        public static double GetBarDiameterFt(RebarBarType barType)
        {
            var param = barType?.get_Parameter(BuiltInParameter.REBAR_BAR_DIAMETER);
            return param != null ? param.AsDouble() : 0.0;
        }

        private static bool IsStirrupOrTie(string name)
            => name != null && (name.Contains("스트럽") || name.Contains("타이")
                || name.Contains("Stirrup") || name.Contains("Tie"));

        /// <summary>
        /// 직경(mm)에 맞는 RebarBarType 검색. 이름 매칭 → 직경 수치 근사 순.
        /// <para>preferStirrup: 직경 근사 단계에서 스트럽/타이 타입을 우선할지 여부.
        /// 전단철근(스터럽)은 true, 종/횡(주근)은 false.</para>
        /// <para>diameterLabel: 비어있지 않으면 라벨 이름 매칭을 1순위로 먼저 시도(전단용).</para>
        /// </summary>
        public static RebarBarType FindRebarBarType(Document doc, double diameterMm,
            bool preferStirrup, string diameterLabel = null)
        {
            int d = (int)Math.Round(diameterMm);
            double targetFt = diameterMm * MmToFt;

            var all = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
            if (all.Count == 0) return null;

            // 1순위(옵션): DiameterLabel 직접/숫자 이름 매칭
            if (!string.IsNullOrEmpty(diameterLabel))
            {
                var direct = all.FirstOrDefault(r => r.Name == diameterLabel);
                if (direct != null) return direct;
                var labelNum = Regex.Match(diameterLabel, @"\d+");
                if (labelNum.Success && int.TryParse(labelNum.Value, out int ld))
                {
                    foreach (var name in new[] { $"D{ld}", $"{ld} 400S", $"D{ld} 400S", $"{ld}" })
                    {
                        var hit = all.FirstOrDefault(r => r.Name == name);
                        if (hit != null) return hit;
                    }
                }
            }

            // 2순위: DiameterMm 기반 이름 매칭
            foreach (var name in new[] { $"D{d}", $"{d} 400S", $"D{d} 400S", $"{d}" })
            {
                var hit = all.FirstOrDefault(r => r.Name == name);
                if (hit != null) return hit;
            }

            // 3순위: 직경 수치 근사치 (ft). preferStirrup 에 맞는 타입을 앞으로.
            return all
                .Where(r => Math.Abs(GetBarDiameterFt(r) - targetFt) < 0.001)
                .OrderBy(r => (IsStirrupOrTie(r.Name) == preferStirrup) ? 0 : 1)
                .FirstOrDefault();
        }

        /// <summary>
        /// 구조도 경계 중심(BoundaryCenter) 조회. 둘 다 0이면 미설정으로 보고 found=false.
        /// </summary>
        public static void GetBoundaryCenter(CivilExportData data, string structureKey,
            out double cx, out double cy, out bool found)
        {
            cx = 0; cy = 0;
            found = false;
            if (data?.StructureRegions == null) return;
            foreach (var cd in data.StructureRegions)
            {
                var m = StructureKeyNumRegex.Match(cd.CycleKey ?? "");
                if (!m.Success) continue;
                if ($"구조도({m.Groups[1].Value})" != structureKey) continue;
                if (cd.BoundaryCenterX == 0 && cd.BoundaryCenterY == 0) continue;
                cx = cd.BoundaryCenterX;
                cy = cd.BoundaryCenterY;
                found = true;
                return;
            }
        }

        /// <summary>
        /// Civil3D 원본 저장 순서 기준 분류: 앞 절반 = 내측(false), 뒤 절반 = 외측(true).
        /// cx/cy/hasCenter 인자는 호출부 호환 목적이며 현재 로직에서는 사용하지 않는다.
        /// </summary>
        public static Dictionary<string, bool> ClassifyInnerOuter(List<TransverseRebarData> rebars,
            double cx, double cy, bool hasCenter)
        {
            var result = new Dictionary<string, bool>();
            if (rebars == null || rebars.Count < 2) return result;

            int half = rebars.Count / 2;
            for (int i = 0; i < rebars.Count; i++)
            {
                var r = rebars[i];
                if (r?.Segments == null || r.Segments.Count == 0) continue;
                result[r.Id] = (i >= half); // true = outer (뒤 절반)
            }
            return result;
        }
    }
}
