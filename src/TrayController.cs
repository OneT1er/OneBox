using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using OneBox.Contracts;
using PowerAudioManager.Commands;
using System.Windows.Threading;
using System.Windows.Media;

namespace PowerAudioManager
{
    // WPF-native tray host. TaskbarIcon owns the shell handle and uses the
    // authored app.ico shared with the window and assembly.
    internal sealed class TrayController
    {
        readonly MainWindow _owner;
        readonly Action _onExit;
        TaskbarIcon _tray;
        ContextMenu _menu;
        MenuItem _topmostItem;
        MenuItem _lockItem;
        MenuItem _autoStartItem;
        MenuItem _oneMicItem, _memoryItem, _hiddenAudioItem;
        DispatcherTimer _recreateTimer;
        IDisposable _createdSubscription;
        IDisposable _removedSubscription;
        bool _disposed;
        bool _hasObservedState;
        bool _lastObservedCreated;
        int _retryAttempts;
        string _lastFailure;

        const int MaxTrayRetryAttempts = 10;

        public TrayController(MainWindow owner, Action onExit) { _owner = owner; _onExit = onExit; }

        public void Init()
        {
            if (_disposed) return;
            if (_tray != null && _tray.IsCreated)
            {
                AppLog.Log("Tray", "already initialized (created=true)");
                return;
            }
            try
            {
                _tray = new TaskbarIcon
                {
                    IconSource = AppResources.LoadAppImage("app.ico"),
                    ToolTipText = "OneBox",
                    Visibility = Visibility.Visible
                };
                _menu = ContextMenuTheme.Create(includeHeader: true);
                var showItem = AddMenuItem("显示窗口", async (_, __) => await _owner.ExecuteCommandAsync(AppCommandId.WindowShow, CommandSource.Tray),
                    UiKit.Glyphs.Window);
                showItem.InputGestureText = "左键";
                _oneMicItem = AddMenuItem("OneMic…", async (_, __) => await _owner.ExecuteCommandAsync(AppCommandId.StudioOpen, CommandSource.Tray),
                    null, IconKey.OneMic);
                _autoStartItem = new MenuItem { Header = "开机自启", IsCheckable = true, IsChecked = AutoStartService.GetCurrent() != AutoStartMethod.None };
                _autoStartItem.Click += async (_, __) =>
                {
                    var result = await _owner.ExecuteCommandAsync(AppCommandId.AutoStartApply, CommandSource.Tray,
                        new AutoStartApplyPayload(_autoStartItem.IsChecked, AppPrefs.Get(PreferenceKeys.AutoStart.LastMethod)));
                    if (!result.Success) _autoStartItem.IsChecked = !_autoStartItem.IsChecked;
                };
                _menu.Items.Add(new Separator());
                _menu.Items.Add(_autoStartItem);
                _topmostItem = new MenuItem { Header = "窗口置顶", IsCheckable = true, IsChecked = _owner._topmost };
                _topmostItem.Click += async (_, __) =>
                {
                    bool previous = _owner._topmost;
                    bool next = _topmostItem.IsChecked;
                    if (!AppPrefs.Set(PreferenceKeys.Window.Topmost, next))
                    {
                        _topmostItem.IsChecked = previous;
                        OneBoxMessage.Error(_owner, "OneBox 设置", "窗口置顶设置保存失败。");
                        return;
                    }
                    var result = await _owner.ExecuteCommandAsync(AppCommandId.RuntimeApplyGeneral, CommandSource.Tray,
                        new GeneralRuntimePayload(next, _owner._lockPosition, false));
                    if (!result.Success)
                    {
                        AppPrefs.Set(PreferenceKeys.Window.Topmost, previous);
                        _topmostItem.IsChecked = previous;
                    }
                };
                _menu.Items.Add(_topmostItem);
                _lockItem = new MenuItem { Header = "锁定位置", IsCheckable = true, IsChecked = _owner._lockPosition };
                _lockItem.Click += async (_, __) =>
                {
                    bool previous = _owner._lockPosition;
                    bool next = _lockItem.IsChecked;
                    if (!AppPrefs.Set(PreferenceKeys.Window.LockPosition, next))
                    {
                        _lockItem.IsChecked = previous;
                        OneBoxMessage.Error(_owner, "OneBox 设置", "锁定位置设置保存失败。");
                        return;
                    }
                    var result = await _owner.ExecuteCommandAsync(AppCommandId.RuntimeApplyGeneral, CommandSource.Tray,
                        new GeneralRuntimePayload(_owner._topmost, next, false));
                    if (!result.Success)
                    {
                        AppPrefs.Set(PreferenceKeys.Window.LockPosition, previous);
                        _lockItem.IsChecked = previous;
                    }
                };
                _menu.Items.Add(_lockItem);
                _hiddenAudioItem = new MenuItem { Header = "显示已隐藏设备", Icon = UiKit.Glyph(UiKit.Glyphs.Eye, 14, UiKit.FrozenBrush(UiKit.TextSecondary)) };
                _menu.Items.Add(_hiddenAudioItem);
                _menu.Items.Add(new Separator());
                _memoryItem = AddMenuItem("内存清理", async (_, __) => await _owner.ExecuteCommandAsync(AppCommandId.MemoryClean, CommandSource.Tray,
                    new MemoryCleanPayload(MemoryCleaner.GetSavedFlags())), null, IconKey.MemoryClean, UiKit.AccentColor);
                _memoryItem.InputGestureText = "中键";
                AddMenuItem("设置…", async (_, __) => await _owner.ExecuteCommandAsync(AppCommandId.SettingsOpen, CommandSource.Tray, new SettingsOpenPayload(0)),
                    null, IconKey.Settings);
                AddMenuItem("检查更新…", async (_, __) => await _owner.ExecuteCommandAsync(AppCommandId.UpdateCheck, CommandSource.Tray, new UpdateCheckPayload(true)),
                    UiKit.Glyphs.Refresh);
                _menu.Items.Add(new Separator());
                var exitItem = AddMenuItem("退出 OneBox", async (_, __) => await _owner.ExecuteCommandAsync(AppCommandId.AppExit, CommandSource.Tray),
                    UiKit.Glyphs.Exit, null, UiKit.DangerColor);
                exitItem.Foreground = UiKit.FrozenBrush(UiKit.DangerColor);
                _menu.Opened += (_, _) =>
                {
                    RefreshModuleVisibility();
                    RefreshStatusHeader();
                    if (MainWindow.ModuleVisible("Audio"))
                        PopulateHiddenDevices(_hiddenAudioItem, AudioDevices.GetOutputDevices(), device =>
                        {
                            DevicePrefs.SetHidden(device.Name, false);
                            _owner.LoadData();
                            _owner.RefreshHotkeys();
                        });
                };
                RefreshModuleVisibility();
                _tray.ContextMenu = _menu;
                _tray.TrayLeftMouseDown += (_, __) => _ = _owner.ExecuteCommandAsync(AppCommandId.WindowShow, CommandSource.Tray);
                _tray.TrayMiddleMouseDown += (_, __) =>
                {
                    if (MainWindow.ModuleVisible("Mem")) _ = _owner.ExecuteCommandAsync(AppCommandId.MemoryClean, CommandSource.Tray,
                        new MemoryCleanPayload(MemoryCleaner.GetSavedFlags()));
                };
                if (_tray.TrayIcon != null)
                {
                    _createdSubscription = _tray.TrayIcon.SubscribeToCreated((_, __) =>
                    {
                        if (_disposed) return;
                        bool changed = !_hasObservedState || !_lastObservedCreated;
                        _hasObservedState = true;
                        _lastObservedCreated = true;
                        StopRetry();
                        if (changed) AppLog.Log("Tray", "state changed: shell icon created");
                    });
                    _removedSubscription = _tray.TrayIcon.SubscribeToRemoved((_, __) =>
                    {
                        if (_disposed) return;
                        bool changed = !_hasObservedState || _lastObservedCreated;
                        _hasObservedState = true;
                        _lastObservedCreated = false;
                        if (changed) AppLog.Log("Tray", "state changed: shell icon removed");
                        StartRetry("removed");
                    });
                }

                // Dynamic TaskbarIcon instances are not placed in Application.Resources,
                // so force creation explicitly and verify the shell handle. This also
                // gives us a deterministic recovery path after Explorer restarts.
                EnsureCreated("initial");
            }
            catch (Exception ex)
            {
                _lastFailure = ex.ToString();
                AppLog.Log("Tray", "initialization failed: " + ex);
                CleanupTrayOnly();
            }
        }

