using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

using Microsoft.Win32;

using Newtonsoft.Json;

using RevitRebarModeler.Models;

namespace RevitRebarModeler.UI
{
    public partial class CreateStructureWindow : Window
    {
        public CivilExportData LoadedData { get; private set; }
        public List<StructureCycleData> SelectedCycles { get; private set; }
        public double DepthMm { get; private set; } = 1000;

        private ObservableCollection<RegionItem> _regionItems = new ObservableCollection<RegionItem>();

        public CreateStructureWindow()
        {
            InitializeComponent();
            LstRegions.ItemsSource = _regionItems;
        }

        // ============================================================
        // JSON 파일 로드
        // ============================================================

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "JSON 파일 (*.json)|*.json",
                Title = "Civil3D 내보내기 JSON 선택"
            };

            if (dlg.ShowDialog() != true) return;

            TxtJsonPath.Text = dlg.FileName;

            try
            {
                string json = System.IO.File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
                LoadedData = JsonConvert.DeserializeObject<CivilExportData>(json);

                if (LoadedData?.StructureRegions == null || LoadedData.StructureRegions.Count == 0)
                {
                    MessageBox.Show("StructureRegions 데이터가 없습니다.", "오류");
                    return;
                }

                // 영역별 아이템 생성
                _regionItems.Clear();
                foreach (var cycle in LoadedData.StructureRegions)
                {
                    foreach (var region in cycle.Regions)
                    {
                        _regionItems.Add(new RegionItem
                        {
                            IsChecked = true,
                            CycleKey = cycle.CycleKey,
                            RegionId = region.Id,
                            AreaMm2 = region.Area,
                            VertexCount = region.VertexCount,
                            Layer = region.Layer ?? "",
                            RegionData = region,
                            ParentCycle = cycle
                        });
                    }
                }

                UpdateSelectionInfo();
                BtnCreate.IsEnabled = true;

                int totalRegions = _regionItems.Count;
                var cycleKeys = _regionItems.Select(r => r.CycleKey).Distinct().ToList();
                TxtInfo.Text = $"프로젝트: {LoadedData.ProjectName} | " +
                               $"{cycleKeys.Count}개 사이클, 총 {totalRegions}개 영역";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"JSON 파싱 오류:\n{ex.Message}", "오류");
            }
        }

        // ============================================================
        // 생성 버튼
        // ============================================================

        private void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(TxtDepth.Text, out double depth) || depth <= 0)
            {
                MessageBox.Show("돌출 깊이를 올바르게 입력하세요.", "오류");
                return;
            }

            var checkedItems = _regionItems.Where(r => r.IsChecked).ToList();
            if (checkedItems.Count == 0)
            {
                MessageBox.Show("영역을 하나 이상 선택하세요.", "오류");
                return;
            }

            DepthMm = depth;

            var cycleGroups = checkedItems.GroupBy(r => r.CycleKey);
            var selected = new List<StructureCycleData>();

            foreach (var group in cycleGroups)
            {
                var regions = group.Select(r => r.RegionData).ToList();
                selected.Add(new StructureCycleData
                {
                    CycleKey = group.Key,
                    RegionCount = regions.Count,
                    Regions = regions
                });
            }

            SelectedCycles = selected;
            DialogResult = true;
            Close();
        }

        // ============================================================
        // 선택 제어
        // ============================================================

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _regionItems) item.IsChecked = true;
            ChkAll.IsChecked = true;
            UpdateSelectionInfo();
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _regionItems) item.IsChecked = false;
            ChkAll.IsChecked = false;
            UpdateSelectionInfo();
        }

        private void ChkAll_Click(object sender, RoutedEventArgs e)
        {
            bool isChecked = ChkAll.IsChecked == true;
            foreach (var item in _regionItems) item.IsChecked = isChecked;
            UpdateSelectionInfo();
        }

        private void RegionCheckBox_Click(object sender, RoutedEventArgs e)
        {
            UpdateHeaderCheckState();
            UpdateSelectionInfo();
        }

        private void UpdateHeaderCheckState()
        {
            int checkedCount = _regionItems.Count(r => r.IsChecked);
            if (checkedCount == _regionItems.Count) ChkAll.IsChecked = true;
            else if (checkedCount == 0) ChkAll.IsChecked = false;
            else ChkAll.IsChecked = null;
        }

        private void UpdateSelectionInfo()
        {
            int checkedCount = _regionItems.Count(r => r.IsChecked);
            double totalArea = _regionItems.Where(r => r.IsChecked).Sum(r => r.AreaMm2);
            TxtSelectionInfo.Text = $"선택: {checkedCount}/{_regionItems.Count}개 | " +
                                    $"면적: {totalArea / 1_000_000:F2} m²";
        }

        // ============================================================
        // 열 정렬
        // ============================================================

        private GridViewColumnHeader _lastHeaderClicked = null;
        private ListSortDirection _lastDirection = ListSortDirection.Ascending;

        private void LstRegions_ColumnHeaderClick(object sender, RoutedEventArgs e)
        {
            if (!(e.OriginalSource is GridViewColumnHeader header)) return;
            if (header.Role == GridViewColumnHeaderRole.Padding) return;

            string sortBy = null;
            string headerText = header.Column?.Header?.ToString() ?? "";

            switch (headerText)
            {
                case "사이클": sortBy = "CycleKey"; break;
                case "영역": sortBy = "RegionId"; break;
                case "면적 (m²)": sortBy = "AreaMm2"; break;
                case "꼭짓점": sortBy = "VertexCount"; break;
                default: return;
            }

            ListSortDirection direction;
            if (header == _lastHeaderClicked)
                direction = _lastDirection == ListSortDirection.Ascending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;
            else
                direction = ListSortDirection.Ascending;

            _lastHeaderClicked = header;
            _lastDirection = direction;

            var view = CollectionViewSource.GetDefaultView(LstRegions.ItemsSource);
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(sortBy, direction));
        }
    }

    // ============================================================
    // 영역 아이템 ViewModel (간소화)
    // ============================================================

    public class RegionItem : INotifyPropertyChanged
    {
        private bool _isChecked = true;

        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value; Notify(nameof(IsChecked)); } }
        }

        public string CycleKey { get; set; }
        public int RegionId { get; set; }
        public double AreaMm2 { get; set; }
        public int VertexCount { get; set; }
        public string Layer { get; set; }

        /// <summary>면적 표시 (m²)</summary>
        public string AreaDisplay => $"{AreaMm2 / 1_000_000:F4}";

        public StructureRegionData RegionData { get; set; }
        public StructureCycleData ParentCycle { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
