using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Documents;
using System.Windows.Threading;
using System.IO;
using PowerAudioManager.Commands;

namespace PowerAudioManager
{
    // 悬浮窗 UI 构建（方向 A「紫影精修」）：标题栏 + 分组卡片（硬件监控/电源/音频/OneMic/内存/快捷操作/启动栏）。
    public partial class MainWindow : Window
    {
        // Card geometry shared by every section of the floating window.
        const double CardRadius = 10;
        const double CardGap = 8;

        Button _collapseBtn;
        Button _closeBtn;
        TextBlock _oneMicStatusLabel;
        TextBlock _memPercentLabel;
        ColumnDefinition _memUsedColumn, _memCacheColumn, _memFreeColumn;

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
                CornerRadius = new CornerRadius(12),
                Background = UiKit.FrozenBrush(UiKit.BgColor),
                BorderBrush = UiKit.FrozenBrush(UiKit.BorderColor),
                BorderThickness = new Thickness(1),
                // 柔和投影：0 8px 24px / 35%，尺寸由 WindowScaling 按缩放同步。
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24,
                    ShadowDepth = 6,
                    Direction = 270,
                    Opacity = 0.35,
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
                Height = 40,
                LastChildFill = true
            };
            var titleStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            var titleIcon = IconCatalog.CreateElement(IconKey.Brand, 18,
                UiKit.FrozenBrush(UiKit.AccentColor));
            titleIcon.Margin = new Thickness(0, 0, 8, 0);
            var titleLabel = new TextBlock
            {
                Text = "OneBox",
                Foreground = UiKit.FrozenBrush(UiKit.TextPrimary),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleStack.Children.Add(titleIcon);
            titleStack.Children.Add(titleLabel);
            // 折叠态右侧摘要：「CPU 52° GPU 47° 内存 63%」
            _collapsedTempLabel = new TextBlock
            {
                Foreground = UiKit.FrozenBrush(UiKit.TextSecondary),
                FontFamily = AppFont,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 0, 2, 0),
                Visibility = Visibility.Collapsed
            };
            Typography.SetNumeralAlignment(_collapsedTempLabel, FontNumeralAlignment.Tabular);
            var pinBtn = new Button
            {
                Content = UiKit.PinIcon(_lockPosition, UiKit.FrozenBrush(_lockPosition ? UiKit.AccentColor : UiKit.TextSecondary)),
                Width = 32, Height = 32,
                Foreground = UiKit.FrozenBrush(_lockPosition ? UiKit.AccentColor : UiKit.TextSecondary),
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
                Content = IconCatalog.CreateElement(_isExpanded ? IconKey.ChevronUp : IconKey.ChevronDown, 16, UiKit.FrozenBrush(UiKit.TextSecondary)),
                Width = 32, Height = 32,
                Foreground = UiKit.FrozenBrush(UiKit.TextSecondary),
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
            _collapseBtn = collapseBtn;
            var closeBtn = new Button
            {
                Content = IconCatalog.CreateElement(IconKey.Close, 16, UiKit.FrozenBrush(UiKit.TextSecondary)),
                Width = 32, Height = 32,
                Foreground = UiKit.FrozenBrush(UiKit.TextSecondary),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "隐藏窗口"
            };
            AutomationProperties.SetName(closeBtn, "隐藏窗口");
            UiKit.ApplyIconButtonStyle(closeBtn);
            closeBtn.Command = CreateUiCommand(AppCommandId.WindowHide, CommandSource.MainWindow);
            _closeBtn = closeBtn;
            DockPanel.SetDock(closeBtn, Dock.Right);
            DockPanel.SetDock(collapseBtn, Dock.Right);
            DockPanel.SetDock(pinBtn, Dock.Right);
            DockPanel.SetDock(_collapsedTempLabel, Dock.Right);
            titleBar.Children.Add(closeBtn);
            titleBar.Children.Add(collapseBtn);
            titleBar.Children.Add(pinBtn);
            titleBar.Children.Add(_collapsedTempLabel);
            titleBar.Children.Add(titleStack);
            var tipBlock = new TextBlock { FontSize = 12 };
            var tip = new ToolTip { Content = tipBlock };
            ToolTipService.SetInitialShowDelay(titleBar, 200);
            ToolTipService.SetShowDuration(titleBar, 8000);
            titleBar.ToolTip = tip;
            titleBar.ToolTipOpening += (s, ev) => {
                if (_isExpanded) { ev.Handled = true; return; }
                tipBlock.Text = TrayStatusText;
            };
            // 仅位置解锁时可拖动。锁定时位置固定，切换分辨率不移动窗口。
            titleBar.MouseLeftButtonDown += (s, e) => { if (!_lockPosition) try { DragMove(); } catch { } };
            // 用圆角 Border 包裹标题栏，使上方圆角匹配外层卡片的 CornerRadius 12。
            var titleBarBorder = new Border
            {
                CornerRadius = new CornerRadius(12, 12, 0, 0),
                Background = UiKit.FrozenBrush(ThemeTokens.TitleSurface),
                Child = titleBar
            };
            _titleBarBorder = titleBarBorder;
            _root.Children.Add(titleBarBorder);

            if (_isExpanded)
            {
                BuildExpandedContent();
                _root.Children.Add(_contentPanel);
            }
            else
            {
                ApplyTitleBarState(false);
            }
            _mainBorder.Child = _root;
            Content = _mainBorder;
            if (IsLoaded) _volumeInputReady = true;
        }

