using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using ClosedXML.Excel;

namespace RevitRebarModeler.Models
{
    /// <summary>
    /// RebarScheduleRow 목록을 Excel(.xlsx) 파일로 출력.
    /// 일람표 양식:
    ///   - 첫 컬럼 "구 분": TYPE 헤더 행 + 들여쓰기 데이터 행 (TYPE1 ~ TYPEN)
    ///   - 두 번째 컬럼 "직경": 횡/종 → "D16", 전단 → "D13_전단" 으로 종류 통합 표기
    ///   - 이후 컬럼: 본수 / 단위길이 / 총길이 / 단위중량 / 총중량 / 할증 / 할증중량
    ///   - 마지막에 직경별 합계 + 총합.
    /// 컬럼 총 10개 (ColCount).
    /// </summary>
    public static class ScheduleExcelExporter
    {
        private const int ColCount = 10;

        /// <summary>
        /// 일람표 1, 2, 3을 하나의 워크북에 3개 시트로 출력.
        /// 시트 1: 일람표 1 — TYPE×직경 그룹 (RebarScheduleRow)
        /// 시트 2: 일람표 2 — 마크 인터리브 (RebarScheduleRowDetailed)
        /// 시트 3: 일람표 3 — Cycle 그룹 + 해설 컬럼 (RebarScheduleRowDetailed)
        /// </summary>
        public static void Export(
            string filePath,
            List<RebarScheduleRow> rows1,
            List<RebarScheduleRowDetailed> rowsDetailed,
            string projectName = null)
        {
            // 대상 파일이 Excel 등에서 열려 있으면 먼저 친절한 메시지로 알린다.
            if (System.IO.File.Exists(filePath) && IsFileLocked(filePath))
                throw new System.IO.IOException(
                    $"대상 파일이 다른 프로그램(Excel 등)에서 열려 있어 저장할 수 없습니다.\n파일을 닫고 다시 시도해 주세요.\n\n{filePath}");

            // 임시 파일에 먼저 저장한 뒤 교체(atomic) → 도중 실패해도 기존 파일/반쪽 파일을 남기지 않음.
            string tempPath = filePath + ".tmp";
            using (var wb = new XLWorkbook())
            {
                WriteSheet1(wb, rows1, projectName);
                WriteSheet2(wb, rowsDetailed);
                WriteSheet3(wb, rowsDetailed);
                wb.SaveAs(tempPath);
            }

            if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath);
            System.IO.File.Move(tempPath, filePath);
        }

        /// <summary>파일이 다른 프로세스에 의해 잠겨(쓰기 불가) 있는지 검사.</summary>
        private static bool IsFileLocked(string path)
        {
            try
            {
                using (System.IO.File.Open(path, System.IO.FileMode.Open,
                    System.IO.FileAccess.ReadWrite, System.IO.FileShare.None))
                {
                    return false;
                }
            }
            catch (System.IO.IOException)
            {
                return true;
            }
        }

