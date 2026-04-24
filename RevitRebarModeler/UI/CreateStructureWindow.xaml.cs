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
        private Dictionary<string, CycleSharedState> _cycleStates = new Dictionary<string, CycleSharedState>();

        public CreateStructureWindow()
        {
            InitializeComponent();
            LstRegions.ItemsSource = _regionItems;

            var view = CollectionViewSource.GetDefaultView(_regionItems);
            view.GroupDescriptions.Clear();
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RegionItem.CycleKey)));
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(nameof(RegionItem.CycleKey), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(RegionItem.RegionId), ListSortDirection.Ascending));
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

                // 사이클(구조도)별 공유 상태 생성
                _cycleStates.Clear();
                foreach (var cycle in LoadedData.StructureRegions)
                {
                    double totalArea = cycle.Regions?.Sum(r => r.Area) ?? 0;
                    int count = cycle.Regions?.Count ?? 0;
                    _cycleStates[cycle.CycleKey] = new CycleSharedState
                    {
                        CycleKey = cycle.CycleKey,
                        TotalArea = totalArea,
                        TotalCount = count,
                        SelectedCount = count // 초기 전체 선택
                    };
                }

                // 영역별 아이템 생성 (공유 상태 참조)
                _regionItems.Clear();
                foreach (var cycle in LoadedData.StructureRegions)
                {
                    _cycleStates.TryGetValue(cycle.CycleKey, out var state);
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
                            ParentCycle = cycle,
                            SharedState = state
                        });
                    }
                }

                UpdateSelectionInfo();
                RecomputeAllGroupSelectedCounts();
                BtnCreate.IsEnabled = true;

                int totalRegions = _regionItems.Count;
                int cycleCount = _cycleStates.Count;
                TxtInfo.Text = $"프로젝트: {LoadedData.ProjectName} | " +
                               $"{cycleCount}개 사이클, 총 {totalRegions}개 영역";
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
            RecomputeAllGroupSelectedCounts();
            UpdateSelectionInfo();
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _regionItems) item.IsChecked = false;
            ChkAll.IsChecked = false;
            RecomputeAllGroupSelectedCounts();
            UpdateSelectionInfo();
        }

        private void ChkAll_Click(object sender, RoutedEventArgs e)
        {
            bool isChecked = ChkAll.IsChecked == true;
            foreach (var item in _regionItems) item.IsChecked = isChecked;
            RecomputeAllGroupSelectedCounts();
            UpdateSelectionInfo();
        }

        private void RegionCheckBox_Click(object sender, RoutedEventArgs e)
        {
            UpdateHeaderCheckState();
            RecomputeAllGroupSelectedCounts();
            UpdateSelectionInfo();
        }

        private void BtnGroupSelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn) || !(btn.Tag is string cycleKey)) return;
            foreach (var item in _regionItems.Where(r => r.CycleKey == cycleKey))
                item.IsChecked = true;
            UpdateHeaderCheckState();
            RecomputeAllGroupSelectedCounts();
            UpdateSelectionInfo();
        }

        private void BtnGroupDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn) || !(btn.Tag is string cycleKey)) return;
            foreach (var item in _regionItems.Where(r => r.CycleKey == cycleKey))
                item.IsChecked = false;
            UpdateHeaderCheckState();
            RecomputeAllGroupSelectedCounts();
            UpdateSelectionInfo();
        }

        private void RecomputeAllGroupSelectedCounts()
        {
            foreach (var kv in _cycleStates)
            {
                int sel = _regionItems.Count(r => r.CycleKey == kv.Key && r.IsChecked);
                double selArea = _regionItems
                    .Where(r => r.CycleKey == kv.Key && r.IsChecked)
                    .Sum(r => r.AreaMm2);
                kv.Value.SelectedCount = sel;
                kv.Value.SelectedArea = selArea;
            }
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
            TxtSelectionInfo.Text = $"전체 선택: {checkedCount}/{_regionItems.Count}개  ·  면적: {totalArea / 1_000_000:F2} m²";
        }
    }

    /// <summary>
    /// 사이클(구조도)별 공유 상태.
    /// 같은 CycleKey의 모든 RegionItem이 같은 인스턴스를 참조.
    /// </summary>
    public class CycleSharedState : INotifyPropertyChanged
    {
        public string CycleKey { get; set; }
        public int TotalCount { get; set; }
        public double TotalArea { get; set; }

        private int _selectedCount;
        public int SelectedCount
        {
            get => _selectedCount;
            set
            {
                if (_selectedCount != value)
                {
                    _selectedCount = value;
                    Notify(nameof(SelectedCount));
                }
            }
        }

        private double _selectedArea;
        public double SelectedArea
        {
            get => _selectedArea;
            set
            {
                if (Math.Abs(_selectedArea - value) > 1e-3)
                {
                    _selectedArea = value;
                    Notify(nameof(SelectedArea));
                    Notify(nameof(SelectedAreaDisplay));
                }
            }
        }

        public string SelectedAreaDisplay => $"{_selectedArea / 1_000_000:F2}";
        public string TotalAreaDisplay => $"{TotalArea / 1_000_000:F2}";

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

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

        public string AreaDisplay => $"{AreaMm2 / 1_000_000:F4}";

        public StructureRegionData RegionData { get; set; }
        public StructureCycleData ParentCycle { get; set; }

        public CycleSharedState SharedState { get; set; }

        // 그룹 헤더 바인딩용 passthrough
        public int GroupSelectedCount => SharedState?.SelectedCount ?? 0;
        public string GroupAreaDisplay => SharedState?.SelectedAreaDisplay ?? "0";

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
