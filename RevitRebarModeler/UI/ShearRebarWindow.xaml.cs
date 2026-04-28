using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

using Autodesk.Revit.DB;

using Microsoft.Win32;

using Newtonsoft.Json;

using RevitRebarModeler.Models;

namespace RevitRebarModeler.UI
{
    public partial class ShearRebarWindow : Window
    {
        public CivilExportData LoadedData { get; private set; }
        public Dictionary<string, ShearSheetSetting> SheetSettings { get; private set; }

        private ObservableCollection<ShearSheetItem> _items = new ObservableCollection<ShearSheetItem>();

        public ObservableCollection<string> GroupOptions { get; } = new ObservableCollection<string> { "A", "B" };
        public ObservableCollection<string> DiameterOptions { get; } = new ObservableCollection<string>
        {
            "D10", "D13", "D16", "D19", "D22", "D25"
        };
        public ObservableCollection<string> GradeOptions { get; } = new ObservableCollection<string>
        {
            "SD400", "SD500", "SD600"
        };

        // 구조도별 host depth (mm)
        private Dictionary<string, double> _depthMap = new Dictionary<string, double>();
        // 구조도별 Revit 모델에서 자동 추출한 정보 (세션 폴백)
        private Dictionary<string, double> _autoTransCtcMap = new Dictionary<string, double>();
        private Dictionary<string, int> _autoTransUnitMap = new Dictionary<string, int>(); // 횡방향 max 단 번호
        private Dictionary<string, int> _autoLongiUnitMap = new Dictionary<string, int>();

        public ShearRebarWindow() : this(null) { }

