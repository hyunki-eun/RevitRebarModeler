using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

using RevitRebarModeler.Models;
using static RevitRebarModeler.Models.RebarHelpers;

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

        private readonly Autodesk.Revit.DB.Document _doc;

        public TransverseRebarWindow(Autodesk.Revit.DB.Document doc)
        {
            _doc = doc;
            InitializeComponent();
            LstRebars.ItemsSource = _rebarItems;

            // 구조도별 그룹핑
            var view = CollectionViewSource.GetDefaultView(_rebarItems);
            view.GroupDescriptions.Clear();
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RebarItem.SheetKey)));
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(nameof(RebarItem.SheetKey), ListSortDirection.Ascending));

            LoadFromSession();
        }

        /// <summary>
        /// Revit 호스트(구조 프레임)의 Comments에서 'depth=N' 값을 추출해 구조도(N)→depth(mm) 매핑.
        /// '구조물 생성' 명령이 배치한 host 만 인식 (Mark가 "구조도(N)" 패턴).
        /// </summary>
        private Dictionary<string, double> BuildDepthMap()
        {
            var map = new Dictionary<string, double>();
            if (_doc == null) return map;
            try
            {
                var hosts = new Autodesk.Revit.DB.FilteredElementCollector(_doc)
                    .OfClass(typeof(Autodesk.Revit.DB.FamilyInstance))
                    .OfCategory(Autodesk.Revit.DB.BuiltInCategory.OST_StructuralFraming)
                    .WhereElementIsNotElementType()
                    .ToList();
                if (hosts.Count == 0)
                {
                    hosts = new Autodesk.Revit.DB.FilteredElementCollector(_doc)
                        .OfClass(typeof(Autodesk.Revit.DB.FamilyInstance))
                        .OfCategory(Autodesk.Revit.DB.BuiltInCategory.OST_GenericModel)
                        .WhereElementIsNotElementType()
                        .ToList();
                }
                var depthRegex = new Regex(@"depth=(\d+\.?\d*)");
                foreach (var elem in hosts)
                {
                    string comments = elem.get_Parameter(Autodesk.Revit.DB.BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
                    string key = ExtractStructureKey(comments);
                    if (string.IsNullOrEmpty(key)) continue;
                    var m = depthRegex.Match(comments);
                    if (m.Success && double.TryParse(m.Groups[1].Value,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double d) && d > 0)
                    {
                        if (!map.ContainsKey(key)) map[key] = d;
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[TransverseRebarWindow] depth 맵 구성 실패: {ex.Message}"); }
            return map;
        }

        private void LoadFromSession()
        {
            LoadedData = SessionCache.LoadedJson;
            TxtJsonPath.Text = "JSON: " + (string.IsNullOrEmpty(SessionCache.LoadedJsonPath)
                ? "(세션 메모리)"
                : System.IO.Path.GetFileName(SessionCache.LoadedJsonPath));

            if (LoadedData?.TransverseRebars == null || LoadedData.TransverseRebars.Count == 0)
            {
                TxtInfo.Text = "JSON에 TransverseRebars 데이터가 없습니다.";
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

            // '구조물 생성' 명령으로 배치된 host 의 Comments(depth=...)를 읽어 구조도별 두께 매핑
            var depthMap = BuildDepthMap();

            _sheetStates.Clear();
            var groupedBySheet = LoadedData.TransverseRebars
                .GroupBy(r => ExtractStructureKey(r.SheetId))
                .Where(g => !string.IsNullOrEmpty(g.Key));
            foreach (var g in groupedBySheet)
            {
                double defaultCtc = ctcLookup.TryGetValue(g.Key, out double c) ? c : 200;
                double depthMm = depthMap.TryGetValue(g.Key, out double d) ? d : 0;
                _sheetStates[g.Key] = new SheetSharedState
                {
                    SheetKey = g.Key,
                    CtcMm = defaultCtc,
                    DepthMm = depthMm,
                    Cycle1Count = g.Count(r => r.CycleNumber == 1),
                    Cycle2Count = g.Count(r => r.CycleNumber == 2),
                    TotalCount = g.Count(),
                    SelectedCount = g.Count()
                };
            }

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

        // [통합] ExtractStructureKey 는 Models.RebarHelpers 로 이동.

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

            // 세션 캐시에 저장 — 전단철근 등 후속 명령에서 횡방향 N단 Z 위치 산출 시 사용
            // LoadedJson 자체는 LoadJsonCommand가 단독 관리하므로 여기서는 건드리지 않음
            SessionCache.TransverseCtcMap = SheetCtcMap;

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

        /// <summary>'구조물 생성' 시 host에 저장된 depth(mm). 0이면 host 미배치 또는 값 추출 실패.</summary>
        public double DepthMm { get; set; }

        /// <summary>그룹 헤더에 표시할 두께 문자열. "1000 mm" 또는 host 매칭 실패 시 "— (host 미배치)".</summary>
        public string DepthDisplay => DepthMm > 0 ? $"{DepthMm:N0} mm" : "— (host 미배치)";

        public int Cycle1Count { get; set; }
        public int Cycle2Count { get; set; }
        public int TotalCount { get; set; }

        /// <summary>
        /// 일람표2/3 마크 라벨 예상 범위. Cycle1만 있으면 "A1~A3", Cycle2도 있으면 "A1~A3 / A1-1~A2-1".
        /// </summary>
        public string MarkRangeDisplay
        {
            get
            {
                var parts = new List<string>();
                if (Cycle1Count > 0) parts.Add(Cycle1Count == 1 ? "A1" : $"A1~A{Cycle1Count}");
                if (Cycle2Count > 0) parts.Add(Cycle2Count == 1 ? "A1-1" : $"A1-1~A{Cycle2Count}-1");
                return parts.Count == 0 ? "—" : string.Join(" / ", parts);
            }
        }

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
        public string SheetDepthDisplay => SharedState?.DepthDisplay ?? "—";
        public string SheetMarkRangeDisplay => SharedState?.MarkRangeDisplay ?? "—";

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
