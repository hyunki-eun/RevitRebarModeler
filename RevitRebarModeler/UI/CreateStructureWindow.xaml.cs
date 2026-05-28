using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

using RevitRebarModeler.Models;

namespace RevitRebarModeler.UI
{
    public partial class CreateStructureWindow : Window
    {
        public CivilExportData LoadedData { get; private set; }
        public List<StructureCycleData> SelectedCycles { get; private set; }

        /// <summary>
        /// 구조도(CycleKey) → 돌출 깊이(mm). 사용자가 헤더 입력란에서 개별 설정.
        /// </summary>
        public Dictionary<string, double> DepthByCycle { get; private set; } = new Dictionary<string, double>();

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

            LoadFromSession();
        }

        // ============================================================
        // 세션 캐시에서 JSON 로드 (LoadJsonCommand가 미리 채워둠)
        // ============================================================

        private void LoadFromSession()
        {
            LoadedData = SessionCache.LoadedJson;
            TxtJsonPath.Text = "JSON: " + (string.IsNullOrEmpty(SessionCache.LoadedJsonPath)
                ? "(세션 메모리)"
                : System.IO.Path.GetFileName(SessionCache.LoadedJsonPath));

            if (LoadedData?.StructureRegions == null || LoadedData.StructureRegions.Count == 0)
            {
                TxtInfo.Text = "JSON에 StructureRegions 데이터가 없습니다.";
                return;
            }

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
                    SelectedCount = count
                };
            }

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

        // ============================================================
        // 생성 버튼
        // ============================================================

        private void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            var checkedItems = _regionItems.Where(r => r.IsChecked).ToList();
            if (checkedItems.Count == 0)
            {
                MessageBox.Show("영역을 하나 이상 선택하세요.", "오류");
                return;
            }

            // 선택된 구조도들의 두께 검증 — 0 이하/NaN 차단
            var cycleGroups = checkedItems.GroupBy(r => r.CycleKey).ToList();
            var invalid = new List<string>();
            foreach (var group in cycleGroups)
            {
                double d = group.First().SharedState?.DepthMm ?? 0;
                if (d <= 0 || double.IsNaN(d) || double.IsInfinity(d))
                    invalid.Add(group.Key);
            }
            if (invalid.Count > 0)
            {
                MessageBox.Show(
                    $"다음 구조도의 두께가 올바르지 않습니다:\n  {string.Join(", ", invalid)}\n\n" +
                    "각 헤더의 [두께] 입력란에 양수(mm)를 입력하세요.",
                    "오류");
                return;
            }

            DepthByCycle = new Dictionary<string, double>();
            var selected = new List<StructureCycleData>();

            foreach (var group in cycleGroups)
            {
                var regions = group.Select(r => r.RegionData).ToList();
                double depthMm = group.First().SharedState?.DepthMm ?? 1000;
                DepthByCycle[group.Key] = depthMm;

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

        /// <summary>
        /// 그룹 헤더의 두께 TextBox를 클릭했을 때 Expander가 토글되는 것을 차단.
        /// </summary>
        private void DepthTextBox_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is TextBox tb && !tb.IsKeyboardFocusWithin)
            {
                tb.Focus();
                tb.SelectAll();
                e.Handled = true;
            }
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

        private double _depthMm = 1000;
        public double DepthMm
        {
            get => _depthMm;
            set
            {
                if (Math.Abs(_depthMm - value) > 1e-9)
                {
                    _depthMm = value;
                    Notify(nameof(DepthMm));
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

        // 그룹 헤더의 두께 입력란(TwoWay) — SharedState.DepthMm로 위임
        public double GroupDepthMm
        {
            get => SharedState?.DepthMm ?? 1000;
            set
            {
                if (SharedState != null && Math.Abs(SharedState.DepthMm - value) > 1e-9)
                {
                    SharedState.DepthMm = value;
                    Notify(nameof(GroupDepthMm));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
