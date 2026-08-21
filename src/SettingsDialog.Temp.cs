using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PowerAudioManager.Commands;
using System.Windows.Shapes;

namespace PowerAudioManager
{
    internal static partial class SettingsDialog
    {
        static ScrollViewer BuildTempTab(Window owner, Window dlg, SolidColorBrush fg)
        {
            var stack = new StackPanel { Margin = new Thickness(20) };
            var hw = HardwareMonitorService.Instance;

            // 标题
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            titleRow.Children.Add(IconCatalog.CreateElement(IconKey.Performance, 18, UiKit.FrozenBrush(UiKit.AccentColor)));
            titleRow.Children.Add(new TextBlock { Text = "性能监控", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(titleRow);

            // 传感器统计
            var stats = new TextBlock { Foreground = fg, FontSize = 10, Margin = new Thickness(0, 0, 0, 10) };
            stats.Inlines.Add($"已发现 ");
            stats.Inlines.Add(new Run($"{hw.AllTempSensors.Count}") { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
            stats.Inlines.Add($" 温度 · ");
            stats.Inlines.Add(new Run($"{hw.AllFanSensors.Count}") { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
            stats.Inlines.Add($" 风扇 · ");
            stats.Inlines.Add(new Run($"{hw.AllControlSensors.Count}") { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
            stats.Inlines.Add($" 控制");
            stack.Children.Add(stats);

            // Card: 指标列表
            var metricList = new StackPanel();
            RefreshMetricList(metricList, hw, fg);
            var metricCard = new Border { Background = new SolidColorBrush(Color.FromRgb(34, 32, 50)), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10) };
            var metricInner = new StackPanel();
            metricInner.Children.Add(new TextBlock { Text = "已添加的指标", Foreground = fg, FontSize = 10, Margin = new Thickness(2, 0, 0, 6) });
            metricInner.Children.Add(metricList);
            var addPanel = new StackPanel { Margin = new Thickness(2, 4, 2, 0) };
            var addBtn = new Button { Content = "+ 添加", Height = 26, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(addBtn, true);
            addBtn.Click += (_, _) => { addPanel.Children.Clear(); addPanel.Children.Add(BuildAddForm(metricList, addPanel, hw, fg)); };
            metricInner.Children.Add(addBtn);
            metricInner.Children.Add(addPanel);
            metricCard.Child = metricInner;
            stack.Children.Add(metricCard);

            // Card: 刷新设置
            var setCard = new Border { Background = new SolidColorBrush(Color.FromRgb(34, 32, 50)), CornerRadius = new CornerRadius(6), Padding = new Thickness(12) };
            var setInner = new StackPanel();
            setInner.Children.Add(new TextBlock { Text = "刷新设置", Foreground = fg, FontSize = 10, Margin = new Thickness(0, 0, 0, 8) });

            var ivBox = AddSetRow(setInner, "刷新间隔", AppPrefs.GetInt("Temp.IntervalMs", 1000).ToString(), "ms", 70);
            var warnBox = AddSetRowColored(setInner, Color.FromRgb(255, 140, 60), "高温警告", AppPrefs.GetInt("Temp.WarnC", 80).ToString(), "°C", 50);
            var critBox = AddSetRowColored(setInner, Color.FromRgb(255, 60, 60), "超高温", AppPrefs.GetInt("Temp.CriticalC", 95).ToString(), "°C", 50);
            setCard.Child = setInner;
            stack.Children.Add(setCard);

            var btns = MakeButtons();
            ((Button)btns.Children[0]).Click += async (_, _) =>
            {
                if (!int.TryParse(ivBox.Text, out int iv) || iv < 500 || iv > 60000 ||
                    !int.TryParse(warnBox.Text, out int w) || w <= 0 ||
                    !int.TryParse(critBox.Text, out int c) || c <= 0)
                {
                    MessageBox.Show(dlg, "刷新间隔必须在 500 到 60000 毫秒之间，温度阈值必须为正整数。",
                        "OneBox 设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!TryPersist(dlg,
                    () => AppPrefs.Set(PreferenceKeys.Monitor.IntervalMs, iv),
                    () => AppPrefs.Set(PreferenceKeys.Monitor.WarningC, w),
                    () => AppPrefs.Set(PreferenceKeys.Monitor.CriticalC, c))) return;
                if (owner is MainWindow mw)
                {
                    var result = await mw.ExecuteCommandAsync(AppCommandId.MonitorStart, CommandSource.Settings);
                    if (!result.Success) return;
                }
                dlg.DialogResult = true; dlg.Close();
            };
            ((Button)btns.Children[1]).Click += (_, _) => { dlg.DialogResult = false; dlg.Close(); };
            stack.Children.Add(btns);

            return Scroll(stack);
        }

        static TextBox AddSetRow(StackPanel parent, string label, string value, string unit, int width)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };
            row.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(190, 188, 220)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            var box = new TextBox { Text = value, Width = width, MinHeight = 26, FontSize = 11, Padding = new Thickness(6, 0, 6, 0), Background = new SolidColorBrush(Color.FromRgb(20, 18, 28)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(60, 55, 80)), BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center };
            var inputStack = new StackPanel { Orientation = Orientation.Horizontal };
            inputStack.Children.Add(box);
            inputStack.Children.Add(new TextBlock { Text = " " + unit, Foreground = new SolidColorBrush(Color.FromRgb(190, 188, 220)), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
            DockPanel.SetDock(inputStack, Dock.Right);
            row.Children.Add(inputStack);
            parent.Children.Add(row);
            return box;
        }

        static TextBox AddSetRowColored(StackPanel parent, Color dotColor, string label, string value, string unit, int width)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };
            var labelPanel = new StackPanel { Orientation = Orientation.Horizontal };
            labelPanel.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(dotColor), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            labelPanel.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(190, 188, 220)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(labelPanel);
            var box = new TextBox { Text = value, Width = width, MinHeight = 26, FontSize = 11, Padding = new Thickness(6, 0, 6, 0), Background = new SolidColorBrush(Color.FromRgb(20, 18, 28)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(60, 55, 80)), BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center };
            var inputStack = new StackPanel { Orientation = Orientation.Horizontal };
            inputStack.Children.Add(box);
            inputStack.Children.Add(new TextBlock { Text = " " + unit, Foreground = new SolidColorBrush(Color.FromRgb(190, 188, 220)), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
            DockPanel.SetDock(inputStack, Dock.Right);
            row.Children.Add(inputStack);
            parent.Children.Add(row);
            return box;
        }
    }
}


