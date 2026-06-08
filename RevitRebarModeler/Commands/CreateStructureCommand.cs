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
            if (!LicenseGuard.CheckOrBlock()) return Result.Cancelled;

            var uiApp = commandData.Application;
            var app = uiApp.Application;
            var doc = uiApp.ActiveUIDocument.Document;

            if (SessionCache.LoadedJson == null)
            {
                TaskDialog.Show("Civil3D JSON 필요",
                    "먼저 리본의 [Civil3D JSON 불러오기]를 실행하세요.");
                return Result.Cancelled;
            }

            var window = new UI.CreateStructureWindow();
            if (window.ShowDialog() != true)
                return Result.Cancelled;

            var selectedCycles = window.SelectedCycles;
            var depthByCycle = window.DepthByCycle ?? new Dictionary<string, double>();

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
                    double depthMm = depthByCycle.TryGetValue(cycle.CycleKey, out double d) ? d : 1000;
                    fullLog.Add($"\n========== [{cycle.CycleKey}] Region={cycle.RegionCount}개, depth={depthMm}mm ==========");
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
                        XYZ placementPoint = result.Item2;  // (0,0,0) — 패밀리 내부가 절대좌표

                        // Structural Framing 카테고리 + Level 없는 overload.
                        // placementPoint=(0,0,0)이라 워크플레인 잠금 영향 없음.
                        FamilyInstance instance = doc.Create.NewFamilyInstance(
                            placementPoint, symbol, StructuralType.NonStructural);

                        // depth는 마침표 소수점으로 기록해야 ParseDepthFromHost의 정규식/InvariantCulture 파싱과 round-trip이 맞음
                        string depthStr = depthMm.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        instance.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                            ?.Set($"{cycle.CycleKey}|depth={depthStr}");

                        // Structural Framing 카테고리에서는 NewFamilyInstance 점 좌표가 무시되고
                        // (0,0,0)에 배치되는 케이스가 있어, 실제 위치를 보고 명시적으로 이동시킨다.
                        XYZ actual = (instance.Location as LocationPoint)?.Point ?? XYZ.Zero;
                        XYZ moveDelta = placementPoint - actual;
                        if (moveDelta.GetLength() > 1e-6)
                        {
                            try
                            {
                                ElementTransformUtils.MoveElement(doc, instance.Id, moveDelta);
                                actual = (instance.Location as LocationPoint)?.Point ?? placementPoint;
                            }
                            catch (Exception mvEx)
                            {
                                fullLog.Add($"  [WARN] {cycle.CycleKey}: MoveElement 실패 - {mvEx.Message}");
                            }
                        }

                        // 배치 검증용 로그: 예상 vs 실제 LocationPoint
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

                var depthSummary = depthByCycle.Count > 0
                    ? string.Join(", ", depthByCycle.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value:N0}mm"))
                    : "(없음)";

                var lines = new List<string>
                {
                    $"=== 구조 프레임 생성 로그 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===",
                    $"성공: {created} / 실패: {failed} / 선택 사이클: {selectedCycles.Count}",
                    $"돌출 깊이 (구조도별): {depthSummary}",
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
            string depthMsg;
            if (depthByCycle.Count == 0)
                depthMsg = "(없음)";
            else if (depthByCycle.Values.Distinct().Count() == 1)
                depthMsg = $"{depthByCycle.Values.First():N0}mm";
            else
                depthMsg = string.Join(", ", depthByCycle.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value:N0}mm"));

            string msg = $"구조물 생성 완료\n\n" +
                         $"  생성: {created}개  /  실패: {failed}개\n" +
                         $"  돌출 길이: {depthMsg}";

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
