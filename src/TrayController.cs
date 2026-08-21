using System;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using OneBox.Contracts;
using PowerAudioManager.Commands;

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
        bool _disposed;

        public TrayController(MainWindow owner, Action onExit) { _owner = owner; _onExit = onExit; }

        public void Init()
        {
            if (_disposed) return;
            try
            {
                _tray = new TaskbarIcon
                {
                    IconSource = AppResources.LoadAppImage("app.ico"),
                    ToolTipText = "OneBox",
                    Visibility = Visibility.Visible
                };
                _menu = new ContextMenu { Background = UiKit.FrozenBrush(UiKit.BgColor), Foreground = UiKit.FrozenBrush(UiKit.TextPrimary), Padding = new Thickness(4) };
                AddMenuItem("显示窗口", async (_, __) => await _owner.ExecuteCommandAsync(AppCommandId.WindowShow, CommandSource.Tray));
                _autoStartItem = new MenuItem { Header = "开机自启", IsCheckable = true, IsChecked = AutoStartService.GetCurrent() != AutoStartMethod.None };
                _autoStartItem.Click += async (_, __) =>
                {
                    var result = await _owner.ExecuteCommandAsync(AppCommandId.AutoStartApply, CommandSource.Tray,
                        new AutoStartApplyPayload(_autoStartItem.IsChecked, AppPrefs.Get(PreferenceKeys.AutoStart.LastMethod)));
                    if (!result.Success) _autoStartItem.IsChecked = !_autoStartItem.IsChecked;
                };
                _menu.Items.Add(_autoStartItem);
                _menu.Items.Add(new Separator());
                _topmostItem = new MenuItem { Header = "窗口置顶", IsCheckable = true, IsChecked = _owner._topmost };
                _topmostItem.Click += async (_, __) =>
                {
                    bool previous = _owner._topmost;
                    bool next = _topmostItem.IsChecked;
                    if (!AppPrefs.Set(PreferenceKeys.Window.Topmost, next))
                    {
                        _topmostItem.IsChecked = previous;
                        MessageBox.Show(_owner, "窗口置顶设置保存失败。", "OneBox 设置",
                            MessageBoxButton.OK, MessageBoxImage.Error);
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
                        MessageBox.Show(_owner, "锁定位置设置保存失败。", "OneBox 设置",
                            MessageBoxButton.OK, MessageBoxImage.Error);
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
                var hiddenSub = new MenuItem { Header = "显示已隐藏设备" };
                hiddenSub.SubmenuOpened += (_, __) =>
                {
                    hiddenSub.Items.Clear(); bool any = false;
                    foreach (var d in AudioDevices.GetOutputDevices()) if (d.IsHidden)
                    {
                        any = true; var copy = d; var item = new MenuItem { Header = d.Name };
                        item.Click += (__, ___) => { DevicePrefs.SetHidden(copy.Name, false); _owner.LoadData(); };
                        hiddenSub.Items.Add(item);
                    }
                    hiddenSub.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
                };
                _menu.Items.Add(hiddenSub);
                _menu.Items.Add(new Separator());
                AddMenuItem("内存清理", async (_, __) => await _owner.ExecuteCommandAsync(AppCommandId.MemoryClean, CommandSource.Tray,
                    new MemoryCleanPayload(MemoryCleaner.GetSavedFlags())));
                AddMenuItem("设置...", async (_, __) => await _owner.ExecuteCommandAsync(AppCommandId.SettingsOpen, CommandSource.Tray, new SettingsOpenPayload(0)));
                AddMenuItem("检查更新...", async (_, __) => await _owner.ExecuteCommandAsync(AppCommandId.UpdateCheck, CommandSource.Tray, new UpdateCheckPayload(true)));
                _menu.Items.Add(new Separator());
                AddMenuItem("退出", async (_, __) => await _owner.ExecuteCommandAsync(AppCommandId.AppExit, CommandSource.Tray));
                _tray.ContextMenu = _menu;
                _tray.TrayLeftMouseDown += (_, __) => _ = _owner.ExecuteCommandAsync(AppCommandId.WindowShow, CommandSource.Tray);
                _tray.TrayMiddleMouseDown += (_, __) => _ = _owner.ExecuteCommandAsync(AppCommandId.MemoryClean, CommandSource.Tray,
                    new MemoryCleanPayload(MemoryCleaner.GetSavedFlags()));
            }
            catch (Exception ex) { AppLog.Log("InitTray", ex); }
        }

        void AddMenuItem(string header, RoutedEventHandler action)
        {
            var item = new MenuItem { Header = header };
            item.Click += action;
            _menu.Items.Add(item);
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
            try { _menu?.Items.Clear(); } catch { }
            try { _tray?.Dispose(); } catch { }
            _tray = null; _menu = null;
        }
    }
}