        internal bool IsCreated => _tray != null && _tray.IsCreated;
        internal string LastFailure => _lastFailure;

        void EnsureCreated(string reason)
        {
            if (_disposed || _tray == null) return;
            try
            {
                if (!_tray.IsCreated) _tray.ForceCreate(false);
                bool created = _tray.IsCreated;
                if (created)
                {
                    bool changed = !_hasObservedState || !_lastObservedCreated;
                    _hasObservedState = true;
                    _lastObservedCreated = true;
                    StopRetry();
                    if (changed) AppLog.Log("Tray", $"state changed: created=true (reason={reason})");
                }
                else
                {
                    bool changed = !_hasObservedState || _lastObservedCreated;
                    _hasObservedState = true;
                    _lastObservedCreated = false;
                    _lastFailure = "TaskbarIcon.ForceCreate returned without creating a shell icon.";
                    if (changed) AppLog.Log("Tray", $"state changed: created=false (reason={reason})");
                    StartRetry(reason);
                }
            }
            catch (Exception ex)
            {
                _lastFailure = ex.ToString();
                AppLog.Log("Tray", $"create failed (reason={reason}): {ex}");
                StartRetry(reason);
            }
        }

        void StartRetry(string reason)
        {
            if (_disposed || _tray == null || _recreateTimer != null) return;
            _retryAttempts = 0;
            _recreateTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _recreateTimer.Tick += (_, __) =>
            {
                if (_retryAttempts >= MaxTrayRetryAttempts)
                {
                    AppLog.Log("Tray", $"retry stopped after {MaxTrayRetryAttempts} attempts (reason={reason})");
                    StopRetry();
                    return;
                }
                _retryAttempts++;
                EnsureCreated("retry-" + _retryAttempts);
            };
            AppLog.Log("Tray", $"retry scheduled (reason={reason})");
            _recreateTimer.Start();
        }

