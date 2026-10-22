using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
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
            // 读取后台持续采集的历史，关闭图表不停止采样。
            try { PerfHistory.Acquire(); } catch (Exception ex) { AppLog.Log("PerfChartWindow", ex); }
            try { ForegroundHistory.Acquire(); } catch (Exception ex) { AppLog.Log("PerfChartWindow", ex); }

            Title = "性能趋势";
            Width = 860; Height = 560;
            MinWidth = 640; MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;

            var shell = new DockPanel();

            // Title bar.
            var titleBar = new DockPanel { Height = 40, LastChildFill = true, Background = Brushes.Transparent };
            var icon = IconCatalog.CreateElement(IconKey.Performance, 16, UiKit.FrozenBrush(UiKit.AccentColor));
            icon.Margin = new Thickness(14, 0, 8, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(icon, Dock.Left);
            var close = UiKit.MakeIconButton(IconKey.Close, "关闭", 32);
            close.Margin = new Thickness(0, 0, 4, 0);
            close.Click += (s, e) => Close();
            DockPanel.SetDock(close, Dock.Right);
            titleBar.Children.Add(icon);
            titleBar.Children.Add(close);
            titleBar.Children.Add(UiKit.Text("性能趋势", 13, UiKit.TextPrimary, FontWeights.SemiBold));
            titleBar.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
            DockPanel.SetDock(titleBar, Dock.Top);
            shell.Children.Add(titleBar);

            var root = new DockPanel { Margin = new Thickness(14, 0, 14, 14) };

            // 时长切换（分段控件）+ 采样说明
            double ivSec = AppPrefs.GetInt("Temp.IntervalMs", 1000) / 1000.0;
            if (ivSec < 0.1) ivSec = 1;
            var labels = new[] { "5 分钟", "15 分钟", "30 分钟", "1 小时", "2 小时", "6 小时", "12 小时", "全天" };
            var spans = new double[] { 300, 900, 1800, 3600, 7200, 21600, 43200, 0 };
            var bar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
            var ranges = UiKit.MakeSegmented(labels, 1, i => { _chart.WindowSec = spans[i]; Refresh(); }, "时长");
            ranges.Background = UiKit.FrozenBrush(UiKit.CardColor);
            ranges.HorizontalAlignment = HorizontalAlignment.Left;
            DockPanel.SetDock(ranges, Dock.Left);
            bar.Children.Add(ranges);
            var sampling = UiKit.Text("每 " + ivSec.ToString("0.#") + " 秒采样 · 悬停查看数值", 11, UiKit.TextSecondary);
            sampling.HorizontalAlignment = HorizontalAlignment.Right;
            sampling.Margin = new Thickness(12, 0, 0, 0);
            bar.Children.Add(sampling);
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);

            // 图例芯片：点击显示/隐藏对应曲线
            _legend = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(_legend, Dock.Top);
            root.Children.Add(_legend);

            _chart = new PerfChart
            {
                WindowSec = 900, EnableTooltip = true, IntervalSec = ivSec,  // 默认 15 分钟
                ShowLegend = false, HiddenSeries = _hidden,
                WarnTemp = AppPrefs.GetInt("Temp.WarnC", 80)
            };
            root.Children.Add(_chart);
            shell.Children.Add(root);

            Content = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = UiKit.FrozenBrush(UiKit.BgColor),
                BorderBrush = UiKit.FrozenBrush(UiKit.BorderColor),
                BorderThickness = new Thickness(1),
                Child = shell
            };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();
            Refresh();
        }

        readonly HashSet<string> _hidden = new HashSet<string>(StringComparer.Ordinal);
        WrapPanel _legend;
        string _legendKey = "";
        readonly Dictionary<string, (Button Chip, Ellipse Dot, TextBlock Name, TextBlock Value, Color Color)> _chips =
            new Dictionary<string, (Button, Ellipse, TextBlock, TextBlock, Color)>(StringComparer.Ordinal);

        void UpdateLegend(List<ChartSeries> series)
        {
            string key = string.Join("\u001f", series.Select(s => s.Name + "|" + s.Color));
            if (key != _legendKey)
            {
                _legendKey = key;
                _legend.Children.Clear();
                _chips.Clear();
                foreach (var s in series)
                {
                    if (string.IsNullOrEmpty(s.Name) || _chips.ContainsKey(s.Name)) continue;
                    string name = s.Name;
                    var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                    var label = new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                    var value = new TextBlock { FontSize = 12, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                        Foreground = UiKit.FrozenBrush(UiKit.TextTertiary) };
                    var content = new StackPanel { Orientation = Orientation.Horizontal };
                    content.Children.Add(dot); content.Children.Add(label); content.Children.Add(value);
                    var chip = new Button { Content = content, Height = 26, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 6, 6) };
                    UiKit.ApplyButtonVariant(chip, ButtonVariant.Secondary);
                    chip.MinHeight = 26;
                    chip.Click += (_, _) =>
                    {
                        if (!_hidden.Remove(name)) _hidden.Add(name);
                        StyleChip(name);
                        _chart.Refresh();
                    };
                    _chips[name] = (chip, dot, label, value, s.Color);
                    _legend.Children.Add(chip);
                    StyleChip(name);
                }
            }
            foreach (var s in series)
            {
                if (s.Name == null || !_chips.TryGetValue(s.Name, out var c) || s.Points == null || s.Points.Count == 0) continue;
                c.Value.Text = s.Points[s.Points.Count - 1].ToString("0") + (s.IsTemp ? "°" : " rpm");
            }
        }

        void StyleChip(string name)
        {
            if (!_chips.TryGetValue(name, out var c)) return;
            bool on = !_hidden.Contains(name);
            c.Dot.Fill = UiKit.FrozenBrush(on ? c.Color : UiKit.TextDisabled);
            c.Name.Foreground = UiKit.FrozenBrush(on ? UiKit.TextPrimary : UiKit.TextDisabled);
            c.Chip.BorderBrush = UiKit.FrozenBrush(on ? UiKit.BorderStrongColor : UiKit.RaisedColor);
            System.Windows.Automation.AutomationProperties.SetName(c.Chip, (on ? "隐藏 " : "显示 ") + name);
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
            try { UpdateLegend(series); } catch (Exception ex) { AppLog.Log("PerfChartWindow.Legend", ex); }
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
