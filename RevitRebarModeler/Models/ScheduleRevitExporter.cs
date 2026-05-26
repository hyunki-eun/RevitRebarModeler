using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.DB;

namespace RevitRebarModeler.Models
{
    /// <summary>
    /// 보강철근 ViewSchedule을 일람표 양식 (Excel과 동일) 으로 생성.
    /// 동일 이름의 Schedule이 이미 있으면 삭제 후 재생성.
    ///
    /// 일람표1: TYPE × 직경 그룹 (RBR_*, OrderedNames)
    /// 일람표2: 마크 인터리브 (RBR_M_*, OrderedNamesM2)
    /// 일람표3: Cycle 서브그룹 + 해설 (RBR_M_*, OrderedNamesM3)
    /// </summary>
    public static class ScheduleRevitExporter
    {
        public const string ScheduleName1 = "보강철근 수량 일람표1";
        public const string ScheduleName2 = "보강철근 수량 일람표2";
        public const string ScheduleName3 = "보강철근 수량 일람표3";

        // 하위 호환: 기존 코드가 참조할 수 있는 단일 이름 (일람표1을 가리킴)
        public const string ScheduleName = ScheduleName1;

        /// <summary>
        /// 일람표1/2/3 ViewSchedule 3개를 생성/재생성. 트랜잭션 내부에서 호출.
        /// </summary>
        public static List<ElementId> CreateAllOrReplace(Document doc)
        {
            var ids = new List<ElementId>();
            ids.Add(CreateSchedule1(doc));
            ids.Add(CreateSchedule2(doc));
            ids.Add(CreateSchedule3(doc));
            return ids;
        }

        /// <summary>일람표1만 생성 (하위 호환).</summary>
        public static ElementId CreateOrReplace(Document doc) => CreateSchedule1(doc);

        // ───────────────────────────────────────────────────────────────────
        // 일람표1: TYPE × 직경
        // ───────────────────────────────────────────────────────────────────
        private static ElementId CreateSchedule1(Document doc)
        {
            DeleteExisting(doc, ScheduleName1);

            var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Rebar));
            try { schedule.Name = ScheduleName1; } catch { }

            var fieldByName = BuildSchedulableFieldMap(schedule, doc);
            AddOrderedFields(schedule, fieldByName, RebarScheduleParameters.OrderedNames);

            // 정렬 + 그룹화: 구 분 → 직경
            TryAddSort(schedule, fieldByName, RebarScheduleParameters.Names.Type, showHeader: true);
            TryAddSort(schedule, fieldByName, RebarScheduleParameters.Names.DiameterLabel);

            try { schedule.Definition.IsItemized = false; } catch { }

            // RBR_Type이 채워진 Rebar만 표시
            TryAddHasParameterFilter(schedule, RebarScheduleParameters.Names.Type);

