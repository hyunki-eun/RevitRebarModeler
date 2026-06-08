using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using RevitRebarModeler.Models;

namespace RevitRebarModeler.Commands
{
    /// <summary>
    /// 모든 외부 명령의 공통 기반 클래스. Execute 진입 시 라이선스 가드를 중앙에서 호출하므로,
    /// 개별 명령이 가드 호출을 빠뜨려 무방비로 실행되는 일이 없도록 한다.
    /// 파생 명령은 Execute 대신 <see cref="Run"/> 를 구현한다.
    ///
    /// ※ [Transaction] 속성은 Revit이 구체 클래스에서 찾으므로 각 파생 클래스에 그대로 유지한다.
    /// </summary>
    public abstract class CommandBase : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!LicenseGuard.CheckOrBlock()) return Result.Cancelled;
            return Run(commandData, ref message, elements);
        }

        /// <summary>실제 명령 본문. 가드 통과 후 호출된다.</summary>
        protected abstract Result Run(ExternalCommandData commandData, ref string message, ElementSet elements);
    }
}
