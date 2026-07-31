using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerAudioManager
{
    internal static partial class SettingsDialog
    {
        static readonly string[] IconKeyOptions = { "cpu", "gpu", "hot", "vram", "dram", "disk", "fan", "ctrl", "mb", "def" };

        static readonly string[] IconKeyLabels = { "CPU芯片", "GPU显卡", "火焰", "显存", "内存条", "硬盘", "风扇", "滑动条", "主板", "圆点" };

        static void RefreshMetricList(StackPanel list, HardwareMonitorService hw, SolidColorBrush fg)
        {
            list.Children.Clear();
            foreach (var key in hw.EnabledMetrics)
            {
                string displayName, iconKey;
                var cfg = HardwareMonitorService.DecodeConfig(key, out displayName, out iconKey);
                if (cfg == null) continue;
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };
                string unit = string.Equals(cfg.SensorType, "Temperature", StringComparison.OrdinalIgnoreCase) ? "°C" :
                              string.Equals(cfg.SensorType, "Control", StringComparison.OrdinalIgnoreCase) ? "%" : "RPM";
                float? val = hw.ReadSensorPreview(cfg);
                string valStr = val.HasValue ? $" {val.Value:0}{unit}" : "";

                // 矢量图标 + 名称 + 值
                var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
                var iconColor = UiKit.MetricIconColorByKey(iconKey);
                nameRow.Children.Add(UiKit.MetricIcon(iconKey, iconColor));
                nameRow.Children.Add(new TextBlock { Text = " " + displayName, Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                nameRow.Children.Add(new TextBlock { Text = valStr, Foreground = fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
                nameRow.Children.Add(new TextBlock { Text = $"  {cfg.SensorName}", Foreground = fg, FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
                row.Children.Add(nameRow);

                // 编辑按钮 → 内联编辑所有属性
                var editBtn = new Button { Content = "✎", Width = 24, Height = 22, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Foreground = fg, FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(0), ToolTip = "编辑" };
                UiKit.ApplyFlatStyle(editBtn); editBtn.MinWidth = 0; editBtn.MinHeight = 0;
                var delBtn = new Button { Content = "✕", Width = 24, Height = 22, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(200, 100, 100)), FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(0), ToolTip = "删除" };
                UiKit.ApplyFlatStyle(delBtn); delBtn.MinWidth = 0; delBtn.MinHeight = 0;

                string capturedKey = key;
                var capturedList = list;
                delBtn.Click += (s2, e2) =>
                {
                    var updated = new List<string>(hw.EnabledMetrics);
                    updated.Remove(capturedKey);
                    hw.SaveEnabledMetrics(updated);
                    RefreshMetricList(capturedList, hw, fg);
                };
                editBtn.Click += (s2, e2) =>
                {
                    // 展开内联编辑面板
                    row.Children.Clear();
                    var editPanel = new StackPanel();
                    // 名称
                    editPanel.Children.Add(new TextBlock { Text = "名称", Foreground = fg, FontSize = 10, Margin = new Thickness(0, 0, 0, 2) });
                    var nameBox = new TextBox { Text = displayName, Width = 120, Height = 22, FontSize = 11, Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)) };
                    editPanel.Children.Add(nameBox);
                    // 图标
                    editPanel.Children.Add(new TextBlock { Text = "图标", Foreground = fg, FontSize = 10, Margin = new Thickness(0, 6, 0, 2) });
                    var iconCombo = new ComboBox { Height = 24, FontSize = 11, Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)) };
                    AppResources.StyleDarkComboBox(iconCombo);
                    int selIcon = 0;
                    for (int ii = 0; ii < IconKeyOptions.Length; ii++)
                    {
                        var ik = IconKeyOptions[ii];
                        var panel = new StackPanel { Orientation = Orientation.Horizontal };
                        var iconImg = UiKit.MetricIcon(ik, UiKit.MetricIconColorByKey(ik));
                        iconImg.Width = 14; iconImg.Height = 14;
                        panel.Children.Add(iconImg);
                        panel.Children.Add(new TextBlock { Text = " " + IconKeyLabels[ii], FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
                        var item = new ComboBoxItem { Content = panel, Tag = ik };
                        iconCombo.Items.Add(item);
                        if (ik == iconKey) selIcon = ii;
                    }
                    iconCombo.SelectedIndex = selIcon;
                    editPanel.Children.Add(iconCombo);
                    // 传感器
                    editPanel.Children.Add(new TextBlock { Text = "传感器", Foreground = fg, FontSize = 10, Margin = new Thickness(0, 6, 0, 2) });
                    var sensorCombo2 = new ComboBox { Height = 24, FontSize = 11, Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)) };
                    AppResources.StyleDarkComboBox(sensorCombo2);
                    var pool = string.Equals(cfg.SensorType, "Fan", StringComparison.OrdinalIgnoreCase) ? hw.AllFanSensors :
                               string.Equals(cfg.SensorType, "Control", StringComparison.OrdinalIgnoreCase) ? hw.AllControlSensors : hw.AllTempSensors;
                    int selSensor = 0;
                    for (int si = 0; si < pool.Count; si++)
                    {
                        var s = pool[si];
                        sensorCombo2.Items.Add(new ComboBoxItem { Content = $"{s.HardwareName} — {s.SensorName}", Tag = HardwareMonitorService.EncodeConfig(s, displayName, iconKey) });
                        if (s.HardwareName == cfg.HardwareName && s.SensorName == cfg.SensorName) selSensor = si;
                    }
                    sensorCombo2.SelectedIndex = selSensor;
                    editPanel.Children.Add(sensorCombo2);
                    // 保存/取消
                    var actRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                    var saveBtn = new Button { Content = "保存", Height = 22, FontSize = 11, Padding = new Thickness(8, 0, 8, 0) };
                    AppResources.StyleDialogButton(saveBtn, true);
                    var cancelBtn2 = new Button { Content = "取消", Height = 22, FontSize = 11, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(6, 0, 0, 0) };
                    AppResources.StyleDialogButton(cancelBtn2, false);
                    actRow.Children.Add(saveBtn); actRow.Children.Add(cancelBtn2);
                    editPanel.Children.Add(actRow);

                    saveBtn.Click += (s3, e3) =>
                    {
                        var selKey = (sensorCombo2.SelectedItem as ComboBoxItem)?.Tag as string ?? capturedKey;
                        var newIcon = (iconCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? iconKey;
                        var newName = string.IsNullOrWhiteSpace(nameBox.Text) ? displayName : nameBox.Text.Trim();
                        // 重建 key：用新传感器 + 新名称 + 新图标
                        string dn2; string ik2;
                        var newCfg = HardwareMonitorService.DecodeConfig(selKey, out dn2, out ik2);
                        var finalKey = HardwareMonitorService.EncodeConfig(newCfg, newName, newIcon);
                        var updated = new List<string>(hw.EnabledMetrics);
                        int idx = updated.IndexOf(capturedKey);
                        if (idx >= 0) updated[idx] = finalKey;
                        else updated.Add(finalKey);
                        hw.SaveEnabledMetrics(updated);
                        RefreshMetricList(capturedList, hw, fg);
                    };
                    cancelBtn2.Click += (s3, e3) => RefreshMetricList(capturedList, hw, fg);

                    row.Children.Add(editPanel);
                };

                // 上移/下移按钮
                int curIdx = hw.EnabledMetrics.IndexOf(capturedKey);
                var upBtn = new Button { Content = "▲", Width = 24, Height = 22, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Foreground = fg, FontSize = 9, Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(0), ToolTip = "上移", IsEnabled = curIdx > 0 };
                UiKit.ApplyFlatStyle(upBtn); upBtn.MinWidth = 0; upBtn.MinHeight = 0;
                var downBtn = new Button { Content = "▼", Width = 24, Height = 22, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Foreground = fg, FontSize = 9, Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(0), ToolTip = "下移", IsEnabled = curIdx < hw.EnabledMetrics.Count - 1 };
                UiKit.ApplyFlatStyle(downBtn); downBtn.MinWidth = 0; downBtn.MinHeight = 0;

                upBtn.Click += (s3, e3) =>
                {
                    var updated = new List<string>(hw.EnabledMetrics);
                    int idx = updated.IndexOf(capturedKey);
                    if (idx > 0) { var tmp = updated[idx]; updated[idx] = updated[idx - 1]; updated[idx - 1] = tmp; }
                    hw.SaveEnabledMetrics(updated);
                    RefreshMetricList(capturedList, hw, fg);
                };
                downBtn.Click += (s3, e3) =>
                {
                    var updated = new List<string>(hw.EnabledMetrics);
                    int idx = updated.IndexOf(capturedKey);
                    if (idx >= 0 && idx < updated.Count - 1) { var tmp = updated[idx]; updated[idx] = updated[idx + 1]; updated[idx + 1] = tmp; }
                    hw.SaveEnabledMetrics(updated);
                    RefreshMetricList(capturedList, hw, fg);
                };

                DockPanel.SetDock(delBtn, Dock.Right);
                DockPanel.SetDock(editBtn, Dock.Right);
                DockPanel.SetDock(downBtn, Dock.Right);
                DockPanel.SetDock(upBtn, Dock.Right);
                row.Children.Add(delBtn);
                row.Children.Add(editBtn);
                row.Children.Add(downBtn);
                row.Children.Add(upBtn);

                list.Children.Add(row);
            }
            if (hw.EnabledMetrics.Count == 0)
                list.Children.Add(new TextBlock { Text = "(无指标，点下方按钮添加)", Foreground = fg, FontSize = 11, FontStyle = FontStyles.Italic });
        }

        static UIElement BuildAddForm(StackPanel metricList, StackPanel addPanel, HardwareMonitorService hw, SolidColorBrush fg)
        {
            var form = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            // 类型选择
            form.Children.Add(new TextBlock { Text = "类型", Foreground = fg, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
            var typeCombo = new ComboBox { Height = 26, FontSize = 11, Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = new SolidColorBrush(Color.FromRgb(220, 218, 245)), BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)) };
            AppResources.StyleDarkComboBox(typeCombo);
            typeCombo.Items.Add(new ComboBoxItem { Content = "温度", Tag = "Temp" });
            typeCombo.Items.Add(new ComboBoxItem { Content = "风扇转速 (RPM)", Tag = "Fan" });
            typeCombo.Items.Add(new ComboBoxItem { Content = "风扇控制 (%)", Tag = "Control" });
            typeCombo.SelectedIndex = 0;
            form.Children.Add(typeCombo);

            // 传感器选择
            form.Children.Add(new TextBlock { Text = "传感器", Foreground = fg, FontSize = 11, Margin = new Thickness(0, 6, 0, 2) });
            var sensorCombo = new ComboBox { Height = 26, FontSize = 11, Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = new SolidColorBrush(Color.FromRgb(220, 218, 245)), BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)) };
            AppResources.StyleDarkComboBox(sensorCombo);
            form.Children.Add(sensorCombo);

            void PopulateSensors()
            {
                sensorCombo.Items.Clear();
                var tag = (typeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                List<SensorInfo> pool;
                string unit;
                if (tag == "Fan")      { pool = hw.AllFanSensors;    unit = "RPM"; }
                else if (tag == "Control") { pool = hw.AllControlSensors; unit = "%"; }
                else                     { pool = hw.AllTempSensors;  unit = "°C"; }

                if (pool.Count == 0)
                {
                    sensorCombo.Items.Add(new ComboBoxItem { Content = "(无可用传感器)", Tag = null });
                }
                else
                {
                    // 先触发一次硬件刷新确保预览值有效
                    hw.Update();
                    foreach (var s in pool)
                    {
                        float? preview = hw.ReadSensorPreview(s);
                        string valStr = preview.HasValue ? $"  [{preview.Value:0}{unit}]" : "  [--]";
                        string dn = HardwareMonitorService.DefaultDisplayName(s.HardwareName, s.SensorName, s.SensorType);
                        string ik = HardwareMonitorService.AutoIconKey(dn, s);
                        sensorCombo.Items.Add(new ComboBoxItem { Content = $"{s.HardwareName} — {s.SensorName}{valStr}", Tag = HardwareMonitorService.EncodeConfig(s, dn, ik) });
                    }
                }
                sensorCombo.SelectedIndex = pool.Count > 0 ? 0 : -1;
            }
            PopulateSensors();
            typeCombo.SelectionChanged += (_, _) => PopulateSensors();

            // 显示名称
            form.Children.Add(new TextBlock { Text = "显示名称", Foreground = fg, FontSize = 11, Margin = new Thickness(0, 6, 0, 2) });
            var nameBox = new TextBox { Height = 24, FontSize = 11, Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0) };
            // 传感器切换时更新默认名称
            sensorCombo.SelectionChanged += (_, _) =>
            {
                var item = sensorCombo.SelectedItem as ComboBoxItem;
                if (item?.Tag is string key && key.Contains("|"))
                {
                    var parts = key.Split('|');
                    if (parts.Length >= 4) nameBox.Text = parts[3];
                }
            };
            form.Children.Add(nameBox);
            if (sensorCombo.SelectedItem is ComboBoxItem initItem && initItem.Tag is string initKey && initKey.Contains("|"))
            {
                var initParts = initKey.Split('|');
                if (initParts.Length >= 4) nameBox.Text = initParts[3];
            }

            // 按钮行
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var confirmBtn = new Button { Content = "确认添加", Height = 26, FontSize = 11, Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(confirmBtn, true);
            var cancelBtn = new Button { Content = "取消", Height = 26, FontSize = 11, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(8, 0, 0, 0) };
            AppResources.StyleDialogButton(cancelBtn, false);
            btnRow.Children.Add(confirmBtn);
            btnRow.Children.Add(cancelBtn);
            form.Children.Add(btnRow);

            confirmBtn.Click += (_, _) =>
            {
                var keyTemplate = (sensorCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                if (!string.IsNullOrEmpty(keyTemplate))
                {
                    // 用用户输入的显示名重建 key
                    var parts = keyTemplate.Split('|');
                    if (parts.Length >= 4)
                        parts[3] = string.IsNullOrWhiteSpace(nameBox.Text) ? parts[3] : nameBox.Text.Trim();
                    var finalKey = string.Join("|", parts);
                    var updated = new List<string>(hw.EnabledMetrics);
                    if (!updated.Contains(finalKey))
                    {
                        updated.Add(finalKey);
                        hw.SaveEnabledMetrics(updated);
                    }
                }
                RefreshMetricList(metricList, hw, fg);
                addPanel.Children.Clear();
            };
            cancelBtn.Click += (_, _) => addPanel.Children.Clear();

            return form;
        }
    }
}

