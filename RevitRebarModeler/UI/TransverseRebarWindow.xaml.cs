using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

using Microsoft.Win32;

using Newtonsoft.Json;

using RevitRebarModeler.Models;

namespace RevitRebarModeler.UI
{
    public partial class TransverseRebarWindow : Window
    {
        public CivilExportData LoadedData { get; private set; }
        public List<TransverseRebarData> SelectedRebars { get; private set; }
        public Dictionary<string, double> SheetCtcMap { get; private set; }

        private ObservableCollection<RebarItem> _rebarItems = new ObservableCollection<RebarItem>();
        // 구조도별 공유 상태 (CTC, 선택수 등) — 같은 SheetKey의 모든 RebarItem이 공유.
        private Dictionary<string, SheetSharedState> _sheetStates = new Dictionary<string, SheetSharedState>();

        public TransverseRebarWindow(Autodesk.Revit.DB.Document doc)
        {
            InitializeComponent();
            LstRebars.ItemsSource = _rebarItems;

            // 구조도별 그룹핑
            var view = CollectionViewSource.GetDefaultView(_rebarItems);
            view.GroupDescriptions.Clear();
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RebarItem.SheetKey)));
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(nameof(RebarItem.SheetKey), ListSortDirection.Ascending));
        }

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

                if (LoadedData?.TransverseRebars == null || LoadedData.TransverseRebars.Count == 0)
                {
                    MessageBox.Show("TransverseRebars 데이터가 없습니다.", "오류");
                    return;
                }

                // 구조도별 CTC 기본값 준비
                var ctcLookup = new Dictionary<string, double>();
                if (LoadedData.StructureRegions != null)
                {
                    var keyRegex = new Regex(@"구조도\((\d+)\)");
                    foreach (var sr in LoadedData.StructureRegions)
                    {
                        var m = keyRegex.Match(sr.CycleKey ?? "");
                        if (!m.Success) continue;
                        string sheetKey = $"구조도({m.Groups[1].Value})";
                        double ctc = sr.Cycle1CtcMm > 0 ? sr.Cycle1CtcMm : sr.Cycle2CtcMm;
                        if (ctc > 0) ctcLookup[sheetKey] = ctc;
                    }
                }

                // 구조도별 공유 상태(SheetSharedState) 생성
                _sheetStates.Clear();
                var groupedBySheet = LoadedData.TransverseRebars
                    .GroupBy(r => ExtractStructureKey(r.SheetId))
                    .Where(g => !string.IsNullOrEmpty(g.Key));
                foreach (var g in groupedBySheet)
                {
                    double defaultCtc = ctcLookup.TryGetValue(g.Key, out double c) ? c : 200;
                    _sheetStates[g.Key] = new SheetSharedState
                    {
                        SheetKey = g.Key,
                        CtcMm = defaultCtc,
                        Cycle1Count = g.Count(r => r.CycleNumber == 1),
                        Cycle2Count = g.Count(r => r.CycleNumber == 2),
                        TotalCount = g.Count(),
                        SelectedCount = g.Count() // 초기 전체 선택
                    };
                }

                // 철근 아이템 구성 (공유 상태 참조)
                _rebarItems.Clear();
                foreach (var rebar in LoadedData.TransverseRebars)
                {
                    string sheetKey = ExtractStructureKey(rebar.SheetId);
                    _sheetStates.TryGetValue(sheetKey, out var state);
                    _rebarItems.Add(new RebarItem
                    {
                        IsChecked = true,
                        Id = rebar.Id.ToString(),
                        SheetKey = sheetKey,
                        CycleNumber = rebar.CycleNumber,
                        MatchedText = rebar.MatchedText ?? "",
                        DiameterMm = rebar.DiameterMm,
                        SegmentCount = rebar.Segments?.Count ?? 0,
                        RebarData = rebar,
                        SharedState = state
                    });
                }

                UpdateSelectionInfo();
                BtnPlace.IsEnabled = true;

                int total = _rebarItems.Count;
                int sheets = _sheetStates.Count;
                TxtInfo.Text = $"프로젝트: {LoadedData.ProjectName}\n" +
                               $"횡방향 철근: {total}개 | 구조도: {sheets}개 → Host 자동 매칭";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"JSON 파싱 오류:\n{ex.Message}", "오류");
            }
        }

        private string ExtractStructureKey(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var match = Regex.Match(text, @"구조도\(\d+\)");
            return match.Success ? match.Value : "";
        }

        private void BtnPlace_Click(object sender, RoutedEventArgs e)
        {
            var checkedItems = _rebarItems.Where(r => r.IsChecked).ToList();
            if (checkedItems.Count == 0)
            {
                MessageBox.Show("철근을 하나 이상 선택하세요.", "오류");
                return;
            }

            SheetCtcMap = new Dictionary<string, double>();
            foreach (var kv in _sheetStates)
            {
                double ctc = kv.Value.CtcMm <= 0 ? 200 : kv.Value.CtcMm;
                SheetCtcMap[kv.Key] = ctc;
            }

            SelectedRebars = checkedItems.Select(r => r.RebarData).ToList();
            DialogResult = true;
            Close();
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _rebarItems) item.IsChecked = true;
            ChkAll.IsChecked = true;
            RecomputeAllSheetSelectedCounts();
            UpdateSelectionInfo();
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _rebarItems) item.IsChecked = false;
            ChkAll.IsChecked = false;
            RecomputeAllSheetSelectedCounts();
            UpdateSelectionInfo();
        }

        private void ChkAll_Click(object sender, RoutedEventArgs e)
        {
            bool isChecked = ChkAll.IsChecked == true;
            foreach (var item in _rebarItems) item.IsChecked = isChecked;
            RecomputeAllSheetSelectedCounts();
            UpdateSelectionInfo();
        }

        private void RebarCheckBox_Click(object sender, RoutedEventArgs e)
        {
            int checkedCount = _rebarItems.Count(r => r.IsChecked);
            if (checkedCount == _rebarItems.Count) ChkAll.IsChecked = true;
            else if (checkedCount == 0) ChkAll.IsChecked = false;
            else ChkAll.IsChecked = null;
            RecomputeAllSheetSelectedCounts();
            UpdateSelectionInfo();
        }

        private void BtnSheetSelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn) || !(btn.Tag is string sheetKey)) return;
            foreach (var item in _rebarItems.Where(r => r.SheetKey == sheetKey))
                item.IsChecked = true;
            RecomputeAllSheetSelectedCounts();
            UpdateSelectionInfo();
        }

        private void BtnSheetDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn) || !(btn.Tag is string sheetKey)) return;
            foreach (var item in _rebarItems.Where(r => r.SheetKey == sheetKey))
                item.IsChecked = false;
            RecomputeAllSheetSelectedCounts();
            UpdateSelectionInfo();
        }

        private void RecomputeAllSheetSelectedCounts()
        {
            foreach (var kv in _sheetStates)
            {
                int sel = _rebarItems.Count(r => r.SheetKey == kv.Key && r.IsChecked);
                kv.Value.SelectedCount = sel;
            }
        }

        private void UpdateSelectionInfo()
        {
            int checkedCount = _rebarItems.Count(r => r.IsChecked);
            TxtSelectionInfo.Text = $"전체 선택: {checkedCount}/{_rebarItems.Count}개";
        }
    }

    /// <summary>
    /// 구조도별로 공유되는 상태.
    /// 같은 SheetKey의 모든 RebarItem 이 같은 인스턴스를 참조하므로
    /// CTC/선택수 등이 하나만 바뀌면 그룹 헤더에 즉시 반영됨.
    /// </summary>
    public class SheetSharedState : INotifyPropertyChanged
    {
        public string SheetKey { get; set; }

        private double _ctcMm = 200;
        public double CtcMm
        {
            get => _ctcMm;
            set { if (Math.Abs(_ctcMm - value) > 0.01) { _ctcMm = value; Notify(nameof(CtcMm)); } }
        }

        public int Cycle1Count { get; set; }
        public int Cycle2Count { get; set; }
        public int TotalCount { get; set; }

        private int _selectedCount;
        public int SelectedCount
        {
            get => _selectedCount;
            set { if (_selectedCount != value) { _selectedCount = value; Notify(nameof(SelectedCount)); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class RebarItem : INotifyPropertyChanged
    {
        private bool _isChecked = true;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value; Notify(nameof(IsChecked)); } }
        }

        public string Id { get; set; }
        public string SheetKey { get; set; }
        public int CycleNumber { get; set; }
        public string MatchedText { get; set; }
        public double DiameterMm { get; set; }
        public int SegmentCount { get; set; }
        public TransverseRebarData RebarData { get; set; }

        public SheetSharedState SharedState { get; set; }

        // 그룹 헤더 바인딩용 passthrough
        public double SheetCtcMm
        {
            get => SharedState?.CtcMm ?? 200;
            set { if (SharedState != null) SharedState.CtcMm = value; }
        }
        public int Cycle1Count => SharedState?.Cycle1Count ?? 0;
        public int Cycle2Count => SharedState?.Cycle2Count ?? 0;
        public int SheetSelectedCount => SharedState?.SelectedCount ?? 0;

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
