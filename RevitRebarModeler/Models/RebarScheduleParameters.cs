using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace RevitRebarModeler.Models
{
    /// <summary>
    /// 일람표용 Shared Parameter 8종을 정의하고 Rebar 카테고리에 Instance Binding으로 등록한다.
    /// 이 파라미터들은 RebarSchedulePopulator가 각 Rebar에 그룹 단위 계산값을 채워 넣고,
    /// ScheduleRevitExporter가 Schedule 컬럼으로 노출한다.
    ///
    /// 그룹화 전략: Itemize every instance = false + 동일 그룹 Rebar에 동일 값 → 그룹 1행으로 자동 합쳐짐.
    /// </summary>
    public static class RebarScheduleParameters
    {
        public static class Names
        {
            // ── 일람표1 (TYPE × 직경 그룹) ──
            public const string Type = "RBR_Type";                          // "TYPE1"
            public const string DiameterLabel = "RBR_DiameterLabel";        // "D16" / "D13_전단"
            public const string UnitLengthMm = "RBR_UnitLengthMm";          // 한 본 평균 길이
            public const string Count = "RBR_Count";                        // 그룹 총 본수
            public const string SetCount = "RBR_SetCount";                  // E = D × 1000/CTC (횡만)
            public const string TotalLengthM = "RBR_TotalLengthM";          // 그룹 총길이 m
            public const string UnitWeightKgM = "RBR_UnitWeightKgM";        // kg/m (룩업)
            public const string TotalWeightKg = "RBR_TotalWeightKg";        // kg
            public const string SurchargeWeightKg = "RBR_SurchargeWeightKg";// kg (할증 포함)

            // ── 일람표2/3 (마크 단위) ──
            public const string M_MarkLabel = "RBR_M_MarkLabel";              // "A1", "A1-1", "B1", "T1"
            public const string M_SubGroup = "RBR_M_SubGroup";                // "TYPE1_CY1", "배력철근", "전단철근"
            public const string M_DiameterLabel = "RBR_M_DiameterLabel";      // "H16" / "H13_전단철근"
            public const string M_SortKey2 = "RBR_M_SortKey2";                // 일람표2 마크 인터리브 정렬용
            public const string M_MarkIndex = "RBR_M_MarkIndex";              // 1-based
            public const string M_Count = "RBR_M_Count";                      // 마크 본수
            public const string M_UnitLengthMm = "RBR_M_UnitLengthMm";        // 한 본 평균 (숫자, 내부보관)
            public const string M_UnitLengthText = "RBR_M_UnitLengthText";    // 철근 길이 표시(m): 전단=최소~최대, 그외=단일
            public const string M_TotalLengthM = "RBR_M_TotalLengthM";        // 전체 철근 길이(m) = avg × count / 1000
            public const string M_OneMPerCount = "RBR_M_OneMPerCount";        // 1m당 철근개수 (횡만)
            public const string M_TotalLengthPerM = "RBR_M_TotalLengthPerM";  // 철근 총길이(m, 1m라이닝당)
            public const string M_UnitWeightKgM = "RBR_M_UnitWeightKgM";      // kg/m
            public const string M_TotalWeightT = "RBR_M_TotalWeightT";        // 총중량 (t)
            public const string M_SurchargePercent = "RBR_M_SurchargePercent";// 할증 %
            public const string M_SurchargeTotalT = "RBR_M_SurchargeTotalT";  // 할증중량 (t)
        }

        /// <summary>일람표1 Schedule 컬럼 헤더 (한국어 표시명).</summary>
        public static readonly Dictionary<string, string> ColumnHeadings =
            new Dictionary<string, string>
            {
                { Names.Type, "구 분" },
                { Names.DiameterLabel, "직경" },
                { Names.UnitLengthMm, "전체 철근길이(mm)" },
                { Names.Count, "수량" },
                { Names.SetCount, "철근 SET 개수" },
                { Names.TotalLengthM, "총길이(m)" },
                { Names.UnitWeightKgM, "단위중량(kg/m)" },
                { Names.TotalWeightKg, "총중량(kg)" },
                { Names.SurchargeWeightKg, "할증중량(kg)" },

                // 일람표2/3 공통
                { Names.M_MarkLabel, "일람표 마크" },
                { Names.M_SubGroup, "해설" },
                { Names.M_DiameterLabel, "유형" },
                { Names.M_Count, "수량" },
                { Names.M_UnitLengthMm, "철근 길이(mm)" },
                { Names.M_UnitLengthText, "철근 길이(m)" },
                { Names.M_TotalLengthM, "전체 철근 길이(m)" },
                { Names.M_OneMPerCount, "1m당 철근개수" },
                { Names.M_TotalLengthPerM, "철근 총길이(m)" },
                { Names.M_UnitWeightKgM, "단위중량(kg/m)" },
                { Names.M_TotalWeightT, "총중량(t)" },
                { Names.M_SurchargePercent, "할증(%)" },
                { Names.M_SurchargeTotalT, "총중량_ADD(t)" },
            };

        /// <summary>일람표1 컬럼 출력 순서.</summary>
        public static readonly string[] OrderedNames =
        {
            Names.Type,
            Names.DiameterLabel,
            Names.UnitLengthMm,
            Names.Count,
            Names.SetCount,
            Names.TotalLengthM,
            Names.UnitWeightKgM,
            Names.TotalWeightKg,
            Names.SurchargeWeightKg,
        };

        /// <summary>일람표2 (마크 인터리브) 컬럼 출력 순서.
        /// Excel WriteSheet2와 동일 컬럼 구성: 타입 / 마크 / 유형 / 전체길이 / 수량 / 1m당 / 총길이 / 단위중량 / 총중량(t) / 할증% / 총중량_ADD(t)</summary>
        public static readonly string[] OrderedNamesM2 =
        {
            Names.Type,
            Names.M_MarkLabel,
            Names.M_DiameterLabel,
            Names.M_UnitLengthText,
            Names.M_TotalLengthM,
            Names.M_Count,
            Names.M_OneMPerCount,
            Names.M_TotalLengthPerM,
            Names.M_UnitWeightKgM,
            Names.M_TotalWeightT,
            Names.M_SurchargePercent,
            Names.M_SurchargeTotalT,
        };

        /// <summary>일람표3 (Cycle 그룹 + 해설) 컬럼 출력 순서.</summary>
        public static readonly string[] OrderedNamesM3 =
        {
            Names.Type,
            Names.M_SubGroup,
            Names.M_MarkLabel,
            Names.M_DiameterLabel,
            Names.M_UnitLengthText,
            Names.M_Count,
            Names.M_TotalLengthM,
            Names.M_OneMPerCount,
            Names.M_TotalLengthPerM,
            Names.M_UnitWeightKgM,
            Names.M_TotalWeightT,
            Names.M_SurchargePercent,
            Names.M_SurchargeTotalT,
        };

        private const string GroupName = "RebarSchedule";

        /// <summary>
        /// 빈 공유 파라미터 파일이 OpenSharedParameterFile 에서 거부되지 않도록 하는 최소 유효 헤더.
        /// (0바이트 파일은 헤더가 없어 일부 환경에서 null 반환/예외)
        /// </summary>
        internal const string SharedParamFileHeader =
            "# This is a Revit shared parameter file.\n" +
            "# Do not edit manually.\n" +
            "*META\tVERSION\tMINVERSION\n" +
            "META\t2\t1\n" +
            "*GROUP\tID\tNAME\n" +
            "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\n";

        /// <summary>
        /// Shared Parameter 8종을 정의하고 Rebar 카테고리에 Instance Binding 등록.
        /// 트랜잭션 내부에서 호출해야 함.
        /// </summary>
        public static void EnsureBound(Application app, Document doc)
        {
            // app.SharedParametersFilename 은 앱 전역(다른 프로젝트 공유) 설정이므로,
            // 우리가 임시로 바꿨다면 작업 후 원래대로 복원한다.
            string originalSpPath = app.SharedParametersFilename;
            bool changedSpPath = false;
            try
            {
                string spPath = originalSpPath;
                if (string.IsNullOrEmpty(spPath) || !File.Exists(spPath))
                {
                    string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    spPath = Path.Combine(dllDir ?? "", "RevitRebarModeler_SharedParams.txt");
                    if (!File.Exists(spPath))
                        File.WriteAllText(spPath, SharedParamFileHeader);
                    app.SharedParametersFilename = spPath;
                    changedSpPath = true;
                }

                DefinitionFile defFile = app.OpenSharedParameterFile();
                if (defFile == null)
                    throw new InvalidOperationException("Shared Parameter 파일을 열 수 없습니다: " + spPath);

                DefinitionGroup grp = defFile.Groups.get_Item(GroupName) ?? defFile.Groups.Create(GroupName);

                var catSet = app.Create.NewCategorySet();
                Category rebarCat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Rebar);
                if (rebarCat == null)
                    throw new InvalidOperationException("Rebar 카테고리를 찾을 수 없습니다.");
                catSet.Insert(rebarCat);

                EnsureParam(grp, Names.Type, SpecTypeId.String.Text, catSet, doc, app);
            EnsureParam(grp, Names.DiameterLabel, SpecTypeId.String.Text, catSet, doc, app);
            EnsureParam(grp, Names.UnitLengthMm, SpecTypeId.Number, catSet, doc, app);
            EnsureParam(grp, Names.Count, SpecTypeId.Int.Integer, catSet, doc, app);
            EnsureParam(grp, Names.SetCount, SpecTypeId.Number, catSet, doc, app);
            EnsureParam(grp, Names.TotalLengthM, SpecTypeId.Number, catSet, doc, app);
            EnsureParam(grp, Names.UnitWeightKgM, SpecTypeId.Number, catSet, doc, app);
            EnsureParam(grp, Names.TotalWeightKg, SpecTypeId.Number, catSet, doc, app);
            EnsureParam(grp, Names.SurchargeWeightKg, SpecTypeId.Number, catSet, doc, app);

            // 일람표2/3 (마크 단위)
            EnsureParam(grp, Names.M_MarkLabel, SpecTypeId.String.Text, catSet, doc, app);
            EnsureParam(grp, Names.M_SubGroup, SpecTypeId.String.Text, catSet, doc, app);
            EnsureParam(grp, Names.M_DiameterLabel, SpecTypeId.String.Text, catSet, doc, app);
            EnsureParam(grp, Names.M_SortKey2, SpecTypeId.String.Text, catSet, doc, app);
            EnsureParam(grp, Names.M_MarkIndex, SpecTypeId.Int.Integer, catSet, doc, app);
            EnsureParam(grp, Names.M_Count, SpecTypeId.Int.Integer, catSet, doc, app);
            EnsureParam(grp, Names.M_UnitLengthMm, SpecTypeId.Number, catSet, doc, app);
            EnsureParam(grp, Names.M_UnitLengthText, SpecTypeId.String.Text, catSet, doc, app);
            EnsureParam(grp, Names.M_TotalLengthM, SpecTypeId.Number, catSet, doc, app);
            EnsureParam(grp, Names.M_OneMPerCount, SpecTypeId.Number, catSet, doc, app);
            EnsureParam(grp, Names.M_TotalLengthPerM, SpecTypeId.Number, catSet, doc, app);
            EnsureParam(grp, Names.M_UnitWeightKgM, SpecTypeId.Number, catSet, doc, app);
            EnsureParam(grp, Names.M_TotalWeightT, SpecTypeId.Number, catSet, doc, app);
            EnsureParam(grp, Names.M_SurchargePercent, SpecTypeId.Number, catSet, doc, app);
            EnsureParam(grp, Names.M_SurchargeTotalT, SpecTypeId.Number, catSet, doc, app);
            }
            finally
            {
                if (changedSpPath)
                {
                    try { app.SharedParametersFilename = originalSpPath; } catch { }
                }
            }
        }

        private static void EnsureParam(
            DefinitionGroup grp,
            string name,
            ForgeTypeId spec,
            CategorySet cats,
            Document doc,
            Application app)
        {
            Definition def = grp.Definitions.get_Item(name);
            if (def == null)
            {
                var opts = new ExternalDefinitionCreationOptions(name, spec);
                def = grp.Definitions.Create(opts);
            }
            if (def == null) return;

            BindingMap bm = doc.ParameterBindings;
            if (bm.Contains(def)) return;

            InstanceBinding binding = app.Create.NewInstanceBinding(cats);
            bm.Insert(def, binding, GroupTypeId.Data);
        }
    }
}
