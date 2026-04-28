using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

using Microsoft.Win32;

using Newtonsoft.Json;

using RevitRebarModeler.Models;

namespace RevitRebarModeler.UI
{
    public partial class LongitudinalRebarWindow : Window
    {
        public CivilExportData LoadedData { get; private set; }
        public Dictionary<string, LongitudinalSheetSetting> SheetSettings { get; private set; }

        private ObservableCollection<LongiSheetItem> _items = new ObservableCollection<LongiSheetItem>();

        public ObservableCollection<string> Pos1Options { get; } = new ObservableCollection<string>
        {
            "내측", "중앙", "외측"
        };

        public ObservableCollection<string> Pos2Options { get; } = new ObservableCollection<string>
        {
            "+offset/2", "0", "-offset/2"
        };

        public ObservableCollection<string> DiameterOptions { get; } = new ObservableCollection<string>
        {
            "D10", "D13", "D16", "D19", "D22", "D25", "D29", "D32", "D38"
        };

        public LongitudinalRebarWindow(Autodesk.Revit.DB.Document doc)
        {
            InitializeComponent();
            DataContext = this;
            LstSheets.ItemsSource = _items;
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "JSON 파일 (*.json)|*.json",
                Title = "Civil3D 내보내기 JSON 선택"
            };

            if (dlg.ShowDialog() != true) return;
            TxtJsonPath.Text = System.IO.Path.GetFileName(dlg.FileName);