            return schedule.Id;
        }

        // ───────────────────────────────────────────────────────────────────
        // 일람표2: 마크 인터리브 (A1, A1-1, A2, A2-1, ..., B1, T1, T2, ...)
        // ───────────────────────────────────────────────────────────────────
        private static ElementId CreateSchedule2(Document doc)
        {
            DeleteExisting(doc, ScheduleName2);

            var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Rebar));
            try { schedule.Name = ScheduleName2; } catch { }

            var fieldByName = BuildSchedulableFieldMap(schedule, doc);

            // 정렬 보조 컬럼 (SortKey2) 도 같이 추가 후 Hidden 처리
            var displayed = RebarScheduleParameters.OrderedNamesM2;
            var withSort = displayed.Concat(new[] { RebarScheduleParameters.Names.M_SortKey2 }).ToArray();
            AddOrderedFields(schedule, fieldByName, withSort);

            // SortKey2 컬럼은 화면에서 숨김
            TryHideField(schedule, RebarScheduleParameters.Names.M_SortKey2);

            // 정렬: 구 분(TYPE) → SortKey2 (오름차순 = 마크 인터리브)
            TryAddSort(schedule, fieldByName, RebarScheduleParameters.Names.Type, showHeader: true);
            TryAddSort(schedule, fieldByName, RebarScheduleParameters.Names.M_SortKey2);

            try { schedule.Definition.IsItemized = false; } catch { }

            TryAddHasParameterFilter(schedule, RebarScheduleParameters.Names.M_MarkLabel);

            return schedule.Id;
        }

        // ───────────────────────────────────────────────────────────────────
        // 일람표3: Cycle 서브그룹 + 해설
        // ───────────────────────────────────────────────────────────────────
        private static ElementId CreateSchedule3(Document doc)
        {
            DeleteExisting(doc, ScheduleName3);

            var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Rebar));
            try { schedule.Name = ScheduleName3; } catch { }

            var fieldByName = BuildSchedulableFieldMap(schedule, doc);

            var displayed = RebarScheduleParameters.OrderedNamesM3;
            var withSort = displayed.Concat(new[] { RebarScheduleParameters.Names.M_MarkIndex }).ToArray();
            AddOrderedFields(schedule, fieldByName, withSort);

            TryHideField(schedule, RebarScheduleParameters.Names.M_MarkIndex);

            // 정렬: TYPE → SubGroup → MarkIndex
            TryAddSort(schedule, fieldByName, RebarScheduleParameters.Names.Type, showHeader: true);
            TryAddSort(schedule, fieldByName, RebarScheduleParameters.Names.M_SubGroup, showHeader: true);
            TryAddSort(schedule, fieldByName, RebarScheduleParameters.Names.M_MarkIndex);

            try { schedule.Definition.IsItemized = false; } catch { }

            TryAddHasParameterFilter(schedule, RebarScheduleParameters.Names.M_MarkLabel);

            return schedule.Id;
        }

        // ───────────────────────────────────────────────────────────────────
        // 헬퍼
        // ───────────────────────────────────────────────────────────────────

        private static void DeleteExisting(Document doc, string scheduleName)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .FirstOrDefault(v => v.Name == scheduleName);
            if (existing != null)
                doc.Delete(existing.Id);
        }

        private static Dictionary<string, SchedulableField> BuildSchedulableFieldMap(
            ViewSchedule schedule, Document doc)
        {
            var fieldByName = new Dictionary<string, SchedulableField>(StringComparer.Ordinal);
            foreach (var sf in schedule.Definition.GetSchedulableFields())
            {
                try
                {
                    string nm = sf.GetName(doc);
                    if (!string.IsNullOrEmpty(nm) && !fieldByName.ContainsKey(nm))
                        fieldByName[nm] = sf;
                }
                catch { }
            }
            return fieldByName;
        }

        private static void AddOrderedFields(ViewSchedule schedule,
            Dictionary<string, SchedulableField> fieldByName, string[] paramNames)
        {
            foreach (var paramName in paramNames)
            {
                if (!fieldByName.TryGetValue(paramName, out var sf)) continue;
                ScheduleField field;
                try { field = schedule.Definition.AddField(sf); }
                catch { continue; }

                if (field != null &&
                    RebarScheduleParameters.ColumnHeadings.TryGetValue(paramName, out string heading))
                {
                    try { field.ColumnHeading = heading; } catch { }
                }
            }
        }

        private static void TryAddSort(ViewSchedule schedule,
            Dictionary<string, SchedulableField> fieldByName,
            string paramName, bool showHeader = false)
        {
            try
            {
                if (!fieldByName.ContainsKey(paramName)) return;
                var field = FindFieldByName(schedule, paramName);
                if (field == null) return;
                var sg = new ScheduleSortGroupField(field.FieldId)
                {
                    SortOrder = ScheduleSortOrder.Ascending,
                    ShowHeader = showHeader,
                };
                schedule.Definition.AddSortGroupField(sg);
            }
            catch { }
        }

        private static void TryAddHasParameterFilter(ViewSchedule schedule, string paramName)
        {
            try
            {
                var field = FindFieldByName(schedule, paramName);
                if (field == null) return;
                var filter = new ScheduleFilter(field.FieldId, ScheduleFilterType.HasParameter);
                schedule.Definition.AddFilter(filter);
            }
            catch { }
        }

        private static void TryHideField(ViewSchedule schedule, string paramName)
        {
            try
            {
                var field = FindFieldByName(schedule, paramName);
                if (field != null) field.IsHidden = true;
            }
            catch { }
        }

        private static ScheduleField FindFieldByName(ViewSchedule schedule, string paramName)
        {
            var def = schedule.Definition;
            int n = def.GetFieldCount();
            for (int i = 0; i < n; i++)
            {
                var f = def.GetField(i);
                try
                {
                    if (f.GetName() == paramName) return f;
                }
                catch { }
            }
            return null;
        }
    }
}
