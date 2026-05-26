using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using Autodesk.Revit.DB;
using Microsoft.Win32;

using RevitRebarModeler.Models;

namespace RevitRebarModeler.UI
{
    public partial class ScheduleWindow : Window
    {
        public double SurchargePercent { get; private set; }
        public bool ExportExcel { get; private set; }
        public bool CreateRevitSchedule { get; private set; }
        public string ExcelPath { get; private set; }

        public List<RebarScheduleRow> Rows1 { get; private set; }
        public List<RebarScheduleRowDetailed> RowsDetailed { get; private set; }

        private readonly Document _doc;
        private readonly Dictionary<string, double> _ctcMap;
        private bool _suppressRecalc = true;  // 생성자 동안 TextChanged 이벤트 무시

        public ScheduleWindow(Document doc,
            List<RebarScheduleRow> rows1,
            List<RebarScheduleRowDetailed> rowsDetailed,
            Dictionary<string, double> transverseCtcMap)
        {
            InitializeComponent();
            _doc = doc;
            _ctcMap = transverseCtcMap;
            Rows1 = rows1 ?? new List<RebarScheduleRow>();
            RowsDetailed = rowsDetailed ?? new List<RebarScheduleRowDetailed>();

            // 정렬: 일람표 1은 TYPE/Kind/직경, 일람표 2/3은 별도 정렬
            var rows1Sorted = Rows1
                .OrderBy(r => StructureSortKey(r.StructureKey))
                .ThenBy(r => KindSortOrder(r.Kind))
                .ThenByDescending(r => r.DiameterMm)
                .ToList();
            GridSheet1.ItemsSource = rows1Sorted;

            // 일람표 2: TYPE → Mark 인터리브 (Index, Cycle)
            var sheet2 = RowsDetailed
                .OrderBy(r => StructureSortKey(r.StructureKey))
                .ThenBy(r => KindSortOrder(r.Kind))
                .ThenBy(r => r.MarkIndex)
                .ThenBy(r => r.CycleNumber ?? 0)
                .ToList();
            GridSheet2.ItemsSource = sheet2;

            // 일람표 3: TYPE → SubGroup → MarkIndex
            var sheet3 = RowsDetailed
                .OrderBy(r => StructureSortKey(r.StructureKey))
                .ThenBy(r => SubGroupOrder(r))
                .ThenBy(r => r.MarkIndex)
                .ToList();
            GridSheet3.ItemsSource = sheet3;

            // 통계
            int typeCount = Rows1.Select(r => r.StructureKey).Distinct().Count();
            int totalCount = Rows1.Sum(r => r.Count);
            double totalWeightKg = Rows1.Sum(r => r.WeightWithSurchargeKg);
            TxtStats.Text = $"TYPE {typeCount}개 · 행 {Rows1.Count}개 · 총 본수 {totalCount} · 총 중량(할증) {totalWeightKg:N1} kg";

            // 기본 저장 경로
            string defaultDir = null;
            string defaultName = "보강철근_수량_일람표.xlsx";
            try
            {
                if (doc != null && !string.IsNullOrEmpty(doc.PathName))
                {
                    defaultDir = Path.GetDirectoryName(doc.PathName);
                    string baseName = Path.GetFileNameWithoutExtension(doc.PathName);
                    if (!string.IsNullOrEmpty(baseName))
                        defaultName = $"{baseName}_보강철근수량.xlsx";
                }
            }
            catch { }
            if (string.IsNullOrEmpty(defaultDir))
                defaultDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            TxtExcelPath.Text = Path.Combine(defaultDir, defaultName);

            TxtInfo.Text = "그리드에서 데이터를 확인하세요. 할증율을 변경하면 모든 탭의 할증중량이 자동 갱신됩니다.";

            _suppressRecalc = false;
        }

        private void TxtSurcharge_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressRecalc) return;
            if (!double.TryParse(TxtSurcharge.Text, out double sp)) return;
            if (sp < 0 || sp > 100) return;

            // 모든 행의 할증 컬럼 재계산 (전체 일괄 적용)
            // 일람표 1은 새 수식: TotalWeightKg = RebarTotalLengthM × UnitWeightKgPerM
            foreach (var row in Rows1)
            {
                row.SurchargePercent = sp;
                row.WeightWithSurchargeKg = row.TotalWeightKg * (1.0 + sp / 100.0);
            }
            foreach (var row in RowsDetailed)
            {
                row.SurchargePercent = sp;
                row.SurchargeTotalKg = row.TotalWeightKg * (1.0 + sp / 100.0);
            }