        private static void WriteSheet1(XLWorkbook wb, List<RebarScheduleRow> rows, string projectName)
        {
            {
                var ws = wb.Worksheets.Add("일람표1");

                int r = 1;

                // 제목
                ws.Cell(r, 1).Value = "보강철근 수량 비교";
                ws.Range(r, 1, r, ColCount).Merge();
                ws.Cell(r, 1).Style.Font.Bold = true;
                ws.Cell(r, 1).Style.Font.FontSize = 14;
                ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                r++;

                if (!string.IsNullOrEmpty(projectName))
                {
                    ws.Cell(r, 1).Value = $"프로젝트: {projectName}";
                    ws.Range(r, 1, r, ColCount).Merge();
                    ws.Cell(r, 1).Style.Font.FontSize = 10;
                    r++;
                }
                r++;

                // 헤더
                string[] headers = {
                    "구 분",
                    "직경",
                    "전체 철근 길이 C(m)",
                    "수량 D",
                    "1m당 철근개수 E",
                    "철근 총길이 F(m)",
                    "단위중량 G(kg/m)",
                    "총중량 H(kg)",
                    "할증 I(%)",
                    "할증중량 J(kg)"
                };
                for (int i = 0; i < headers.Length; i++)
                {
                    var cell = ws.Cell(r, i + 1);
                    cell.Value = headers[i];
                    cell.Style.Font.Bold = true;
                    cell.Style.Fill.BackgroundColor = XLColor.LightGray;
                    cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                }
                r++;

                // TYPE 그룹화 → 헤더 행 + 들여쓰기 데이터 행
                var byStructure = rows
                    .GroupBy(x => x.StructureKey)
                    .OrderBy(g => StructureSortKey(g.Key))
                    .ToList();

                foreach (var grp in byStructure)
                {
                    string typeLabel = ToTypeLabel(grp.Key);

                    // TYPE 헤더 행 (전체 컬럼 병합, Bold, 배경색)
                    ws.Cell(r, 1).Value = typeLabel;
                    ws.Range(r, 1, r, ColCount).Merge();
                    ws.Cell(r, 1).Style.Font.Bold = true;
                    ws.Cell(r, 1).Style.Fill.BackgroundColor = XLColor.LightSteelBlue;
                    for (int c = 1; c <= ColCount; c++)
                        ws.Cell(r, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    r++;

                    // 데이터 행 (들여쓰기)
                    foreach (var row in grp.OrderBy(x => KindSortOrder(x.Kind))
                                          .ThenByDescending(x => x.DiameterMm))
                    {
                        ws.Cell(r, 1).Value = typeLabel;
                        ws.Cell(r, 1).Style.Alignment.Indent = 2;

                        ws.Cell(r, 2).Value = row.DiameterLabelWithSuffix;
                        ws.Cell(r, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                        ws.Cell(r, 3).Value = row.TotalLengthMm / 1000.0;  // C 전체 철근 길이(m) = 그룹 총
                        ws.Cell(r, 4).Value = row.Count;                    // D 수량
                        if (row.SetCount > 0) ws.Cell(r, 5).Value = row.SetCount; // E 1m당 철근개수
                        ws.Cell(r, 6).Value = row.RebarTotalLengthM;        // F 철근 총길이(m) = C × E
                        ws.Cell(r, 7).Value = row.UnitWeightKgPerM;         // G 단위중량
                        ws.Cell(r, 8).Value = row.TotalWeightKg;            // H 총중량(kg) = F × G
                        ws.Cell(r, 9).Value = row.SurchargePercent;         // I 할증
                        ws.Cell(r, 10).Value = row.WeightWithSurchargeKg;   // J 할증중량 = H × (1+I/100)

                        ws.Cell(r, 3).Style.NumberFormat.Format = "#,##0.000";
                        ws.Cell(r, 5).Style.NumberFormat.Format = "#,##0.00";
                        ws.Cell(r, 6).Style.NumberFormat.Format = "#,##0.000";
                        ws.Cell(r, 7).Style.NumberFormat.Format = "0.000";
                        ws.Cell(r, 8).Style.NumberFormat.Format = "#,##0.00";
                        ws.Cell(r, 9).Style.NumberFormat.Format = "0.0";
                        ws.Cell(r, 10).Style.NumberFormat.Format = "#,##0.00";

                        for (int c = 1; c <= ColCount; c++)
                            ws.Cell(r, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                        r++;
                    }
                }

                // 직경별 합계
                if (rows.Count > 0)
                {
                    r++;
                    ws.Cell(r, 1).Value = "직경별 합계 (할증 포함)";
                    ws.Range(r, 1, r, ColCount).Merge();
                    ws.Cell(r, 1).Style.Font.Bold = true;
                    ws.Cell(r, 1).Style.Fill.BackgroundColor = XLColor.LightYellow;
                    r++;

                    // 직경별 합계 헤더 (데이터 행과 컬럼 위치 일치)
                    ws.Cell(r, 2).Value = "직경";
                    ws.Cell(r, 4).Value = "수량";
                    ws.Cell(r, 6).Value = "철근 총길이(m)";
                    ws.Cell(r, 8).Value = "총중량(kg)";
                    ws.Cell(r, 10).Value = "할증중량(kg)";
                    for (int c = 1; c <= ColCount; c++)
                    {
                        ws.Cell(r, c).Style.Font.Bold = true;
                        ws.Cell(r, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    }
                    r++;

                    var byDiameter = rows
                        .GroupBy(x => x.DiameterMm)
                        .OrderByDescending(g => g.Key);
                    foreach (var g in byDiameter)
                    {
                        ws.Cell(r, 2).Value = $"D{g.Key}";
                        ws.Cell(r, 4).Value = g.Sum(x => x.Count);
                        ws.Cell(r, 6).Value = g.Sum(x => x.RebarTotalLengthM);  // 철근 총길이 합
                        ws.Cell(r, 8).Value = g.Sum(x => x.TotalWeightKg);
                        ws.Cell(r, 10).Value = g.Sum(x => x.WeightWithSurchargeKg);

                        ws.Cell(r, 6).Style.NumberFormat.Format = "#,##0.000";
                        ws.Cell(r, 8).Style.NumberFormat.Format = "#,##0.00";
                        ws.Cell(r, 10).Style.NumberFormat.Format = "#,##0.00";
                        r++;
                    }

                    // 총합
                    r++;
                    ws.Cell(r, 1).Value = "총합";
                    ws.Cell(r, 4).Value = rows.Sum(x => x.Count);
                    ws.Cell(r, 6).Value = rows.Sum(x => x.RebarTotalLengthM);
                    ws.Cell(r, 8).Value = rows.Sum(x => x.TotalWeightKg);
                    ws.Cell(r, 10).Value = rows.Sum(x => x.WeightWithSurchargeKg);
                    ws.Cell(r, 6).Style.NumberFormat.Format = "#,##0.000";
                    ws.Cell(r, 8).Style.NumberFormat.Format = "#,##0.00";
                    ws.Cell(r, 10).Style.NumberFormat.Format = "#,##0.00";
                    for (int c = 1; c <= ColCount; c++)
                    {
                        ws.Cell(r, c).Style.Font.Bold = true;
                        ws.Cell(r, c).Style.Fill.BackgroundColor = XLColor.LightCyan;
                    }
                }

                ws.Columns().AdjustToContents();
                ws.Column(1).Width = 18;  // "구 분" 들여쓰기 여유
                ws.Column(2).Width = 14;
            }
        }

        // ───────────────────────────────────────────────────────────────────
        // 시트 2: 마크 인터리브 (A1, A1-1, A2, A2-1, ..., B1, T1, T2, T3)
        // 컬럼: 01_타입 | 일람표 마크 | 유형 | 전체 철근 길이 | 철근 길이 | 수량
        //       | 1m당 철근개수 | 철근 총길이 | 단위중량 | 총중량(t) | 05_% | 총중량_ADD(t)
        // 총 12 컬럼
        // ───────────────────────────────────────────────────────────────────
        private static void WriteSheet2(XLWorkbook wb, List<RebarScheduleRowDetailed> rows)
        {
            const int Cols = 12;
            var ws = wb.Worksheets.Add("일람표2");
            int r = 1;

            // 헤더
            string[] headers = {
                "01_타입", "일람표 마크", "유형",
                "전체 철근 길이", "철근 길이", "수량",
                "1m당 철근개수", "철근 총길이",
                "단위중량", "총중량",
                "05_%", "총중량_ADD %"
            };
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cell(r, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.LightGray;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            }
            r++;

            // TYPE 그룹별
            var byType = rows.GroupBy(x => x.StructureKey)
                             .OrderBy(g => StructureSortKey(g.Key))
                             .ToList();
            foreach (var grp in byType)
            {
                string typeLabel = ToTypeLabel(grp.Key);
                // TYPE 헤더 행 (병합)
                ws.Cell(r, 1).Value = typeLabel;
                ws.Range(r, 1, r, Cols).Merge();
                ws.Cell(r, 1).Style.Font.Bold = true;
                ws.Cell(r, 1).Style.Fill.BackgroundColor = XLColor.LightSteelBlue;
                for (int c = 1; c <= Cols; c++)
                    ws.Cell(r, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                r++;

                // 마크 인터리브 정렬:
                //   횡: (MarkIndex, CycleNumber) — A1(CY1), A1-1(CY2), A2(CY1), A2-1(CY2), ...
                //   배력: 그 뒤
                //   전단: 마지막
                var transRows = grp.Where(x => x.Kind == RebarKind.Transverse)
                                   .OrderBy(x => x.MarkIndex)
                                   .ThenBy(x => x.CycleNumber)
                                   .ToList();
                var longiRows = grp.Where(x => x.Kind == RebarKind.Longitudinal)
                                   .OrderBy(x => x.MarkIndex).ToList();
                var shearRows = grp.Where(x => x.Kind == RebarKind.Shear)
                                   .OrderBy(x => x.MarkIndex).ToList();
                var ordered = transRows.Concat(longiRows).Concat(shearRows);

                foreach (var row in ordered)
                {
                    ws.Cell(r, 1).Value = typeLabel;
                    ws.Cell(r, 1).Style.Alignment.Indent = 2;

                    ws.Cell(r, 2).Value = row.MarkLabel;
                    ws.Cell(r, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    ws.Cell(r, 3).Value = row.DiameterLabel;
                    ws.Cell(r, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    ws.Cell(r, 4).Value = row.TotalLengthMm / 1000.0;     // 전체 철근 길이 (m)
                    // 철근 길이(한 본): 전단은 깊이별 편차가 커서 "최소 ~ 최대"(m), 그 외는 한 본 단일값
                    ws.Cell(r, 5).Value = row.UnitLengthText;
                    ws.Cell(r, 6).Value = row.Count;                       // 수량

                    if (row.OneMPerCount > 0) ws.Cell(r, 7).Value = row.OneMPerCount;
                    if (row.TotalLengthPerMm > 0) ws.Cell(r, 8).Value = row.TotalLengthPerMm / 1000.0;

                    ws.Cell(r, 9).Value = row.UnitWeightKgM;
                    ws.Cell(r, 10).Value = row.TotalWeightKg / 1000.0;     // t 단위
                    ws.Cell(r, 11).Value = row.SurchargePercent;
                    ws.Cell(r, 12).Value = row.SurchargeTotalKg / 1000.0; // t 단위

                    ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.000\" m\"";
                    ws.Cell(r, 5).Style.NumberFormat.Format = "#,##0.000\" m\"";
                    ws.Cell(r, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Cell(r, 7).Style.NumberFormat.Format = "0.00";
                    ws.Cell(r, 8).Style.NumberFormat.Format = "#,##0.000\" m\"";
                    ws.Cell(r, 9).Style.NumberFormat.Format = "0.000\" kg/m\"";
                    ws.Cell(r, 10).Style.NumberFormat.Format = "0.000\" t\"";
                    ws.Cell(r, 11).Style.NumberFormat.Format = "0";
                    ws.Cell(r, 12).Style.NumberFormat.Format = "0.000\" t\"";

                    for (int c = 1; c <= Cols; c++)
                        ws.Cell(r, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    r++;
                }
            }

            ws.Columns().AdjustToContents();
            ws.Column(1).Width = 18;
        }

        // ───────────────────────────────────────────────────────────────────
        // 시트 3: Cycle 그룹화 + 해설 컬럼
        // 컬럼: 01_타입 | 해설 | 일람표 마크 | 유형 | 철근 길이 | 수량 | 전체 철근 길이
        //       | 1m당 철근개수 | 철근 총길이 | 단위중량 | 총중량(t) | 05_% | 총중량_ADD(t)
        // 총 13 컬럼
        // 정렬: TYPE → CY1 → CY2 → 배력철근 → 전단철근
        // ───────────────────────────────────────────────────────────────────
        private static void WriteSheet3(XLWorkbook wb, List<RebarScheduleRowDetailed> rows)
        {
            const int Cols = 13;
            var ws = wb.Worksheets.Add("일람표3");
            int r = 1;

            string[] headers = {
                "01_타입", "해설", "일람표 마크", "유형",
                "철근 길이", "수량", "전체 철근 길이",
                "1m당 철근개수", "철근 총길이",
                "단위중량", "총중량",
                "05_%", "총중량_ADD %"
            };
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cell(r, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.LightGray;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            }
            r++;

            var byType = rows.GroupBy(x => x.StructureKey)
                             .OrderBy(g => StructureSortKey(g.Key))
                             .ToList();
            foreach (var grp in byType)
            {
                string typeLabel = ToTypeLabel(grp.Key);
                ws.Cell(r, 1).Value = typeLabel;
                ws.Range(r, 1, r, Cols).Merge();
                ws.Cell(r, 1).Style.Font.Bold = true;
                ws.Cell(r, 1).Style.Fill.BackgroundColor = XLColor.LightSteelBlue;
                for (int c = 1; c <= Cols; c++)
                    ws.Cell(r, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                r++;

                // 서브그룹별로 묶기: CY1 → CY2 → 배력철근 → 전단철근
                var subGroups = grp
                    .GroupBy(x => x.SubGroupLabel)
                    .OrderBy(g => SubGroupSortOrder(g.Key, g.First().Kind, g.First().CycleNumber));
                foreach (var sg in subGroups)
                {
                    // 서브그룹 헤더 행
                    ws.Cell(r, 1).Value = typeLabel;
                    ws.Cell(r, 1).Style.Alignment.Indent = 1;
                    ws.Cell(r, 2).Value = sg.Key;
                    ws.Cell(r, 2).Style.Font.Bold = true;
                    ws.Range(r, 2, r, Cols).Merge();
                    ws.Cell(r, 2).Style.Fill.BackgroundColor = XLColor.LightCyan;
                    for (int c = 1; c <= Cols; c++)
                        ws.Cell(r, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    r++;

                    // 데이터 행
                    foreach (var row in sg.OrderBy(x => x.MarkIndex))
                    {
                        ws.Cell(r, 1).Value = typeLabel;
                        ws.Cell(r, 1).Style.Alignment.Indent = 2;

                        ws.Cell(r, 2).Value = sg.Key;
                        ws.Cell(r, 2).Style.Alignment.Indent = 1;

                        ws.Cell(r, 3).Value = row.MarkLabel;
                        ws.Cell(r, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                        ws.Cell(r, 4).Value = row.DiameterLabel;
                        ws.Cell(r, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                        // 철근 길이(한 본): 전단은 "최소 ~ 최대"(m), 그 외는 한 본 단일값
                        ws.Cell(r, 5).Value = row.UnitLengthText;
                        ws.Cell(r, 6).Value = row.Count;                   // 수량
                        ws.Cell(r, 7).Value = row.TotalLengthMm / 1000.0;  // 전체 철근 길이

                        if (row.OneMPerCount > 0) ws.Cell(r, 8).Value = row.OneMPerCount;
                        if (row.TotalLengthPerMm > 0) ws.Cell(r, 9).Value = row.TotalLengthPerMm / 1000.0;

                        ws.Cell(r, 10).Value = row.UnitWeightKgM;
                        ws.Cell(r, 11).Value = row.TotalWeightKg / 1000.0;
                        ws.Cell(r, 12).Value = row.SurchargePercent;
                        ws.Cell(r, 13).Value = row.SurchargeTotalKg / 1000.0;

                        ws.Cell(r, 5).Style.NumberFormat.Format = "#,##0.000\" m\"";
                        ws.Cell(r, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0.000\" m\"";
                        ws.Cell(r, 8).Style.NumberFormat.Format = "0.00";
                        ws.Cell(r, 9).Style.NumberFormat.Format = "#,##0.000\" m\"";
                        ws.Cell(r, 10).Style.NumberFormat.Format = "0.000\" kg/m\"";
                        ws.Cell(r, 11).Style.NumberFormat.Format = "0.000\" t\"";
                        ws.Cell(r, 12).Style.NumberFormat.Format = "0";
                        ws.Cell(r, 13).Style.NumberFormat.Format = "0.000\" t\"";

                        for (int c = 1; c <= Cols; c++)
                            ws.Cell(r, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                        r++;
                    }
                }
            }

            ws.Columns().AdjustToContents();
            ws.Column(1).Width = 18;
            ws.Column(2).Width = 14;
        }

        private static int SubGroupSortOrder(string label, RebarKind kind, int? cycle)
        {
            switch (kind)
            {
                case RebarKind.Transverse:
                    return (cycle ?? 0) == 1 ? 0 : 1;
                case RebarKind.Longitudinal: return 2;
                case RebarKind.Shear: return 3;
                default: return 99;
            }
        }

        // "구조도(N)" → "TYPEN"
        private static string ToTypeLabel(string structureKey)
        {
            var m = Regex.Match(structureKey ?? "", @"구조도\((\d+)\)");
            return m.Success ? $"TYPE{m.Groups[1].Value}" : structureKey;
        }

        private static int StructureSortKey(string key)
        {
            var m = Regex.Match(key ?? "", @"\((\d+)\)");
            return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : int.MaxValue;
        }

        // 행 정렬: 횡 → 전단 → 종
        private static int KindSortOrder(RebarKind k)
        {
            switch (k)
            {
                case RebarKind.Transverse: return 0;
                case RebarKind.Shear: return 1;
                case RebarKind.Longitudinal: return 2;
                default: return 99;
            }
        }
    }
}