        public ShearRebarWindow(Document doc)
        {
            InitializeComponent();
            DataContext = this;
            LstSheets.ItemsSource = _items;

            if (doc != null)
            {
                BuildDepthMap(doc);
                BuildAutoMapsFromRevit(doc);
            }

            // 세션 캐시에서 자동 로드
            if (SessionCache.LoadedJson != null)
            {
                LoadedData = SessionCache.LoadedJson;
                TxtJsonPath.Text = SessionCache.LoadedJsonPath ?? "(세션 메모리)";

                bool hasLongi = SessionCache.LongitudinalSettings != null && SessionCache.LongitudinalSettings.Count > 0;
                if (hasLongi)
                {
                    TxtSessionInfo.Text = $"세션 JSON 로드됨 · 종방향 철근 설정 {SessionCache.LongitudinalSettings.Count}건 발견 → 단(段) 자동 산출 가능";
                    TxtSessionInfo.Foreground = System.Windows.Media.Brushes.SeaGreen;
                }
                else
                {
                    TxtSessionInfo.Text = "세션 JSON 로드됨. 종방향 철근 배치를 먼저 실행하면 단(段) 위치를 자동으로 가져옵니다.";
                    TxtSessionInfo.Foreground = System.Windows.Media.Brushes.DarkOrange;
                }

                BuildSheetTable();
                BtnPlace.IsEnabled = _items.Count > 0;
            }
            else
            {
                TxtSessionInfo.Text = "세션 메모리에 JSON이 없습니다. [JSON 다시 선택]으로 파일을 불러오세요.";
                TxtSessionInfo.Foreground = System.Windows.Media.Brushes.IndianRed;
            }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "JSON 파일 (*.json)|*.json",
                Title = "Civil3D 내보내기 JSON 선택"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                string json = System.IO.File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
                LoadedData = JsonConvert.DeserializeObject<CivilExportData>(json);

                if (LoadedData?.TransverseRebars == null || LoadedData.TransverseRebars.Count == 0)
                {
                    MessageBox.Show("TransverseRebars 데이터가 없습니다.", "오류");
                    return;
                }

                // 세션 캐시 갱신
                SessionCache.LoadedJson = LoadedData;
                SessionCache.LoadedJsonPath = dlg.FileName;
                TxtJsonPath.Text = System.IO.Path.GetFileName(dlg.FileName);
                TxtSessionInfo.Text = "JSON을 다시 로드하여 세션 메모리에 갱신했습니다.";

                BuildSheetTable();
                BtnPlace.IsEnabled = _items.Count > 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"JSON 파싱 오류:\n{ex.Message}", "오류");
            }
        }

        private void BuildSheetTable()
        {
            _items.Clear();
            if (LoadedData?.TransverseRebars == null) return;

            var sheetGroups = LoadedData.TransverseRebars
                .Where(r => r.CycleNumber == 1)
                .GroupBy(r => ExtractStructureKey(r.SheetId))
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .OrderBy(g => g.Key);

            int totalSheets = 0, totalRebars = 0;
            var longiMap = SessionCache.LongitudinalSettings;
            var transCtcMap = SessionCache.TransverseCtcMap;
            foreach (var group in sheetGroups)
            {
                int rebarCnt = group.Count();
                // 종철근 단 수: 1) Revit 모델 Mark 역파싱 (가장 정확) → 2) 세션 LongitudinalSettings 추정
                int longiUnits = 0;
                if (_autoLongiUnitMap.TryGetValue(group.Key, out int autoLU))
                    longiUnits = autoLU;
                // 폴백: 세션에서 (Count / 2 = 샘플 위치 수 추정) — 실제 배치 개수와 다를 수 있음
                if (longiUnits == 0 && longiMap != null && longiMap.TryGetValue(group.Key, out var ls))
                    longiUnits = ls.Count / 2;

                // 횡철근 단 수: 1) Revit 모델 Mark max 단 번호 (가장 정확) → 2) 세션/자동 CTC + depth 계산
                int transUnits = 0;
                if (_autoTransUnitMap.TryGetValue(group.Key, out int autoTU))
                    transUnits = autoTU;

                if (transUnits == 0) // 폴백: CTC + depth 공식
                {
                    double ctcUsed = 0;
                    if (transCtcMap != null && transCtcMap.TryGetValue(group.Key, out double ctc) && ctc > 0)
                        ctcUsed = ctc;
                    else if (_autoTransCtcMap.TryGetValue(group.Key, out double autoCtc) && autoCtc > 0)
                        ctcUsed = autoCtc;

                    if (ctcUsed > 0 && _depthMap.TryGetValue(group.Key, out double depthMm) && depthMm > 0)
                    {
                        double stride = ctcUsed / 2.0;
                        transUnits = (int)Math.Floor(depthMm / stride) + 1;
                    }
                }

                var item = new ShearSheetItem
                {
                    SheetKey = group.Key,
                    TransRebarCount = rebarCnt,    // 원본 폴리라인 수 (참고용)
                    TransUnitCount = transUnits,    // 횡방향 단 수 (배치 기준)
                    LongiUnitCount = longiUnits,
                    GroupSize = 3,
                    StartGroup = "A",
                    DiameterLabel = "D13",
                    Grade = "SD400",
                    HookLengthMm = 100
                };
                item.Revalidate();
                _items.Add(item);
                totalSheets++;
                totalRebars += rebarCnt;
            }

            TxtInfo.Text = $"프로젝트: {LoadedData.ProjectName}  ·  구조도 {totalSheets}개  ·  횡철근 합계 {totalRebars}개";
        }

        private string ExtractStructureKey(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var match = Regex.Match(text, @"구조도\(\d+\)");
            return match.Success ? match.Value : "";
        }

        /// <summary>
        /// Revit 모델에 이미 배치된 종방향/횡방향 Rebar 객체에서
        /// 자동으로 단 수 / CTC 정보를 추출 (세션이 비어 있는 기존 도면 대응).
        /// </summary>
        private void BuildAutoMapsFromRevit(Document doc)
        {
            _autoTransCtcMap.Clear();
            _autoTransUnitMap.Clear();
            _autoLongiUnitMap.Clear();
            try
            {
                var rebars = new FilteredElementCollector(doc)
                    .OfClass(typeof(Autodesk.Revit.DB.Structure.Rebar))
                    .Cast<Autodesk.Revit.DB.Structure.Rebar>()
                    .ToList();

                // 종방향: Mark = 구조도(N)_longi_(outer|inner)_M단 → 구조도별 max(M)
                var longiRegex = new Regex(@"^(구조도\(\d+\))_longi_(outer|inner)_(\d+)단$");
                // 횡방향: Mark = 구조도(N)_M단_(inner|outer)_K → 구조도별 (M, Z 평균) 모음
                var transRegex = new Regex(@"^(구조도\(\d+\))_(\d+)단_(inner|outer)_(\d+)$");

                var longiMaxByKey = new Dictionary<string, int>();
                var transMaxByKey = new Dictionary<string, int>(); // 횡방향 max 단 번호
                var transZByKey = new Dictionary<string, Dictionary<int, List<double>>>();

                foreach (var r in rebars)
                {
                    string mk = r.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";

                    var lm = longiRegex.Match(mk);
                    if (lm.Success)
                    {
                        string sk = lm.Groups[1].Value;
                        int dan = int.Parse(lm.Groups[3].Value);
                        if (!longiMaxByKey.TryGetValue(sk, out int cur) || dan > cur)
                            longiMaxByKey[sk] = dan;
                        continue;
                    }

                    var tm = transRegex.Match(mk);
                    if (tm.Success)
                    {
                        string sk = tm.Groups[1].Value;
                        int dan = int.Parse(tm.Groups[2].Value);

                        // 횡방향 max 단 번호 추적
                        if (!transMaxByKey.TryGetValue(sk, out int curMax) || dan > curMax)
                            transMaxByKey[sk] = dan;

                        // Y 좌표 추출 (Revit Y = 종방향 오프셋 = 단 위치)
                        double y = TryGetRebarY(r);
                        if (double.IsNaN(y)) continue;
                        if (!transZByKey.TryGetValue(sk, out var zMap))
                        {
                            zMap = new Dictionary<int, List<double>>();
                            transZByKey[sk] = zMap;
                        }
                        if (!zMap.TryGetValue(dan, out var list))
                        {
                            list = new List<double>();
                            zMap[dan] = list;
                        }
                        list.Add(y);
                        continue;
                    }
                }

                // 종방향: 단 수
                foreach (var kv in longiMaxByKey)
                    _autoLongiUnitMap[kv.Key] = kv.Value;

                // 횡방향: max 단 번호 저장
                foreach (var kv in transMaxByKey)
                    _autoTransUnitMap[kv.Key] = kv.Value;

                // 횡방향: 1단·2단 Z 차이 = stride = CTC/2 → CTC = stride × 2
                foreach (var kv in transZByKey)
                {
                    string sk = kv.Key;
                    var zMap = kv.Value;
                    if (!zMap.ContainsKey(1) || !zMap.ContainsKey(2)) continue;
                    double z1 = zMap[1].Average();
                    double z2 = zMap[2].Average();
                    double strideFt = Math.Abs(z2 - z1);
                    if (strideFt <= 0) continue;
                    double strideMm = strideFt * 304.8;
                    double ctc = strideMm * 2.0;
                    _autoTransCtcMap[sk] = ctc;
                }
            }
            catch { }
        }

        /// <summary>Rebar 첫 centerline curve 시작점의 Y (ft) — 종방향 오프셋을 나타냄.</summary>
        /// <remarks>
        /// 좌표 규약: Civil3D X → Revit X, Civil3D Y → Revit Z, 종방향 오프셋 → Revit Y.
        /// 횡단 Rebar의 단(단면 반복 위치)는 Revit Y에 담겨 있으므로 .Y를 읽어야 함.
        /// </remarks>
        private double TryGetRebarY(Autodesk.Revit.DB.Structure.Rebar r)
        {
            try
            {
                var curves = r.GetCenterlineCurves(false, false, false,
                    Autodesk.Revit.DB.Structure.MultiplanarOption.IncludeOnlyPlanarCurves, 0);
                if (curves == null || curves.Count == 0) return double.NaN;
                return curves[0].GetEndPoint(0).Y;  // Revit Y = 종방향 오프셋
            }
            catch { return double.NaN; }
        }

        /// <summary>
        /// Revit 호스트(구조 프레임)의 Comments 에서 depth=N 추출 → 구조도별 depth(mm) 매핑.
        /// </summary>
        private void BuildDepthMap(Document doc)
        {
            _depthMap.Clear();
            try
            {
                var hosts = new FilteredElementCollector(doc)
                    .OfClass(typeof(Autodesk.Revit.DB.FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_StructuralFraming)
                    .WhereElementIsNotElementType()
                    .ToList();
                if (hosts.Count == 0)
                {
                    hosts = new FilteredElementCollector(doc)
                        .OfClass(typeof(Autodesk.Revit.DB.FamilyInstance))
                        .OfCategory(BuiltInCategory.OST_GenericModel)
                        .WhereElementIsNotElementType()
                        .ToList();
                }
                var depthRegex = new Regex(@"depth=(\d+\.?\d*)");
                foreach (var elem in hosts)
                {
                    string comments = elem.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
                    string key = ExtractStructureKey(comments);
                    if (string.IsNullOrEmpty(key)) continue;
                    var m = depthRegex.Match(comments);
                    if (m.Success && double.TryParse(m.Groups[1].Value, out double d) && d > 0)
                    {
                        if (!_depthMap.ContainsKey(key)) _depthMap[key] = d;
                    }
                }
            }
            catch { }
        }

        private void BtnPlace_Click(object sender, RoutedEventArgs e)
        {
            SheetSettings = new Dictionary<string, ShearSheetSetting>();

            var hardErrors = new List<string>();
            foreach (var item in _items)
            {
                if (item.GroupSize <= 0) { hardErrors.Add($"[{item.SheetKey}] 묶음 수는 0보다 커야 합니다."); continue; }
                if (item.HookLengthMm < 0) { hardErrors.Add($"[{item.SheetKey}] 후크 길이는 음수일 수 없습니다."); continue; }
                if (string.IsNullOrWhiteSpace(item.DiameterLabel)) { hardErrors.Add($"[{item.SheetKey}] 직경을 선택하세요."); continue; }

                SheetSettings[item.SheetKey] = new ShearSheetSetting
                {
                    SheetKey = item.SheetKey,
                    GroupSize = item.GroupSize,
                    StartGroup = item.StartGroup == "B" ? ShearStartGroup.B : ShearStartGroup.A,
                    DiameterLabel = item.DiameterLabel,
                    DiameterMm = ParseDiameter(item.DiameterLabel),
                    Grade = item.Grade,
                    HookLengthMm = item.HookLengthMm
                };
            }

            if (hardErrors.Count > 0)
            {
                MessageBox.Show("입력 오류:\n\n" + string.Join("\n", hardErrors), "오류");
                return;
            }

            DialogResult = true;
            Close();
        }

        private double ParseDiameter(string label)
        {
            if (string.IsNullOrEmpty(label)) return 13;
            var m = Regex.Match(label, @"\d+");
            return m.Success ? double.Parse(m.Value) : 13;
        }
    }

    public enum ShearStartGroup { A, B }

    public class ShearSheetSetting
    {
        public string SheetKey { get; set; }
        public int GroupSize { get; set; }
        public ShearStartGroup StartGroup { get; set; }
        public string DiameterLabel { get; set; }
        public double DiameterMm { get; set; }
        public string Grade { get; set; }
        public double HookLengthMm { get; set; }
    }

    public class ShearSheetItem : INotifyPropertyChanged
    {
        public string SheetKey { get; set; }
        public int TransRebarCount { get; set; } // JSON 폴리라인 수 (참고용)
        public int TransUnitCount { get; set; }  // 횡방향 단 수 (= depth ÷ (CTC/2) + 1)
        public int LongiUnitCount { get; set; }  // 종방향 단 수 (= LongiSettings.Count / 2)

        private int _groupSize = 3;
        public int GroupSize
        {
            get => _groupSize;
            set { if (_groupSize != value) { _groupSize = value; Notify(nameof(GroupSize)); Notify(nameof(GroupPreview)); Revalidate(); } }
        }

        private string _startGroup = "A";
        public string StartGroup
        {
            get => _startGroup;
            set { if (_startGroup != value) { _startGroup = value; Notify(nameof(StartGroup)); Revalidate(); } }
        }

        private string _diameterLabel = "D13";
        public string DiameterLabel
        {
            get => _diameterLabel;
            set { if (_diameterLabel != value) { _diameterLabel = value; Notify(nameof(DiameterLabel)); Revalidate(); } }
        }

        private string _grade = "SD400";
        public string Grade
        {
            get => _grade;
            set { if (_grade != value) { _grade = value; Notify(nameof(Grade)); } }
        }

        private double _hookLengthMm = 100;
        public double HookLengthMm
        {
            get => _hookLengthMm;
            set { if (Math.Abs(_hookLengthMm - value) > 0.01) { _hookLengthMm = value; Notify(nameof(HookLengthMm)); Revalidate(); } }
        }

        /// <summary>
        /// 묶음 분할 미리보기 (횡방향 단 수 기준, 한 단씩 겹침).
        /// GroupSize=3, 횡방향 단=10 → "[1-3] [3-5] [5-7] [7-9]"
        /// </summary>
        public string GroupPreview
        {
            get
            {
                int total = TransUnitCount;
                int g = _groupSize;
                if (total <= 0) return "(횡방향 미배치 / depth·CTC 미상)";
                if (g <= 0) return "—";
                if (g > total) return $"묶음 수({g}) > 횡방향 단({total})";

                int step = Math.Max(1, g - 1);
                var parts = new List<string>();
                int s = 1;
                while (s + g - 1 <= total)
                {
                    int e = s + g - 1;
                    parts.Add(g == 1 ? $"[{s}]" : $"[{s}-{e}]");
                    s += step;
                }
                int lastEndCovered = (parts.Count == 0) ? 0 : (1 + (parts.Count - 1) * step + g - 1);
                int leftover = total - lastEndCovered;
                string leftoverText = leftover > 0 ? $"  마지막 {leftover}단 미배치" : "";
                return string.Join(" ", parts) + leftoverText;
            }
        }

        private string _statusText = "준비됨";
        public string StatusText
        {
            get => _statusText;
            private set { if (_statusText != value) { _statusText = value; Notify(nameof(StatusText)); } }
        }

        private Brush _statusBrush = Brushes.SteelBlue;
        public Brush StatusBrush
        {
            get => _statusBrush;
            private set { if (_statusBrush != value) { _statusBrush = value; Notify(nameof(StatusBrush)); } }
        }

        public void Revalidate()
        {
            var msgs = new List<string>();
            int level = 0;

            if (_groupSize <= 0) { msgs.Add("묶음 수>0"); level = Math.Max(level, 2); }
            if (_hookLengthMm < 0) { msgs.Add("후크 ≥0"); level = Math.Max(level, 2); }
            if (TransRebarCount <= 0) { msgs.Add("횡철근 없음"); level = Math.Max(level, 2); }
            else if (_groupSize > TransRebarCount) { msgs.Add("묶음>횡철근수"); level = Math.Max(level, 1); }

            int leftover = (_groupSize > 0) ? TransRebarCount % _groupSize : 0;
            if (leftover > 0 && leftover < _groupSize)
                msgs.Add($"나머지 {leftover}개 미배치");

            if (msgs.Count == 0)
            {
                StatusText = "준비됨";
                StatusBrush = Brushes.SteelBlue;
            }
            else
            {
                StatusText = string.Join(" · ", msgs);
                StatusBrush = level == 2 ? Brushes.IndianRed : Brushes.DarkOrange;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
