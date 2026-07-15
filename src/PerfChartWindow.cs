using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PowerAudioManager
{
    /// <summary>
    /// 双击性能趋势弹出的放大图表窗口。提供 15 分钟 / 1 小时 / 全天 三档时长切换，
    /// 每 1s 从 PerfHistory 拉最新数据刷新。全天靠 86400 容量的 ring buffer + 降采样支撑。
    /// </summary>
    public class PerfChartWindow : Window
    {
        PerfChart _chart;
        DispatcherTimer _timer;

        public PerfChartWindow()
        {
            Title = "性能趋势";
            Width = 760; Height = 360;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(28, 26, 40));
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(12) };

            // 顶部时长切换栏
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            bar.Children.Add(new TextBlock { Text = "时长：", Foreground = new SolidColorBrush(Color.FromRgb(190, 188, 220)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            var b15 = RangeBtn("15 分钟", 900);
            var b1h = RangeBtn("1 小时", 3600);
            var ball = RangeBtn("全天", 0);
            bar.Children.Add(b15); bar.Children.Add(b1h); bar.Children.Add(ball);
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);

            double ivSec = AppPrefs.GetInt("Temp.IntervalMs", 1000) / 1000.0;
            if (ivSec < 0.1) ivSec = 1;
            _chart = new PerfChart { MaxPoints = 900, EnableTooltip = true, IntervalSec = ivSec };  // 默认 15 分钟
            root.Children.Add(_chart);
            Content = root;

            _timer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();
            Refresh();
        }

        Button RangeBtn(string text, int maxPoints)
        {
            var b = new Button { Content = text, Height = 26, FontSize = 12, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 6, 0) };
            AppResources.StyleDialogButton(b, false);
            b.Click += (s, e) => { _chart.MaxPoints = maxPoints; Refresh(); };
            return b;
        }

        void Refresh()
        {
            _chart.Series = PerfHistory.GetSeries(_chart.MaxPoints);
            double window = (_chart.MaxPoints > 0 ? _chart.MaxPoints : PerfHistory.Capacity) * _chart.IntervalSec;
            _chart.Segments = ForegroundHistory.GetSegments(DateTime.Now.AddSeconds(-window), DateTime.Now);
            _chart.Refresh();
        }

        protected override void OnClosed(System.EventArgs e)
        {
            try { _timer?.Stop(); } catch { }
            base.OnClosed(e);
        }
    }
}
