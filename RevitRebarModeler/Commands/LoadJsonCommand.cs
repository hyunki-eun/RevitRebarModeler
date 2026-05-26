using System;
using System.IO;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using Microsoft.Win32;

using Newtonsoft.Json;

using RevitRebarModeler.Models;

namespace RevitRebarModeler.Commands
{
    /// <summary>
    /// Civil3D 추출 JSON을 한 번만 로드해서 세션 캐시에 둔다.
    /// 이후 모든 Window/Command는 SessionCache.LoadedJson 만 읽고, 재선택은 이 명령으로만.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class LoadJsonCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "JSON 파일 (*.json)|*.json",
                Title = "Civil3D 내보내기 JSON 선택"
            };

            if (!string.IsNullOrEmpty(SessionCache.LoadedJsonPath) && File.Exists(SessionCache.LoadedJsonPath))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(SessionCache.LoadedJsonPath);
                dlg.FileName = Path.GetFileName(SessionCache.LoadedJsonPath);
            }

            if (dlg.ShowDialog() != true)
                return Result.Cancelled;

            try
            {
                string json = File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
                var data = JsonConvert.DeserializeObject<CivilExportData>(json);
                if (data == null)
                {
                    TaskDialog.Show("JSON 로드 실패", "JSON 파싱 결과가 비어있습니다.");
                    return Result.Failed;
                }

                SessionCache.LoadedJson = data;
                SessionCache.LoadedJsonPath = dlg.FileName;
                // 새 JSON 로드 시 이전에 캐싱된 도면별 설정/CTC는 무효 → 초기화
                SessionCache.LongitudinalSettings = null;
                SessionCache.TransverseCtcMap = null;

                int structureCount = data.StructureRegions?.Count ?? 0;
                int transverseCount = data.TransverseRebars?.Count ?? 0;

                TaskDialog.Show("Civil3D JSON 불러오기",
                    $"파일: {Path.GetFileName(dlg.FileName)}\n" +
                    $"프로젝트: {data.ProjectName ?? "(이름 없음)"}\n\n" +
                    $"구조물 사이클: {structureCount}개\n" +
                    $"횡방향 철근: {transverseCount}개\n\n" +
                    $"이제 다른 명령들이 이 JSON을 자동으로 사용합니다.");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("JSON 로드 실패", $"파싱 오류:\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}
