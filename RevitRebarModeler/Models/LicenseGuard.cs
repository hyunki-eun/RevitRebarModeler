using System;

using Microsoft.Win32;

using Autodesk.Revit.UI;

namespace RevitRebarModeler.Models
{
    /// <summary>
    /// 빌드에 박힌 만료일까지만 명령 실행을 허용.
    /// 명령 실행 가드는 <see cref="RevitRebarModeler.Commands.CommandBase"/> 가 중앙에서 호출한다.
    ///
    /// 시계 롤백 방어: 최근 관측 날짜(high-water mark)를 HKCU 레지스트리에 난독화해 저장하고,
    /// 현재 날짜가 그보다 과거이면 시스템 시계를 되돌린 것으로 보고 차단한다.
    /// 모든 날짜 비교는 UTC 기준(타임존 조작 방지).
    /// </summary>
    internal static class LicenseGuard
    {
        // 만료일 (이날까지 사용 가능, 다음 날부터 차단) — UTC 날짜 기준
        private static readonly DateTime ExpirationDate = new DateTime(2026, 6, 26);

        // 레지스트리 위치 / 값 이름 (의도가 드러나지 않게 일반적인 이름 사용)
        private const string RegSubKey = @"Software\IBIM\RevitRebarModeler";
        private const string RegValueName = "State";
        // 난독화용 XOR 키 (평문 날짜 노출 방지 — 강력한 보호는 아님)
        private const long ObfuscationKey = 0x5A3C_7E91_24B6_0F8DL;

        public static bool IsExpired => DateTime.UtcNow.Date > ExpirationDate;

        /// <summary>
        /// 사용 가능 여부 확인. 만료 또는 시계 롤백 감지 시 안내 후 false.
        /// 레지스트리 접근 실패는 치명적이지 않게 처리(롤백 탐지만 생략, 만료 검사는 유지).
        /// </summary>
        public static bool CheckOrBlock()
        {
            DateTime today = DateTime.UtcNow.Date;

            // 1) 시계 롤백 탐지 (레지스트리 high-water mark)
            DateTime stored = ReadHighWater();
            if (stored != DateTime.MinValue && today < stored)
            {
                TaskDialog.Show(
                    "RevitRebarModeler",
                    "시스템 시간이 비정상적으로 과거로 설정되어 있어 실행할 수 없습니다.\n\n" +
                    "PC 날짜/시간을 올바르게 맞춘 뒤 다시 시도해 주세요.");
                return false;
            }

            // 최근 관측일 갱신 (항상 증가 방향으로만)
            if (today > stored) WriteHighWater(today);

            // 2) 만료 검사
            if (today > ExpirationDate)
            {
                TaskDialog.Show(
                    "RevitRebarModeler 사용기간 만료",
                    $"사용 가능 기간이 종료되었습니다.\n\n" +
                    $"  · 만료일: {ExpirationDate:yyyy-MM-dd}\n" +
                    $"  · 현재일: {today:yyyy-MM-dd} (UTC)\n\n" +
                    "최신 버전을 받아 다시 설치하시거나 담당자에게 문의해 주세요.");
                return false;
            }

            return true;
        }

        private static DateTime ReadHighWater()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegSubKey))
                {
                    var raw = key?.GetValue(RegValueName) as string;
                    if (string.IsNullOrEmpty(raw)) return DateTime.MinValue;
                    if (!long.TryParse(raw, out long obf)) return DateTime.MinValue;
                    long ticks = obf ^ ObfuscationKey;
                    if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                        return DateTime.MinValue;
                    return new DateTime(ticks, DateTimeKind.Utc).Date;
                }
            }
            catch
            {
                // 레지스트리 접근 실패 시 롤백 탐지 생략 (만료 검사는 계속 동작)
                return DateTime.MinValue;
            }
        }

        private static void WriteHighWater(DateTime date)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegSubKey))
                {
                    long obf = date.Date.Ticks ^ ObfuscationKey;
                    key?.SetValue(RegValueName, obf.ToString(), RegistryValueKind.String);
                }
            }
            catch
            {
                // 쓰기 실패는 무시 (다음 실행에서 재시도)
            }
        }
    }
}
