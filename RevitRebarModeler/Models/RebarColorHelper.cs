using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitRebarModeler.Models
{
    /// <summary>
    /// Rebar Mark 패턴별 색상 View Filter 적용.
    /// 횡철근(Mark에 "단_inner_" 또는 "단_outer_") = 파랑.
    /// 종철근(Mark에 "_longi_") + 전단철근(Mark에 "_shear_") = 빨강.
    /// 모든 3D 뷰에 자동 적용. 호출은 반드시 Transaction 내부에서.
    /// </summary>
    public static class RebarColorHelper
    {
        private const string FilterTransName = "RebarColor_Transverse";
        private const string FilterRedName   = "RebarColor_LongiShear";

        public static void ApplyToAll3DViews(Document doc)
        {
            ElementId blueFilterId = GetOrCreateFilter(doc, FilterTransName,
                new[] { "단_inner_", "단_outer_" });
            ElementId redFilterId  = GetOrCreateFilter(doc, FilterRedName,
                new[] { "_longi_", "_shear_" });

            var blueOgs = MakeColorOverride(doc, new Color(0, 0, 255));
            var redOgs  = MakeColorOverride(doc, new Color(255, 0, 0));

            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(v => !v.IsTemplate)
                .ToList();

            foreach (var v in views)
            {
                ApplyFilter(v, blueFilterId, blueOgs);
                ApplyFilter(v, redFilterId,  redOgs);
            }
        }

        private static void ApplyFilter(View view, ElementId filterId, OverrideGraphicSettings ogs)
        {
            try
            {
                if (!view.GetFilters().Contains(filterId))
                    view.AddFilter(filterId);
                view.SetFilterOverrides(filterId, ogs);
                view.SetFilterVisibility(filterId, true);
            }
            catch { }
        }

        private static ElementId GetOrCreateFilter(Document doc, string name, string[] containsValues)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .FirstOrDefault(f => f.Name == name);
            if (existing != null) return existing.Id;

            var categories = new List<ElementId> { new ElementId(BuiltInCategory.OST_Rebar) };
            var markParamId = new ElementId(BuiltInParameter.ALL_MODEL_MARK);

            var elementFilters = new List<ElementFilter>();
            foreach (var s in containsValues)
            {
                var rule = ParameterFilterRuleFactory.CreateContainsRule(markParamId, s);
                elementFilters.Add(new ElementParameterFilter(rule));
            }

            ElementFilter combined = elementFilters.Count == 1
                ? elementFilters[0]
                : new LogicalOrFilter(elementFilters);

            var filter = ParameterFilterElement.Create(doc, name, categories, combined);
            return filter.Id;
        }

        private static OverrideGraphicSettings MakeColorOverride(Document doc, Color color)
        {
            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            ogs.SetCutLineColor(color);

            var solidFill = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(p => p.GetFillPattern() != null && p.GetFillPattern().IsSolidFill);
            if (solidFill != null)
            {
                ElementId fpId = solidFill.Id;
                ogs.SetSurfaceForegroundPatternId(fpId);
                ogs.SetSurfaceForegroundPatternColor(color);
                ogs.SetCutForegroundPatternId(fpId);
                ogs.SetCutForegroundPatternColor(color);
            }
            return ogs;
        }
    }
}