        // Title bar adapts to the fold state: the collapsed bar shows the
        // summary and only the expand chevron, the expanded one the full set.
        void ApplyTitleBarState(bool expanded)
        {
            if (_titleBarBorder != null)
                _titleBarBorder.CornerRadius = expanded ? new CornerRadius(12, 12, 0, 0) : new CornerRadius(12);
            if (_collapsedTempLabel != null)
                _collapsedTempLabel.Visibility = !expanded && (ModuleVisible("Temp") || ModuleVisible("Mem"))
                    ? Visibility.Visible : Visibility.Collapsed;
            if (_pinBtn != null) _pinBtn.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            if (_closeBtn != null) _closeBtn.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            if (_collapseBtn != null)
            {
                _collapseBtn.Content = IconCatalog.CreateElement(expanded ? IconKey.ChevronUp : IconKey.ChevronDown, 16,
                    UiKit.FrozenBrush(UiKit.TextSecondary));
                _collapseBtn.ToolTip = expanded ? "折叠窗口" : "展开窗口";
                AutomationProperties.SetName(_collapseBtn, expanded ? "折叠窗口" : "展开窗口");
                _collapseBtn.Margin = expanded ? new Thickness(0) : new Thickness(0, 0, 4, 0);
            }
        }

        void BuildExpandedContent()
        {
            CancelPendingVolumeCommand();
            var contentPanel = new StackPanel { Margin = new Thickness(8, 0, 8, 8) };
            _contentPanel = contentPanel;
            _metricSig = null;

            // 硬件监控（展开时显示在电源计划上方）
            if (ModuleVisible("Temp"))
            {
                var metricsBody = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
                _metricRow = metricsBody;
                metricsBody.Visibility = AppPrefs.GetBool("UI.MetricsCollapsed", false) ? Visibility.Collapsed : Visibility.Visible;
                AddBlock(contentPanel, MakeSectionCard(IconKey.Temperature, "硬件监控", metricsBody, "UI.MetricsCollapsed"));
            }

            // 板块可见性（用户可在设置中隐藏）。每个板块是独立卡片，隐藏不会遗留孤立分割线。
            bool showPower = ModuleVisible("Power");
            bool showAudio = ModuleVisible("Audio");
            bool showOneMic = ModuleVisible("OneMic");
            bool showMem   = ModuleVisible("Mem");

            if (showPower)
            {
                _powerSection = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = AppPrefs.GetBool("UI.PowerCollapsed", false) ? Visibility.Collapsed : Visibility.Visible };
                AddBlock(contentPanel, MakeSectionCard(IconKey.Power, "电源计划", _powerSection, "UI.PowerCollapsed"));
            }

