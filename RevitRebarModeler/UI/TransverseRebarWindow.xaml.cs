using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

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
        private ObservableCollection<SheetCtcItem> _ctcItems = new ObservableCollection<SheetCtcItem>();

        public TransverseRebarWindow(Autodesk.Revit.DB.Document doc)
        {
            InitializeComponent();
            LstRebars.ItemsSource = _rebarItems;
            LstCtc.ItemsSource = _ctcItems;
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

                // 철근 목록
                _rebarItems.Clear();
                foreach (var rebar in LoadedData.TransverseRebars)
                {
                    _rebarItems.Add(new RebarItem
                    {
                        IsChecked = true,
                        Id = rebar.Id.ToString(),
                        SheetKey = ExtractStructureKey(rebar.SheetId),
                        CycleNumber = rebar.CycleNumber,
                        MatchedText = rebar.MatchedText ?? "",
                        DiameterMm = rebar.DiameterMm,
                        SegmentCount = rebar.Segments?.Count ?? 0,
                        RebarData = rebar
                    });
                }

                // JSON의 StructureRegions에서 sheet별 CTC 기본값 준비 (Civil3D ④에서 지정된 값)
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

                // 구조도별 CTC 테이블 생성
                _ctcItems.Clear();
                var sheetGroups = LoadedData.TransverseRebars
                    .GroupBy(r => ExtractStructureKey(r.SheetId))
                    .OrderBy(g => g.Key);

                foreach (var group in sheetGroups)
                {
                    if (string.IsNullOrEmpty(group.Key)) continue;
                    double defaultCtc = ctcLookup.TryGetValue(group.Key, out double c) ? c : 200;
                    _ctcItems.Add(new SheetCtcItem
                    {
                        SheetKey = group.Key,
                        CtcMm = defaultCtc,
                        Cycle1Count = group.Count(r => r.CycleNumber == 1),
                        Cycle2Count = group.Count(r => r.CycleNumber == 2)
                    });
                }

                UpdateSelectionInfo();
                BtnPlace.IsEnabled = true;

                int total = _rebarItems.Count;
                int sheets = _ctcItems.Count;
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

            // 구조도별 CTC 수집
            SheetCtcMap = new Dictionary<string, double>();
            foreach (var item in _ctcItems)
            {
                if (item.CtcMm <= 0) item.CtcMm = 200;
                SheetCtcMap[item.SheetKey] = item.CtcMm;
            }

            SelectedRebars = checkedItems.Select(r => r.RebarData).ToList();
            DialogResult = true;
            Close();
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _rebarItems) item.IsChecked = true;
            ChkAll.IsChecked = true;
            UpdateSelectionInfo();
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _rebarItems) item.IsChecked = false;
            ChkAll.IsChecked = false;
            UpdateSelectionInfo();
        }

        private void ChkAll_Click(object sender, RoutedEventArgs e)
        {
            bool isChecked = ChkAll.IsChecked == true;
            foreach (var item in _rebarItems) item.IsChecked = isChecked;
            UpdateSelectionInfo();
        }

        private void RebarCheckBox_Click(object sender, RoutedEventArgs e)
        {
            int checkedCount = _rebarItems.Count(r => r.IsChecked);
            if (checkedCount == _rebarItems.Count) ChkAll.IsChecked = true;
            else if (checkedCount == 0) ChkAll.IsChecked = false;
            else ChkAll.IsChecked = null;
            UpdateSelectionInfo();
        }

        private void UpdateSelectionInfo()
        {
            int checkedCount = _rebarItems.Count(r => r.IsChecked);
            TxtSelectionInfo.Text = $"선택: {checkedCount}/{_rebarItems.Count}개";
        }
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

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class SheetCtcItem : INotifyPropertyChanged
    {
        private double _ctcMm = 200;

        public string SheetKey { get; set; }

        public double CtcMm
        {
            get => _ctcMm;
            set { if (Math.Abs(_ctcMm - value) > 0.01) { _ctcMm = value; Notify(nameof(CtcMm)); } }
        }

        public int Cycle1Count { get; set; }
        public int Cycle2Count { get; set; }
        public int TotalCount => Cycle1Count + Cycle2Count;

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
