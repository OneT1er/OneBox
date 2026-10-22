using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Threading;
using System.IO;

namespace PowerAudioManager
{
    public static class MonitorLifecyclePolicy
    {
        public static bool ShouldCollect(bool moduleEnabled, bool isExiting)
        {
            return moduleEnabled && !isExiting;
        }
    }

    // 温度/性能监控：OneBoxSvc 守护、硬件传感器轮询、指标行与折叠态温度刷新。
    public partial class MainWindow : Window
    {
        void EnsureServiceRunning()
        {
            try
            {
                if (App.RendererDiagnosticMode)
                {
                    using var installed = new System.ServiceProcess.ServiceController("OneBoxSvc");
                    AppLog.Log("Renderer diagnostic", $"installed service path={AutoStartService.GetInstalledServiceImagePath()} status={installed.Status}; no service migration");
                    if (installed.Status != System.ServiceProcess.ServiceControllerStatus.Running)
                        AppLog.Log("Renderer diagnostic", "INVALID: installed service is not running; no start or repair attempted");
                    return;
                }
                var registration = AutoStartService.GetServiceRegistrationKind();
                if (registration == OneBox.Contracts.ServiceImagePathKind.LegacyGui || registration == OneBox.Contracts.ServiceImagePathKind.Other)
                {
                    AppLog.Log("Service", "legacy or foreign service image path detected; requesting explicit repair");
                    string repairError = AutoStartService.RepairService();
                    if (repairError != null) AppLog.Log("Service", "repair failed: " + repairError);
                    return;
                }
                using (var svc = new System.ServiceProcess.ServiceController("OneBoxSvc"))
                {
                    if (svc.Status != System.ServiceProcess.ServiceControllerStatus.Running)
                    {
                        AppLog.Log("Service", "OneBoxSvc not running, starting (UAC)");
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sc.exe", "start OneBoxSvc") { Verb = "runas", UseShellExecute = true }); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log("Service", "EnsureServiceRunning: " + ex.Message);
                if (App.RendererDiagnosticMode) AppLog.Log("Renderer diagnostic", "INVALID: installed service unavailable; no start or repair attempted");
            }
        }

        void StartTempMonitor()
        {
            if (!MonitorLifecyclePolicy.ShouldCollect(ModuleVisible("Temp"), _isExiting))
            {
                StopTempMonitor();
                return;
            }
            // 前台历史与性能采样一起持续记录，图表关闭后仍保留真实时间覆盖范围。
            ForegroundHistory.Start();
            HardwareMonitorService.Instance.Start();
            StartTempTimer();
        }


        void StartTempTimer()
        {
            StopTempTimerOnly();
            int intervalMs = AppPrefs.GetInt("Temp.IntervalMs", 1000);
            if (intervalMs < 500) intervalMs = 500;
            if (intervalMs > 60000) intervalMs = 60000;
            _tempTimer = new System.Threading.Timer(_ =>
            {
                if (!MonitorLifecyclePolicy.ShouldCollect(ModuleVisible("Temp"), _isExiting)) return;
                try { HardwareMonitorService.Instance.Update(); }
                catch (Exception ex) { AppLog.Log("Temperature update", ex); return; }
                if (Dispatcher.HasShutdownStarted) return;
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!MonitorLifecyclePolicy.ShouldCollect(ModuleVisible("Temp"), _isExiting)) return;
                        UpdateTempUI();
                        PerfHistory.Add(HardwareMonitorService.Instance.ActiveMetrics);
                    }));
                }
                catch (Exception ex) { AppLog.Log("Temperature dispatch", ex); }
            }, null, 2000, intervalMs);
        }

        internal void RestartTempTimer()
        {
            StopTempMonitor();
            if (!MonitorLifecyclePolicy.ShouldCollect(ModuleVisible("Temp"), _isExiting)) return;
            HardwareMonitorService.Instance.Start();
            StartTempTimer();
        }

        void StopTempTimerOnly()
        {
            var timer = System.Threading.Interlocked.Exchange(ref _tempTimer, null);
            try { timer?.Dispose(); } catch { }
        }

        void StopTempMonitor()
        {
            ForegroundHistory.Stop();
            StopTempTimerOnly();
            try { HardwareMonitorService.Instance.Stop(); } catch (Exception ex) { AppLog.Log("Temperature stop", ex); }
            _metricSig = null;
        }

        // ---- 硬件监控卡片 -------------------------------------------------------
        // 结构只在「指标集合 / 置顶集合 / 展开状态」变化时重建；每秒只改 Run 文本与颜色。
        string _metricSig;                       // 上次构建的结构签名；null 强制重建
        bool _metricsExpanded;                   // 「另 N 项」是否展开（会话内保留）
        List<Run> _metricValueRuns;              // 与 ActiveMetrics 下标一一对应
        List<Run> _metricUnitRuns;
        List<TextBlock> _metricNameBlocks;
        List<Border> _metricTiles;
        List<bool> _metricPinned;
        Border _metricsMoreButton;
        TextBlock _metricsMoreWarn;
        DateTime _collapsedMemoryReadUtc;
        uint _collapsedMemoryPercent;
        bool _collapsedMemoryKnown;

        static readonly Color WarnValueColor = Color.FromRgb(0xF5, 0xC7, 0x7A);

        void UpdateTempUI()
        {
            try
            {
                var metrics = HardwareMonitorService.Instance.ActiveMetrics;
                int warnC = AppPrefs.GetInt("Temp.WarnC", 80);
                int critC = AppPrefs.GetInt("Temp.CriticalC", 95);

                if (_isExpanded && _metricRow is StackPanel metricHost)
                    RenderMetrics(metricHost, metrics, warnC, critC);

                if (!_isExpanded && _collapsedTempLabel != null)
                    UpdateCollapsedSummary(metrics);
            }
            catch { }
        }

        static string FormatMetricValue(MetricValue m) => m.Value?.ToString("0") ?? "--";

        static string FormatMetricUnit(MetricValue m)
        {
            if (string.IsNullOrEmpty(m.Unit)) return "";
            if (m.Unit == "RPM") return " rpm";
            return m.Unit;
        }

        // 置顶集合：AppPrefs「Metrics.Pinned」（逗号分隔的 ConfigKey，由设置页
        // SettingsDialog.PinnedMetricsPrefKey 写入）。未设置时默认前 3 项；已设置时
        // 只认精确匹配的键，与设置页 LoadPinnedMetrics 的判定保持一致。
        static bool[] ResolvePinned(List<MetricValue> metrics)
        {
            var pinned = new bool[metrics.Count];
            string pref = AppPrefs.GetString(SettingsDialog.PinnedMetricsPrefKey, null);
            if (pref == null)
            {
                for (int i = 0; i < metrics.Count && i < 3; i++) pinned[i] = true;
                return pinned;
            }
            var keys = new HashSet<string>(pref.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            for (int i = 0; i < metrics.Count; i++)
                if (metrics[i].ConfigKey != null && keys.Contains(metrics[i].ConfigKey)) pinned[i] = true;
            return pinned;
        }

        void RenderMetrics(StackPanel host, List<MetricValue> metrics, int warnC, int critC)
        {
            if (metrics.Count == 0)
            {
                if (_metricSig != "") 
                {
                    host.Children.Clear();
                    host.Children.Add(new TextBlock { Text = "传感器初始化中…", Foreground = UiKit.FrozenBrush(UiKit.TextSecondary), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
                    _metricSig = "";
                    _metricValueRuns = null;
                }
                return;
            }

            var pinned = ResolvePinned(metrics);
            var sig = new System.Text.StringBuilder(metrics.Count * 12);
            for (int i = 0; i < metrics.Count; i++)
                sig.Append(metrics[i].ConfigKey).Append(pinned[i] ? "*" : "").Append('|');
            sig.Append(_metricsExpanded ? "E" : "C");
            string signature = sig.ToString();
            if (!string.Equals(signature, _metricSig, StringComparison.Ordinal) || _metricValueRuns == null)
                BuildMetricStructure(host, metrics, pinned, signature);

            // 每秒只改数值文本与颜色，零结构重建
            MetricValue hottestHidden = null;
            for (int i = 0; i < metrics.Count && i < _metricValueRuns.Count; i++)
            {
                var m = metrics[i];
                string text = FormatMetricValue(m);
                if (_metricValueRuns[i].Text != text) _metricValueRuns[i].Text = text;
                bool crit = m.IsTemp && m.Value.HasValue && m.Value.Value >= critC;
                bool warn = m.IsTemp && m.Value.HasValue && m.Value.Value >= warnC;
                var tileBg = UiKit.FrozenBrush(crit ? UiKit.DangerSurface : warn ? UiKit.WarnSurface : UiKit.RaisedColor);
                if (!ReferenceEquals(_metricTiles[i].Background, tileBg)) _metricTiles[i].Background = tileBg;
                var valueBrush = UiKit.FrozenBrush(crit ? UiKit.DangerColor : warn ? WarnValueColor : UiKit.TextPrimary);
                if (!ReferenceEquals(_metricValueRuns[i].Foreground, valueBrush)) _metricValueRuns[i].Foreground = valueBrush;
                var labelBrush = UiKit.FrozenBrush(crit ? UiKit.DangerColor : warn ? UiKit.WarnColor : UiKit.TextSecondary);
                if (!ReferenceEquals(_metricNameBlocks[i].Foreground, labelBrush)) _metricNameBlocks[i].Foreground = labelBrush;
                if (!ReferenceEquals(_metricUnitRuns[i].Foreground, labelBrush)) _metricUnitRuns[i].Foreground = labelBrush;
                if (warn && !_metricPinned[i] && (hottestHidden == null || m.Value > hottestHidden.Value)) hottestHidden = m;
            }

            if (_metricsMoreButton != null && _metricsMoreWarn != null)
            {
                bool showWarn = !_metricsExpanded && hottestHidden != null;
                string warnText = showWarn
                    ? $"{hottestHidden.DisplayName} {FormatMetricValue(hottestHidden)}{FormatMetricUnit(hottestHidden)} 偏高"
                    : "";
                if (_metricsMoreWarn.Text != warnText) _metricsMoreWarn.Text = warnText;
                var moreBg = UiKit.FrozenBrush(showWarn ? UiKit.WarnSurface : UiKit.RaisedColor);
                if (!ReferenceEquals(_metricsMoreButton.Background, moreBg)) _metricsMoreButton.Background = moreBg;
            }
        }

        void BuildMetricStructure(StackPanel host, List<MetricValue> metrics, bool[] pinned, string signature)
        {
            host.Children.Clear();
            _metricSig = signature;
            _metricValueRuns = new List<Run>(metrics.Count);
            _metricUnitRuns = new List<Run>(metrics.Count);
            _metricNameBlocks = new List<TextBlock>(metrics.Count);
            _metricTiles = new List<Border>(metrics.Count);
            _metricPinned = new List<bool>(metrics.Count);
            _metricsMoreButton = null;
            _metricsMoreWarn = null;

            var pinnedGrid = new UniformGrid { Columns = 3, Margin = new Thickness(-2, 0, -2, 0) };
            var restGrid = new UniformGrid { Columns = 3, Margin = new Thickness(-2, 0, -2, 0) };
            int restCount = 0;
            // Tiles are created for every metric so per-second updates can
            // index by metric position; hidden ones are simply not attached.
            for (int i = 0; i < metrics.Count; i++)
            {
                bool isPinned = pinned[i];
                var tile = MakeMetricTile(metrics[i], isPinned);
                _metricPinned.Add(isPinned);
                if (isPinned) pinnedGrid.Children.Add(tile);
                else
                {
                    restCount++;
                    if (_metricsExpanded) restGrid.Children.Add(tile);
                }
            }
            if (pinnedGrid.Children.Count > 0) host.Children.Add(pinnedGrid);
            if (restCount == 0) return;

            if (_metricsExpanded)
            {
                var caption = new DockPanel { Margin = new Thickness(0, 8, 0, 6), LastChildFill = true };
                var captionText = UiKit.Text("其余", 11, UiKit.TextTertiary);
                DockPanel.SetDock(captionText, Dock.Left);
                caption.Children.Add(captionText);
                caption.Children.Add(new Border { Height = 1, Background = UiKit.FrozenBrush(UiKit.BorderColor), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                host.Children.Add(caption);
                host.Children.Add(restGrid);
            }

            // 「另 N 项」/「收起」：隐藏项里有偏高温度时整行转为琥珀色提醒。
            var row = new DockPanel { LastChildFill = true };
            var chevron = IconCatalog.CreateElement(_metricsExpanded ? IconKey.ChevronUp : IconKey.ChevronDown, 12,
                UiKit.FrozenBrush(UiKit.TextSecondary));
            chevron.VerticalAlignment = VerticalAlignment.Center;
            chevron.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(chevron, Dock.Right);
            row.Children.Add(chevron);
            var count = UiKit.Text(_metricsExpanded ? "收起" : $"另 {restCount} 项", 12, UiKit.TextSecondary);
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);
            _metricsMoreWarn = UiKit.Text("", 12, UiKit.WarnColor);
            row.Children.Add(_metricsMoreWarn);
            var more = new Border
            {
                Background = UiKit.FrozenBrush(UiKit.RaisedColor),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 0, 10, 0),
                Height = 32,
                Margin = new Thickness(0, 6, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = row,
                ToolTip = _metricsExpanded ? "收起其余指标" : "展开其余指标"
            };
            System.Windows.Automation.AutomationProperties.SetName(more, _metricsExpanded ? "收起其余指标" : "展开其余指标");
            more.MouseLeftButtonUp += (s, e) =>
            {
                _metricsExpanded = !_metricsExpanded;
                _metricSig = null;
                UpdateTempUI();
                e.Handled = true;
            };
            _metricsMoreButton = more;
            host.Children.Add(more);
        }

        Border MakeMetricTile(MetricValue m, bool large)
        {
            var name = new TextBlock
            {
                Text = m.DisplayName ?? "",
                FontFamily = AppFont,
                FontSize = 11,
                Foreground = UiKit.FrozenBrush(UiKit.TextSecondary),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = m.DisplayName
            };
            var valueRun = new Run(FormatMetricValue(m)) { Foreground = UiKit.FrozenBrush(UiKit.TextPrimary) };
            var unitRun = new Run(FormatMetricUnit(m))
            {
                FontSize = large ? 11 : 10,
                FontWeight = FontWeights.Normal,
                Foreground = UiKit.FrozenBrush(UiKit.TextSecondary)
            };
            var value = new TextBlock
            {
                FontFamily = AppFont,
                FontSize = large ? 18 : 13,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
            value.Inlines.Add(valueRun);
            value.Inlines.Add(unitRun);
            var stack = new StackPanel();
            stack.Children.Add(name);
            stack.Children.Add(value);
            var tile = new Border
            {
                Background = UiKit.FrozenBrush(UiKit.RaisedColor),
                CornerRadius = new CornerRadius(large ? 8 : 6),
                Padding = large ? new Thickness(10, 7, 8, 7) : new Thickness(8, 5, 6, 5),
                Margin = new Thickness(2),
                Child = stack
            };
            _metricValueRuns.Add(valueRun);
            _metricUnitRuns.Add(unitRun);
            _metricNameBlocks.Add(name);
            _metricTiles.Add(tile);
            return tile;
        }

        // 折叠态摘要：置顶指标里的前两个温度 + 内存占用（内存每 10 秒读一次）。
        void UpdateCollapsedSummary(List<MetricValue> metrics)
        {
            var parts = new System.Text.StringBuilder(48);
            if (ModuleVisible("Temp") && metrics.Count > 0)
            {
                var pinned = ResolvePinned(metrics);
                int shown = 0;
                for (int pass = 0; pass < 2 && shown < 2; pass++)
                    for (int i = 0; i < metrics.Count && shown < 2; i++)
                    {
                        var m = metrics[i];
                        if (!m.IsTemp || !m.Value.HasValue || (pass == 0) != pinned[i]) continue;
                        if (parts.Length > 0) parts.Append("  ");
                        parts.Append(m.DisplayName).Append(' ').Append(m.Value.Value.ToString("0")).Append('°');
                        shown++;
                    }
            }
            if (ModuleVisible("Mem"))
            {
                var now = DateTime.UtcNow;
                if (!_collapsedMemoryKnown || (now - _collapsedMemoryReadUtc).TotalSeconds >= 10)
                {
                    _collapsedMemoryReadUtc = now;
                    try
                    {
                        var status = MemoryCleaner.GetStatus();
                        if (status != null) { _collapsedMemoryPercent = status.MemoryLoadPercent; _collapsedMemoryKnown = true; }
                    }
                    catch { }
                }
                if (_collapsedMemoryKnown)
                {
                    if (parts.Length > 0) parts.Append("  ");
                    parts.Append("内存 ").Append(_collapsedMemoryPercent).Append('%');
                }
            }
            string text = parts.ToString();
            if (_collapsedTempLabel.Text != text)
            {
                _collapsedTempLabel.FontFamily = AppFont;
                _collapsedTempLabel.Text = text;
            }
        }
    }
}

