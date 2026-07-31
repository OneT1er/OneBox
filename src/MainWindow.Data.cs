using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;

namespace PowerAudioManager
{
    // 数据加载与渲染：电源计划 / 音频设备 / 音量 / 托盘状态文本。
    public partial class MainWindow : Window
    {
        internal void LoadData()
        {
            try { UpdateVolumeUI(); } catch { }
            try { UpdateMemoryUI(); } catch { }
            try { UpdateTrayTooltip(); } catch { }
            // 防止卡死的后台刷新（如 powercfg 在策略刷新时挂起）：超过 10s 认为已死，允许新的一次。
            if (_loading && (DateTime.Now - _loadStartTime).TotalSeconds < 10) return;
            _loading = true;
            _loadStartTime = DateTime.Now;
            // 在线程池获取电源计划/设备，独立 try/catch 保证 powercfg 异常不会清空音频列表（设备来自注册表）。
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                List<PowerPlanInfo> plans = null;
                List<AudioDeviceInfo> devices = null;
                try { plans = PowerPlanService.GetPowerPlans(); } catch (Exception ex) { AppLog.Log("LoadData plans", ex); }
                try { devices = AudioDevices.GetOutputDevices(); } catch (Exception ex) { AppLog.Log("LoadData devices", ex); }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _loading = false;
                    RenderPlans(plans);
                    RenderDevices(devices);
                }));
            });
        }

        void RenderPlans(List<PowerPlanInfo> plans)
        {
            try
            {
                // 获取失败时保留上次列表，避免短暂 powercfg 失败清空 UI。
                if (plans == null) plans = _powerPlans;
                _powerPlans = plans ?? new List<PowerPlanInfo>();
                var active = _powerPlans.Find(p => p.IsActive);
                if (active != null) _currentPlanId = active.Guid;
                if (_powerSection == null) return; // module hidden
                _powerSection.Children.Clear();
                if (_powerPlans.Count == 0)
                {
                    _powerSection.Children.Add(new TextBlock
                    {
                        Text = "未找到电源计划",
                        Foreground = new SolidColorBrush(UiKit.TextSecondary),
                        FontSize = 11
                    });
                }
                else
                {
                    foreach (var plan in _powerPlans)
                        _powerSection.Children.Add(CreatePlanButton(plan));
                }
            }
            catch { }
        }

        void RenderDevices(List<AudioDeviceInfo> devices)
        {
            try
            {
                // 获取失败保留上次列表，避免短暂错误清空音频设备名称。
                if (devices == null) devices = _audioDevices;
                _audioDevices = devices ?? new List<AudioDeviceInfo>();
                var defaultDev = _audioDevices.Find(d => d.IsDefault);
                if (defaultDev != null) _currentDeviceId = defaultDev.Id;
                if (_audioSection == null) return; // module hidden
                _audioSection.Children.Clear();
                if (_audioDevices.Count == 0)
                {
                    _audioSection.Children.Add(new TextBlock
                    {
                        Text = "未找到音频设备",
                        Foreground = new SolidColorBrush(UiKit.TextSecondary),
                        FontSize = 11
                    });
                }
                else
                {
                    foreach (var dev in _audioDevices) if (!dev.IsHidden)
                        _audioSection.Children.Add(CreateDeviceButton(dev));
                }
            }
            catch { }
        }

        Button CreatePlanButton(PowerPlanInfo plan)
        {
            var isActive = plan.IsActive || plan.Guid == _currentPlanId;
            var btn = new Button
            {
                Content = plan.Name,
                Tag = plan.Guid,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 2, 0, 2),
                FontSize = 12,
                Cursor = Cursors.Hand
            };
            StyleButton(btn, isActive);
            btn.MouseDoubleClick += (s, e) => { try { System.Diagnostics.Process.Start("control.exe", "powercfg.cpl"); } catch { } e.Handled = true; };
            btn.Click += (s, e) =>
            {
                // 乐观标记选中计划为活动态让 UI 即时响应，再后台切换避免系统策略刷新导致 1-3s UI 冻结。
                _currentPlanId = plan.Guid;
                foreach (var p in _powerPlans) p.IsActive = p.Guid == plan.Guid;
                if (_powerSection == null) return;
                _powerSection.Children.Clear();
                foreach (var p in _powerPlans) _powerSection.Children.Add(CreatePlanButton(p));
                PowerPlanService.SetActivePlanAsync(plan.Guid, Dispatcher, ok => { AppLog.Log("PowerPlan", "switch to " + plan.Name + " (" + plan.Guid + ") ok=" + ok); if (ok) LoadData(); });
            };
            return btn;
        }

        Button CreateDeviceButton(AudioDeviceInfo dev)
        {
            var isActive = dev.IsDefault || dev.Id == _currentDeviceId;
            var content = new DockPanel { LastChildFill = true };
            string hkText = dev.HotkeyIndex != 0 ? HotkeyCaptureDialog.Format(dev.HotkeyIndex) : null;
            if (hkText != null)
            {
                var hkBlock = new TextBlock {
                    Text = hkText,
                    FontSize = 10,
                    Opacity = 0.75,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(hkBlock, Dock.Right);
                content.Children.Add(hkBlock);
            }
            var nameBlock = new TextBlock {
                Text = string.IsNullOrEmpty(dev.Name) ? "(未命名设备)" : dev.Name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White // 显式设白色避免继承隐藏色
            };
            content.Children.Add(nameBlock);
            var btn = new Button {
                Content = content,
                Tag = dev.Id,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 2, 0, 2),
                FontSize = 12,
                Cursor = Cursors.Hand,
                ToolTip = hkText != null ? dev.Name + "  [" + hkText + "]" : dev.Name
            };
            StyleButton(btn, isActive);
            btn.Click += (s, e) =>
            {
                if (AudioDevices.SetDefaultDevice(dev.Id))
                {
                    _currentDeviceId = dev.Id;
                    VolumeControl.Invalidate();
                    LoadData();
                    ScheduleVolumeRefresh();
                }
            };
            var devCtx = new ContextMenu();
            var hideItem = new MenuItem { Header = "隐藏此设备" };
            hideItem.Click += (s, e) => { DevicePrefs.SetHidden(dev.Name, true); LoadData(); RefreshHotkeys(); };
            devCtx.Items.Add(hideItem);
            var hkItem = new MenuItem { Header = "设置快捷键..." };
            hkItem.Click += (s, e) => {
                // 暂时释放全部全局快捷键，让对话框能捕获冲突组合键。
                UnregisterAllHotkeys();
                int? captured = null;
                try { captured = HotkeyCaptureDialog.Show(this, dev.HotkeyIndex); }
                finally
                {
                    if (captured.HasValue) DevicePrefs.SetHotkeyKey(dev.Name, captured.Value);
                    LoadData();
                    RefreshHotkeys();
                }
            };
            var clearItem = new MenuItem { Header = "清除快捷键" };
            clearItem.Click += (s, e) => { DevicePrefs.SetHotkeyKey(dev.Name, 0); LoadData(); RefreshHotkeys(); };
            devCtx.Items.Add(hkItem);
            devCtx.Items.Add(clearItem);
            btn.ContextMenu = devCtx;
            return btn;
        }

        void UpdateVolumeUI()
        {
            if (_volSlider == null) return;
            _volSliderUpdating = true;
            try { _volSlider.Value = VolumeControl.GetVolume() * 100; if (_volLabel != null) _volLabel.Text = ((int)_volSlider.Value).ToString() + "%"; } catch { }
            _volSliderUpdating = false;
            _muteBtn.Content = UiKit.MuteIcon(VolumeControl.GetMute());
        }

        void ScheduleVolumeRefresh()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            int hits = 0;
            t.Tick += (s, e) =>
            {
                VolumeControl.Invalidate();
                UpdateVolumeUI();
                if (++hits >= 3) t.Stop();
            };
            t.Start();
        }

        internal string TrayStatusText
        {
            get
            {
                string plan = "(无)", dev = "(无)";
                try { if (_powerPlans != null) { var p = _powerPlans.Find(x => x.IsActive || x.Guid == _currentPlanId); if (p != null) plan = p.Name; } } catch { }
                try { if (_audioDevices != null) { var d = _audioDevices.Find(x => x.IsDefault); if (d != null) dev = d.Name; } } catch { }
                string mem = "";
                try { var ms = MemoryCleaner.GetStatus(); if (ms != null) mem = string.Format(System.Environment.NewLine + "内存: {0:0.0}/{1:0.0} GB ({2}%) · 已缓存 {3:0.0}GB", (ms.TotalBytes - ms.AvailableBytes) / 1073741824.0, ms.TotalBytes / 1073741824.0, ms.MemoryLoadPercent, ms.CachedBytes / 1073741824.0); } catch { }
                return "电源计划: " + plan + System.Environment.NewLine + "音频设备: " + dev + mem;
            }
        }

        void UpdateTrayTooltip()
        {
            if (_tray != null) _tray.UpdateTooltip();
        }
    }
}


