using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace PowerAudioManager
{
    // 「性能」页的指标列表（按硬件分组、可折叠、筛选、置顶）与添加/编辑传感器弹窗。
    internal static partial class SettingsDialog
    {
        static readonly string[] IconKeyOptions = { "cpu", "gpu", "hot", "vram", "dram", "disk", "fan", "ctrl", "mb", "def" };

        static readonly string[] IconKeyLabels = { "CPU芯片", "GPU显卡", "火焰", "显存", "内存条", "硬盘", "风扇", "滑动条", "主板", "圆点" };

        // Pinned metrics are shown in the floating window's compact state.
        // Stored as comma-separated config keys; unset = the first three.
        internal const string PinnedMetricsPrefKey = "Metrics.Pinned";

        static readonly string[] CategoryOrder = { "CPU 芯片", "GPU 显卡", "主板", "内存条", "硬盘", "其他" };

        // Per-page UI state of the metric list (filter, collapsed groups, live value cells).
        sealed class MetricPageState
        {
            public string Filter = "all";               // all | Temperature | Fan | Control
            public HashSet<string> Collapsed;           // category names
            public readonly List<TextBlock> ValueCells = new List<TextBlock>();
            public ContentControl FilterHost;
            public Window Dialog;
        }

        static bool IsMetricSensorEnabled(HardwareMonitorService hw, SensorInfo sensor)
        {
            return hw.IsSensorEnabled(sensor);
        }

        static string CategoryFromHwType(string type)
        {
            type ??= "";
            if (type.Equals("Cpu", StringComparison.OrdinalIgnoreCase)) return "CPU 芯片";
            if (type.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase)) return "GPU 显卡";
            if (type.Equals("Motherboard", StringComparison.OrdinalIgnoreCase) || type.Equals("SuperIO", StringComparison.OrdinalIgnoreCase)) return "主板";
            if (type.Equals("Memory", StringComparison.OrdinalIgnoreCase)) return "内存条";
            if (type.Equals("Storage", StringComparison.OrdinalIgnoreCase)) return "硬盘";
            return "其他";
        }

        static string SensorCategory(SensorInfo sensor) => CategoryFromHwType(sensor?.HwType);

        // Saved configs do not carry the hardware type: look the sensor up in
        // the live catalog, otherwise fall back to the metric's icon.
        static string MetricCategory(HardwareMonitorService hw, SensorInfo cfg, string iconKey)
        {
            try
            {
                var match = hw.GetSensors(cfg.SensorType).FirstOrDefault(s =>
                    string.Equals(s.HardwareName, cfg.HardwareName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.SensorName, cfg.SensorName, StringComparison.OrdinalIgnoreCase));
                if (match != null && !string.IsNullOrEmpty(match.HwType)) return SensorCategory(match);
            }
            catch { }
            return iconKey switch
            {
                "cpu" => "CPU 芯片",
                "gpu" or "vram" => "GPU 显卡",
                "mb" => "主板",
                "dram" => "内存条",
                "disk" => "硬盘",
                _ => "其他"
            };
        }

        static string UnitOf(string sensorType) =>
            string.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase) ? "°C" :
            string.Equals(sensorType, "Control", StringComparison.OrdinalIgnoreCase) ? "%" : "rpm";

        static string FilterKindOf(string sensorType) =>
            string.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase) ? "Temperature" :
            string.Equals(sensorType, "Control", StringComparison.OrdinalIgnoreCase) ? "Control" : "Fan";

        static string FormatReading(SensorInfo cfg, float? value)
        {
            if (!value.HasValue) return "暂无读数";
            string unit = UnitOf(cfg.SensorType);
            return unit == "°C" ? $"{value.Value:0}°C" : unit == "%" ? $"{value.Value:0}%" : $"{value.Value:0} rpm";
        }

        // Names are embedded in '|'-separated config keys and the
        // comma-separated pinned list, so neither separator may appear.
        static string SanitizeMetricName(string name) =>
            (name ?? "").Replace('|', ' ').Replace(',', '，').Trim();

        internal static List<string> LoadPinnedMetrics(HardwareMonitorService hw)
        {
            string raw = AppPrefs.GetString(PinnedMetricsPrefKey, null);
            if (raw == null) return hw.EnabledMetrics.Take(3).ToList();
            var enabled = new HashSet<string>(hw.EnabledMetrics, StringComparer.Ordinal);
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(enabled.Contains).ToList();
        }

        static bool SavePinnedMetrics(IEnumerable<string> keys) =>
            AppPrefs.SetString(PinnedMetricsPrefKey, string.Join(",", keys));

        static void ReportMetricSaveFailure(Window owner) =>
            OneBoxMessage.Error(owner, "OneBox 设置", "指标设置保存失败，当前运行状态未改变。");

        // Rebuild the filter segmented control with live counts.
        static void RefreshMetricFilter(StackPanel list, HardwareMonitorService hw, SolidColorBrush fg, MetricPageState state)
        {
            if (state.FilterHost == null) return;
            var kinds = hw.EnabledMetrics
                .Select(k => HardwareMonitorService.DecodeConfig(k, out _, out _))
                .Where(c => c != null).Select(c => FilterKindOf(c.SensorType)).ToList();
            string[] keys = { "all", "Temperature", "Fan", "Control" };
            string[] labels =
            {
                $"全部 {kinds.Count}",
                $"温度 {kinds.Count(k => k == "Temperature")}",
                $"风扇 {kinds.Count(k => k == "Fan")}",
                $"控制 {kinds.Count(k => k == "Control")}"
            };
            int selected = Math.Max(0, Array.IndexOf(keys, state.Filter));
            state.FilterHost.Content = UiKit.MakeSegmented(labels, selected, index =>
            {
                if (state.Filter == keys[index]) return;
                state.Filter = keys[index];
                RefreshMetricList(list, hw, fg, state);
            }, "按类型筛选");
        }

        static void RefreshMetricList(StackPanel list, HardwareMonitorService hw, SolidColorBrush fg, MetricPageState state)
        {
            list.Children.Clear();
            state.ValueCells.Clear();
            RefreshMetricFilter(list, hw, fg, state);
            int warnAt = AppPrefs.GetInt("Temp.WarnC", 80);
            var pinned = LoadPinnedMetrics(hw);

            var entries = new List<(string Key, SensorInfo Cfg, string Name, string Icon, string Category)>();
            foreach (var key in hw.EnabledMetrics)
            {
                var cfg = HardwareMonitorService.DecodeConfig(key, out string displayName, out string iconKey);
                if (cfg == null) continue;
                entries.Add((key, cfg, displayName, iconKey, MetricCategory(hw, cfg, iconKey)));
            }
            if (state.Collapsed == null)
            {
                // First view: everything open for short lists; long lists open
                // only the first two hardware groups.
                state.Collapsed = new HashSet<string>();
                if (entries.Count > 8)
                    foreach (var cat in CategoryOrder.Where(c => entries.Any(e => e.Category == c)).Skip(2))
                        state.Collapsed.Add(cat);
            }

            if (entries.Count == 0)
            {
                list.Children.Add(UiKit.Card(new TextBlock
                {
                    Text = "暂无指标，点击上方「添加」选择传感器", Foreground = fg, FontSize = 12, TextWrapping = TextWrapping.Wrap
                }, new Thickness(14, 12, 14, 12)));
                return;
            }

            foreach (var category in CategoryOrder)
            {
                var items = entries.Where(e => e.Category == category && (state.Filter == "all" || FilterKindOf(e.Cfg.SensorType) == state.Filter)).ToList();
                if (items.Count == 0) continue;
                bool open = !state.Collapsed.Contains(category);

                // Collapsed summary: temperature range + fan range + control values.
                var readings = items.Select(e => (e.Cfg, Value: hw.ReadSensorPreview(e.Cfg))).Where(x => x.Value.HasValue).ToList();
                var temps = readings.Where(x => UnitOf(x.Cfg.SensorType) == "°C").Select(x => (int)Math.Round(x.Value.Value)).ToList();
                var fans = readings.Where(x => UnitOf(x.Cfg.SensorType) == "rpm").Select(x => (int)Math.Round(x.Value.Value)).ToList();
                var ctrls = readings.Where(x => UnitOf(x.Cfg.SensorType) == "%").Select(x => (int)Math.Round(x.Value.Value)).ToList();
                string Range(List<int> v, string unit) => v.Count == 0 ? null :
                    v.Min() == v.Max() ? v.Max() + unit : v.Min() + "–" + v.Max() + unit;
                string summary = string.Join(" · ", new[] { Range(temps, "°C"), Range(fans, " rpm"), Range(ctrls, "%") }.Where(x => x != null));
                bool hot = temps.Any(t => t >= warnAt);

                var chevron = IconCatalog.CreateElement(open ? IconKey.ChevronDown : IconKey.ChevronRight, 12, UiKit.FrozenBrush(UiKit.TextSecondary));
                chevron.Margin = new Thickness(0, 0, 8, 0);
                var head = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(chevron, Dock.Left);
                head.Children.Add(chevron);
                var summaryText = UiKit.Text(open ? "" : summary, 11, hot ? UiKit.WarnColor : UiKit.TextSecondary);
                DockPanel.SetDock(summaryText, Dock.Right);
                head.Children.Add(summaryText);
                var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                title.Children.Add(UiKit.Text(category, 12, UiKit.TextPrimary, FontWeights.SemiBold));
                var count = UiKit.Text($"{items.Count} 项", 11, UiKit.TextSecondary);
                count.Margin = new Thickness(8, 0, 0, 0);
                title.Children.Add(count);
                head.Children.Add(title);
                var headBtn = new Button { Content = head, Height = 36, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                UiKit.ApplyFlatStyle(headBtn);
                headBtn.Padding = new Thickness(12, 0, 12, 0);
                headBtn.BorderBrush = Brushes.Transparent;
                AutomationProperties.SetName(headBtn, category + (open ? "，已展开" : "，已折叠"));
                string capturedCategory = category;
                headBtn.Click += (_, _) =>
                {
                    if (!state.Collapsed.Remove(capturedCategory)) state.Collapsed.Add(capturedCategory);
                    RefreshMetricList(list, hw, fg, state);
                };

                var groupStack = new StackPanel();
                groupStack.Children.Add(headBtn);
                if (open)
                {
                    var rows = new StackPanel { Margin = new Thickness(4, 0, 4, 4) };
                    foreach (var e in items)
                        rows.Children.Add(BuildMetricRow(list, hw, fg, state, e.Key, e.Cfg, e.Name, e.Icon, pinned.Contains(e.Key), warnAt));
                    groupStack.Children.Add(rows);
                }
                var card = UiKit.Card(groupStack, new Thickness(0));
                card.Margin = new Thickness(0, 0, 0, 6);
                list.Children.Add(card);
            }
        }

        static UIElement BuildMetricRow(StackPanel list, HardwareMonitorService hw, SolidColorBrush fg, MetricPageState state,
            string key, SensorInfo cfg, string displayName, string iconKey, bool isPinned, int warnAt)
        {
            float? val = hw.ReadSensorPreview(cfg);
            bool warn = val.HasValue && UnitOf(cfg.SensorType) == "°C" && val.Value >= warnAt;
            var grid = new Grid { Height = 32 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = UiKit.MetricIcon(iconKey, UiKit.TextSecondary, 12);
            icon.Margin = new Thickness(0, 0, 8, 0);
            grid.Children.Add(icon);
            var name = UiKit.Text(displayName, 12, UiKit.TextPrimary);
            name.ToolTip = displayName;
            name.MaxWidth = 130;
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);
            var source = UiKit.Text($"{cfg.HardwareName} — {cfg.SensorName}", 11, UiKit.TextDisabled);
            source.ToolTip = $"{cfg.HardwareName} — {cfg.SensorName}";
            source.Margin = new Thickness(8, 0, 4, 0);
            Grid.SetColumn(source, 2);
            grid.Children.Add(source);
            var value = new TextBlock
            {
                Tag = cfg, Text = FormatReading(cfg, val), FontSize = val.HasValue ? 12 : 11,
                FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiKit.FrozenBrush(warn ? UiKit.WarnColor : val.HasValue ? UiKit.TextPrimary : UiKit.TextSecondary)
            };
            Grid.SetColumn(value, 3);
            grid.Children.Add(value);
            state.ValueCells.Add(value);

            var pinBtn = new Button
            {
                Width = 26, Height = 26,
                Content = UiKit.Glyph(UiKit.Glyphs.Pin, 12,
                    UiKit.FrozenBrush(isPinned ? UiKit.AccentColor : UiKit.TextDisabled), 2,
                    isPinned ? UiKit.FrozenBrush(UiKit.AccentColor) : null),
                ToolTip = isPinned ? "取消置顶（精简态不再显示）" : "置顶（悬浮窗精简态显示）"
            };
            UiKit.ApplyIconButtonStyle(pinBtn);
            pinBtn.MinWidth = 26; pinBtn.MinHeight = 26;
            pinBtn.Margin = new Thickness(6, 0, 0, 0);
            AutomationProperties.SetName(pinBtn, (isPinned ? "取消置顶 " : "置顶 ") + displayName);
            pinBtn.Click += (_, _) =>
            {
                var pins = LoadPinnedMetrics(hw);
                if (!pins.Remove(key)) pins.Add(key);
                // Keep the floating window order consistent with the list order.
                var ordered = hw.EnabledMetrics.Where(pins.Contains).ToList();
                if (!SavePinnedMetrics(ordered)) { ReportMetricSaveFailure(state.Dialog); return; }
                RefreshMetricList(list, hw, fg, state);
            };
            Grid.SetColumn(pinBtn, 4);
            grid.Children.Add(pinBtn);

            var moreBtn = new Button
            {
                Width = 26, Height = 26, ToolTip = "更多：编辑、排序或删除",
                Content = UiKit.Glyph(UiKit.Glyphs.More, 14, UiKit.FrozenBrush(UiKit.TextSecondary), 3)
            };
            UiKit.ApplyIconButtonStyle(moreBtn);
            moreBtn.MinWidth = 26; moreBtn.MinHeight = 26;
            AutomationProperties.SetName(moreBtn, "更多操作 " + displayName);
            moreBtn.Click += (_, _) => ShowMetricMenu(moreBtn, list, hw, fg, state, key, cfg, displayName, iconKey);
            Grid.SetColumn(moreBtn, 5);
            grid.Children.Add(moreBtn);

            return new Border
            {
                Background = warn ? UiKit.FrozenBrush(UiKit.WarnSurface) : Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 0, 4, 0),
                Child = grid
            };
        }

        // ⋯ menu: 编辑 / 上移 / 下移 / 删除, as a dark popup anchored to the button.
        static void ShowMetricMenu(Button anchor, StackPanel list, HardwareMonitorService hw, SolidColorBrush fg, MetricPageState state,
            string key, SensorInfo cfg, string displayName, string iconKey)
        {
            var popup = new Popup
            {
                PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false,
                AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade
            };
            var items = new StackPanel { Margin = new Thickness(4) };
            int idx = hw.EnabledMetrics.IndexOf(key);
            Button Item(string text, Color color, bool enabled, Action act)
            {
                var b = new Button { Content = text, Height = 30, MinWidth = 120, HorizontalContentAlignment = HorizontalAlignment.Left, IsEnabled = enabled };
                UiKit.ApplyFlatStyle(b);
                b.BorderBrush = Brushes.Transparent;
                b.Padding = new Thickness(10, 0, 10, 0);
                b.Foreground = UiKit.FrozenBrush(color);
                b.Click += (_, _) => { popup.IsOpen = false; act(); };
                items.Children.Add(b);
                return b;
            }
            bool Move(int delta)
            {
                var updated = new List<string>(hw.EnabledMetrics);
                int i = updated.IndexOf(key);
                int j = i + delta;
                if (i < 0 || j < 0 || j >= updated.Count) return true;
                (updated[i], updated[j]) = (updated[j], updated[i]);
                if (!hw.SaveEnabledMetrics(updated)) { ReportMetricSaveFailure(state.Dialog); return false; }
                return true;
            }
            Item("编辑", UiKit.TextPrimary, true, () =>
            {
                if (ShowEditMetricDialog(state.Dialog, hw, key, cfg, displayName, iconKey)) RefreshMetricList(list, hw, fg, state);
            });
            Item("上移", UiKit.TextPrimary, idx > 0, () => { if (Move(-1)) RefreshMetricList(list, hw, fg, state); });
            Item("下移", UiKit.TextPrimary, idx >= 0 && idx < hw.EnabledMetrics.Count - 1, () => { if (Move(1)) RefreshMetricList(list, hw, fg, state); });
            items.Children.Add(new Border { Height = 1, Background = UiKit.FrozenBrush(UiKit.RaisedColor), Margin = new Thickness(6, 4, 6, 4) });
            Item("删除", UiKit.DangerColor, true, () =>
            {
                var updated = new List<string>(hw.EnabledMetrics);
                updated.Remove(key);
                if (!hw.SaveEnabledMetrics(updated)) { ReportMetricSaveFailure(state.Dialog); return; }
                var pins = LoadPinnedMetrics(hw);
                if (pins.Remove(key)) SavePinnedMetrics(pins);
                RefreshMetricList(list, hw, fg, state);
            });
            popup.Child = new Border
            {
                Background = UiKit.FrozenBrush(UiKit.CardColor),
                BorderBrush = UiKit.FrozenBrush(UiKit.BorderStrongColor), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Child = items
            };
            popup.IsOpen = true;
        }

        // Icon chooser: one segment per icon option, glyph only.
        static Border BuildIconChooser(string current, Action<string> picked)
        {
            int selected = Math.Max(0, Array.IndexOf(IconKeyOptions, current));
            var grid = new UniformGrid { Rows = 1, Columns = IconKeyOptions.Length };
            string group = "icon" + Guid.NewGuid().ToString("N");
            for (int i = 0; i < IconKeyOptions.Length; i++)
            {
                string ik = IconKeyOptions[i];
                var rb = new RadioButton
                {
                    Content = UiKit.MetricIcon(ik, UiKit.TextPrimary, 14), GroupName = group, IsChecked = i == selected,
                    ToolTip = IconKeyLabels[i], Margin = new Thickness(1, 0, 1, 0)
                };
                if (Application.Current?.TryFindResource(ThemeTokens.SegmentKey) is Style style) rb.Style = style;
                rb.Padding = new Thickness(0);
                AutomationProperties.SetName(rb, IconKeyLabels[i]);
                rb.Checked += (_, _) => picked(ik);
                grid.Children.Add(rb);
            }
            return new Border { Background = UiKit.FrozenBrush(UiKit.BgColor), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = grid };
        }

        static StackPanel LabeledField(string label, UIElement input, double top = 10)
        {
            var field = new StackPanel { Margin = new Thickness(0, top, 0, 0) };
            var caption = UiKit.Text(label, 11, UiKit.TextSecondary);
            caption.Margin = new Thickness(0, 0, 0, 4);
            field.Children.Add(caption);
            field.Children.Add(input);
            return field;
        }

        // Edit an existing metric: display name, icon, and (re)bind the sensor.
        static bool ShowEditMetricDialog(Window owner, HardwareMonitorService hw, string capturedKey, SensorInfo cfg, string displayName, string iconKey)
        {
            bool saved = false;
            var body = new DockPanel { Margin = new Thickness(16, 4, 16, 0) };
            var form = new StackPanel();
            var nameBox = UiKit.MakeTextBox(displayName);
            AutomationProperties.SetName(nameBox, "显示名称");
            form.Children.Add(LabeledField("显示名称", nameBox, 0));
            string newIcon = iconKey;
            form.Children.Add(LabeledField("图标", BuildIconChooser(iconKey, ik => newIcon = ik)));

            var sensorCombo = new ComboBox { FontSize = 12, MaxDropDownHeight = 300 };
            AppResources.StyleDarkComboBox(sensorCombo);
            AutomationProperties.SetName(sensorCombo, "传感器");
            var pool = hw.GetSensors(cfg.SensorType);
            SensorInfo resolvedSensor = HardwareMonitorService.ResolveSensor(pool, cfg);
            int selSensor = resolvedSensor == null ? -1 : pool.IndexOf(resolvedSensor);
            for (int si = 0; si < pool.Count; si++)
            {
                var s = pool[si];
                sensorCombo.Items.Add(new ComboBoxItem { Content = $"{s.HardwareName} — {s.SensorName}", Tag = HardwareMonitorService.EncodeConfig(s, displayName, iconKey) });
            }
            if (selSensor < 0)
            {
                sensorCombo.Items.Insert(0, new ComboBoxItem
                {
                    Content = $"当前暂不可用 — {cfg.HardwareName} — {cfg.SensorName}",
                    Tag = capturedKey,
                });
                selSensor = 0;
            }
            sensorCombo.SelectedIndex = selSensor;
            form.Children.Add(LabeledField("传感器", sensorCombo));

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 16) };
            var cancel = UiKit.MakeButton("取消", ButtonVariant.Secondary);
            cancel.Width = 80;
            var save = UiKit.MakeButton("保存", ButtonVariant.Primary);
            save.Width = 80; save.Margin = new Thickness(8, 0, 0, 0);
            footer.Children.Add(cancel);
            footer.Children.Add(save);
            DockPanel.SetDock(footer, Dock.Bottom);
            body.Children.Add(footer);
            body.Children.Add(form);

            var dialog = OneBoxWindow.Create(owner, "编辑指标", 420, 330, body, false);
            cancel.Click += (_, _) => dialog.Close();
            save.Click += (_, _) =>
            {
                var selKey = (sensorCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? capturedKey;
                var newName = string.IsNullOrWhiteSpace(nameBox.Text) ? displayName : SanitizeMetricName(nameBox.Text);
                if (string.IsNullOrEmpty(newName)) newName = displayName;
                // 重建 key：用新传感器 + 新名称 + 新图标
                var newCfg = HardwareMonitorService.DecodeConfig(selKey, out _, out _);
                if (newCfg == null) { dialog.Close(); return; }
                var finalKey = HardwareMonitorService.EncodeConfig(newCfg, newName, newIcon);
                var updated = new List<string>(hw.EnabledMetrics);
                int idx = updated.IndexOf(capturedKey);
                if (idx >= 0) updated[idx] = finalKey;
                else updated.Add(finalKey);
                var pins = LoadPinnedMetrics(hw);
                if (!hw.SaveEnabledMetrics(updated)) { ReportMetricSaveFailure(dialog); return; }
                int pinIdx = pins.IndexOf(capturedKey);
                if (pinIdx >= 0) { pins[pinIdx] = finalKey; SavePinnedMetrics(pins); }
                saved = true;
                dialog.Close();
            };
            dialog.ShowDialog();
            return saved;
        }

        // 添加传感器：类型 + 搜索 + 硬件分类列表 + 多选传感器 + 最后选中项的名称/图标编辑。
        static bool ShowAddSensorDialog(Window owner, HardwareMonitorService hw)
        {
            bool added = false;
            var tempSensors = hw.GetSensors("Temperature");
            var fanSensors = hw.GetSensors("Fan");
            var controlSensors = hw.GetSensors("Control");

            // 选中集合：编码 key → 名称/图标覆盖。保持勾选顺序。
            var picks = new List<string>();
            var overrides = new Dictionary<string, (string Name, string Icon)>(StringComparer.Ordinal);
            string category = "全部";
            string lastPicked = null;

            // ---- 工具栏：搜索 + 类型 ----
            var searchBox = UiKit.MakeTextBox();
            searchBox.Padding = new Thickness(28, 5, 8, 5);
            AutomationProperties.SetName(searchBox, "搜索传感器");
            var searchHint = UiKit.Text("搜索传感器", 12, UiKit.TextDisabled);
            searchHint.IsHitTestVisible = false;
            searchHint.Margin = new Thickness(29, 0, 0, 0);
            var searchIcon = UiKit.Glyph(UiKit.Glyphs.Search, 13, UiKit.FrozenBrush(UiKit.TextSecondary));
            searchIcon.HorizontalAlignment = HorizontalAlignment.Left;
            searchIcon.Margin = new Thickness(9, 0, 0, 0);
            searchIcon.IsHitTestVisible = false;
            var searchHost = new Grid();
            searchHost.Children.Add(searchBox);
            searchHost.Children.Add(searchHint);
            searchHost.Children.Add(searchIcon);

            var typeCombo = new ComboBox { Width = 170, FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
            AppResources.StyleDarkComboBox(typeCombo);
            AutomationProperties.SetName(typeCombo, "传感器类型");
            typeCombo.Items.Add(new ComboBoxItem { Content = $"温度 ({tempSensors.Count})", Tag = "Temp" });
            typeCombo.Items.Add(new ComboBoxItem { Content = $"风扇转速 RPM ({fanSensors.Count})", Tag = "Fan" });
            typeCombo.Items.Add(new ComboBoxItem { Content = $"风扇控制 % ({controlSensors.Count})", Tag = "Control" });
            typeCombo.SelectedIndex = 0;
            var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(typeCombo, Dock.Right);
            toolbar.Children.Add(typeCombo);
            toolbar.Children.Add(searchHost);

            var sensorHint = UiKit.Text("", 11, UiKit.TextSecondary);
            sensorHint.TextWrapping = TextWrapping.Wrap;
            sensorHint.TextTrimming = TextTrimming.None;
            sensorHint.Margin = new Thickness(2, 0, 0, 8);

            // ---- 分类列表 + 传感器列表 ----
            var categoryList = new ListBox { Width = 132, Background = Brushes.Transparent, BorderThickness = new Thickness(0), ItemContainerStyle = SidebarItemStyle() };
            ScrollViewer.SetHorizontalScrollBarVisibility(categoryList, ScrollBarVisibility.Disabled);
            var sensorStack = new StackPanel();
            var sensorScroll = new ScrollViewer { Content = sensorStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            sensorScroll.Resources[typeof(ScrollBar)] = ThemeTokens.CreateDarkScrollBarStyle();
            var sensorCard = UiKit.Card(sensorScroll, new Thickness(4));
            sensorCard.Margin = new Thickness(8, 0, 0, 0);
            var main = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(categoryList, Dock.Left);
            main.Children.Add(categoryList);
            main.Children.Add(sensorCard);

            // ---- 编辑区（最后勾选的传感器）----
            var editName = UiKit.MakeTextBox();
            AutomationProperties.SetName(editName, "显示名称");
            var editIconHost = new ContentControl();
            var editTitle = UiKit.Text("勾选传感器后可修改显示名称和图标", 11, UiKit.TextSecondary);
            var editGrid = new Grid();
            editGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            editGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var nameField = LabeledField("显示名称", editName, 6);
            nameField.Margin = new Thickness(0, 6, 10, 0);
            var iconField = LabeledField("图标", editIconHost, 6);
            Grid.SetColumn(iconField, 1);
            editGrid.Children.Add(nameField);
            editGrid.Children.Add(iconField);
            var editPanel = new StackPanel();
            editPanel.Children.Add(editTitle);
            editPanel.Children.Add(editGrid);
            editPanel.IsEnabled = false;
            var editCard = UiKit.Card(editPanel, new Thickness(12, 10, 12, 12));
            editCard.Margin = new Thickness(0, 8, 0, 0);

            bool loadingEdit = false;
            void LoadEditor()
            {
                loadingEdit = true;
                if (lastPicked == null || !overrides.TryGetValue(lastPicked, out var o))
                {
                    editPanel.IsEnabled = false;
                    editTitle.Text = "勾选传感器后可修改显示名称和图标";
                    editName.Text = "";
                    editIconHost.Content = BuildIconChooser("def", _ => { });
                }
                else
                {
                    var s = HardwareMonitorService.DecodeConfig(lastPicked, out _, out _);
                    editPanel.IsEnabled = true;
                    editTitle.Text = $"编辑 · {s?.HardwareName} — {s?.SensorName}";
                    editName.Text = o.Name;
                    string key = lastPicked;
                    editIconHost.Content = BuildIconChooser(o.Icon, ik =>
                    {
                        if (overrides.TryGetValue(key, out var cur)) overrides[key] = (cur.Name, ik);
                    });
                }
                loadingEdit = false;
            }
            editName.TextChanged += (_, _) =>
            {
                if (loadingEdit || lastPicked == null || !overrides.TryGetValue(lastPicked, out var cur)) return;
                overrides[lastPicked] = (editName.Text, cur.Icon);
            };

            // ---- 底栏 ----
            var selectedText = UiKit.Text("已选 0 项", 12, UiKit.TextSecondary);
            var cancelBtn = UiKit.MakeButton("取消", ButtonVariant.Secondary);
            cancelBtn.Width = 80;
            var confirmBtn = UiKit.MakeButton("确认添加", ButtonVariant.Primary);
            confirmBtn.Margin = new Thickness(8, 0, 0, 0);
            confirmBtn.IsEnabled = false;
            var footerButtons = new StackPanel { Orientation = Orientation.Horizontal };
            footerButtons.Children.Add(cancelBtn);
            footerButtons.Children.Add(confirmBtn);
            var footer = new DockPanel { LastChildFill = true, Height = 56 };
            DockPanel.SetDock(footerButtons, Dock.Right);
            footer.Children.Add(footerButtons);
            footer.Children.Add(selectedText);
            void UpdateSelection()
            {
                selectedText.Text = $"已选 {picks.Count} 项";
                confirmBtn.IsEnabled = picks.Count > 0;
            }

            List<SensorInfo> CurrentPool()
            {
                var tag = (typeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                return tag == "Fan" ? fanSensors : tag == "Control" ? controlSensors : tempSensors;
            }
            string CurrentUnit()
            {
                var tag = (typeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                return tag == "Fan" ? "rpm" : tag == "Control" ? "%" : "°C";
            }
            List<SensorInfo> DistinctSorted(List<SensorInfo> pool) => pool
                .GroupBy(s => $"{s.SensorType}\0{s.HardwareName}\0{s.SensorName}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(s => Array.IndexOf(CategoryOrder, SensorCategory(s)))
                .ThenBy(s => s.HardwareName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(s => s.SensorName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var valueCells = new List<(TextBlock Text, SensorInfo Sensor)>();
            bool populatingCategories = false;
            void PopulateCategories()
            {
                populatingCategories = true;
                var sensors = DistinctSorted(CurrentPool());
                categoryList.Items.Clear();
                var cats = new List<string> { "全部" };
                cats.AddRange(CategoryOrder.Where(c => sensors.Any(s => SensorCategory(s) == c)));
                if (!cats.Contains(category)) category = "全部";
                foreach (var cat in cats)
                {
                    int available = sensors.Count(s => (cat == "全部" || SensorCategory(s) == cat) && !IsMetricSensorEnabled(hw, s));
                    var item = SidebarItem(cat);
                    if (item.Content is StackPanel row)
                    {
                        var countText = UiKit.Text(available.ToString(), 11, UiKit.TextTertiary);
                        countText.Margin = new Thickness(6, 0, 0, 0);
                        row.Children.Add(countText);
                    }
                    item.Tag = cat;
                    categoryList.Items.Add(item);
                }
                categoryList.SelectedIndex = Math.Max(0, cats.IndexOf(category));
                foreach (ListBoxItem item in categoryList.Items)
                    if (item.Content is StackPanel row && row.Children.Count >= 2)
                    {
                        if (row.Children[0] is System.Windows.Shapes.Ellipse dot) dot.Fill = item.IsSelected ? UiKit.FrozenBrush(UiKit.AccentColor) : Brushes.Transparent;
                        if (row.Children[1] is TextBlock label) { label.Foreground = UiKit.FrozenBrush(item.IsSelected ? UiKit.TextPrimary : UiKit.TextSecondary); label.FontWeight = item.IsSelected ? FontWeights.SemiBold : FontWeights.Normal; }
                    }
                populatingCategories = false;
            }

            void PopulateSensors()
            {
                sensorStack.Children.Clear();
                valueCells.Clear();
                var all = DistinctSorted(CurrentPool());
                string query = (searchBox.Text ?? "").Trim();
                var sensors = all.Where(s => (category == "全部" || SensorCategory(s) == category) &&
                    (query.Length == 0 || $"{s.HardwareName} {s.SensorName}".IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)).ToList();
                int availableCount = all.Count(s => !IsMetricSensorEnabled(hw, s));
                if (all.Count == 0)
                {
                    sensorHint.Text = "未检测到此类传感器，请确认 OneBoxSvc 和硬件采集进程正在运行。";
                    sensorStack.Children.Add(new TextBlock { Text = "(无可用传感器)", Foreground = UiKit.FrozenBrush(UiKit.TextSecondary), FontSize = 12, Margin = new Thickness(10) });
                    return;
                }
                sensorHint.Text = availableCount > 0
                    ? $"按硬件分类排列，{availableCount} 个可添加；已添加的传感器会自动禁用。"
                    : "此类型的传感器均已添加。";
                if (sensors.Count == 0)
                {
                    sensorStack.Children.Add(new TextBlock { Text = "没有匹配的传感器", Foreground = UiKit.FrozenBrush(UiKit.TextSecondary), FontSize = 12, Margin = new Thickness(10) });
                    return;
                }
                string unit = CurrentUnit();
                foreach (var s in sensors)
                {
                    bool already = IsMetricSensorEnabled(hw, s);
                    string dn = SanitizeMetricName(HardwareMonitorService.DefaultDisplayName(s.HardwareName, s.SensorName, s.SensorType));
                    string ik = HardwareMonitorService.AutoIconKey(dn, s);
                    string key = HardwareMonitorService.EncodeConfig(s, dn, ik);

                    var line = new Grid();
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var label = UiKit.Text($"{s.HardwareName} — {s.SensorName}", 12, already ? UiKit.TextDisabled : UiKit.TextPrimary);
                    label.ToolTip = $"{s.HardwareName}\n{s.SensorName}";
                    line.Children.Add(label);
                    float? preview = hw.ReadSensorPreview(s);
                    var reading = UiKit.Text(preview.HasValue ? $"{preview.Value:0}{(unit == "rpm" ? " rpm" : unit)}" : "--", 12, UiKit.TextSecondary);
                    reading.Margin = new Thickness(10, 0, 0, 0);
                    if (already)
                    {
                        var tagged = new StackPanel { Orientation = Orientation.Horizontal };
                        tagged.Children.Add(reading);
                        tagged.Children.Add(UiKit.Tag("已添加", UiKit.TextSecondary, UiKit.RaisedColor));
                        Grid.SetColumn(tagged, 1);
                        line.Children.Add(tagged);
                    }
                    else
                    {
                        Grid.SetColumn(reading, 1);
                        line.Children.Add(reading);
                    }
                    valueCells.Add((reading, s));

                    var cb = DarkCheck(line, picks.Contains(key));
                    cb.IsEnabled = !already;
                    cb.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    cb.Margin = new Thickness(8, 7, 8, 7);
                    AutomationProperties.SetName(cb, $"{s.HardwareName} {s.SensorName}");
                    cb.Checked += (_, _) =>
                    {
                        if (!picks.Contains(key)) picks.Add(key);
                        if (!overrides.ContainsKey(key)) overrides[key] = (dn, ik);
                        lastPicked = key;
                        LoadEditor();
                        UpdateSelection();
                    };
                    cb.Unchecked += (_, _) =>
                    {
                        picks.Remove(key);
                        if (lastPicked == key) lastPicked = picks.LastOrDefault();
                        LoadEditor();
                        UpdateSelection();
                    };
                    sensorStack.Children.Add(cb);
                }
            }

            categoryList.SelectionChanged += (_, _) =>
            {
                if (populatingCategories) return;
                if (categoryList.SelectedItem is ListBoxItem item && item.Tag is string cat && cat != category)
                {
                    category = cat;
                    PopulateCategories();
                    PopulateSensors();
                }
            };
            typeCombo.SelectionChanged += (_, _) => { category = "全部"; PopulateCategories(); PopulateSensors(); };
            searchBox.TextChanged += (_, _) =>
            {
                searchHint.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
                PopulateSensors();
            };
            PopulateCategories();
            PopulateSensors();
            LoadEditor();
            UpdateSelection();

            // ---- 布局 ----
            var root = new Grid { Margin = new Thickness(16, 4, 16, 0) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(toolbar);
            Grid.SetRow(sensorHint, 1); root.Children.Add(sensorHint);
            Grid.SetRow(main, 2); root.Children.Add(main);
            Grid.SetRow(editCard, 3); root.Children.Add(editCard);
            Grid.SetRow(footer, 4); root.Children.Add(footer);

            var dialog = OneBoxWindow.Create(owner, "添加传感器", 600, 600, root, true);
            dialog.MinWidth = 520;
            dialog.MinHeight = 480;

            // 实时读数与目录变化（传感器热插拔）刷新。
            var previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            string catalogIdentity = null;
            previewTimer.Tick += (_, _) =>
            {
                var temperatures = hw.GetSensors("Temperature");
                var fans = hw.GetSensors("Fan");
                var controls = hw.GetSensors("Control");
                string identity = string.Join(";", temperatures.Concat(fans).Concat(controls).Select(sensor => HardwareMonitorService.EncodeConfig(sensor, "", "")));
                if (catalogIdentity != null && identity != catalogIdentity)
                {
                    tempSensors = temperatures; fanSensors = fans; controlSensors = controls;
                    ((ComboBoxItem)typeCombo.Items[0]).Content = $"温度 ({tempSensors.Count})";
                    ((ComboBoxItem)typeCombo.Items[1]).Content = $"风扇转速 RPM ({fanSensors.Count})";
                    ((ComboBoxItem)typeCombo.Items[2]).Content = $"风扇控制 % ({controlSensors.Count})";
                    PopulateCategories();
                    PopulateSensors();
                }
                catalogIdentity = identity;
                string unit = CurrentUnit();
                foreach (var (text, sensor) in valueCells)
                {
                    float? value = hw.ReadSensorPreview(sensor);
                    text.Text = value.HasValue ? $"{value.Value:0}{(unit == "rpm" ? " rpm" : unit)}" : "--";
                }
            };
            dialog.Loaded += (_, _) => previewTimer.Start();
            dialog.Closed += (_, _) => previewTimer.Stop();

            cancelBtn.Click += (_, _) => dialog.Close();
            confirmBtn.Click += (_, _) =>
            {
                var updated = new List<string>(hw.EnabledMetrics);
                foreach (var key in picks)
                {
                    var sensor = HardwareMonitorService.DecodeConfig(key, out string dn, out string ik);
                    if (sensor == null || IsMetricSensorEnabled(hw, sensor)) continue;
                    if (overrides.TryGetValue(key, out var o))
                    {
                        string name = SanitizeMetricName(o.Name);
                        if (!string.IsNullOrEmpty(name)) dn = name;
                        ik = o.Icon ?? ik;
                    }
                    // 用用户输入的显示名重建 key
                    string finalKey = HardwareMonitorService.EncodeConfig(sensor, dn, ik);
                    if (!updated.Contains(finalKey)) updated.Add(finalKey);
                }
                if (updated.Count != hw.EnabledMetrics.Count)
                {
                    if (!hw.SaveEnabledMetrics(updated)) { ReportMetricSaveFailure(dialog); return; }
                    added = true;
                }
                dialog.Close();
            };
            dialog.ShowDialog();
            return added;
        }

        static void RefreshMetricPreviewValues(MetricPageState state, HardwareMonitorService hw)
        {
            int warnAt = AppPrefs.GetInt("Temp.WarnC", 80);
            foreach (var text in state.ValueCells)
            {
                if (text.Tag is not SensorInfo sensor) continue;
                float? value = hw.ReadSensorPreview(sensor);
                bool warn = value.HasValue && UnitOf(sensor.SensorType) == "°C" && value.Value >= warnAt;
                text.Text = FormatReading(sensor, value);
                text.FontSize = value.HasValue ? 12 : 11;
                text.Foreground = UiKit.FrozenBrush(warn ? UiKit.WarnColor : value.HasValue ? UiKit.TextPrimary : UiKit.TextSecondary);
            }
        }
    }
}
