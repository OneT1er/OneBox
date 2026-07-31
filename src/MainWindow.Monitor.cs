using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Threading;
using System.IO;

namespace PowerAudioManager
{
    // 温度/性能监控：OneBoxSvc 守护、硬件传感器轮询、指标行与折叠态温度刷新。
    public partial class MainWindow : Window
    {
        void EnsureServiceRunning()
        {
            try
            {
                using (var svc = new System.ServiceProcess.ServiceController("OneBoxSvc"))
                {
                    if (svc.Status != System.ServiceProcess.ServiceControllerStatus.Running)
                    {
                        AppLog.Log("Service", "OneBoxSvc not running, starting (UAC)");
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sc.exe", "start OneBoxSvc") { Verb = "runas", UseShellExecute = true }); } catch { }
                    }
                }
            }
            catch (Exception ex) { AppLog.Log("Service", "EnsureServiceRunning: " + ex.Message); }
        }

        void StartTempMonitor()
        {
            if (!ModuleVisible("Temp")) return;
            // PerfHistory/ForegroundHistory 改为性能趋势图窗口打开时按需加载采集（见 PerfChartWindow.Acquire/Release），
            // 不在启动时加载、不在后台常驻--图表关闭即释放每条 series ~1MB 内存。
            HardwareMonitorService.Instance.Start();
            StartTempTimer();
        }


        void StartTempTimer()
        {
            try { _tempTimer?.Dispose(); } catch { }
            int intervalMs = AppPrefs.GetInt("Temp.IntervalMs", 1000);
            if (intervalMs < 500) intervalMs = 500;
            if (intervalMs > 60000) intervalMs = 60000;
            _tempTimer = new System.Threading.Timer(_ =>
            {
                HardwareMonitorService.Instance.Update();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateTempUI();
                    PerfHistory.Add(HardwareMonitorService.Instance.ActiveMetrics);
                }));
            }, null, 2000, intervalMs);
        }

        internal void RestartTempTimer()
        {
            try { _tempTimer?.Dispose(); } catch { }
            if (!ModuleVisible("Temp")) return;
            HardwareMonitorService.Instance.Start();
            StartTempTimer();
        }

        void UpdateTempUI()
        {
            try
            {
                var metrics = HardwareMonitorService.Instance.ActiveMetrics;
                int warnC = AppPrefs.GetInt("Temp.WarnC", 80);
                int critC = AppPrefs.GetInt("Temp.CriticalC", 95);

                Color TempColor(float? v)
                {
                    if (!v.HasValue) return UiKit.TextSecondary;
                    if (v >= critC) return Color.FromRgb(255, 80, 80);
                    if (v >= warnC) return Color.FromRgb(255, 180, 80);
                    return UiKit.TextSecondary;
                }

                // 展开视图：仅在指标集合变化时重建结构；每秒只更新数值文本与颜色。
                // 旧实现每秒 Clear+new 一批 TextBlock/Brush/Image/Geometry，是高频 GC 与组合树碎片主因。
                if (_metricRow != null)
                {
                    if (metrics.Count == 0)
                    {
                        if (_metricValBlocks != null || _metricRow.Children.Count == 0)
                        {
                            _metricRow.Children.Clear();
                            _metricRow.Children.Add(new TextBlock { Text = "传感器初始化中…", Foreground = UiKit.FrozenBrush(UiKit.TextSecondary), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
                            _metricValBlocks = null; _metricKeys = null;
                        }
                    }
                    else
                    {
                        bool sameSet = _metricValBlocks != null && _metricValBlocks.Count == metrics.Count;
                        if (sameSet)
                            for (int i = 0; i < metrics.Count; i++)
                                if (!string.Equals(metrics[i].ConfigKey, _metricKeys[i], StringComparison.Ordinal)) { sameSet = false; break; }
                        if (!sameSet)
                        {
                            _metricRow.Children.Clear();
                            _metricValBlocks = new List<TextBlock>(metrics.Count);
                            _metricKeys = new List<string>(metrics.Count);
                            for (int i = 0; i < metrics.Count; i++)
                            {
                                if (i > 0)
                                    _metricRow.Children.Add(new TextBlock { Text = " │ ", Foreground = UiKit.FrozenBrush(UiKit.BorderColor), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.5 });

                                var m = metrics[i];
                                var iconColor = UiKit.MetricIconColorByKey(m.IconKey);
                                var chip = new StackPanel { Orientation = Orientation.Horizontal };
                                chip.Children.Add(UiKit.MetricIcon(m.IconKey, iconColor));
                                chip.Children.Add(new TextBlock { Text = " " + m.DisplayName + " ", FontFamily = AppFont, FontSize = 11, Foreground = UiKit.FrozenBrush(UiKit.TextSecondary), VerticalAlignment = VerticalAlignment.Center });
                                var val = new TextBlock { FontFamily = AppFont, FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                                chip.Children.Add(val);
                                _metricRow.Children.Add(chip);
                                _metricValBlocks.Add(val);
                                _metricKeys.Add(m.ConfigKey ?? "");
                            }
                        }
                        // 每秒只改数值文本与颜色，零结构重建
                        for (int i = 0; i < metrics.Count && i < _metricValBlocks.Count; i++)
                        {
                            var m = metrics[i];
                            var blk = _metricValBlocks[i];
                            blk.Text = $"{m.Value?.ToString("0") ?? "--"}{m.Unit}";
                            blk.Foreground = UiKit.FrozenBrush(m.IsTemp ? TempColor(m.Value) : UiKit.TextSecondary);
                        }
                    }
                }

                // 折叠视图：固定 CPU + GPU，也用 Inlines 保持字体统一
                if (_collapsedTempLabel != null)
                {
                    var hw = HardwareMonitorService.Instance;
                    _collapsedTempLabel.Inlines.Clear();
                    _collapsedTempLabel.FontFamily = CompFont;
                    _collapsedTempLabel.FontSize = 10;
                    if (hw.CpuTemperature.HasValue)
                    {
                        _collapsedTempLabel.Inlines.Add(new Run("\U0001F321 "));
                        _collapsedTempLabel.Inlines.Add(new Run($"{hw.CpuTemperature.Value:0}  "));
                    }
                    if (hw.GpuTemperature.HasValue)
                    {
                        _collapsedTempLabel.Inlines.Add(new Run("\U0001F3AE "));
                        _collapsedTempLabel.Inlines.Add(new Run($"{hw.GpuTemperature.Value:0}"));
                    }
                }
            }
            catch { }
        }
    }
}