            try
            {
                string json = System.IO.File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
                LoadedData = JsonConvert.DeserializeObject<CivilExportData>(json);

                if (LoadedData?.TransverseRebars == null || LoadedData.TransverseRebars.Count == 0)
                {
                    MessageBox.Show("TransverseRebars 데이터가 없습니다.", "오류");
                    return;
                }

                BuildSheetTable();
                BtnPlace.IsEnabled = _items.Count > 0;

                int sheets = _items.Count;
                int totalInner = _items.Sum(s => s.InnerPolylineCount);
                int totalOuter = _items.Sum(s => s.OuterPolylineCount);
                TxtInfo.Text = $"프로젝트: {LoadedData.ProjectName}\n" +
                               $"구조도: {sheets}개 | 내측 곡선: {totalInner}개 | 외측 곡선: {totalOuter}개";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"JSON 파싱 오류:\n{ex.Message}", "오류");
            }
        }

        private void BuildSheetTable()
        {
            _items.Clear();

            var sheetGroups = LoadedData.TransverseRebars
                .Where(r => r.CycleNumber == 1)
                .GroupBy(r => ExtractStructureKey(r.SheetId))
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .OrderBy(g => g.Key);

            foreach (var group in sheetGroups)
            {
                var sheetRebars = group.ToList();

                // 구조도 BoundaryCenter로 내측/외측 분류
                GetBoundaryCenter(LoadedData, group.Key, out double cx, out double cy, out bool hasCenter);
                var classification = ClassifyInnerOuter(sheetRebars, cx, cy, hasCenter);

                double innerLen = 0, outerLen = 0;
                int innerCnt = 0, outerCnt = 0;
                double avgInnerDiam = 0, avgOuterDiam = 0;
                int innerDiamCnt = 0, outerDiamCnt = 0;
                var innerSegLists = new List<List<RebarSegment>>();
                var outerSegLists = new List<List<RebarSegment>>();
                foreach (var r in sheetRebars)
                {
                    bool isOuter = classification.TryGetValue(r.Id, out var o) ? o : true;
                    double len = PolylineSampler.TotalLength(r.Segments);
                    if (isOuter)
                    {
                        outerLen += len; outerCnt++;
                        if (r.DiameterMm > 0) { avgOuterDiam += r.DiameterMm; outerDiamCnt++; }
                        if (r.Segments != null && r.Segments.Count > 0) outerSegLists.Add(r.Segments);
                    }
                    else
                    {
                        innerLen += len; innerCnt++;
                        if (r.DiameterMm > 0) { avgInnerDiam += r.DiameterMm; innerDiamCnt++; }
                        if (r.Segments != null && r.Segments.Count > 0) innerSegLists.Add(r.Segments);
                    }
                }
                if (innerDiamCnt > 0) avgInnerDiam /= innerDiamCnt;
                if (outerDiamCnt > 0) avgOuterDiam /= outerDiamCnt;
                double avgTransDiam = (avgInnerDiam + avgOuterDiam) / Math.Max(1, (innerDiamCnt > 0 ? 1 : 0) + (outerDiamCnt > 0 ? 1 : 0));
                if (innerDiamCnt == 0) avgTransDiam = avgOuterDiam;
                else if (outerDiamCnt == 0) avgTransDiam = avgInnerDiam;

                // 실제 배치에서 사용할 TrimmedChain 공간 길이 (겹침 제외)
                double outerTrimmedLen = 0, innerTrimmedLen = 0;
                try
                {
                    if (outerSegLists.Count > 0)
                    {
                        var oChain = LongiCurveSampler.ConcatenatePolylinesTrimmed(outerSegLists);
                        outerTrimmedLen = LongiCurveSampler.TotalLengthTrimmed(oChain);
                    }
                    if (innerSegLists.Count > 0)
                    {
                        var iChain = LongiCurveSampler.ConcatenatePolylinesTrimmed(innerSegLists);
                        innerTrimmedLen = LongiCurveSampler.TotalLengthTrimmed(iChain);
                    }
                }
                catch { /* TrimmedChain 실패 시 기존 단순합 길이로 fallback */ }

                if (outerTrimmedLen <= 0) outerTrimmedLen = outerLen;
                if (innerTrimmedLen <= 0) innerTrimmedLen = innerLen;

                var item = new LongiSheetItem
                {
                    SheetKey = group.Key,
                    InnerPolylineCount = innerCnt,
                    OuterPolylineCount = outerCnt,
                    InnerArcLenMm = innerTrimmedLen,
                    OuterArcLenMm = outerTrimmedLen,
                    AvgTransDiameterMm = avgTransDiam,
                    CtcMm = 200,
                    DiameterLabel = "D13",
                    Count = 198,
                    Pos1 = "외측",
                    Pos2Shift = "-offset/2",
                    InnerTrimmedChain = null,
                    OuterTrimmedChain = null,
                    BCx = cx,
                    BCy = cy
                };

                try
                {
                    if (outerSegLists.Count > 0)
                        item.OuterTrimmedChain = LongiCurveSampler.MaterializeTrimmed(LongiCurveSampler.ConcatenatePolylinesTrimmed(outerSegLists));
                    if (innerSegLists.Count > 0)
                        item.InnerTrimmedChain = LongiCurveSampler.MaterializeTrimmed(LongiCurveSampler.ConcatenatePolylinesTrimmed(innerSegLists));
                }
                catch { }

                item.RefreshOffsetAuto(); // (D_횡 + D_종)/2 초기값
                item.Revalidate();
                _items.Add(item);
            }
        }

        private void GetBoundaryCenter(CivilExportData data, string structureKey, out double cx, out double cy, out bool found)
        {
            cx = cy = 0;
            found = false;
            if (data?.StructureRegions == null) return;
            var keyRegex = new Regex(@"구조도\((\d+)\)");
            foreach (var cd in data.StructureRegions)
            {
                var m = keyRegex.Match(cd.CycleKey ?? "");
                if (!m.Success) continue;
                if ($"구조도({m.Groups[1].Value})" != structureKey) continue;
                if (cd.BoundaryCenterX == 0 && cd.BoundaryCenterY == 0) continue;
                cx = cd.BoundaryCenterX;
                cy = cd.BoundaryCenterY;
                found = true;
                return;
            }
        }

        /// <summary>
        /// Civil3D 원본 저장 순서 기준 분류: 앞 절반 = 내측, 뒤 절반 = 외측.
        /// BC 인자는 시그니처 유지 목적으로 남겨두며 현재는 사용하지 않는다.
        /// </summary>
        private Dictionary<string, bool> ClassifyInnerOuter(List<TransverseRebarData> rebars, double cx, double cy, bool hasCenter)
        {
            var result = new Dictionary<string, bool>();
            if (rebars == null || rebars.Count < 2) return result;

            int half = rebars.Count / 2;
            for (int i = 0; i < rebars.Count; i++)
            {
                var r = rebars[i];
                if (r?.Segments == null || r.Segments.Count == 0) continue;
                result[r.Id] = (i >= half); // true = outer (뒤 절반)
            }
            return result;
        }

        private string ExtractStructureKey(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var match = Regex.Match(text, @"구조도\(\d+\)");
            return match.Success ? match.Value : "";
        }

        private void BtnRevalidate_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items)
            {
                RunInternalTest(item);
            }
            BtnPlace.IsEnabled = true;
        }

        private void RunInternalTest(LongiSheetItem item)
        {
            if (item.Count <= 0 || item.CtcMm <= 0)
            {
                item.ForceStatus("입력 오류", Brushes.IndianRed);
                return;
            }

            var targetChain = item.Pos1 == "내측" ? item.InnerTrimmedChain : item.OuterTrimmedChain;
            if (item.Pos1 == "중앙")
            {
                targetChain = item.OuterTrimmedChain ?? item.InnerTrimmedChain;
            }

            if (targetChain == null || targetChain.Count == 0)
            {
                item.ForceStatus("기준선 없음", Brushes.IndianRed);
                return;
            }

            try
            {
                bool offsetAway = item.Pos1 == "외측";
                var offsetSegs = LongiCurveSampler.OffsetPolyline(targetChain, item.OffsetMm, item.BCx, item.BCy, offsetAway);
                var testSamples = LongiCurveSampler.SampleFromCenterWithChordNormal(offsetSegs, item.CtcMm, item.Count);
                int realCount = testSamples.Count * 2;

                if (realCount == item.Count)
                {
                    item.ForceStatus($"검증 OK · {item.Count}개", Brushes.SeaGreen);
                }
                else
                {
                    item.ForceStatus($"검증 OK · 실제 {realCount}개", Brushes.DarkOrange);
                }
            }
            catch
            {
                item.ForceStatus("연산 실패", Brushes.IndianRed);
            }
        }

        private void BtnPlace_Click(object sender, RoutedEventArgs e)
        {
            SheetSettings = new Dictionary<string, LongitudinalSheetSetting>();

            var hardErrors = new List<string>();
            var softWarnings = new List<string>();

            foreach (var item in _items)
            {
                if (item.CtcMm <= 0) { hardErrors.Add($"[{item.SheetKey}] CTC는 0보다 커야 합니다."); continue; }
                if (item.Count <= 0) { hardErrors.Add($"[{item.SheetKey}] 갯수는 0보다 커야 합니다."); continue; }
                if (item.Count % 2 != 0) { hardErrors.Add($"[{item.SheetKey}] 갯수는 짝수여야 합니다 (현재 {item.Count})."); continue; }
                if (string.IsNullOrWhiteSpace(item.DiameterLabel)) { hardErrors.Add($"[{item.SheetKey}] 직경을 선택하세요."); continue; }

                int sets = item.Count / 2;
                if (sets % 2 == 0)
                    softWarnings.Add($"[{item.SheetKey}] 갯수 {item.Count} (4N+2 아님) — 중심 없이 ±CTC/2 대칭 배치");

                // 예측에 따른 "공간 부족" 소프트 경고 삭제 (간격 유지를 위한 정상적 스킵 처리)
                
                SheetSettings[item.SheetKey] = new LongitudinalSheetSetting
                {
                    SheetKey = item.SheetKey,
                    Pos1 = ParsePos1(item.Pos1),
                    Pos2Shift = ParsePos2Shift(item.Pos2Shift),
                    CtcMm = item.CtcMm,
                    Count = item.Count,
                    DiameterLabel = item.DiameterLabel,
                    DiameterMm = ParseDiameter(item.DiameterLabel),
                    OffsetMm = item.OffsetMm
                };
            }

            if (hardErrors.Count > 0)
            {
                MessageBox.Show("입력 오류:\n\n" + string.Join("\n", hardErrors), "오류");
                return;
            }

            if (softWarnings.Count > 0)
            {
                var result = MessageBox.Show(
                    "경고:\n\n" + string.Join("\n", softWarnings) + "\n\n계속 진행하시겠습니까?",
                    "경고", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
            }

            // 세션 캐시에 저장 — 전단철근 등 후속 명령에서 단(段) 위치 산출 시 동일 설정 사용
            SessionCache.LoadedJson = LoadedData;
            SessionCache.LongitudinalSettings = SheetSettings;

            DialogResult = true;
            Close();
        }

        private double GetRefArcLen(LongiSheetItem item)
        {
            switch (item.Pos1)
            {
                case "내측": return item.InnerArcLenMm;
                case "외측": return item.OuterArcLenMm;
                default: return (item.InnerArcLenMm + item.OuterArcLenMm) / 2.0;
            }
        }

        private Pos1Kind ParsePos1(string s)
        {
            switch (s)
            {
                case "내측": return Pos1Kind.Inner;
                case "외측": return Pos1Kind.Outer;
                default: return Pos1Kind.Center;
            }
        }

        private Pos2ShiftKind ParsePos2Shift(string s)
        {
            switch (s)
            {
                case "+offset/2": return Pos2ShiftKind.PlusHalf;
                case "-offset/2": return Pos2ShiftKind.MinusHalf;
                default: return Pos2ShiftKind.Zero;
            }
        }

        private double ParseDiameter(string label)
        {
            if (string.IsNullOrEmpty(label)) return 16;
            var m = Regex.Match(label, @"\d+");
            return m.Success ? double.Parse(m.Value) : 16;
        }
    }

    public class LongiSheetItem : INotifyPropertyChanged
    {
        public string SheetKey { get; set; }
        public int InnerPolylineCount { get; set; }
        public int OuterPolylineCount { get; set; }
        public double InnerArcLenMm { get; set; }
        public double OuterArcLenMm { get; set; }
        public double AvgTransDiameterMm { get; set; }

        public List<RebarSegment> InnerTrimmedChain { get; set; }
        public List<RebarSegment> OuterTrimmedChain { get; set; }
        public double BCx { get; set; }
        public double BCy { get; set; }

        public string ArcLenDisplay => $"내측:{InnerArcLenMm:N0} / 외측:{OuterArcLenMm:N0}";

        private string _pos1 = "중앙";
        public string Pos1 { get => _pos1; set { if (_pos1 != value) { _pos1 = value; Notify(nameof(Pos1)); Revalidate(); } } }

        private string _pos2Shift = "+offset/2";
        public string Pos2Shift { get => _pos2Shift; set { if (_pos2Shift != value) { _pos2Shift = value; Notify(nameof(Pos2Shift)); Revalidate(); } } }

        private double _ctcMm = 200;
        public double CtcMm { get => _ctcMm; set { if (Math.Abs(_ctcMm - value) > 0.01) { _ctcMm = value; Notify(nameof(CtcMm)); Revalidate(); } } }

        private string _diameterLabel = "D13";
        public string DiameterLabel
        {
            get => _diameterLabel;
            set
            {
                if (_diameterLabel != value)
                {
                    _diameterLabel = value;
                    Notify(nameof(DiameterLabel));
                    // 직경 변경 시 offset 자동 재계산 (사용자가 수동 입력 전까지)
                    RefreshOffsetAuto();
                }
            }
        }

        private double _offsetMm = 40;
        private bool _offsetUserModified = false;
        private bool _suppressUserFlag = false;
        public double OffsetMm
        {
            get => _offsetMm;
            set
            {
                if (Math.Abs(_offsetMm - value) > 0.01)
                {
                    _offsetMm = value;
                    if (!_suppressUserFlag) _offsetUserModified = true;
                    Notify(nameof(OffsetMm));
                    Revalidate();
                }
            }
        }

        /// <summary>직경 변경/로드 시 (D_횡 + D_종)/2 로 자동 재계산. 사용자가 수동 입력했으면 건너뜀.</summary>
        public void RefreshOffsetAuto()
        {
            if (_offsetUserModified) return;
            double longiDiam = ParseDiameterStatic(_diameterLabel);
            double auto = (AvgTransDiameterMm + longiDiam);
            _suppressUserFlag = true;
            OffsetMm = auto;
            _suppressUserFlag = false;
        }

        private static double ParseDiameterStatic(string label)
        {
            if (string.IsNullOrEmpty(label)) return 13;
            var m = Regex.Match(label, @"\d+");
            return m.Success ? double.Parse(m.Value) : 13;
        }

        private int _count = 10;
        public int Count { get => _count; set { if (_count != value) { _count = value; Notify(nameof(Count)); Revalidate(); } } }

        private string _statusText;
        public string StatusText { get => _statusText; private set { if (_statusText != value) { _statusText = value; Notify(nameof(StatusText)); } } }

        private Brush _statusBrush = Brushes.Black;
        public Brush StatusBrush { get => _statusBrush; private set { if (_statusBrush != value) { _statusBrush = value; Notify(nameof(StatusBrush)); } } }

        public void Revalidate()
        {
            var msgs = new List<string>();
            var level = 0; // 0=OK, 1=warn, 2=error

            if (_count <= 0) { msgs.Add("갯수 필요"); level = Math.Max(level, 2); }
            else if (_count % 2 != 0) { msgs.Add("갯수 짝수"); level = Math.Max(level, 2); }
            else
            {
                int sets = _count / 2;
                if (sets % 2 == 0) { msgs.Add("4N+2 아님"); level = Math.Max(level, 1); }
            }

            if (InnerPolylineCount == 0 || OuterPolylineCount == 0)
            {
                msgs.Add("내/외측 부족");
                level = Math.Max(level, 1);
            }

            if (_ctcMm <= 0) { msgs.Add("CTC>0"); level = Math.Max(level, 2); }

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

        public void ForceStatus(string text, Brush brush)
        {
            StatusText = text;
            StatusBrush = brush;
        }

        public static int PredictSampleCountPublic(int sets, double totalLen, double ctc) => PredictSampleCount(sets, totalLen, ctc);

        /// <summary>
        /// 실제 배치 로직(SampleFromCenterWithChordNormal)과 동일한 규칙으로 sets 개 중 공간에 들어가는 수 예측.
        /// </summary>
        private static int PredictSampleCount(int sets, double totalLen, double ctc)
        {
            if (sets <= 0 || totalLen <= 0 || ctc <= 0) return 0;
            double center = totalLen / 2.0;
            bool hasCenterPoint = (sets % 2 != 0);
            int count = 0;

            if (hasCenterPoint)
            {
                if (center >= -1e-4 && center <= totalLen + 1e-4) count++;
                int kMax = (sets - 1) / 2;
                for (int k = 1; k <= kMax; k++)
                {
                    double rPos = center + k * ctc;
                    double lPos = center - k * ctc;
                    if (rPos <= totalLen + 1e-4) count++;
                    if (lPos >= -1e-4) count++;
                }
            }
            else
            {
                int kMax = sets / 2;
                for (int k = 1; k <= kMax; k++)
                {
                    double offset = (k - 0.5) * ctc;
                    double rPos = center + offset;
                    double lPos = center - offset;
                    if (rPos <= totalLen + 1e-4) count++;
                    if (lPos >= -1e-4) count++;
                }
            }
            return count;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public enum Pos1Kind { Inner, Center, Outer }
    public enum Pos2ShiftKind { PlusHalf, Zero, MinusHalf }

    public class LongitudinalSheetSetting
    {
        public string SheetKey { get; set; }
        public Pos1Kind Pos1 { get; set; }
        public Pos2ShiftKind Pos2Shift { get; set; }
        public double CtcMm { get; set; }
        public int Count { get; set; }
        public string DiameterLabel { get; set; }
        public double DiameterMm { get; set; }
        public double OffsetMm { get; set; }
    }
}