            if (showAudio)
            {
                _audioSection = new StackPanel { Margin = new Thickness(0, 6, 0, 0), Visibility = AppPrefs.GetBool("UI.AudioCollapsed", false) ? Visibility.Collapsed : Visibility.Visible };
                _audioDeviceSection = new StackPanel();
                _audioSection.Children.Add(_audioDeviceSection);

                var volRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0), LastChildFill = true };
                _muteBtn = new Button {
                    Content = UiKit.MuteIcon(false, UiKit.FrozenBrush(UiKit.TextSecondary)),
                    Width = 32, Height = 32,
                    Background = Brushes.Transparent,
                    Foreground = UiKit.FrozenBrush(UiKit.TextSecondary),
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
                    Text = ((int)(VolumeControl.GetVolume()*100)).ToString(),
                    Foreground = UiKit.FrozenBrush(UiKit.TextPrimary),
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                    MinWidth = 32,
                    TextAlignment = TextAlignment.Right
                };
                Typography.SetNumeralAlignment(_volLabel, FontNumeralAlignment.Tabular);
                DockPanel.SetDock(_volLabel, Dock.Right);
                volRow.Children.Add(_volLabel);
                // 「磁贴」样式音量条：已调部分铺满深紫，末端紫色亮边。
                _volSlider = UiKit.MakeTileSlider(0, 100, VolumeControl.GetVolume() * 100);
                AutomationProperties.SetName(_volSlider, "音量");
                _volSlider.ValueChanged += (s, e) => {
                    if (_volLabel != null) _volLabel.Text = ((int)_volSlider.Value).ToString();
                    if (!_volSliderUpdating && _volumeInputReady)
                        QueueUserVolumeCommand((float)(_volSlider.Value / 100.0));
                };
                // A slider can receive layout/template value changes while the
                // window is being composed. Those are state synchronization, not
                // user input; only accept notifications after the control is live.
                _volumeInputReady = false;
                _volSlider.Loaded += (s, e) => _volumeInputReady = true;
                volRow.Children.Add(_volSlider);
                _audioSection.Children.Add(volRow);
                AddBlock(contentPanel, MakeSectionCard(IconKey.Audio, "音频输出", _audioSection, "UI.AudioCollapsed"));
            }

            if (showOneMic)
            {
                // OneMic 的标题行和按钮区是 _contentPanel 的直接子元素（测试与刷新逻辑依赖该结构），
                // 用分段圆角裁剪拼成一张卡片。
                _oneMicStatusLabel = new TextBlock
                {
                    FontSize = 11,
                    Foreground = UiKit.FrozenBrush(UiKit.TextSecondary),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                _oneMicSection = new StackPanel
                {
                    Background = UiKit.FrozenBrush(UiKit.CardColor),
                    Visibility = AppPrefs.GetBool("UI.OneMicCollapsed", false) ? Visibility.Collapsed : Visibility.Visible
                };
                var section = _oneMicSection;
                DockPanel micHeader = null;
                micHeader = MakeSectionHeader(IconKey.OneMic, "OneMic", () => _oneMicSection, "UI.OneMicCollapsed",
                    _oneMicStatusLabel, 10, collapsed => UpdateSegmentClip(micHeader, true, collapsed));
                micHeader.Height = 38;
                micHeader.Background = UiKit.FrozenBrush(UiKit.CardColor);
                AttachSegmentClip(micHeader, true, () => section.Visibility != Visibility.Visible);
                AttachSegmentClip(section, false, () => false);
                section.Children.Add(BuildOneMicControls());
                AddBlock(contentPanel, micHeader);
                contentPanel.Children.Add(section);
            }

            if (showMem)
                AddBlock(contentPanel, BuildMemoryCard());

            var quick = BuildQuickActions();
            if (quick != null) AddBlock(contentPanel, quick);

            if (ModuleVisible("Launcher")) LauncherBar.Build(contentPanel, RebuildUI, this);
        }

        static void AddBlock(StackPanel panel, FrameworkElement block)
        {
            if (panel.Children.Count > 0)
            {
                var m = block.Margin;
                block.Margin = new Thickness(m.Left, CardGap, m.Right, m.Bottom);
            }
            panel.Children.Add(block);
        }

        // A collapsible section card: header row + the given body inside one
        // rounded Card surface.  The body's Visibility is the collapse state.
        Border MakeSectionCard(IconKey icon, string title, FrameworkElement body, string prefKey, UIElement trailing = null)
        {
            var stack = new StackPanel();
            stack.Children.Add(MakeSectionHeader(icon, title, () => body, prefKey, trailing, 0, null));
            stack.Children.Add(body);
            return UiKit.Card(stack, new Thickness(10), CardRadius);
        }

        DockPanel MakeSectionHeader(IconKey iconKey, string title, Func<UIElement> sectionGetter, string prefKey,
            UIElement trailing, double sidePadding, Action<bool> onToggled)
        {
            bool initiallyCollapsed = AppPrefs.GetBool(prefKey, false);
            var dock = new DockPanel {
                LastChildFill = true,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                MinHeight = 20
            };
            var sectionIcon = IconCatalog.CreateElement(iconKey, 14, UiKit.FrozenBrush(UiKit.AccentColor));
            sectionIcon.Margin = new Thickness(sidePadding, 0, 6, 0);
            sectionIcon.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(sectionIcon, Dock.Left);
            dock.Children.Add(sectionIcon);

            var arrowHost = new Border { Margin = new Thickness(6, 0, sidePadding, 0), VerticalAlignment = VerticalAlignment.Center };
            arrowHost.Child = IconCatalog.CreateElement(initiallyCollapsed ? IconKey.ChevronRight : IconKey.ChevronDown,
                14, UiKit.FrozenBrush(UiKit.TextDisabled));
            DockPanel.SetDock(arrowHost, Dock.Right);
            dock.Children.Add(arrowHost);
            if (trailing != null)
            {
                DockPanel.SetDock(trailing, Dock.Right);
                dock.Children.Add(trailing);
            }

            var label = new TextBlock {
                FontFamily = AppFont,
                Foreground = UiKit.FrozenBrush(UiKit.TextPrimary),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Text = title
            };
            dock.Children.Add(label);

            dock.MouseLeftButtonUp += (s, e) => {
                var sec = sectionGetter() as FrameworkElement;
                if (sec == null) return;
                bool nowCollapsed = sec.Visibility == Visibility.Visible;
                sec.Visibility = nowCollapsed ? Visibility.Collapsed : Visibility.Visible;
                arrowHost.Child = IconCatalog.CreateElement(nowCollapsed ? IconKey.ChevronRight : IconKey.ChevronDown,
                    14, UiKit.FrozenBrush(UiKit.TextDisabled));
                AppPrefs.SetBool(prefKey, nowCollapsed);
                onToggled?.Invoke(nowCollapsed);
            };
            return dock;
        }

        // Rounded clipping for a card split across two sibling elements
        // (header on top, body below).  Recomputed whenever the size changes.
        static void AttachSegmentClip(FrameworkElement element, bool isTop, Func<bool> roundBothEnds)
        {
            element.SizeChanged += (_, _) => UpdateSegmentClip(element, isTop, roundBothEnds());
        }

        static void UpdateSegmentClip(FrameworkElement element, bool isTop, bool roundBothEnds)
        {
            if (element == null) return;
            double top = isTop || roundBothEnds ? CardRadius : 0;
            double bottom = !isTop || roundBothEnds ? CardRadius : 0;
            element.Clip = RoundedRect(element.ActualWidth, element.ActualHeight, top, bottom);
        }

        static Geometry RoundedRect(double w, double h, double top, double bottom)
        {
            if (w <= 0 || h <= 0) return null;
            top = Math.Min(top, Math.Min(w, h) / 2);
            bottom = Math.Min(bottom, Math.Min(w, h) / 2);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(top, 0), true, true);
                c.LineTo(new Point(w - top, 0), false, false);
                if (top > 0) c.ArcTo(new Point(w, top), new Size(top, top), 0, false, SweepDirection.Clockwise, false, false);
                c.LineTo(new Point(w, h - bottom), false, false);
                if (bottom > 0) c.ArcTo(new Point(w - bottom, h), new Size(bottom, bottom), 0, false, SweepDirection.Clockwise, false, false);
                c.LineTo(new Point(bottom, h), false, false);
                if (bottom > 0) c.ArcTo(new Point(0, h - bottom), new Size(bottom, bottom), 0, false, SweepDirection.Clockwise, false, false);
                c.LineTo(new Point(0, top), false, false);
                if (top > 0) c.ArcTo(new Point(top, 0), new Size(top, top), 0, false, SweepDirection.Clockwise, false, false);
            }
            g.Freeze();
            return g;
        }

        Border BuildMemoryCard()
        {
            var stack = new StackPanel();
            _memPercentLabel = new TextBlock
            {
                FontSize = 12,
                Foreground = UiKit.FrozenBrush(UiKit.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center
            };
            Typography.SetNumeralAlignment(_memPercentLabel, FontNumeralAlignment.Tabular);
            stack.Children.Add(UiKit.SectionHeader(IconKey.MemoryClean, "内存", _memPercentLabel));

            // 6px 堆叠条：已用（强调色）/ 缓存（#5A5680）/ 可用。
            var bar = new Grid { Height = 6, Margin = new Thickness(0, 8, 0, 8) };
            _memUsedColumn = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
            _memCacheColumn = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
            _memFreeColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            bar.ColumnDefinitions.Add(_memUsedColumn);
            bar.ColumnDefinitions.Add(_memCacheColumn);
            bar.ColumnDefinitions.Add(_memFreeColumn);
            var used = new Border { Background = UiKit.FrozenBrush(UiKit.AccentColor), CornerRadius = new CornerRadius(3, 0, 0, 3) };
            var cache = new Border { Background = UiKit.FrozenBrush(Color.FromRgb(0x5A, 0x56, 0x80)) };
            var free = new Border { Background = UiKit.FrozenBrush(ThemeTokens.BorderStrong), CornerRadius = new CornerRadius(0, 3, 3, 0) };
            Grid.SetColumn(used, 0); Grid.SetColumn(cache, 1); Grid.SetColumn(free, 2);
            bar.Children.Add(used); bar.Children.Add(cache); bar.Children.Add(free);
            stack.Children.Add(bar);

            var detail = new DockPanel { LastChildFill = true };
            var memBtn = new Button
            {
                Content = "立即清理",
                Height = 30,
                Padding = new Thickness(12, 0, 12, 0),
                ToolTip = "执行内存清理"
            };
            UiKit.ApplyButtonVariant(memBtn, ButtonVariant.Primary);
            AutomationProperties.SetName(memBtn, "执行内存清理");
            memBtn.Command = CreateUiCommand(AppCommandId.MemoryClean, CommandSource.MainWindow,
                () => new MemoryCleanPayload(MemoryCleaner.GetSavedFlags()));
            DockPanel.SetDock(memBtn, Dock.Right);
            detail.Children.Add(memBtn);
            _memStatusLabel = new TextBlock {
                Foreground = UiKit.FrozenBrush(UiKit.TextSecondary),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Typography.SetNumeralAlignment(_memStatusLabel, FontNumeralAlignment.Tabular);
            detail.Children.Add(_memStatusLabel);
            stack.Children.Add(detail);
            return UiKit.Card(stack, new Thickness(10), CardRadius);
        }

        // 快捷操作：翻译 / 趋势 / 剪贴板 / 截图，列数随可见项自适应。
        FrameworkElement BuildQuickActions()
        {
            var tiles = new List<Button>();
            if (ModuleVisible("Translate"))
                tiles.Add(QuickTile(IconKey.Translate, "翻译", "文本翻译 · 全局快捷键 Ctrl+Shift+T 自动翻译剪贴板",
                    CreateUiCommand(AppCommandId.TranslateText, CommandSource.MainWindow, () => new TextTranslatePayload(null))));
            if (ModuleVisible("Temp") && AppPrefs.GetBool("Perf.ShowChart", true))
                tiles.Add(QuickTile(IconKey.Performance, "趋势", "查看温度/风扇历史趋势",
                    CreateUiCommand(AppCommandId.MonitorChartOpen, CommandSource.MainWindow)));
            if (ModuleVisible("Clipboard"))
                tiles.Add(QuickTile(IconKey.Clipboard, "剪贴板", "查看最近复制的内容",
                    CreateUiCommand(AppCommandId.ClipboardOpen, CommandSource.MainWindow, () => new ClipboardOpenPayload())));
            if (ModuleVisible("Gallery"))
                tiles.Add(QuickTile(IconKey.Gallery, "截图", "打开截图保存位置",
                    CreateUiCommand(AppCommandId.ScreenshotOpenGallery, CommandSource.MainWindow)));
            if (tiles.Count == 0) return null;
            var grid = new UniformGrid { Rows = 1, Columns = tiles.Count, Margin = new Thickness(-3, 0, -3, 0) };
            foreach (var tile in tiles) grid.Children.Add(tile);
            return grid;
        }

        Button QuickTile(IconKey icon, string label, string tooltip, ICommand command)
        {
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var glyph = IconCatalog.CreateElement(icon, 18, UiKit.FrozenBrush(UiKit.TextSecondary));
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(glyph);
            content.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = AppFont,
                FontSize = 11,
                Foreground = UiKit.FrozenBrush(UiKit.TextSecondary),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            });
            var btn = new Button
            {
                Content = content,
                Height = 56,
                Margin = new Thickness(3, 0, 3, 0),
                Padding = new Thickness(0),
                ToolTip = tooltip,
                Command = command
            };
            UiKit.ApplyFlatStyle(btn);
            btn.Background = UiKit.FrozenBrush(UiKit.CardColor);
            btn.BorderBrush = Brushes.Transparent;
            btn.HorizontalContentAlignment = HorizontalAlignment.Center;
            AutomationProperties.SetName(btn, label);
            return btn;
        }

        Grid BuildOneMicControls()
        {
            var controls = new Grid { Margin = new Thickness(8, 2, 8, 8) };
            for (int i = 0; i < 4; i++) controls.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 2; i++) controls.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            var studio = AudioStudio.StudioController.Instance;
            Button IconButton(IconKey key, string tooltip)
            {
                var button = new Button { Width = 56, Height = 44,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = UiKit.FrozenBrush(UiKit.TextSecondary), Background = UiKit.FrozenBrush(UiKit.RaisedColor),
                    BorderBrush = Brushes.Transparent, Cursor = Cursors.Hand };
                UiKit.ApplyIconButtonStyle(button);
                button.Background = UiKit.FrozenBrush(UiKit.RaisedColor);
                ToolTipService.SetInitialShowDelay(button, 250);
                ToolTipService.SetShowDuration(button, 8000);
                return IconCatalog.ConfigureIconButton(button, key, tooltip, 18);
            }
            var openMic = IconButton(IconKey.OneMic, "打开 OneMic");
            var share = IconButton(IconKey.ShareStart, "开始共享");
            var denoise = IconButton(IconKey.Noise, "切换降噪");
            var eq = IconButton(IconKey.Equalizer, "切换均衡器");
            var bgm = IconButton(IconKey.Music, "切换 BGM");
            var explode = IconButton(IconKey.Explode, "一键炸麦");
            var monitor = IconButton(IconKey.Monitor, "切换监听");
            var settings = IconButton(IconKey.Settings, "OneMic 设置");
            var displayedStates = new Dictionary<Button, (IconKey Icon, bool Active, string Tooltip)>();
            var toggleButtons = new[] { share, denoise, eq, bgm, explode, monitor };
            void SetState(Button button, IconKey icon, bool active, string tooltip)
            {
                bool known = displayedStates.TryGetValue(button, out var previous);
                if (known && previous.Icon == icon && previous.Active == active && previous.Tooltip == tooltip) return;
                if (!known || previous.Active != active)
                {
                    Color fg = ReferenceEquals(button, explode) ? UiKit.DangerColor
                        : active ? UiKit.TextPrimary : UiKit.TextSecondary;
                    var foreground = UiKit.FrozenBrush(fg);
                    button.Foreground = foreground;
                    button.Background = UiKit.FrozenBrush(active ? UiKit.ActiveBg : UiKit.RaisedColor);
                    button.BorderBrush = active ? UiKit.FrozenBrush(UiKit.AccentColor) : Brushes.Transparent;
                    if (button.Content is System.Windows.Shapes.Path path) path.Stroke = foreground;
                }
                // Only the share action swaps its glyph. Replacing every icon on each
                // Changed event invalidated layout and interrupted hover transitions.
                if (!known || previous.Icon != icon)
                    button.Content = IconCatalog.CreateElement(icon, 18, button.Foreground);
                if (!known || previous.Tooltip != tooltip)
                {
                    button.ToolTip = tooltip;
                    AutomationProperties.SetName(button, tooltip);
                }
                displayedStates[button] = (icon, active, tooltip);
            }
            void RefreshButtons()
            {
                SetState(share, studio.Wanted ? IconKey.ShareStop : IconKey.ShareStart, studio.Wanted,
                    studio.Wanted ? "停止共享" : "开始共享");
                SetState(denoise, IconKey.Noise, studio.Settings.Denoise, studio.Settings.Denoise ? "关闭降噪" : "开启降噪");
                SetState(eq, IconKey.Equalizer, studio.Settings.Eq, studio.Settings.Eq ? "关闭均衡器" : "开启均衡器");
                SetState(bgm, IconKey.Music, studio.Settings.Music, studio.Settings.Music ? "关闭 BGM" : "开启 BGM");
                SetState(explode, IconKey.Explode, studio.Settings.Explode, studio.Settings.Explode ? "关闭炸麦音效" : "炸麦音效（慎用）");
                SetState(monitor, IconKey.Monitor, studio.Settings.Monitor, studio.Settings.Monitor ? "关闭监听" : "开启监听");
                bool enabled = !studio.Busy;
                foreach (var button in toggleButtons)
                    if (button.IsEnabled != enabled) button.IsEnabled = enabled;
                if (_oneMicStatusLabel != null)
                {
                    string model = studio.Settings.Model;
                    string noise = studio.Settings.Denoise && !string.IsNullOrEmpty(model) ? "降噪 " + model : "降噪关";
                    string text = noise + " · " + (studio.Wanted ? "共享中" : "未共享");
                    if (_oneMicStatusLabel.Text != text) _oneMicStatusLabel.Text = text;
                }
            }
            SetState(openMic, IconKey.OneMic, false, "打开 OneMic");
            SetState(settings, IconKey.Settings, false, "OneMic 设置");
            openMic.Command = CreateUiCommand(AppCommandId.StudioOpen, CommandSource.MainWindow);
            share.Click += async (_, _) => { studio.Initialize(); if (studio.Wanted) await studio.StopAsync(); else await studio.StartAsync(); RefreshButtons(); };
            denoise.Command = CreateUiCommand(AppCommandId.StudioDenoise, CommandSource.MainWindow);
            eq.Command = CreateUiCommand(AppCommandId.StudioEq, CommandSource.MainWindow);
            bgm.Command = CreateUiCommand(AppCommandId.StudioMusic, CommandSource.MainWindow);
            explode.Command = CreateUiCommand(AppCommandId.StudioExplode, CommandSource.MainWindow);
            monitor.Command = CreateUiCommand(AppCommandId.StudioMonitor, CommandSource.MainWindow);
            settings.Command = CreateUiCommand(AppCommandId.SettingsOpen, CommandSource.MainWindow,
                () => new SettingsOpenPayload(SettingsDialog.OneMicTabIndex));
            bool listening = false;
            int refreshQueued = 0;
            void StudioChanged()
            {
                if (!_isExpanded) return;
                if (System.Threading.Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    System.Threading.Interlocked.Exchange(ref refreshQueued, 0);
                    if (listening && _isExpanded && IsVisible) RefreshButtons();
                }), DispatcherPriority.Render);
            }
            controls.Loaded += (_, _) => { if (!listening) { studio.Changed += StudioChanged; listening = true; } RefreshButtons(); };
            controls.Unloaded += (_, _) => { if (listening) { studio.Changed -= StudioChanged; listening = false; } };
            controls.IsVisibleChanged += (_, _) => { if (controls.IsVisible) RefreshButtons(); };
            void Place(Button button, int row, int column)
            {
                Grid.SetRow(button, row);
                Grid.SetColumn(button, column);
                controls.Children.Add(button);
            }
            Place(openMic, 0, 0); Place(share, 0, 1); Place(bgm, 0, 2); Place(monitor, 0, 3);
            Place(denoise, 1, 0); Place(eq, 1, 1); Place(explode, 1, 2); Place(settings, 1, 3);
            RefreshButtons();
            return controls;
        }

        void StyleButton(Button btn, bool isActive) { StyleButton(btn, isActive, false); }

        void StyleButton(Button btn, bool isActive, bool primary)
        {
            if (primary && !isActive)
            {
                UiKit.ApplyButtonVariant(btn, ButtonVariant.Primary);
                return;
            }
            UiKit.ApplyFlatStyle(btn);
            btn.BorderBrush = Brushes.Transparent;
            if (isActive)
            {
                btn.Background = UiKit.FrozenBrush(UiKit.RaisedColor);
                btn.Foreground = UiKit.FrozenBrush(UiKit.TextPrimary);
                btn.FontWeight = FontWeights.SemiBold;
                return;
            }
            btn.Background = Brushes.Transparent;
            btn.Foreground = UiKit.FrozenBrush(UiKit.TextSecondary);
            btn.FontWeight = FontWeights.Normal;
        }
    }
}
