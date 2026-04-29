using System;
using System.Reflection;

using Autodesk.Revit.UI;

using RevitRebarModeler.Models;

namespace RevitRebarModeler
{
    /// <summary>
    /// 세션 단위로 공유되는 데이터 캐시.
    /// - JSON 데이터: 한 번 로드한 JSON 을 여러 명령에서 공유.
    /// - 종방향 철근 설정: 전단철근 배치에서 단(段) 위치를 산출하기 위해 활용.
    /// </summary>
    public static class SessionCache
    {
        public static CivilExportData LoadedJson { get; set; }
        public static string LoadedJsonPath { get; set; }

        /// <summary>
        /// 종방향 철근 배치 시 사용된 구조도별 설정.
        /// 전단철근이 단(段) = 종철근 샘플 포인트 위치를 그대로 재사용하기 위해 저장.
        /// </summary>
        public static System.Collections.Generic.Dictionary<string, UI.LongitudinalSheetSetting> LongitudinalSettings { get; set; }

        /// <summary>
        /// 횡방향 철근 배치 시 사용된 구조도별 CTC (mm).
        /// 전단철근이 횡방향 N단의 Z 위치 = (N-1) × CTC/2 로 산출하기 위해 저장.
        /// </summary>
        public static System.Collections.Generic.Dictionary<string, double> TransverseCtcMap { get; set; }
    }

    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                string tabName = "지반터널부";
                try { app.CreateRibbonTab(tabName); }
                catch { /* 이미 존재 */ }

                var panel = app.CreateRibbonPanel(tabName, "구조물");
                string dllPath = Assembly.GetExecutingAssembly().Location;

                var btnCreate = new PushButtonData(
                    "cmdCreateStructure",
                    "구조물 생성\n(JSON)",
                    dllPath,
                    "RevitRebarModeler.Commands.CreateStructureCommand")
                {
                    ToolTip = "Civil3D JSON에서 구조물 영역을 읽어 구조 프레임 패밀리로 생성합니다."
                };
                panel.AddItem(btnCreate);

                var btnTransverseRebar = new PushButtonData(
                    "cmdTransverseRebar",
                    "횡방향 철근\n배치",
                    dllPath,
                    "RevitRebarModeler.Commands.CreateTransverseRebarCommand")
                {
                    ToolTip = "Civil3D JSON의 TransverseRebars 데이터로 횡방향 철근을 배치합니다."
                };
                panel.AddItem(btnTransverseRebar);

                var btnLongitudinalRebar = new PushButtonData(
                    "cmdLongitudinalRebar",
                    "종방향 철근\n배치",
                    dllPath,
                    "RevitRebarModeler.Commands.CreateLongitudinalRebarCommand")
                {
                    ToolTip = "Cycle1 횡철근 polyline을 기준으로 CTC 간격으로 종방향 철근을 생성합니다."
                };
                panel.AddItem(btnLongitudinalRebar);

                var btnShearRebar = new PushButtonData(
                    "cmdShearRebar",
                    "전단철근\n배치",
                    dllPath,
                    "RevitRebarModeler.Commands.CreateShearRebarCommand")
                {
                    ToolTip = "횡철근/종철근 데이터를 기반으로 묶음 단위 U자형 전단철근을 배치합니다."
                };
                panel.AddItem(btnShearRebar);

                var btnPreviewCurves = new PushButtonData(
                    "cmdPreviewRebarCurves",
                    "철근\n미리보기",
                    dllPath,
                    "RevitRebarModeler.Commands.PreviewRebarCurvesCommand")
                {
                    ToolTip = "Rebar 생성 없이 철근의 Line/Arc 커브 라인을 DirectShape로 표시하여 터널 외곽 좌표를 검증합니다."
                };
                panel.AddItem(btnPreviewCurves);

                var btnExportRebars = new PushButtonData(
                    "cmdExportRebarsToJson",
                    "철근 → JSON\n추출",
                    dllPath,
                    "RevitRebarModeler.Commands.ExportRebarsToJsonCommand")
                {
                    ToolTip = "현재 문서의 종/횡/전단 철근을 Mark 패턴으로 분류해 JSON으로 추출합니다 (검증/비교용)."
                };
                panel.AddItem(btnExportRebars);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("애드인 초기화 오류", $"{ex.Message}\n\n{ex.StackTrace}");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication app)
        {
            return Result.Succeeded;
        }
    }
}