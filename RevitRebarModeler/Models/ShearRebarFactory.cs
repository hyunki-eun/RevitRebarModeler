using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitRebarModeler.Models
{
    /// <summary>
    /// 전단철근에 필요한 RebarShape 와 RebarBarType 을 준비하는 팩토리.
    ///
    /// 전략:
    /// 1) "전단철근모형" 이라는 RebarShape 가 이미 있으면 그대로 사용 (방안1 폴백).
    /// 2) 없으면 SharedParameter 5개(A,B,C,C1,C2) 를 등록 후 자동 생성 시도 (방안2).
    /// 3) 자동 생성도 실패하면 null 반환 → 호출자는 안내 다이얼로그 표출.
    ///
    /// RebarBarType 은 사용자가 선택한 직경의 기존 타입을 찾아 _전단철근 접미를 붙여 복제.
    /// 이미 같은 이름이 있으면 재사용 (중복 생성 방지).
    /// </summary>
    public static class ShearRebarFactory
    {
        public const string ShapeName = "전단철근모형";
        private const string ParamGroupName = "RevitRebarModeler_ShearShape";
        private static readonly string[] ParamNames = { "A", "B", "C", "C1", "C2" };

        // ============================================================
        // 1. RebarShape 준비 (검색 → 자동 생성)
        // ============================================================

        public static RebarShape EnsureShape(Document doc, out string log)
        {
            // 1단계: 기존 검색
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarShape))
                .Cast<RebarShape>()
                .FirstOrDefault(s => s.Name == ShapeName);
            if (existing != null)
            {
                log = $"기존 RebarShape '{ShapeName}' 재사용";
                return existing;
            }

            // 2단계: 자동 생성
            try
            {
                var paramIds = EnsureSharedParameters(doc);
                if (paramIds == null || paramIds.Count != 5)
                {
                    log = "SharedParameter 등록 실패 — RebarShape 자동 생성 불가";
                    return null;
                }

                var def = new RebarShapeDefinitionBySegments(doc, 5);

                // ── 5세그먼트 Z형 갈고리 ──
                // seg0 (A 후크)  : 위쪽 → 아래쪽   ( 0, -1)
                // seg1 (C1 가로) : 좌 → 우         ( 1,  0)
                // seg2 (C 세로)  : 위 → 아래       ( 0, -1)
                // seg3 (B 가로)  : 좌 → 우 (역방향) ( 1,  0)  ※ Z갈고리이므로 같은 방향이지만 위치가 아래
                // seg4 (C2 후크) : 아래 → 위        ( 0,  1)
                //
                // 단순화: 평면 Z형으로 정의 (수직-수평-수직-수평-수직 갈고리)
                // 실제 갈고리 모양은 fixed direction 으로 세팅하면 됨
                def.SetSegmentFixedDirection(0,  0, -1); // A 시작 후크 (아래)
                def.SetSegmentFixedDirection(1,  1,  0); // C1 상단 가로
                def.SetSegmentFixedDirection(2,  0, -1); // C 좌측 세로
                def.SetSegmentFixedDirection(3,  1,  0); // B 하단 가로
                def.SetSegmentFixedDirection(4,  0,  1); // C2 끝 후크 (위)

                // 파라미터 등록 + 세그먼트 길이 바인딩
                double defaultMm = 100.0;
                double defaultFt = defaultMm / 304.8;
                for (int i = 0; i < 5; i++)
                {
                    def.AddParameter(paramIds[i], defaultFt);
                    def.AddConstraintParallelToSegment(i, paramIds[i], false, false);
                }

                if (!def.Complete)
                {
                    log = "RebarShapeDefinition.Complete=false — 정의 미완성";
                    return null;
                }
                if (!def.CheckDefaultParameterValues(0, 0))
                {
                    log = "CheckDefaultParameterValues 실패 — 기하학 무결성 위반";
                    return null;
                }

                // Revit 2024 시그니처:
                //   Create(doc, definition, multiplanar, style, attachType,
                //          higherEnd, startHookOrient, startHookId, endHookOrient, endHookId)
                var shape = RebarShape.Create(doc, def, null,
                    RebarStyle.StirrupTie,
                    StirrupTieAttachmentType.InteriorFace,
                    0,
                    RebarHookOrientation.Right, 0,
                    RebarHookOrientation.Right, 0);

                if (shape == null)
                {
                    log = "RebarShape.Create() null 반환";
                    return null;
                }
                shape.Name = ShapeName;
                log = $"RebarShape '{ShapeName}' 자동 생성 완료";
                return shape;
            }
            catch (Exception ex)
            {
                log = $"RebarShape 자동 생성 실패: {ex.GetType().Name} — {ex.Message}";
                return null;
            }
        }

        // ============================================================
        // 2. SharedParameter 5개(A,B,C,C1,C2) 등록
        // ============================================================
        /// <summary>
        /// SharedParameter 파일에 그룹 'RevitRebarModeler_ShearShape' 와 5개 파라미터를 등록 후 ElementId 반환.
        /// 이미 등록되어 있으면 재사용.
        /// </summary>
        private static List<ElementId> EnsureSharedParameters(Document doc)
        {
            var app = doc.Application;
            string sharedParamFile = app.SharedParametersFilename;

            // SharedParameter 파일이 없으면 임시 파일 생성
            string tempPath = null;
            if (string.IsNullOrEmpty(sharedParamFile) || !File.Exists(sharedParamFile))
            {
                tempPath = Path.Combine(Path.GetTempPath(), "RevitRebarModeler_SharedParams.txt");
                if (!File.Exists(tempPath)) File.WriteAllText(tempPath, "");
                app.SharedParametersFilename = tempPath;
            }

            DefinitionFile defFile = app.OpenSharedParameterFile();
            if (defFile == null) return null;

            DefinitionGroup group = defFile.Groups.get_Item(ParamGroupName)
                                    ?? defFile.Groups.Create(ParamGroupName);

            var ids = new List<ElementId>();
            foreach (var pName in ParamNames)
            {
                Definition def = group.Definitions.get_Item(pName);
                if (def == null)
                {
                    var opts = new ExternalDefinitionCreationOptions(pName, SpecTypeId.Length);
                    def = group.Definitions.Create(opts);
                }

                // SharedParameterElement 가 이미 doc 에 바인딩되어 있는지 확인
                var existing = SharedParameterElement.Lookup(doc, ((ExternalDefinition)def).GUID);
                if (existing == null)
                {
                    existing = SharedParameterElement.Create(doc, (ExternalDefinition)def);
                }
                ids.Add(existing.Id);
            }
            return ids.Count == 5 ? ids : null;
        }

        // ============================================================
        // 3. RebarBarType 복제 ({원본}_전단철근)
        // ============================================================
        public static RebarBarType EnsureShearBarType(Document doc, double diameterMm, out string log)
        {
            int dInt = (int)Math.Round(diameterMm);
            string targetSuffix = "_전단철근";

            var allTypes = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType))
                .Cast<RebarBarType>().ToList();

            // 같은 직경의 일반 BarType 찾기 (원본 후보)
            string[] originalCandidates = { $"H{dInt}", $"D{dInt}", $"D{dInt} 400S", $"{dInt}" };
            RebarBarType original = null;
            foreach (var name in originalCandidates)
            {
                var hit = allTypes.FirstOrDefault(t => t.Name == name);
                if (hit != null) { original = hit; break; }
            }
            if (original == null)
            {
                // 직경으로만 매칭
                double targetFt = diameterMm / 304.8;
                original = allTypes.FirstOrDefault(t =>
                {
                    var p = t.get_Parameter(BuiltInParameter.REBAR_BAR_DIAMETER);
                    if (p == null) return false;
                    return Math.Abs(p.AsDouble() - targetFt) < 0.001;
                });
            }
            if (original == null)
            {
                log = $"D{dInt} 원본 RebarBarType 매칭 실패";
                return null;
            }

            string newName = original.Name + targetSuffix;
            var existing = allTypes.FirstOrDefault(t => t.Name == newName);
            if (existing != null)
            {
                log = $"기존 BarType '{newName}' 재사용";
                return existing;
            }

            try
            {
                var dup = (RebarBarType)original.Duplicate(newName);
                log = $"BarType '{newName}' 신규 복제 (원본: {original.Name})";
                return dup;
            }
            catch (Exception ex)
            {
                log = $"BarType 복제 실패: {ex.GetType().Name} — {ex.Message}";
                return null;
            }
        }
    }
}
