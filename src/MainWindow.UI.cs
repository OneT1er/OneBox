using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Documents;
using System.Windows.Threading;
using System.IO;
using PowerAudioManager.Commands;

namespace PowerAudioManager
{
    // 悬浮窗 UI 构建：主卡片、标题栏、各板块（温度/电源/音频/内存/翻译/启动栏/剪贴板/图库）。
    public partial class MainWindow : Window
    {
        void BuildUI()
        {
            // RebuildUI can replace the slider while its trailing throttle
            // tick is pending. Drop that old user value before composing the
            // new controls so it cannot target the new endpoint/UI state.
            CancelPendingVolumeCommand();
            if (!_volumeLifecycleHooked)
            {
                Closed += (s, e) => CancelPendingVolumeCommand();
                _volumeLifecycleHooked = true;
            }
            // admin 运行时 UIPI 默认阻止普通进程（资源管理器/浏览器）拖放进来，放宽消息过滤
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
                ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES, MSGFLT_ALLOW, IntPtr.Zero);
                ChangeWindowMessageFilterEx(hwnd, WM_COPYGLOBALDATA, MSGFLT_ALLOW, IntPtr.Zero);
            }
            catch { }

            _mainBorder = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(UiKit.BgColor),
                BorderBrush = new SolidColorBrush(UiKit.BorderColor),                BorderThickness = new Thickness(1),
                // Material 层级阴影 (dp2)：宽柔低透明度投影，悬浮卡片无硬边。
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 36,
                    ShadowDepth = 2,
                    Opacity = 0.32,
                    Color = Colors.Black
                }
            };
            // _mainBorder 挂拖放：折叠态拖入自动展开 + 非按钮区也能 Drop（加到快捷启动）。按钮 AllowDrop 优先（子元素 hit-test）
            _mainBorder.AllowDrop = true;
            _mainBorder.DragEnter += (s, e) => OnWindowDragEnter(e);
            _mainBorder.DragOver += (s, e) => { e.Effects = LauncherBar.HasDropData(e.Data) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            _mainBorder.Drop += (s, e) =>
            {
                var dropped = LauncherBar.ExtractDroppedItems(e.Data);
                if (dropped.Count > 0) _ = ExecuteCommandAsync(AppCommandId.LauncherAdd,
                    CommandSource.MainWindow, new LauncherAddPayload(dropped));
                e.Handled = true;
            };
            _root = new StackPanel();
            var titleBar = new DockPanel
            {
                Background = Brushes.Transparent, // 背景在下方圆角 Border 上
                Height = 36,
                LastChildFill = true
            };
            var titleStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            var titleIcon = IconCatalog.CreateElement(IconKey.Brand, 18,
                UiKit.FrozenBrush(UiKit.AccentColor));
            titleIcon.Margin = new Thickness(0, 0, 6, 0);
            var titleLabel = new TextBlock
            {
                Text = "OneBox",
                                Foreground = new SolidColorBrush(UiKit.TextPrimary),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleStack.Children.Add(titleIcon);
            titleStack.Children.Add(titleLabel);
            _collapsedTempLabel = new TextBlock
            {
                Foreground = new SolidColorBrush(UiKit.TextSecondary),
                FontFamily = AppFont,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                Visibility = Visibility.Collapsed
            };
            titleStack.Children.Add(_collapsedTempLabel);
            var pinBtn = new Button
            {
                Content = UiKit.PinIcon(_lockPosition, UiKit.FrozenBrush(_lockPosition ? UiKit.AccentColor : UiKit.TextSecondary)),
                Width = 28, Height = 28,
                Foreground = new SolidColorBrush(_lockPosition ? UiKit.AccentColor : UiKit.TextSecondary),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
             VerticalAlignment = VerticalAlignment.Center,
             ToolTip = "切换锁定窗口位置"
             };
            AutomationProperties.SetName(pinBtn, _lockPosition ? "解除锁定窗口位置" : "锁定窗口位置");
            UiKit.ApplyIconButtonStyle(pinBtn);
            pinBtn.Click += (s, e) =>
            {
                _ = ExecuteCommandAsync(AppCommandId.RuntimeApplyGeneral, CommandSource.MainWindow,
                    new GeneralRuntimePayload(_topmost, !_lockPosition, false));
            };
            _pinBtn = pinBtn;
            var collapseBtn = new Button
            {
                Content = IconCatalog.CreateElement(IconKey.ChevronUp, 16, UiKit.FrozenBrush(UiKit.TextSecondary)),
                Width = 28, Height = 28,
                Foreground = new SolidColorBrush(UiKit.TextSecondary),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "折叠窗口"
            };
            AutomationProperties.SetName(collapseBtn, "折叠窗口");
            UiKit.ApplyIconButtonStyle(collapseBtn);
            collapseBtn.Command = CreateUiCommand(AppCommandId.WindowSetCollapsed, CommandSource.MainWindow,
                () => new WindowCollapsedPayload(_isExpanded));
            var closeBtn = new Button
            {
                Content = IconCatalog.CreateElement(IconKey.Close, 16, UiKit.FrozenBrush(UiKit.TextSecondary)),
                Width = 28, Height = 28,
                Foreground = new SolidColorBrush(UiKit.TextSecondary),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "隐藏窗口"
            };
            AutomationProperties.SetName(closeBtn, "隐藏窗口");
            UiKit.ApplyIconButtonStyle(closeBtn);
            closeBtn.Command = CreateUiCommand(AppCommandId.WindowHide, CommandSource.MainWindow);
            DockPanel.SetDock(closeBtn, Dock.Right);
            DockPanel.SetDock(collapseBtn, Dock.Right);
            DockPanel.SetDock(pinBtn, Dock.Right);
            titleBar.Children.Add(closeBtn);
            titleBar.Children.Add(collapseBtn);
            titleBar.Children.Add(pinBtn);
            titleBar.Children.Add(titleStack);
            var tipBlock = new TextBlock { FontSize = 12 };
            var tip = new ToolTip { Content = tipBlock };
            ToolTipService.SetInitialShowDelay(titleBar, 200);
            ToolTipService.SetShowDuration(titleBar, 8000);
            titleBar.ToolTip = tip;
            titleBar.ToolTipOpening += (s, ev) => {
                if (_isExpanded) { ev.Handled = true; return; }
                string plan = "(无)", dev = "(无)";
                try { if (_powerPlans != null) { var p = _powerPlans.Find(x => x.IsActive || x.Guid == _currentPlanId); if (p != null) plan = p.Name; } } catch { }
                try { if (_audioDevices != null) { var d = _audioDevices.Find(x => x.IsDefault); if (d != null) dev = d.Name; } } catch { }
                string mem = ""; try { var ms = MemoryCleaner.GetStatus(); if (ms != null) mem = string.Format(System.Environment.NewLine + "内存: {0:0.0}/{1:0.0} GB ({2}%) · 已缓存 {3:0.0}GB", (ms.TotalBytes - ms.AvailableBytes) / 1073741824.0, ms.TotalBytes / 1073741824.0, ms.MemoryLoadPercent, ms.CachedBytes / 1073741824.0); } catch { }
                tipBlock.Text = "电源计划: " + plan + System.Environment.NewLine + "音频输出: " + dev + mem;
            };
            // 仅位置解锁时可拖动。锁定时位置固定，切换分辨率不移动窗口。
            titleBar.MouseLeftButtonDown += (s, e) => { if (!_lockPosition) try { DragMove(); } catch { } };
            // 用圆角 Border 包裹标题栏，使上方圆角匹配外层卡片的 CornerRadius 10。
            // 之前标题栏的纯色背景方角超出卡片 r=10 弧边形成"尖尖"突出，现已修复。
            var titleBarBorder = new Border
            {
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(34, 32, 50)),
                Child = titleBar
            };
            _titleBarBorder = titleBarBorder;
            _root.Children.Add(titleBarBorder);

            var contentPanel = new StackPanel { Margin = new Thickness(14, 10, 14, 14) };
            _contentPanel = contentPanel;

            // 温度行（展开时显示在电源计划上方）
            bool showTemp = ModuleVisible("Temp");
            if (showTemp)
            {
                _metricRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
                // 重置数值块缓存：RebuildUI 重建了新面板，旧缓存指向已脱离视觉树的 TextBlock，
                // 不重置的话 UpdateTempUI 会误判 sameSet 命中、去更新旧块，导致新面板留空（温度行消失）。
                _metricValBlocks = null;
                _metricKeys = null;
                contentPanel.Children.Add(_metricRow);
                contentPanel.Children.Add(UiKit.MakeDivider());

                // 性能趋势入口（按钮样式同剪贴板历史，点击打开大图）
                if (AppPrefs.GetBool("Perf.ShowChart", true))
                {
                    var cContent = IconLabel(IconKey.Performance, "性能趋势");
                    var cBtn = new Button {
                        Content = cContent,
                        Padding = new Thickness(10, 6, 10, 6),
                        Cursor = Cursors.Hand,
                        Margin = new Thickness(0, 6, 0, 0),
                        ToolTip = "查看温度/风扇历史趋势"
                    };
                    StyleButton(cBtn, false);
                    cBtn.Command = CreateUiCommand(AppCommandId.MonitorChartOpen, CommandSource.MainWindow);
                    contentPanel.Children.Add(cBtn);
                    contentPanel.Children.Add(UiKit.MakeDivider());
                }
            }

            // 板块可见性（用户可在设置中隐藏）。每个板块自带头部分割线（第一个除外），隐藏不会遗留孤立分割线。
            bool showPower = ModuleVisible("Power");
            bool showAudio = ModuleVisible("Audio");
            bool showMem   = ModuleVisible("Mem");
            bool showTr    = ModuleVisible("Translate");

            if (showPower)
            {
            var powerHeader = MakeCollapsibleHeader("电源计划", IconKey.Power, () => _powerSection, AppPrefs.GetBool("UI.PowerCollapsed", false));
            contentPanel.Children.Add(powerHeader);
            _powerSection = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            contentPanel.Children.Add(_powerSection);
            }

            if (showAudio)
            {
            if (contentPanel.Children.Count > 0) contentPanel.Children.Add(UiKit.MakeDivider());

            var audioHeader = MakeCollapsibleHeader("音频输出", IconKey.Audio, () => _audioSection, AppPrefs.GetBool("UI.AudioCollapsed", false));
            contentPanel.Children.Add(audioHeader);
            _audioSection = new StackPanel();
            contentPanel.Children.Add(_audioSection);

            var volRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
            _muteBtn = new Button {
                Content = UiKit.MuteIcon(false, UiKit.FrozenBrush(UiKit.TextSecondary)),
                Width = 28, Height = 28,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(UiKit.TextSecondary),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "切换静音"
            };
            AutomationProperties.SetName(_muteBtn, "切换静音");
            UiKit.ApplyIconButtonStyle(_muteBtn);
            _muteBtn.Command = CreateUiCommand(AppCommandId.AudioSetMute, CommandSource.MainWindow,
                () => new AudioMutePayload(!VolumeControl.GetMute()));
            DockPanel.SetDock(_muteBtn, Dock.Left);
            volRow.Children.Add(_muteBtn);
            _volLabel = new TextBlock {
                Text = ((int)(VolumeControl.GetVolume()*100)).ToString() + "%",
                Foreground = new SolidColorBrush(UiKit.TextSecondary),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 32,
                TextAlignment = TextAlignment.Right
            };
            DockPanel.SetDock(_volLabel, Dock.Right);
            volRow.Children.Add(_volLabel);
            _volSlider = new Slider {
                Minimum = 0, Maximum = 100,
                Value = VolumeControl.GetVolume() * 100,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };
            _volSlider.Foreground = new SolidColorBrush(UiKit.AccentColor);
            _volSlider.ValueChanged += (s, e) => {
                if (_volLabel != null) _volLabel.Text = ((int)_volSlider.Value).ToString() + "%";
                if (!_volSliderUpdating && _volumeInputReady)
                    QueueUserVolumeCommand((float)(_volSlider.Value / 100.0));
            };
            // A slider can receive layout/template value changes while the
            // window is being composed. Those are state synchronization, not
            // user input; only accept notifications after the control is live.
            _volumeInputReady = false;
            _volSlider.Loaded += (s, e) => _volumeInputReady = true;
            volRow.Children.Add(_volSlider);
            contentPanel.Children.Add(volRow);
            }

            if (showMem)
            {
            if (contentPanel.Children.Count > 0) contentPanel.Children.Add(UiKit.MakeDivider());
            var memHeader = IconLabel(IconKey.MemoryClean, "内存清理", 12, UiKit.AccentColor);
            memHeader.Margin = new Thickness(0, 0, 0, 6);
            contentPanel.Children.Add(memHeader);
            _memStatusLabel = new TextBlock {
                Foreground = new SolidColorBrush(UiKit.TextSecondary),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 6)
            };
            contentPanel.Children.Add(_memStatusLabel);
            var memBtn = new Button {
                Content = "执行内存清理",
                Padding = new Thickness(10, 6, 10, 6),
                FontSize = 12,
                Cursor = Cursors.Hand
            };
            StyleButton(memBtn, false, true);
            memBtn.Command = CreateUiCommand(AppCommandId.MemoryClean, CommandSource.MainWindow,
                () => new MemoryCleanPayload(MemoryCleaner.GetSavedFlags()));
            contentPanel.Children.Add(memBtn);
            }

            if (showTr)
            {
            if (contentPanel.Children.Count > 0) contentPanel.Children.Add(UiKit.MakeDivider());
            var trContent = IconLabel(IconKey.Translate, "文本翻译");
            var trBtn = new Button {
                Content = trContent,
                Padding = new Thickness(10, 6, 10, 6),
                Cursor = Cursors.Hand,
                ToolTip = "全局快捷键：Ctrl+Shift+T 自动翻译剪贴板"
            };
            StyleButton(trBtn, false, true);
            trBtn.Command = CreateUiCommand(AppCommandId.TranslateText, CommandSource.MainWindow,
                () => new TextTranslatePayload(null));
            contentPanel.Children.Add(trBtn);
            }

            if (ModuleVisible("Launcher")) LauncherBar.Build(contentPanel, RebuildUI, this);

            if (ModuleVisible("Clipboard")) BuildClipboardButton(contentPanel);

            if (ModuleVisible("Gallery")) BuildGalleryButton(contentPanel);

            _root.Children.Add(contentPanel);

            _mainBorder.Child = _root;
            Content = _mainBorder;
            // RebuildUI replaces Content while the window is already loaded;
            // in that case the new slider is immediately eligible for user
            // input even if WPF does not replay Loaded for every child.
            if (IsLoaded) _volumeInputReady = true;
        }

        void BuildClipboardButton(StackPanel contentPanel)
        {
            var cbContent = IconLabel(IconKey.Clipboard, "剪贴板历史");
            var cbBtn = new Button {
                Content = cbContent,
                Padding = new Thickness(10, 6, 10, 6),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 6, 0, 0),
                ToolTip = "查看最近复制的内容"
            };
            StyleButton(cbBtn, false);
            cbBtn.Command = CreateUiCommand(AppCommandId.ClipboardOpen, CommandSource.MainWindow,
                () => new ClipboardOpenPayload());
            contentPanel.Children.Add(cbBtn);
        }

        void BuildGalleryButton(StackPanel contentPanel)
        {
            var gContent = IconLabel(IconKey.Gallery, "截图文件夹");
            var gBtn = new Button {
                Content = gContent,
                Padding = new Thickness(10, 6, 10, 6),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 6, 0, 0),
                ToolTip = "打开截图保存位置"
            };
            StyleButton(gBtn, false);
            gBtn.Command = CreateUiCommand(AppCommandId.ScreenshotOpenGallery, CommandSource.MainWindow);
            contentPanel.Children.Add(gBtn);
        }

        StackPanel IconLabel(IconKey key, string text, double fontSize = 12, Color? textColor = null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(IconCatalog.CreateElement(key, 16, UiKit.FrozenBrush(textColor ?? UiKit.TextSecondary)));
            row.Children.Add(new TextBlock { Text = text, FontFamily = AppFont, FontSize = fontSize,
                Foreground = new SolidColorBrush(textColor ?? UiKit.TextSecondary), Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center });
            return row;
        }

        FrameworkElement MakeCollapsibleHeader(string title, IconKey iconKey, Func<UIElement> sectionGetter, bool initiallyCollapsed)
        {
            var dock = new DockPanel {
                Margin = new Thickness(0, 0, 0, 6),
                LastChildFill = true,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent
            };
            var arrow = IconCatalog.CreateElement(initiallyCollapsed ? IconKey.ChevronRight : IconKey.ChevronDown,
                14, UiKit.FrozenBrush(UiKit.AccentColor));
            arrow.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(arrow, Dock.Left);
            dock.Children.Add(arrow);

            var label = new TextBlock {
                FontFamily = AppFont,
                Foreground = new SolidColorBrush(UiKit.AccentColor),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.Text = title;
            var sectionIcon = IconCatalog.CreateElement(iconKey, 16, UiKit.FrozenBrush(UiKit.AccentColor));
            sectionIcon.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(sectionIcon, Dock.Left);
            dock.Children.Add(sectionIcon);
            dock.Children.Add(label);

            // 异步应用折叠状态，等区块元素创建后再设置可见性。
            Dispatcher.BeginInvoke(new Action(() => {
                var sec = sectionGetter() as FrameworkElement;
                if (sec == null) return;
                sec.Visibility = initiallyCollapsed ? Visibility.Collapsed : Visibility.Visible;
            }), System.Windows.Threading.DispatcherPriority.Loaded);

            string prefKey = title.Contains("电源") ? "UI.PowerCollapsed" : "UI.AudioCollapsed";
            dock.MouseLeftButtonUp += (s, e) => {
                var sec = sectionGetter() as FrameworkElement;
                if (sec == null) return;
                bool nowCollapsed = sec.Visibility == Visibility.Visible;
                sec.Visibility = nowCollapsed ? Visibility.Collapsed : Visibility.Visible;
                var newArrow = IconCatalog.CreateElement(nowCollapsed ? IconKey.ChevronRight : IconKey.ChevronDown,
                    14, UiKit.FrozenBrush(UiKit.AccentColor));
                newArrow.Margin = new Thickness(0, 0, 6, 0);
                dock.Children.RemoveAt(0);
                dock.Children.Insert(0, newArrow);
                AppPrefs.SetBool(prefKey, nowCollapsed);
            };
            return dock;
        }

        void StyleButton(Button btn, bool isActive) { StyleButton(btn, isActive, false); }

        void StyleButton(Button btn, bool isActive, bool primary)
        {
            UiKit.ApplyFlatStyle(btn);
            if (isActive)
            {
                btn.Background = new SolidColorBrush(UiKit.ActiveBg);
                btn.Foreground = Brushes.White;
                btn.FontWeight = FontWeights.SemiBold;
                return;
            }
            if (primary)
            {
                btn.Background = new SolidColorBrush(UiKit.AccentColor);
                btn.Foreground = Brushes.White;
                btn.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                btn.Background = new SolidColorBrush(UiKit.CardColor);
                btn.Foreground = new SolidColorBrush(UiKit.TextSecondary);
                btn.FontWeight = FontWeights.Normal;
            }
        }
    }
}


