using System;

using Autodesk.Revit.UI;

namespace RevitRebarModeler.Models
{
    /// <summary>
    /// 빌드에 박힌 만료일까지만 명령 실행을 허용. 각 IExternalCommand.Execute() 첫 줄에서
    /// CheckOrBlock() 을 호출해 false 면 Result.Cancelled 반환.
    /// </summary>
    internal static class LicenseGuard
    {
        // 만료일 (이날까지 사용 가능, 다음 날부터 차단)
        private static readonly DateTime ExpirationDate = new DateTime(2026, 6, 26);

        public static bool IsExpired => DateTime.Now.Date > ExpirationDate;

        public static bool CheckOrBlock()
        {
            if (!IsExpired) return true;

            TaskDialog.Show(
                "RevitRebarModeler 사용기간 만료",
                $"사용 가능 기간이 종료되었습니다.\n\n" +
                $"  · 만료일: {ExpirationDate:yyyy-MM-dd}\n" +
                $"  · 현재일: {DateTime.Now:yyyy-MM-dd}\n\n" +
                "최신 버전을 받아 다시 설치하시거나 담당자에게 문의해 주세요.");
            return false;
        }
    }
}
