using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PowerAudioManager
{
    /// <summary>
    /// 双击性能趋势弹出的放大图表窗口。时长档位：5分/15分/30分/1时/2时/6时/12时/全天，
    /// 默认 15 分钟。每 1s 从 PerfHistory 拉时间窗内最新数据刷新；缺口处断线而非填旧值。
    /// 全天靠 86400 容量的 ring buffer + 像素级去密支撑。
    /// </summary>
    public class PerfChartWindow : Window
    {
        PerfChart _chart;
        DispatcherTimer _timer;

        public PerfChartWindow()
        {
            // 持有历史数据引用计数：构造时 Load，关闭时 Save+Clear 释放内存。
            // 图表未打开期间 PerfHistory/ForegroundHistory 不驻留内存、不采集。
            try { PerfHistory.Acquire(); } catch (Exception ex) { AppLog.Log("PerfChartWindow", ex); }
            try { ForegroundHistory.Acquire(); } catch (Exception ex) { AppLog.Log("PerfChartWindow", ex); }

            Title = "性能趋势";
            Width = 820; Height = 360;
            MinWidth = 640;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(28, 26, 40));
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(12) };

            // 顶部时长切换栏
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            bar.Children.Add(new TextBlock { Text = "时长：", Foreground = new SolidColorBrush(Color.FromRgb(190, 188, 220)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            bar.Children.Add(RangeBtn("5 分钟", 300));
            bar.Children.Add(RangeBtn("15 分钟", 900));
            bar.Children.Add(RangeBtn("30 分钟", 1800));
            bar.Children.Add(RangeBtn("1 小时", 3600));
            bar.Children.Add(RangeBtn("2 小时", 7200));
            bar.Children.Add(RangeBtn("6 小时", 21600));
            bar.Children.Add(RangeBtn("12 小时", 43200));
            bar.Children.Add(RangeBtn("全天", 0));
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);

            double ivSec = AppPrefs.GetInt("Temp.IntervalMs", 1000) / 1000.0;
            if (ivSec < 0.1) ivSec = 1;
            _chart = new PerfChart { WindowSec = 900, EnableTooltip = true, IntervalSec = ivSec };  // 默认 15 分钟
            root.Children.Add(_chart);
            Content = root;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();
            Refresh();
        }

        Button RangeBtn(string text, double windowSec)
        {
            var b = new Button { Content = text, Height = 26, FontSize = 12, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 4, 0) };
            AppResources.StyleDialogButton(b, false);
            b.Click += (s, e) => { _chart.WindowSec = windowSec; Refresh(); };
            return b;
        }

        void Refresh()
        {
            double windowSec = _chart.WindowSec > 0 ? _chart.WindowSec : PerfHistory.Capacity * _chart.IntervalSec;
            DateTime to = DateTime.Now;
            DateTime from = to.AddSeconds(-windowSec);
            var series = PerfHistory.GetSeries(from, to);
            // 图表刚打开且窗口内暂无数据（上次记录早于窗口起点）时，把窗口锚到最后一条历史记录，
            // 让以前记录的数据立即可见；新数据到达后窗口自然滑回当前时间。
            if (series.Count == 0)
            {
                var last = PerfHistory.GetLastTime();
                if (last.HasValue && last.Value < from)
                {
                    to = last.Value.AddSeconds(Math.Max(_chart.IntervalSec, 1));
                    if (to > DateTime.Now) to = DateTime.Now;
                    from = to.AddSeconds(-windowSec);
                    series = PerfHistory.GetSeries(from, to);
                }
            }
            _chart.Series = series;
            _chart.Segments = ForegroundHistory.GetSegments(from, to);
            _chart.Refresh();
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _timer?.Stop(); } catch { }
            try { PerfHistory.Release(); } catch { }
            try { ForegroundHistory.Release(); } catch { }
            base.OnClosed(e);
        }
    }
}
