using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using H.NotifyIcon;
using OneBox.Contracts;
using PowerAudioManager.Commands;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Documents;

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
                _menu = CreateTrayMenu();
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
                var hiddenSub = new MenuItem { Header = "显示已隐藏设备", Icon = UiKit.Glyph(UiKit.Glyphs.Eye, 14, UiKit.FrozenBrush(UiKit.TextSecondary)) };
                _hiddenAudioItem = hiddenSub;
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
                _menu.Opened += (_, _) => { RefreshModuleVisibility(); RefreshStatusHeader(); };
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

        // H.NotifyIcon hosts a WPF ContextMenu in a separate Popup window. The
        // stock MenuItem template brings back the Windows white check gutter and
        // an opaque popup border, even when ContextMenu.Background is dark. Keep
        // the whole tray menu authored here so the popup chrome, check slot,
        // separators and submenu arrows share the OneBox surface tokens.
        static readonly Color TrayMenuSurface = Color.FromArgb(250, 23, 21, 31);   // #17151F
        static readonly Color TrayMenuBorder = Color.FromArgb(255, 46, 42, 62);    // #2E2A3E

        static ContextMenu CreateTrayMenu()
        {
            var menu = new ContextMenu
            {
                Background = new SolidColorBrush(TrayMenuSurface),
                Foreground = UiKit.FrozenBrush(UiKit.TextPrimary),
                BorderBrush = new SolidColorBrush(TrayMenuBorder),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4),
                MinWidth = 240,
                HasDropShadow = false,
                // The outer popup is transparent; only the rounded Border in
                // the template paints the menu surface (no white rectangle).
                Template = CreateTrayContextMenuTemplate()
            };
            menu.Background.Freeze();
            menu.BorderBrush.Freeze();
            // Keep the Popup host from reintroducing the platform shadow even
            // on Windows builds that read the attached service property.
            ContextMenuService.SetHasDropShadow(menu, false);
            menu.Resources[typeof(MenuItem)] = CreateTrayMenuItemStyle();
            menu.Resources[typeof(Separator)] = CreateTraySeparatorStyle();
            return menu;
        }

        static string SafeVersion()
        {
            try { var v = UpdateChecker.CurrentVersion; return string.IsNullOrEmpty(v) ? "" : "v" + v; }
            catch { return ""; }
        }

        // Menu chrome: rounded surface, a non-interactive header block (brand,
        // version and a status line bound to ContextMenu.Tag), then the items.
        static ControlTemplate CreateTrayContextMenuTemplate()
        {
            var template = new ControlTemplate(typeof(ContextMenu));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "MenuChrome";
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            chrome.SetValue(Border.SnapsToDevicePixelsProperty, true);
            chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            var stack = new FrameworkElementFactory(typeof(StackPanel));

            var head = new FrameworkElementFactory(typeof(StackPanel));
            head.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 8, 10, 6));
            var titleRow = new FrameworkElementFactory(typeof(DockPanel));
            titleRow.SetValue(DockPanel.LastChildFillProperty, true);
            var brand = new FrameworkElementFactory(typeof(Path));
            brand.SetValue(Path.DataProperty, IconCatalog.GetGeometry(IconKey.Brand));
            brand.SetValue(FrameworkElement.WidthProperty, 16d);
            brand.SetValue(FrameworkElement.HeightProperty, 16d);
            brand.SetValue(Path.StretchProperty, Stretch.Uniform);
            brand.SetValue(Path.StrokeProperty, UiKit.FrozenBrush(UiKit.AccentColor));
            brand.SetValue(Path.StrokeThicknessProperty, 1.8d);
            brand.SetValue(Path.StrokeLineJoinProperty, PenLineJoin.Round);
            brand.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
            brand.SetValue(DockPanel.DockProperty, Dock.Left);
            titleRow.AppendChild(brand);
            var version = new FrameworkElementFactory(typeof(TextBlock));
            version.SetValue(TextBlock.TextProperty, SafeVersion());
            version.SetValue(TextBlock.FontSizeProperty, 11d);
            version.SetValue(TextBlock.ForegroundProperty, UiKit.FrozenBrush(UiKit.TextSecondary));
            version.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            version.SetValue(DockPanel.DockProperty, Dock.Right);
            titleRow.AppendChild(version);
            var title = new FrameworkElementFactory(typeof(TextBlock));
            title.SetValue(TextBlock.TextProperty, "OneBox");
            title.SetValue(TextBlock.FontSizeProperty, 13d);
            title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            title.SetValue(TextBlock.ForegroundProperty, UiKit.FrozenBrush(UiKit.TextPrimary));
            title.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            titleRow.AppendChild(title);
            head.AppendChild(titleRow);
            var status = new FrameworkElementFactory(typeof(TextBlock));
            status.Name = "TrayStatus";
            status.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Tag") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            status.SetValue(TextBlock.FontSizeProperty, 11d);
            status.SetValue(TextBlock.ForegroundProperty, UiKit.FrozenBrush(UiKit.TextSecondary));
            status.SetValue(FrameworkElement.MarginProperty, new Thickness(24, 3, 0, 0));
            status.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            head.AppendChild(status);
            stack.AppendChild(head);

            var rule = new FrameworkElementFactory(typeof(Border));
            rule.SetValue(FrameworkElement.HeightProperty, 1d);
            rule.SetValue(Border.BackgroundProperty, UiKit.FrozenBrush(UiKit.RaisedColor));
            rule.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 2, 6, 4));
            stack.AppendChild(rule);

            var items = new FrameworkElementFactory(typeof(ItemsPresenter));
            items.SetValue(ItemsPresenter.SnapsToDevicePixelsProperty, true);
            stack.AppendChild(items);
            chrome.AppendChild(stack);
            template.VisualTree = chrome;

            // Hide the status line when there is nothing to report.
            var emptyStatus = new Trigger { Property = FrameworkElement.TagProperty, Value = null };
            emptyStatus.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "TrayStatus"));
            template.Triggers.Add(emptyStatus);
            var blankStatus = new Trigger { Property = FrameworkElement.TagProperty, Value = "" };
            blankStatus.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "TrayStatus"));
            template.Triggers.Add(blankStatus);
            return template;
        }

        static Style CreateTrayMenuItemStyle()
        {
            var style = new Style(typeof(MenuItem));
            style.Setters.Add(new Setter(Control.ForegroundProperty, UiKit.FrozenBrush(UiKit.TextPrimary)));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 0, 10, 0)));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 32d));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateTrayMenuItemTemplate()));
            return style;
        }

        // Row: [14px icon slot] header ............ [gesture] [switch | ›]
        // Checkable items render a small switch on the right; the accent
        // "CheckMark" tick sits inside its knob when on.
        static ControlTemplate CreateTrayMenuItemTemplate()
        {
            var template = new ControlTemplate(typeof(MenuItem));
            var root = new FrameworkElementFactory(typeof(Grid));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "MenuItemChrome";
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            chrome.SetValue(FrameworkElement.MinHeightProperty, new TemplateBindingExtension(FrameworkElement.MinHeightProperty));

            var row = new FrameworkElementFactory(typeof(DockPanel));
            row.SetValue(DockPanel.LastChildFillProperty, true);
            row.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var iconSlot = new FrameworkElementFactory(typeof(ContentPresenter));
            iconSlot.SetValue(ContentPresenter.ContentSourceProperty, "Icon");
            iconSlot.SetValue(FrameworkElement.WidthProperty, 14d);
            iconSlot.SetValue(FrameworkElement.HeightProperty, 14d);
            iconSlot.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
            iconSlot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            iconSlot.SetValue(DockPanel.DockProperty, Dock.Left);
            row.AppendChild(iconSlot);

            var arrow = new FrameworkElementFactory(typeof(Path));
            arrow.Name = "SubmenuArrow";
            arrow.SetValue(FrameworkElement.WidthProperty, 8d);
            arrow.SetValue(FrameworkElement.HeightProperty, 12d);
            arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 2, 0));
            arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            arrow.SetValue(DockPanel.DockProperty, Dock.Right);
            arrow.SetValue(Path.DataProperty, Geometry.Parse("M 1,1 L 6,6 L 1,11"));
            arrow.SetValue(Path.StrokeProperty, UiKit.FrozenBrush(UiKit.TextSecondary));
            arrow.SetValue(Path.StrokeThicknessProperty, 1.8d);
            arrow.SetValue(Path.StrokeStartLineCapProperty, PenLineCap.Round);
            arrow.SetValue(Path.StrokeEndLineCapProperty, PenLineCap.Round);
            arrow.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            row.AppendChild(arrow);

            // 30x16 switch for checkable items.
            var track = new FrameworkElementFactory(typeof(Border));
            track.Name = "SwitchTrack";
            track.SetValue(FrameworkElement.WidthProperty, 30d);
            track.SetValue(FrameworkElement.HeightProperty, 16d);
            track.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            track.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            track.SetValue(Border.BorderBrushProperty, UiKit.FrozenBrush(Color.FromRgb(90, 86, 128)));
            track.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            track.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 0, 0));
            track.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            track.SetValue(DockPanel.DockProperty, Dock.Right);
            track.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            var knob = new FrameworkElementFactory(typeof(Grid));
            knob.Name = "SwitchKnob";
            knob.SetValue(FrameworkElement.WidthProperty, 10d);
            knob.SetValue(FrameworkElement.HeightProperty, 10d);
            knob.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 2, 0));
            knob.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            knob.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var knobDot = new FrameworkElementFactory(typeof(Ellipse));
            knobDot.Name = "SwitchDot";
            knobDot.SetValue(Shape.FillProperty, UiKit.FrozenBrush(UiKit.TextSecondary));
            knob.AppendChild(knobDot);
            var check = new FrameworkElementFactory(typeof(Path));
            check.Name = "CheckMark";
            check.SetValue(FrameworkElement.WidthProperty, 6d);
            check.SetValue(FrameworkElement.HeightProperty, 6d);
            check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            check.SetValue(Path.StretchProperty, Stretch.Uniform);
            check.SetValue(Path.DataProperty, Geometry.Parse("M 1,6 L 5,10 L 12,1"));
            check.SetValue(Path.StrokeProperty, UiKit.FrozenBrush(UiKit.AccentColor));
            check.SetValue(Path.StrokeThicknessProperty, 1.6d);
            check.SetValue(Path.StrokeStartLineCapProperty, PenLineCap.Round);
            check.SetValue(Path.StrokeEndLineCapProperty, PenLineCap.Round);
            check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            knob.AppendChild(check);
            track.AppendChild(knob);
            row.AppendChild(track);

            var gesture = new FrameworkElementFactory(typeof(TextBlock));
            gesture.Name = "GestureText";
            gesture.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(MenuItem.InputGestureTextProperty));
            gesture.SetValue(TextBlock.FontSizeProperty, 11d);
            gesture.SetValue(TextBlock.ForegroundProperty, UiKit.FrozenBrush(UiKit.TextTertiary));
            gesture.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 0, 0));
            gesture.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            gesture.SetValue(DockPanel.DockProperty, Dock.Right);
            row.AppendChild(gesture);

            var header = new FrameworkElementFactory(typeof(ContentPresenter));
            header.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            header.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            header.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            header.SetValue(TextElement.FontFamilyProperty, AppResources.AppFont);
            header.SetValue(TextElement.FontSizeProperty, 12d);
            row.AppendChild(header);

            chrome.AppendChild(row);
            root.AppendChild(chrome);

            // MenuItem opens its children in a second Popup. Include that
            // surface in the authored template too; otherwise a custom row
            // template would make the arrow decorative and hide the submenu.
            var popup = new FrameworkElementFactory(typeof(Popup));
            popup.Name = "SubmenuPopup";
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Left);
            popup.SetValue(Popup.HorizontalOffsetProperty, -4d);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
            popup.SetValue(Popup.IsOpenProperty, new TemplateBindingExtension(MenuItem.IsSubmenuOpenProperty));
            var submenuChrome = new FrameworkElementFactory(typeof(Border));
            submenuChrome.Name = "SubmenuChrome";
            submenuChrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            submenuChrome.SetValue(Border.PaddingProperty, new Thickness(4));
            submenuChrome.SetValue(FrameworkElement.MinWidthProperty, 200d);
            submenuChrome.SetValue(Border.BackgroundProperty, UiKit.FrozenBrush(TrayMenuSurface));
            submenuChrome.SetValue(Border.BorderBrushProperty, UiKit.FrozenBrush(TrayMenuBorder));
            submenuChrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            var submenuItems = new FrameworkElementFactory(typeof(ItemsPresenter));
            submenuItems.SetValue(ItemsPresenter.SnapsToDevicePixelsProperty, true);
            submenuChrome.AppendChild(submenuItems);
            popup.AppendChild(submenuChrome);
            root.AppendChild(popup);

            template.VisualTree = root;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, UiKit.FrozenBrush(UiKit.RaisedColor), "MenuItemChrome"));
            template.Triggers.Add(hover);

            var highlighted = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            highlighted.Setters.Add(new Setter(Control.BackgroundProperty, UiKit.FrozenBrush(UiKit.RaisedColor), "MenuItemChrome"));
            template.Triggers.Add(highlighted);

            // MenuItem is not a ButtonBase and has no IsPressedProperty. Use
            // its valid submenu-open state for the pressed/active treatment;
            // ordinary clicks are covered by the highlighted trigger above.
            var active = new Trigger { Property = MenuItem.IsSubmenuOpenProperty, Value = true };
            active.Setters.Add(new Setter(Control.BackgroundProperty, UiKit.FrozenBrush(UiKit.RaisedColor), "MenuItemChrome"));
            template.Triggers.Add(active);

            var checkable = new Trigger { Property = MenuItem.IsCheckableProperty, Value = true };
            checkable.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "SwitchTrack"));
            template.Triggers.Add(checkable);

            var checkedTrigger = new Trigger { Property = MenuItem.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckMark"));
            checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, UiKit.FrozenBrush(UiKit.AccentColor), "SwitchTrack"));
            checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, UiKit.FrozenBrush(UiKit.AccentColor), "SwitchTrack"));
            checkedTrigger.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right, "SwitchKnob"));
            checkedTrigger.Setters.Add(new Setter(Shape.FillProperty, UiKit.FrozenBrush(ThemeTokens.OnAccent), "SwitchDot"));
            template.Triggers.Add(checkedTrigger);

            var submenu = new Trigger { Property = ItemsControl.HasItemsProperty, Value = true };
            submenu.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "SubmenuArrow"));
            template.Triggers.Add(submenu);

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.42));
            template.Triggers.Add(disabled);
            return template;
        }

        static Style CreateTraySeparatorStyle()
        {
            var style = new Style(typeof(Separator));
            style.Setters.Add(new Setter(Control.BackgroundProperty, UiKit.FrozenBrush(UiKit.RaisedColor)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 1d));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(6, 4, 6, 4)));
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateTraySeparatorTemplate()));
            return style;
        }

        static ControlTemplate CreateTraySeparatorTemplate()
        {
            var template = new ControlTemplate(typeof(Separator));
            var line = new FrameworkElementFactory(typeof(Border));
            line.SetValue(Border.HeightProperty, 1d);
            line.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            line.SetValue(Border.SnapsToDevicePixelsProperty, true);
            template.VisualTree = line;
            return template;
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
