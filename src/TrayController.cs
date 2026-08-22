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

        void AddMenuItem(string header, RoutedEventHandler action)
        {
            var item = new MenuItem { Header = header };
            item.Click += action;
            _menu.Items.Add(item);
        }

        // H.NotifyIcon hosts a WPF ContextMenu in a separate Popup window. The
        // stock MenuItem template brings back the Windows white check gutter and
        // an opaque popup border, even when ContextMenu.Background is dark. Keep
        // the whole tray menu authored here so the popup chrome, check slot,
        // separators and submenu arrows share the OneBox surface tokens.
        static ContextMenu CreateTrayMenu()
        {
            var menu = new ContextMenu
            {
                Background = new SolidColorBrush(Color.FromArgb(244, 43, 41, 56)),
                Foreground = UiKit.FrozenBrush(UiKit.TextPrimary),
                BorderBrush = new SolidColorBrush(Color.FromArgb(220, 80, 75, 120)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4),
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

        static ControlTemplate CreateTrayContextMenuTemplate()
        {
            var template = new ControlTemplate(typeof(ContextMenu));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "MenuChrome";
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            chrome.SetValue(Border.SnapsToDevicePixelsProperty, true);
            chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            var items = new FrameworkElementFactory(typeof(ItemsPresenter));
            items.SetValue(ItemsPresenter.SnapsToDevicePixelsProperty, true);
            chrome.AppendChild(items);
            template.VisualTree = chrome;
            return template;
        }

        static Style CreateTrayMenuItemStyle()
        {
            var style = new Style(typeof(MenuItem));
            style.Setters.Add(new Setter(Control.ForegroundProperty, UiKit.FrozenBrush(UiKit.TextPrimary)));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30d));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateTrayMenuItemTemplate()));
            return style;
        }

        static ControlTemplate CreateTrayMenuItemTemplate()
        {
            var template = new ControlTemplate(typeof(MenuItem));
            var root = new FrameworkElementFactory(typeof(Grid));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "MenuItemChrome";
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

            var row = new FrameworkElementFactory(typeof(DockPanel));
            row.SetValue(DockPanel.LastChildFillProperty, true);

            // Reserve the conventional check column, but keep it transparent.
            // The check itself is a vector Path so no emoji/system glyph leaks
            // into the tray menu.
            var checkSlot = new FrameworkElementFactory(typeof(Border));
            checkSlot.SetValue(FrameworkElement.WidthProperty, 22d);
            checkSlot.SetValue(DockPanel.DockProperty, Dock.Left);
            var check = new FrameworkElementFactory(typeof(Path));
            check.Name = "CheckMark";
            check.SetValue(FrameworkElement.WidthProperty, 13d);
            check.SetValue(FrameworkElement.HeightProperty, 13d);
            check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            check.SetValue(Path.DataProperty, Geometry.Parse("M 1,6 L 5,10 L 12,1"));
            check.SetValue(Path.StrokeProperty, UiKit.FrozenBrush(UiKit.AccentColor));
            check.SetValue(Path.StrokeThicknessProperty, 2.1d);
            check.SetValue(Path.StrokeStartLineCapProperty, PenLineCap.Round);
            check.SetValue(Path.StrokeEndLineCapProperty, PenLineCap.Round);
            check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            checkSlot.AppendChild(check);
            row.AppendChild(checkSlot);

            var arrow = new FrameworkElementFactory(typeof(Path));
            arrow.Name = "SubmenuArrow";
            arrow.SetValue(FrameworkElement.WidthProperty, 10d);
            arrow.SetValue(FrameworkElement.HeightProperty, 12d);
            arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 2, 0));
            arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            arrow.SetValue(DockPanel.DockProperty, Dock.Right);
            arrow.SetValue(Path.DataProperty, Geometry.Parse("M 1,1 L 6,6 L 1,11"));
            arrow.SetValue(Path.StrokeProperty, UiKit.FrozenBrush(UiKit.TextSecondary));
            arrow.SetValue(Path.StrokeThicknessProperty, 1.8d);
            arrow.SetValue(Path.StrokeStartLineCapProperty, PenLineCap.Round);
            arrow.SetValue(Path.StrokeEndLineCapProperty, PenLineCap.Round);
            arrow.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            row.AppendChild(arrow);

            var header = new FrameworkElementFactory(typeof(ContentPresenter));
            header.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            header.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            header.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            header.SetValue(TextElement.FontFamilyProperty, AppResources.AppFont);
            header.SetValue(TextElement.FontSizeProperty, 14d);
            row.AppendChild(header);

            chrome.AppendChild(row);
            root.AppendChild(chrome);

            // MenuItem opens its children in a second Popup. Include that
            // surface in the authored template too; otherwise a custom row
            // template would make the arrow decorative and hide the submenu.
            var popup = new FrameworkElementFactory(typeof(Popup));
            popup.Name = "SubmenuPopup";
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Right);
            popup.SetValue(Popup.HorizontalOffsetProperty, -4d);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
            popup.SetValue(Popup.IsOpenProperty, new TemplateBindingExtension(MenuItem.IsSubmenuOpenProperty));
            var submenuChrome = new FrameworkElementFactory(typeof(Border));
            submenuChrome.Name = "SubmenuChrome";
            submenuChrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            submenuChrome.SetValue(Border.PaddingProperty, new Thickness(4));
            submenuChrome.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(244, 43, 41, 56)));
            submenuChrome.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(220, 80, 75, 120)));
            submenuChrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            var submenuItems = new FrameworkElementFactory(typeof(ItemsPresenter));
            submenuItems.SetValue(ItemsPresenter.SnapsToDevicePixelsProperty, true);
            submenuChrome.AppendChild(submenuItems);
            popup.AppendChild(submenuChrome);
            root.AppendChild(popup);

            template.VisualTree = root;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, UiKit.FrozenBrush(ThemeTokens.Hover), "MenuItemChrome"));
            hover.Setters.Add(new Setter(Control.ForegroundProperty, UiKit.FrozenBrush(UiKit.TextPrimary)));
            template.Triggers.Add(hover);

            var highlighted = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            highlighted.Setters.Add(new Setter(Control.BackgroundProperty, UiKit.FrozenBrush(ThemeTokens.Hover), "MenuItemChrome"));
            template.Triggers.Add(highlighted);

            // MenuItem is not a ButtonBase and has no IsPressedProperty. Use
            // its valid submenu-open state for the pressed/active treatment;
            // ordinary clicks are covered by the highlighted trigger above.
            var active = new Trigger { Property = MenuItem.IsSubmenuOpenProperty, Value = true };
            active.Setters.Add(new Setter(Control.BackgroundProperty, UiKit.FrozenBrush(UiKit.ActiveBg), "MenuItemChrome"));
            template.Triggers.Add(active);

            var checkedTrigger = new Trigger { Property = MenuItem.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckMark"));
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
            style.Setters.Add(new Setter(Control.BackgroundProperty, UiKit.FrozenBrush(UiKit.BorderColor)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 1d));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 5, 4, 5)));
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
