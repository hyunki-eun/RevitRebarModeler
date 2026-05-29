using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RevitRebarModeler.UI
{
    public partial class ShearGroupHelpWindow : Window
    {
        // 다이어그램 가정: 횡방향 단 10개, 묶음 수 3, 한 단씩 겹침 → [1-3] [3-5] [5-7] [7-9]
        private const int UnitCount = 10;
        private const int GroupSize = 3;

        // 묶음 시작 단 번호 (각 묶음의 첫 단). step = GroupSize - 1 = 2
        private static readonly int[] GroupStartUnits = { 1, 3, 5, 7 };

        private static readonly Brush BarBrush   = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));
        private static readonly Brush ABrush     = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        private static readonly Brush BBrush     = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
        private static readonly Brush ABrushFill = new SolidColorBrush(Color.FromArgb(0x40, 0x3B, 0x82, 0xF6));
        private static readonly Brush BBrushFill = new SolidColorBrush(Color.FromArgb(0x40, 0x22, 0xC5, 0x5E));

        public ShearGroupHelpWindow()
        {
            InitializeComponent();
            Loaded += (_, __) => Redraw();
            SizeChanged += (_, __) => Redraw();
        }

        private void Redraw()
        {
            DrawDiagram(CnvA, startWithA: true);
            DrawDiagram(CnvB, startWithA: false);
        }

        /// <summary>
        /// Canvas 1개에 다이어그램 그리기.
        /// 가로축 = 횡방향 단(1~10), 묶음을 사각형 윤곽으로 표시.
        /// </summary>
        private void DrawDiagram(Canvas cnv, bool startWithA)
        {
            cnv.Children.Clear();
            double W = cnv.ActualWidth;
            double H = cnv.ActualHeight;
            if (W < 50 || H < 50) return;

            const double padLeft = 36;
            const double padRight = 24;
            const double padTop = 38;
            const double padBottom = 64;
            double plotW = W - padLeft - padRight;
            double plotH = H - padTop - padBottom;
            double yBars = padTop + plotH * 0.55;  // 횡철근 점 라인 Y
            double spacing = plotW / (UnitCount - 1);

            // 상단 라벨: "횡방향 단 →"
            var topLabel = new TextBlock
            {
                Text = "← 횡방향 단 →",
                FontSize = 11,
                Foreground = LabelBrush,
                FontWeight = FontWeights.SemiBold
            };
            Canvas.SetLeft(topLabel, padLeft + plotW / 2 - 40);
            Canvas.SetTop(topLabel, 8);
            cnv.Children.Add(topLabel);

            // 가로 기준선
            var baseline = new Line
            {
                X1 = padLeft, Y1 = yBars,
                X2 = padLeft + plotW, Y2 = yBars,
                Stroke = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 3 }
            };
            cnv.Children.Add(baseline);

            // 횡철근 점 + 번호
            for (int i = 0; i < UnitCount; i++)
            {
                double x = padLeft + i * spacing;
                var dot = new Ellipse
                {
                    Width = 8, Height = 8,
                    Fill = BarBrush
                };
                Canvas.SetLeft(dot, x - 4);
                Canvas.SetTop(dot, yBars - 4);
                cnv.Children.Add(dot);

                var num = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = 10,
                    Foreground = LabelBrush
                };
                num.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(num, x - num.DesiredSize.Width / 2);
                Canvas.SetTop(num, yBars + 10);
                cnv.Children.Add(num);
            }

            // 묶음 사각형 + 라벨 (한 단씩 겹치므로 상하 교대로 배치하여 시각 충돌 방지)
            for (int gi = 0; gi < GroupStartUnits.Length; gi++)
            {
                int startUnit = GroupStartUnits[gi];
                int endUnit = startUnit + GroupSize - 1;
                if (endUnit > UnitCount) break;

                bool isA = startWithA ? (gi % 2 == 0) : (gi % 2 == 1);
                Brush stroke = isA ? ABrush : BBrush;
                Brush fill = isA ? ABrushFill : BBrushFill;

                double xs = padLeft + (startUnit - 1) * spacing;
                double xe = padLeft + (endUnit - 1) * spacing;
                double rectW = xe - xs;

                // 묶음을 상하 교대로 배치 (홀수번째=위, 짝수번째=아래)
                bool above = (gi % 2 == 0);
                double rectH = 28;
                double rectY = above ? (yBars - rectH - 14) : (yBars + 26);

                // 묶음 사각형 (둥근 모서리)
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = rectW + 14,
                    Height = rectH,
                    Stroke = stroke,
                    StrokeThickness = 2,
                    Fill = fill,
                    RadiusX = 6, RadiusY = 6
                };
                Canvas.SetLeft(rect, xs - 7);
                Canvas.SetTop(rect, rectY);
                cnv.Children.Add(rect);

                // 라벨 (A 또는 B)
                var label = new TextBlock
                {
                    Text = isA ? "A" : "B",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = stroke
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, xs + rectW / 2 + 3 - label.DesiredSize.Width / 2);
                Canvas.SetTop(label, rectY + rectH / 2 - label.DesiredSize.Height / 2);
                cnv.Children.Add(label);

                // 범위 텍스트 ([1-3])
                var range = new TextBlock
                {
                    Text = $"[{startUnit}-{endUnit}]",
                    FontSize = 10,
                    Foreground = stroke,
                    Opacity = 0.8
                };
                range.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(range, xs + rectW / 2 + 3 - range.DesiredSize.Width / 2);
                Canvas.SetTop(range, above ? rectY - 14 : rectY + rectH + 2);
                cnv.Children.Add(range);
            }

            // 하단 안내
            var footer = new TextBlock
            {
                Text = $"묶음 수 = {GroupSize}, 횡방향 단 = {UnitCount} (예시)",
                FontSize = 10,
                FontStyle = FontStyles.Italic,
                Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
            };
            Canvas.SetLeft(footer, padLeft);
            Canvas.SetBottom(footer, 8);
            cnv.Children.Add(footer);
        }
    }
}