        void StopRetry()
        {
            if (_recreateTimer == null) return;
            try { _recreateTimer.Stop(); } catch { }
            _recreateTimer = null;
            _retryAttempts = 0;
        }

        void CleanupTrayOnly()
        {
            StopRetry();
            try { _createdSubscription?.Dispose(); } catch { }
            try { _removedSubscription?.Dispose(); } catch { }
            _createdSubscription = null;
            _removedSubscription = null;
            try { _tray?.Dispose(); } catch { }
            _tray = null;
            _menu = null;
        }

        MenuItem AddMenuItem(string header, RoutedEventHandler action, string glyph = null, IconKey? icon = null, Color? iconColor = null)
        {
            var item = new MenuItem { Header = header };
            var brush = UiKit.FrozenBrush(iconColor ?? UiKit.TextSecondary);
            if (glyph != null) item.Icon = UiKit.Glyph(glyph, 14, brush);
            else if (icon.HasValue) item.Icon = IconCatalog.CreateElement(icon.Value, 14, brush);
            item.Click += action;
            _menu.Items.Add(item);
            return item;
        }

        // Header status line: 「内存 63% · 平衡」. Built from the same sources as
        // the tooltip, only when the menu opens.
        void RefreshStatusHeader()
        {
            if (_menu == null) return;
            try
            {
                var parts = new System.Collections.Generic.List<string>();
                int memPercent = -1;
                if (MainWindow.ModuleVisible("Mem"))
                {
                    try { var ms = MemoryCleaner.GetStatus(); if (ms != null) memPercent = (int)ms.MemoryLoadPercent; } catch { }
                }
                if (memPercent >= 0) parts.Add("内存 " + memPercent + "%");
                string status = _owner.TrayStatusText ?? "";
                foreach (var line in status.Split('\n'))
                {
                    var trimmed = line.Trim();
                    const string planPrefix = "电源计划: ";
                    if (trimmed.StartsWith(planPrefix, StringComparison.Ordinal) && trimmed.Length > planPrefix.Length)
                        parts.Add(trimmed.Substring(planPrefix.Length));
                }
                _menu.Tag = string.Join(" · ", parts);
                if (_memoryItem != null) _memoryItem.InputGestureText = memPercent >= 0 ? memPercent + "% · 中键" : "中键";
            }
            catch (Exception ex) { AppLog.Log("Tray", "status header: " + ex.Message); }
        }

        public void RefreshModuleVisibility()
        {
            if (_oneMicItem != null) _oneMicItem.Visibility = MainWindow.ModuleVisible("OneMic") ? Visibility.Visible : Visibility.Collapsed;
            if (_memoryItem != null) _memoryItem.Visibility = MainWindow.ModuleVisible("Mem") ? Visibility.Visible : Visibility.Collapsed;
            if (_hiddenAudioItem != null) _hiddenAudioItem.Visibility = MainWindow.ModuleVisible("Audio") ? Visibility.Visible : Visibility.Collapsed;
        }

        // Populate before WPF decides whether this is a submenu: an empty
        // MenuItem never opens, so SubmenuOpened cannot load its first child.
        internal static void PopulateHiddenDevices(MenuItem submenu, IEnumerable<AudioDeviceInfo> devices,
            Action<AudioDeviceInfo> restore)
        {
            submenu.Items.Clear();
            foreach (var device in devices)
            {
                if (!device.IsHidden) continue;
                var item = new MenuItem { Header = device.Name };
                item.Click += (_, _) => restore(device);
                submenu.Items.Add(item);
            }
            if (!submenu.HasItems)
                submenu.Items.Add(new MenuItem { Header = "没有已隐藏的设备", IsEnabled = false });
        }

        public void SetLockChecked(bool locked) { if (_lockItem != null) _lockItem.IsChecked = locked; }
        public void UpdateIcon() { UpdateTooltip(); }
        public void UpdateTooltip()
        {
            if (_tray == null || _disposed) return;
            try { var text = _owner.TrayStatusText ?? "OneBox"; _tray.ToolTipText = text.Length > 127 ? text.Substring(0, 126) + "…" : text; }
            catch (Exception ex) { AppLog.Log("UpdateTrayTooltip", ex); }
        }
        public void UpdateAutoStart() { if (_autoStartItem != null) _autoStartItem.IsChecked = AutoStartService.GetCurrent() != AutoStartMethod.None; }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            StopRetry();
            try { _createdSubscription?.Dispose(); } catch { }
            try { _removedSubscription?.Dispose(); } catch { }
            _createdSubscription = null;
            _removedSubscription = null;
            try { _menu?.Items.Clear(); } catch { }
            try { _tray?.Dispose(); } catch { }
            _tray = null; _menu = null;
        }
    }
}
