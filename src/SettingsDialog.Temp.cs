using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using PowerAudioManager.Commands;
using System.Windows.Threading;

namespace PowerAudioManager
{
    internal static partial class SettingsDialog
    {
        static FrameworkElement BuildTempTab(Window owner, Window dlg, SolidColorBrush fg)
        {
            var hw = HardwareMonitorService.Instance;
            hw.PrepareSelection();

            // 标题 + 传感器统计
            var stats = UiKit.Text("", 11, UiKit.TextSecondary);
            void UpdateStats() => stats.Text = hw.CatalogAvailable || hw.AllTempSensors.Count + hw.AllFanSensors.Count + hw.AllControlSensors.Count > 0
                ? $"已发现 {hw.AllTempSensors.Count} 温度 · {hw.AllFanSensors.Count} 风扇 · {hw.AllControlSensors.Count} 控制"
                : "正在加载传感器…";
            UpdateStats();
            var stack = NewPage("性能", stats);

            // ---- 刷新与阈值：三列紧凑输入 ----
            TextBox Field(Grid host, int column, string label, Color labelColor, string value, string unit)
            {
                var cell = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 5, 0, column == 2 ? 0 : 5, 0) };
                var caption = UiKit.Text(label, 11, labelColor);
                caption.Margin = new Thickness(0, 0, 0, 4);
                cell.Children.Add(caption);
                var box = UiKit.MakeTextBox(value);
                box.Padding = new Thickness(8, 4, 30, 4);
                AutomationProperties.SetName(box, label);
                var unitText = UiKit.Text(unit, 11, UiKit.TextSecondary);
                unitText.HorizontalAlignment = HorizontalAlignment.Right;
                unitText.Margin = new Thickness(0, 0, 8, 0);
                unitText.IsHitTestVisible = false;
                var boxHost = new Grid();
                boxHost.Children.Add(box);
                boxHost.Children.Add(unitText);
                cell.Children.Add(boxHost);
                Grid.SetColumn(cell, column);
                host.Children.Add(cell);
                return box;
            }
            var thresholds = new Grid();
            for (int i = 0; i < 3; i++) thresholds.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var ivBox = Field(thresholds, 0, "刷新间隔", UiKit.TextSecondary, AppPrefs.GetInt("Temp.IntervalMs", 1000).ToString(), "ms");
            var warnBox = Field(thresholds, 1, "高温警告", UiKit.WarnColor, AppPrefs.GetInt("Temp.WarnC", 80).ToString(), "°C");
            var critBox = Field(thresholds, 2, "超高温", UiKit.DangerColor, AppPrefs.GetInt("Temp.CriticalC", 95).ToString(), "°C");
            var thresholdCard = UiKit.Card(thresholds, new Thickness(12, 10, 12, 12));
            thresholdCard.Margin = new Thickness(0, 0, 0, 12);
            stack.Children.Add(thresholdCard);

            // ---- 筛选 + 添加 ----
            var state = new MetricPageState { Dialog = dlg, FilterHost = new ContentControl { VerticalAlignment = VerticalAlignment.Center } };
            var metricList = new StackPanel();
            var addBtn = UiKit.MakeButton("+ 添加", ButtonVariant.Primary);
            addBtn.Margin = new Thickness(8, 0, 0, 0);
            AutomationProperties.SetName(addBtn, "添加传感器");
            addBtn.Click += (_, _) =>
            {
                if (ShowAddSensorDialog(dlg, hw)) RefreshMetricList(metricList, hw, fg, state);
            };
            var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(addBtn, Dock.Right);
            toolbar.Children.Add(addBtn);
            toolbar.Children.Add(state.FilterHost);
            stack.Children.Add(toolbar);

            // 指标按硬件分组，每组可折叠；折叠时显示读数范围。
            RefreshMetricList(metricList, hw, fg, state);
            stack.Children.Add(metricList);
            stack.Children.Add(Hint("按硬件分组 · ⋯ 菜单可编辑、排序和删除 · 图钉 = 悬浮窗精简态显示", 4, 0));

            IDisposable preview = null;
            var previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            previewTimer.Tick += (_, _) =>
            {
                UpdateStats();
                RefreshMetricPreviewValues(state, hw);
            };
            stack.Loaded += (_, _) => { preview ??= hw.BeginCatalogPreview(); previewTimer.Start(); };
            stack.Unloaded += (_, _) => { previewTimer.Stop(); preview?.Dispose(); preview = null; };

            var btns = MakeButtons();
            ((Button)btns.Children[0]).Click += async (_, _) =>
            {
                if (!int.TryParse(ivBox.Text, out int iv) || iv < 500 || iv > 60000 ||
                    !int.TryParse(warnBox.Text, out int w) || w <= 0 ||
                    !int.TryParse(critBox.Text, out int c) || c <= 0)
                {
                    OneBoxMessage.Error(dlg, "OneBox 设置", "刷新间隔必须在 500 到 60000 毫秒之间，温度阈值必须为正整数。");
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
    }
}