            GridSheet1.Items.Refresh();
            GridSheet2.Items.Refresh();
            GridSheet3.Items.Refresh();
        }

        /// <summary>
        /// 그리드 셀 편집 종료 → 의존 컬럼 재계산.
        /// 일람표 1 (RebarScheduleRow): UnitWeight, Surcharge 변경 → TotalWeight, WeightWithSurcharge 재계산
        ///                              SetCount 변경 → 단순 저장 (다른 컬럼 무관)
        /// 일람표 2/3 (RebarScheduleRowDetailed): OneMPerCount, UnitWeight, Surcharge 변경
        ///                                        → TotalLengthPerMm, TotalWeight, SurchargeTotal 재계산
        /// </summary>
        private void Grid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;

            var grid = sender as DataGrid;
            var item = e.Row.Item;

            // 셀 편집 커밋 후 BindingExpression이 source에 반영되도록 한 틱 대기
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (item is RebarScheduleRow r1)
                {
                    // 일람표 1 새 수식:
                    //   철근 총길이(F) = 전체 길이(C, m) × 1m당 본수(E)  ← RebarTotalLengthM (자동)
                    //   총중량(H) = F × 단위중량(G)
                    //   할증중량(J) = H × (1+I/100)
                    r1.TotalWeightKg = r1.RebarTotalLengthM * r1.UnitWeightKgPerM;
                    r1.WeightWithSurchargeKg = r1.TotalWeightKg * (1.0 + r1.SurchargePercent / 100.0);
                    GridSheet1?.Items.Refresh();
                }
                else if (item is RebarScheduleRowDetailed r2)
                {
                    r2.TotalLengthPerMm = r2.TotalLengthMm * r2.OneMPerCount;
                    r2.TotalWeightKg = (r2.TotalLengthPerMm / 1000.0) * r2.UnitWeightKgM;
                    r2.SurchargeTotalKg = r2.TotalWeightKg * (1.0 + r2.SurchargePercent / 100.0);
                    // 일람표 2/3은 같은 객체를 공유하므로 양쪽 그리드 모두 갱신
                    GridSheet2?.Items.Refresh();
                    GridSheet3?.Items.Refresh();
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            string initDir = null;
            string initName = "보강철근_수량_일람표.xlsx";
            try
            {
                string p = TxtExcelPath.Text;
                if (!string.IsNullOrEmpty(p))
                {
                    initDir = Path.GetDirectoryName(p);
                    initName = Path.GetFileName(p);
                }
            }
            catch { }

            var dlg = new SaveFileDialog
            {
                Filter = "Excel 파일 (*.xlsx)|*.xlsx",
                FileName = initName,
                InitialDirectory = initDir,
                Title = "일람표 저장 경로"
            };
            if (dlg.ShowDialog() == true)
                TxtExcelPath.Text = dlg.FileName;
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(TxtSurcharge.Text, out double sp) || sp < 0 || sp > 100)
            {
                MessageBox.Show("할증율은 0~100 사이 숫자여야 합니다.", "입력 오류",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ExportExcel = ChkExcel.IsChecked == true;
            CreateRevitSchedule = ChkRevitSchedule.IsChecked == true;

            if (!ExportExcel && !CreateRevitSchedule)
            {
                MessageBox.Show("출력 형식 중 최소 하나는 선택해야 합니다.", "선택 필요",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ExportExcel)
            {
                string p = TxtExcelPath.Text?.Trim();
                if (string.IsNullOrEmpty(p))
                {
                    MessageBox.Show("Excel 저장 경로를 입력하세요.", "경로 누락",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!p.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) p += ".xlsx";
                ExcelPath = p;
            }

            SurchargePercent = sp;
            DialogResult = true;
            Close();
        }

        private static int StructureSortKey(string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(key ?? "", @"\((\d+)\)");
            return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : int.MaxValue;
        }

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

        private static int SubGroupOrder(RebarScheduleRowDetailed r)
        {
            switch (r.Kind)
            {
                case RebarKind.Transverse: return (r.CycleNumber ?? 0) == 1 ? 0 : 1;
                case RebarKind.Longitudinal: return 2;
                case RebarKind.Shear: return 3;
                default: return 99;
            }
        }
    }
}
