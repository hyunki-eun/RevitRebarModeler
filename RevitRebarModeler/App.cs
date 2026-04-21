using System;
using System.Reflection;

using Autodesk.Revit.UI;

namespace RevitRebarModeler
{
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

                var btnPreviewCurves = new PushButtonData(
                    "cmdPreviewRebarCurves",
                    "커브\n미리보기",
                    dllPath,
                    "RevitRebarModeler.Commands.PreviewRebarCurvesCommand")
                {
                    ToolTip = "Rebar 생성 없이 Line/Arc 커브만 DirectShape로 표시하여 좌표 공간을 검증합니다."
                };
                panel.AddItem(btnPreviewCurves);

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