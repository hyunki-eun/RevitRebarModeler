using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

using RevitRebarModeler.Models;

namespace RevitRebarModeler.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class CreateStructureCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiApp = commandData.Application;
            var app = uiApp.Application;
            var doc = uiApp.ActiveUIDocument.Document;

            var window = new UI.CreateStructureWindow();
            if (window.ShowDialog() != true)
                return Result.Cancelled;

            var selectedCycles = window.SelectedCycles;
            double depthMm = window.DepthMm;

            // 세션 GlobalOrigin 초기화 및 JSON 기반 자동 설정
            Civil3DCoordinate.ResetGlobalOrigin();
            Civil3DCoordinate.AutoSetGlobalOrigin(window.LoadedData);

            int created = 0;
            int failed = 0;
            var failedNames = new List<string>();
            var placementLog = new List<string>();
            var fullLog = new List<string>();

            var creator = new StructureFamilyCreator(app);

            using (var tr = new Transaction(doc, "구조 프레임 생성"))
            {
                tr.Start();

                foreach (var cycle in selectedCycles)
                {
                    fullLog.Add($"\n========== [{cycle.CycleKey}] Region={cycle.RegionCount}개 ==========");
                    try
                    {
                        var result = creator.CreateFamily(doc, cycle, depthMm);

                        foreach (var err in creator.Errors)
                        {
                            fullLog.Add($"  [{cycle.CycleKey}] {err}");
                            if (err.StartsWith("[WARN]") || err.StartsWith("[ERROR]") || err.StartsWith("[FATAL]"))
                                failedNames.Add($"  [{cycle.CycleKey}] {err}");
                        }

                        if (result == null || result.Item1 == null)
                        {
                            string reason = $"{cycle.CycleKey}: 패밀리 생성 실패 (Extrusion 0개)";
                            failedNames.Add(reason);
                            fullLog.Add($"  >>> {reason}");
                            failed++;
                            continue;
                        }

                        FamilySymbol symbol = result.Item1;
                        XYZ placementPoint = result.Item2;

                        // Level 인자 없는 overload — 절대좌표 그대로 배치 (Level 고도 오프셋 없음)
                        FamilyInstance instance = doc.Create.NewFamilyInstance(
                            placementPoint, symbol, StructuralType.NonStructural);

                        instance.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                            ?.Set($"{cycle.CycleKey}|depth={depthMm}");

                        // 배치 검증용 로그: 예상 vs 실제 LocationPoint
                        XYZ actual = (instance.Location as LocationPoint)?.Point ?? XYZ.Zero;
                        double dx = actual.X - placementPoint.X;
                        double dy = actual.Y - placementPoint.Y;
                        double dz = actual.Z - placementPoint.Z;
                        string line =
                            $"[{cycle.CycleKey}] expected=({placementPoint.X:F4},{placementPoint.Y:F4},{placementPoint.Z:F4}) " +
                            $"actual=({actual.X:F4},{actual.Y:F4},{actual.Z:F4}) Δ=({dx:E2},{dy:E2},{dz:E2}) ft";
                        placementLog.Add(line);
                        fullLog.Add("  [OK] " + line);

                        created++;
                    }
                    catch (Exception ex)
                    {
                        string reason = $"{cycle.CycleKey}: {ex.GetType().Name} - {ex.Message}";
                        failedNames.Add(reason);
                        fullLog.Add($"  [EXCEPTION] {reason}\n{ex.StackTrace}");
                        failed++;
                    }
                }

                tr.Commit();
            }

            // ── 전체 로그 파일 저장 (디테일은 모두 로그로) ──
            string logPath = null;
            try
            {
                string logDir = Path.Combine(Path.GetTempPath(), "RevitRebarModeler", "Logs");
                Directory.CreateDirectory(logDir);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                logPath = Path.Combine(logDir, $"CreateStructure_{stamp}.log");

                var lines = new List<string>
                {
                    $"=== 구조 프레임 생성 로그 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===",
                    $"성공: {created} / 실패: {failed} / 선택 사이클: {selectedCycles.Count}",
                    $"돌출 깊이: {depthMm}mm",
                    $"GlobalOrigin: ({Civil3DCoordinate.GlobalOriginXMm:F1}, {Civil3DCoordinate.GlobalOriginYMm:F1}) mm [IsSet={Civil3DCoordinate.IsSet}]",
                    ""
                };
                lines.AddRange(fullLog);
                if (placementLog.Count > 0)
                {
                    lines.Add("");
                    lines.Add("── 배치 검증 ──");
                    lines.AddRange(placementLog);
                }
                if (failedNames.Count > 0)
                {
                    lines.Add("");
                    lines.Add("── 실패/경고 목록 ──");
                    lines.AddRange(failedNames);
                }
                File.WriteAllLines(logPath, lines);
            }
            catch (Exception ex)
            {
                logPath = $"(로그 저장 실패: {ex.Message})";
            }

            // ── 사용자 다이얼로그: 핵심 결과만 ──
            string msg = $"구조물 생성 완료\n\n" +
                         $"  생성: {created}개  /  실패: {failed}개\n" +
                         $"  돌출 길이: {depthMm:N0}mm";

            if (failedNames.Count > 0)
            {
                msg += "\n\n실패 항목:";
                foreach (var name in failedNames.Take(10))
                    msg += $"\n  · {name}";
                if (failedNames.Count > 10)
                    msg += $"\n  · ... 외 {failedNames.Count - 10}건";
            }

            msg += $"\n\n자세한 로그: {logPath}";

            TaskDialog.Show("구조물 생성", msg);

            return Result.Succeeded;
        }
    }
}
