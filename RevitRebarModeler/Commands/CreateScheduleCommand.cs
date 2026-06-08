using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using RevitRebarModeler.Models;

namespace RevitRebarModeler.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class CreateScheduleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!LicenseGuard.CheckOrBlock()) return Result.Cancelled;

            var doc = commandData.Application.ActiveUIDocument.Document;

            // 모델에 Rebar 요소가 하나도 없으면 즉시 안내
            var rebarCount = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Structure.Rebar)).GetElementCount();
            if (rebarCount == 0)
            {
                TaskDialog.Show("일람표 생성 불가",
                    "현재 모델에 보강철근(Rebar) 요소가 없습니다.\n" +
                    "횡/종/전단 철근을 먼저 배치한 후 다시 실행하세요.");
                return Result.Cancelled;
            }

            // 1단계: 데이터 먼저 수집 (할증 기본 3.0%로 계산)
            const double defaultSurcharge = 3.0;
            var rows = RebarScheduleCollector.Collect(
                doc, defaultSurcharge,
                SessionCache.TransverseCtcMap,
                out var stats);
            // 해설 컬럼에 시트별 PatternName을 함께 표기하기 위해 (구조도 → 패턴) 맵 구성.
            // JSON 미로드 세션이면 맵이 비어 기존 해설만 표기됨(그레이스풀 폴백).
            var patternMap = RebarScheduleCollector.BuildPatternMap(SessionCache.LoadedJson);
            var rowsDetailed = RebarScheduleCollector.CollectDetailed(
                doc, defaultSurcharge,
                SessionCache.TransverseCtcMap,
                out _,
                patternMap);

            if (rows.Count == 0)
            {
                TaskDialog.Show("일람표 생성 불가",
                    $"분류 가능한 Rebar가 없습니다.\n\n" +
                    $"• 총 Rebar: {stats.TotalRebars}개\n" +
                    $"• Mark 미매칭: {stats.Unmatched}개\n" +
                    $"• 직경 추출 실패: {stats.DiameterUnknown}개\n" +
                    $"• 길이 0: {stats.LengthZero}개\n" +
                    $"• 길이 계산 오류: {stats.LengthError}개\n" +
                    $"• Mark 숫자 파싱 오류: {stats.MarkParseError}개\n\n" +
                    "Mark 패턴이 '구조도(N)_...' 형식이어야 합니다.");
                return Result.Cancelled;
            }

            // 2단계: 윈도우에 데이터 표시 (3개 탭 미리보기) → 사용자 확인 후 추출
            var window = new UI.ScheduleWindow(doc, rows, rowsDetailed, SessionCache.TransverseCtcMap);
            if (window.ShowDialog() != true) return Result.Cancelled;

            // 윈도우에서 사용자가 수정한 할증율로 데이터가 이미 갱신되어 있음
            // (Rows1, RowsDetailed의 SurchargePercent / 할증중량 컬럼)

            string excelPath = null;
            string excelError = null;
            if (window.ExportExcel)
            {
                try
                {
                    string projectName = SessionCache.LoadedJson?.ProjectName;
                    if (string.IsNullOrEmpty(projectName))
                        projectName = doc.Title;

                    ScheduleExcelExporter.Export(window.ExcelPath, rows, rowsDetailed, projectName);
                    excelPath = window.ExcelPath;
                }
                catch (Exception ex)
                {
                    excelError = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            List<ElementId> scheduleIds = null;
            string scheduleError = null;
            int populatedCount = 0;
            int populatedDetailedCount = 0;
            if (window.CreateRevitSchedule)
            {
                var app = commandData.Application.Application;
                using (var tr = new Transaction(doc, "보강철근 일람표 생성"))
                {
                    try
                    {
                        tr.Start();
                        // 1. Shared Parameter 정의/바인딩 (이미 있으면 스킵) — 일람표1/2/3 전부
                        RebarScheduleParameters.EnsureBound(app, doc);
                        // 2. 각 Rebar에 그룹 계산값 채우기
                        populatedCount = RebarSchedulePopulator.Populate(doc, rows);
                        populatedDetailedCount = RebarSchedulePopulator.PopulateDetailed(doc, rowsDetailed);
                        // 3. ViewSchedule 1/2/3 생성
                        scheduleIds = ScheduleRevitExporter.CreateAllOrReplace(doc);
                        tr.Commit();
                    }
                    catch (Exception ex)
                    {
                        if (tr.HasStarted() && !tr.HasEnded()) tr.RollBack();
                        scheduleError = $"{ex.GetType().Name}: {ex.Message}";
                    }
                }

                // 첫 번째 일람표(일람표1)를 활성 뷰로 표시
                if (scheduleIds != null && scheduleIds.Count > 0)
                {
                    try { commandData.Application.ActiveUIDocument.ActiveView = doc.GetElement(scheduleIds[0]) as View; }
                    catch { }
                }
            }

            // 결과 다이얼로그
            string msg =
                $"일람표 출력 완료\n\n" +
                $"  · 분류된 Rebar: {stats.Matched}개\n" +
                $"  · 행 수: {rows.Count}개\n" +
                $"  · 구조도: {rows.Select(r => r.StructureKey).Distinct().Count()}개\n" +
                $"  · 총 본수: {rows.Sum(r => r.Count)}개\n" +
                $"  · 총 중량(할증): {rows.Sum(r => r.WeightWithSurchargeKg):N2} kg";

            if (stats.Unmatched > 0 || stats.DiameterUnknown > 0 || stats.LengthZero > 0
                || stats.LengthError > 0 || stats.MarkParseError > 0)
            {
                msg += $"\n\n  스킵: Mark 미매칭 {stats.Unmatched}, 직경 추출 실패 {stats.DiameterUnknown}, 길이 0 {stats.LengthZero}";
                if (stats.LengthError > 0) msg += $", 길이 계산 오류 {stats.LengthError}";
                if (stats.MarkParseError > 0) msg += $", Mark 파싱 오류 {stats.MarkParseError}";
            }
            if (stats.UnknownDiameters.Count > 0)
            {
                msg += $"\n\n  ⚠ 단위중량 룩업 미수록 직경: {string.Join(", ", stats.UnknownDiameters)} (G=0 처리됨)";
            }

            if (window.ExportExcel)
            {
                if (excelPath != null)
                    msg += $"\n\nExcel 저장 완료:\n  {excelPath}";
                else
                    msg += $"\n\nExcel 저장 실패: {excelError}";
            }
            if (window.CreateRevitSchedule)
            {
                if (scheduleIds != null && scheduleIds.Count > 0)
                    msg += $"\n\nRevit 일람표 생성 완료 ({scheduleIds.Count}개):\n" +
                           $"  · {ScheduleRevitExporter.ScheduleName1}\n" +
                           $"  · {ScheduleRevitExporter.ScheduleName2}\n" +
                           $"  · {ScheduleRevitExporter.ScheduleName3}\n" +
                           $"  · Rebar 파라미터 채움: 일람표1 {populatedCount}개, 일람표2/3 {populatedDetailedCount}개";
                else
                    msg += $"\n\nRevit 일람표 생성 실패: {scheduleError}";
            }

            TaskDialog.Show("보강철근 수량 일람표", msg);

            // Excel 자동 열기 (선택)
            if (excelPath != null && File.Exists(excelPath))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = excelPath,
                        UseShellExecute = true
                    });
                }
                catch { /* Excel 미설치 등 */ }
            }

            return Result.Succeeded;
        }
    }
}
