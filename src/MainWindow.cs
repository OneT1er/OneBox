using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Documents;
using System.Windows.Threading;
using System.IO;
using Microsoft.Win32;

namespace PowerAudioManager
{
    public class MainWindow : Window
    {
        internal List<PowerPlanInfo> _powerPlans;
        internal List<AudioDeviceInfo> _audioDevices;
        internal string _currentPlanId;
        private string _currentDeviceId;
        private DispatcherTimer _refreshTimer;
        private DispatcherTimer _screenPoll;
        private DispatcherTimer _autoCleanTimer;
        private DateTime _lastCleanTime = DateTime.MinValue;
        private WindowScaling _scaling;
        private StackPanel _root;
        private StackPanel _powerSection;
        private StackPanel _audioSection;
        private bool _isExpanded = true;
        internal bool _topmost = false;
        internal Button _pinBtn;
        internal bool _lockPosition;
        internal TrayController _tray;
        private AudioDevices.DeviceWatcher _deviceWatcher;
        private Slider _volSlider;
        private Button _muteBtn;
        private bool _volSliderUpdating;
        private TextBlock _volLabel;
        private TextBlock _memStatusLabel;
        private StackPanel _contentPanel;
        private Border _titleBarBorder;
        private Border _mainBorder;

        // Shared palette — internal so the extracted helper classes (LauncherBar,
        // TrayController, etc.) can reuse the exact same colours.
        // Organised as Material-style elevation tiers: the deeper the tier, the
        // lighter the surface, so layered cards read as stacked depth.
        internal static readonly Color AccentColor = Color.FromRgb(142, 140, 216);   // 紫影 #8E8CD8
        internal static readonly Color AccentHover = Color.FromRgb(126, 122, 210);   // 强调色悬停(略深)
        internal static readonly Color BgColor = Color.FromRgb(28, 26, 40);          // 深底，与紫影协调
        internal static readonly Color CardColor = Color.FromRgb(42, 39, 60);        // 卡片 (elevation 1)
        internal static readonly Color HoverColor = Color.FromRgb(58, 54, 84);       // 悬停 (elevation 2)
        internal static readonly Color TextPrimary = Colors.White;
        internal static readonly Color TextSecondary = Color.FromRgb(190, 188, 220); // 次要文字
        internal static readonly Color ActiveBg = Color.FromRgb(110, 105, 200);      // 激活态（紫影偏深）
        internal static readonly Color BorderColor = Color.FromRgb(80, 75, 120);     // 边框
        // Material-style tonal surfaces for the filled/outline button variants.
        internal static readonly Color AccentRipple = Color.FromRgb(80, 76, 150);    // 强调按钮按下/悬停叠色

        // Fonts and bundled images live in AppResources now.
        static System.Windows.Media.FontFamily AppFont { get { return AppResources.AppFont; } }
        static System.Windows.Media.FontFamily EmojiFont { get { return AppResources.EmojiFont; } }
        static System.Windows.Media.Imaging.BitmapImage LoadAppImage(string fileName) { return AppResources.LoadAppImage(fileName); }

        public MainWindow()
        {
            _topmost = AppPrefs.GetBool("Topmost", false);
            _lockPosition = AppPrefs.GetBool("LockPosition", false);
            Title = "OneBox";
            FontFamily = AppFont;
            Width = 280;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = _topmost;
            var screen = SystemParameters.WorkArea;
            double sl, st;
            if (AppPrefs.GetDouble("Left", out sl) && AppPrefs.GetDouble("Top", out st))
            {
                // If the saved position lands outside the current work area (e.g. user dragged
                // the window on a 4K monitor and is now booting on 1080p), snap back to the
                // bottom-right corner of the new work area so the window is actually visible.
                double estW = Width;       // SizeToContent.Height -> Width is fixed at 280
                double estH = 200;         // upper bound estimate before layout runs
                bool offscreen =
                    sl + estW <= screen.Left + 8 || sl >= screen.Right - 8 ||
                    st + 36   <= screen.Top  + 8 || st >= screen.Bottom - 8;
                if (offscreen)
                {
                    Left = screen.Right - estW - 20;
                    Top  = screen.Bottom - estH - 20;
                }
                else
                {
                    if (sl + estW > screen.Right)  sl = screen.Right - estW;
                    if (st + estH > screen.Bottom) st = screen.Bottom - estH;
                    if (sl < screen.Left) sl = screen.Left;
                    if (st < screen.Top)  st = screen.Top;
                    Left = sl; Top = st;
                }
            }
            else { Left = screen.Right - Width - 20; Top = screen.Bottom - 200 - 20; }
            BuildUI();
            MouseWheel += (s, e) => { VolumeControl.SetVolume(VolumeControl.GetVolume() + (e.Delta > 0 ? 0.02f : -0.02f)); UpdateVolumeUI(); };
            LoadData();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _refreshTimer.Tick += (s, e) => { LoadData(); if (_tray != null) _tray.UpdateIcon(); try { _scaling.ApplyScaling(); _scaling.Reposition(); } catch { } };
            _refreshTimer.Start();
            // Quick poll (2s) for resolution/DPI changes: SystemEvents.DisplaySettingsChanged
            // is unreliable across DPI switches, so we watch the live screen width directly.
            _screenPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _screenPoll.Tick += (s, e) => { try { if (_scaling != null) _scaling.ApplyScaling(); } catch { } };
            _screenPoll.Start();
            Closing += (s, ev) => { ev.Cancel = true; Hide(); };
            Loaded += OnLoaded;
            LocationChanged += (s, e) => { if (IsLoaded) SavePosition(); };
        }

        // Persist the window's absolute position. Fixed position is unconditional —
        // the window stays at these coordinates across resolution changes; it is
        // only rescued (not repositioned) if it ends up fully off-screen.
        void SavePosition()
        {
            try
            {
                AppPrefs.SetDouble("Left", Left);
                AppPrefs.SetDouble("Top", Top);
            }
            catch { }
        }

        // Start-up nudge: clamp any edge that pokes outside the work area so the
        // whole window is visible on first show (e.g. when the saved position was
        // estimated against a different size). Runs once after the first layout.
        // Later resolution changes deliberately do NOT call this — they honour
        // 固定位置 and leave the window where the user put it.
        void EnsureFullyVisible()
        {
            try
            {
                var screen = System.Windows.Forms.Screen.PrimaryScreen;
                double dpi = 96.0;
                try
                {
                    var src = System.Windows.PresentationSource.FromVisual(this);
                    if (src != null && src.CompositionTarget != null)
                        dpi = 96.0 * src.CompositionTarget.TransformToDevice.M11;
                }
                catch { }
                double s = 96.0 / dpi;
                double waL = screen.WorkingArea.Left * s, waT = screen.WorkingArea.Top * s;
                double waR = screen.WorkingArea.Right * s, waB = screen.WorkingArea.Bottom * s;
                double w = ActualWidth > 0 ? ActualWidth : Width;
                double h = ActualHeight > 0 ? ActualHeight : Height;
                if (double.IsNaN(w) || w <= 0) w = 280;
                if (double.IsNaN(h) || h <= 0) h = 200;
                double left = Left, top = Top;
                // Fully off-screen (e.g. its monitor was unplugged): re-seat at the
                // bottom-right so the window is reachable.
                if (left + w <= waL + 8 || left >= waR - 8 || top + h <= waT + 8 || top >= waB - 8)
                {
                    Left = waR - w - 20;
                    Top = waB - h - 20;
                    return;
                }
                if (left + w > waR) left = waR - w;
                if (top + h > waB) top = waB - h;
                if (left < waL) left = waL;
                if (top < waT) top = waT;
                if (left != Left) Left = left;
                if (top != Top) Top = top;
            }
            catch { }
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int darkMode = 1;
                try { Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int)); } catch { }
                int exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, exStyle | Native.WS_EX_TOOLWINDOW);
                Native.SetWindowPos(hwnd,
                    _topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
                    0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                try { _tray = new TrayController(this, ExitApp); _tray.Init(); } catch { }
                try { _tray.UpdateIcon(); } catch { }
                try { ClipboardHistory.Start(); } catch { }
                try { UpdateChecker.CheckAsync(this, false); } catch { }
                try { RestartAutoCleanTimer(); } catch { }
                try { StartAutoCollapse(); } catch { }
                // Register hotkey window hook
                _hotkeyHwnd = hwnd;
                System.Windows.Interop.HwndSource.FromHwnd(hwnd).AddHook(WndProc);
                RefreshHotkeys();
                _deviceWatcher = new AudioDevices.DeviceWatcher();
                _deviceWatcher.OnChange = () => Dispatcher.BeginInvoke(new Action(() => { VolumeControl.Invalidate(); LoadData(); ScheduleVolumeRefresh(); }));
                Dispatcher.BeginInvoke(new Action(() => { try { TrimWorkingSet(); } catch { } }),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                // Scaling / work-area clamping live in WindowScaling now.
                _scaling = new WindowScaling(this, () => _mainBorder);
                // Re-clamp window into the work area when display config changes (e.g. 4K -> 1080p,
                // monitor unplugged, DPI / scaling change). SystemEvents callbacks fire on a
                // worker thread, so always hop back to the UI dispatcher.
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += _scaling.OnDisplaySettingsChanged;
                Microsoft.Win32.SystemEvents.UserPreferenceChanged += _scaling.OnUserPreferenceChanged;
                // Scale to the screen resolution first, then re-clamp after the first
                // layout pass so ActualWidth/ActualHeight are real.
                _scaling.ApplyScaling();
                // After the first layout pass ActualWidth/ActualHeight are real, so we
                // can nudge the window fully on-screen. This is start-up only — it does
                // NOT run on later resolution changes (those honour 固定位置 and leave
                // the window where the user put it).
                Dispatcher.BeginInvoke(new Action(EnsureFullyVisible), DispatcherPriority.Loaded);
            }
            catch (Exception ex) { AppLog.Log("OnLoaded", ex); }
        }


        void BuildUI()
        {
            _mainBorder = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(BgColor),
                BorderBrush = new SolidColorBrush(BorderColor),                BorderThickness = new Thickness(1),
                // Material elevation (dp2-ish): a wide, soft, low-opacity shadow lifts
                // the floating card off the desktop without a hard edge. Larger blur +
                // smaller depth + lower opacity reads as ambient occlusion, not a drop.
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 36,
                    ShadowDepth = 2,
                    Opacity = 0.32,
                    Color = Colors.Black
                }
            };
            _root = new StackPanel();
            var titleBar = new DockPanel
            {
                Background = Brushes.Transparent, // bg lives on the rounded wrapper below
                Height = 36,
                LastChildFill = true
            };
            var titleStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            var titleIcon = new System.Windows.Controls.Image
            {
                Width = 16, Height = 16,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            try
            {
                // Prefer embedded resource, fall back to external app.ico.
                var icoBmp = LoadAppImage("app.ico");
                if (icoBmp != null) titleIcon.Source = icoBmp;
            }
            catch { }
            var titleLabel = new TextBlock
            {
                Text = "OneBox",
                FontFamily = AppFont,
                Foreground = new SolidColorBrush(TextPrimary),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleStack.Children.Add(titleIcon);
            titleStack.Children.Add(titleLabel);
            var pinBtn = new Button
            {
                Content = _lockPosition ? "🔒" : "🔓", FontFamily = EmojiFont,  // 🔒 locked / 🔓 unlocked
                Width = 28, Height = 28,
                FontSize = 12,
                Foreground = new SolidColorBrush(_lockPosition ? AccentColor : TextSecondary),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "切换锁定窗口位置"
            };
            pinBtn.Click += (s, e) =>
            {
                _lockPosition = !_lockPosition;
                AppPrefs.SetBool("LockPosition", _lockPosition);
                pinBtn.Content = _lockPosition ? "🔒" : "🔓";
                pinBtn.Foreground = new SolidColorBrush(_lockPosition ? AccentColor : TextSecondary);
                if (_tray != null) _tray.SetLockChecked(_lockPosition);
            };
            _pinBtn = pinBtn;
            var collapseBtn = new Button
            {
                Content = "▲",
                Width = 28, Height = 28,
                FontSize = 14,
                Foreground = new SolidColorBrush(TextSecondary),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            collapseBtn.Click += ToggleCollapse;
            var closeBtn = new Button
            {
                Content = "✕",
                Width = 28, Height = 28,
                FontSize = 12,
                Foreground = new SolidColorBrush(TextSecondary),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeBtn.Click += (s, e) => Hide();
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
                tipBlock.Text = "电源计划: " + plan + System.Environment.NewLine + "音频设备: " + dev + mem;
            };
            // Drag the window only when position is unlocked. When locked, the
            // position is fixed and survives resolution changes (ClampToWorkArea
            // only nudges it back if it ends up fully off-screen).
            titleBar.MouseLeftButtonDown += (s, e) => { if (!_lockPosition) try { DragMove(); } catch { } };
            // Wrap the title bar in a Border whose top corners round to match the
            // outer card (CornerRadius 10). Previously the title bar's own opaque
            // #222132 background had square top corners that poked past the card's
            // r=10 arc and showed as "尖尖" spikes. Rounding the wrapper means the
            // title bar fill follows the arc; bottom corners stay square where it
            // meets the content. This replaces the old _root.Clip workaround.
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

            // Module visibility (用户可在设置里隐藏不需要的板块). Each section adds
            // its own leading divider (except the first) so hiding one leaves no
            // orphan divider.
            bool showPower = ModuleVisible("Power");
            bool showAudio = ModuleVisible("Audio");
            bool showMem   = ModuleVisible("Mem");
            bool showTr    = ModuleVisible("Translate");

            if (showPower)
            {
            var powerHeader = MakeCollapsibleHeader("电源计划", "icon-power.png", () => _powerSection, AppPrefs.GetBool("UI.PowerCollapsed", false));
            contentPanel.Children.Add(powerHeader);
            _powerSection = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            contentPanel.Children.Add(_powerSection);
            }

            if (showAudio)
            {
            if (contentPanel.Children.Count > 0) contentPanel.Children.Add(MakeDivider());

            var audioHeader = MakeCollapsibleHeader("音频输出", "icon-audio.png", () => _audioSection, AppPrefs.GetBool("UI.AudioCollapsed", false));
            contentPanel.Children.Add(audioHeader);
            _audioSection = new StackPanel();
            contentPanel.Children.Add(_audioSection);

            // Volume row
            var volRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
            _muteBtn = new Button {
                Content = "\uD83D\uDD0A", FontFamily = EmojiFont,
                Width = 28, Height = 28,
                FontSize = 14,
                Background = new SolidColorBrush(CardColor),
                Foreground = new SolidColorBrush(TextSecondary),
                BorderBrush = new SolidColorBrush(BorderColor),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 6, 0)
            };
            _muteBtn.Click += (s, e) => { VolumeControl.SetMute(!VolumeControl.GetMute()); UpdateVolumeUI(); };
            DockPanel.SetDock(_muteBtn, Dock.Left);
            volRow.Children.Add(_muteBtn);
            _volLabel = new TextBlock {
                Text = ((int)(VolumeControl.GetVolume()*100)).ToString() + "%",
                Foreground = new SolidColorBrush(TextSecondary),
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
                Foreground = new SolidColorBrush(AccentColor),
                Background = new SolidColorBrush(Color.FromRgb(60, 55, 90)),
                Template = BuildVolumeSliderTemplate()
            };
            _volSlider.ValueChanged += (s, e) => {
                if (_volLabel != null) _volLabel.Text = ((int)_volSlider.Value).ToString() + "%";
                if (!_volSliderUpdating) VolumeControl.SetVolume((float)(_volSlider.Value / 100.0));
            };
            volRow.Children.Add(_volSlider);
            contentPanel.Children.Add(volRow);
            }

            if (showMem)
            {
            if (contentPanel.Children.Count > 0) contentPanel.Children.Add(MakeDivider());
            // Memory section
            var memHeader = new TextBlock {
                Foreground = new SolidColorBrush(AccentColor),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            memHeader.Inlines.Add(new Run("🗑") { FontFamily = EmojiFont });
            memHeader.Inlines.Add(new Run(" 内存清理"));
            contentPanel.Children.Add(memHeader);
            _memStatusLabel = new TextBlock {
                Foreground = new SolidColorBrush(TextSecondary),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 6)
            };
            contentPanel.Children.Add(_memStatusLabel);
            var memBtn = new Button {
                Content = "立即清理内存",
                Padding = new Thickness(10, 6, 10, 6),
                FontSize = 12,
                Cursor = Cursors.Hand
            };
            StyleButton(memBtn, false, true);
            memBtn.Click += (s, e) => CleanMemory();
            contentPanel.Children.Add(memBtn);

            // Show "以管理员重启" hint button only for non-admin runs
            if (!AdminUtils.IsAdmin())
            {
                var elevateBtn = new Button {
                    Content = "以管理员身份重启 (启用全部清理项)",
                    Padding = new Thickness(10, 6, 10, 6),
                    FontSize = 11,
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(0, 6, 0, 0),
                    ToolTip = "Standby list / Modified page list / Registry cache 等清理项需要管理员权限"
                };
                StyleButton(elevateBtn, false);
                elevateBtn.Click += (s, e) => AdminUtils.RestartAsAdmin();
                contentPanel.Children.Add(elevateBtn);
            }
            }

            if (showTr)
            {
            if (contentPanel.Children.Count > 0) contentPanel.Children.Add(MakeDivider());
            // Translate section
            var trContent = new TextBlock {
                FontSize = 12,
                Foreground = new SolidColorBrush(TextSecondary)
            };
            trContent.Inlines.Add(new Run("\uD83D\uDCDD") { FontFamily = EmojiFont });
            trContent.Inlines.Add(new Run("  打开翻译窗口"));
            var trBtn = new Button {
                Content = trContent,
                Padding = new Thickness(10, 6, 10, 6),
                Cursor = Cursors.Hand,
                ToolTip = "全局快捷键：Ctrl+Shift+T 自动翻译剪贴板"
            };
            StyleButton(trBtn, false, true);
            trBtn.Click += (s, e) => OpenTranslateWindow(null);
            contentPanel.Children.Add(trBtn);
            }

            // ---- Launcher bar (4 quick-launch slots) ------------------------------
            if (ModuleVisible("Launcher")) LauncherBar.Build(contentPanel, RebuildUI);

            // ---- Clipboard-history button -----------------------------------------
            if (ModuleVisible("Clipboard")) BuildClipboardButton(contentPanel);

            _root.Children.Add(contentPanel);
            _mainBorder.Child = _root;
            Content = _mainBorder;
        }

        // Module visibility defaults: all on. Keys: UI.ShowPower / UI.ShowAudio /
        // UI.ShowMem / UI.ShowTranslate. Reads the registry directly (returns true
        // when the key is absent) so the first run shows everything.
        public static bool ModuleVisible(string module)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PowerAudioManager\App"))
                {
                    if (k != null)
                    {
                        var s = k.GetValue("UI.Show" + module) as string;
                        if (s == "0") return false;
                        if (s == "1") return true;
                    }
                }
            }
            catch { }
            return true;
        }

        // Rebuild the whole floating window after module-visibility changes.
        // Preserves position; SizeToContent recomputes height after BuildUI.
        public void RebuildUI()
        {
            double left = Left, top = Top;
            _contentPanel = null;
            _powerSection = null;
            _audioSection = null;
            _memStatusLabel = null;
            _root = null;
            _mainBorder = null;
            BuildUI();
            if (_scaling != null) _scaling.ApplyScaling(); // re-apply scale to the freshly built _mainBorder
            LoadData();
            Left = left; Top = top;
        }

        // Re-read the user-selected font and apply it to the window, then rebuild
        // so child elements pick it up. Called after the font is changed in settings.
        internal void ApplyFont()
        {
            FontFamily = AppResources.ReloadFont();
            RebuildUI();
        }

        // ---- Clipboard history button ------------------------------------------
        void BuildClipboardButton(StackPanel contentPanel)
        {
            var cbContent = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(TextSecondary) };
            cbContent.Inlines.Add(new Run("📋") { FontFamily = EmojiFont });
            cbContent.Inlines.Add(new Run("  剪贴板历史"));
            var cbBtn = new Button {
                Content = cbContent,
                Padding = new Thickness(10, 6, 10, 6),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 6, 0, 0),
                ToolTip = "查看最近复制的内容"
            };
            StyleButton(cbBtn, false);
            cbBtn.Click += (s, e) => ClipboardHistoryPanel.Show(this);
            contentPanel.Children.Add(cbBtn);
        }


        FrameworkElement MakeCollapsibleHeader(string title, string iconFile, Func<UIElement> sectionGetter, bool initiallyCollapsed)
        {
            var dock = new DockPanel {
                Margin = new Thickness(0, 0, 0, 6),
                LastChildFill = true,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent
            };
            var arrow = new TextBlock {
                Text = initiallyCollapsed ? "\u25B6" : "\u25BC",
                Foreground = new SolidColorBrush(AccentColor),
                FontSize = 10,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(arrow, Dock.Left);
            dock.Children.Add(arrow);
            // Icon
            var iconImg = LoadAppImage(iconFile);
            if (iconImg != null)
            {
                var img = new System.Windows.Controls.Image {
                    Source = iconImg,
                    Width = 14, Height = 14,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Stretch = System.Windows.Media.Stretch.Uniform
                };
                DockPanel.SetDock(img, Dock.Left);
                dock.Children.Add(img);
            }
            var label = new TextBlock {
                Text = title,
                Foreground = new SolidColorBrush(AccentColor),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            dock.Children.Add(label);

            // Apply the saved collapsed state asynchronously, after the section element exists
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
                arrow.Text = nowCollapsed ? "\u25B6" : "\u25BC";
                AppPrefs.SetBool(prefKey, nowCollapsed);
            };
            return dock;
        }
        // True while a background data load is pending. Prevents overlapping powercfg
        // invocations when several callers (device watcher, refresh timer, clicks) fire
        // LoadData in quick succession.
        bool _loading;
        DateTime _loadStartTime;

        internal void LoadData()
        {
            try { UpdateVolumeUI(); } catch { }
            try { UpdateMemoryUI(); } catch { }
            try { UpdateTrayTooltip(); } catch { }
            // Guard against a stuck background refresh (e.g. powercfg hanging during a
            // policy refresh): if a previous load has been "in flight" for over 10s,
            // assume it died and allow a new one.
            if (_loading && (DateTime.Now - _loadStartTime).TotalSeconds < 10) return;
            _loading = true;
            _loadStartTime = DateTime.Now;
            // Fetch plans and devices on a threadpool thread, render on the UI thread.
            // These are gathered together but each is independently try/caught so a
            // powercfg hiccup never blanks the audio list (devices come from the registry).
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
                // If the fetch failed (plans == null), keep the last good list rather than
                // blanking the section — a transient powercfg failure during plan add/remove
                // shouldn't wipe the UI.
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
                        Foreground = new SolidColorBrush(TextSecondary),
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
                // Keep the last good list if the fetch failed, so a transient error doesn't
                // blank the audio device names.
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
                        Foreground = new SolidColorBrush(TextSecondary),
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
                // Optimistically mark the chosen plan active so the UI reflects the tap
                // instantly, then switch on a background thread (avoids a 1-3s UI freeze
                // during the system policy refresh) and refresh once it's done.
                _currentPlanId = plan.Guid;
                foreach (var p in _powerPlans) p.IsActive = p.Guid == plan.Guid;
                if (_powerSection == null) return;
                _powerSection.Children.Clear();
                foreach (var p in _powerPlans) _powerSection.Children.Add(CreatePlanButton(p));
                PowerPlanService.SetActivePlanAsync(plan.Guid, Dispatcher, ok => { if (ok) LoadData(); });
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
                Foreground = Brushes.White // explicit so it never inherits a hidden colour
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
            // Right-click menu: hide / set hotkey
            var devCtx = new ContextMenu();
            var hideItem = new MenuItem { Header = "隐藏此设备" };
            hideItem.Click += (s, e) => { DevicePrefs.SetHidden(dev.Name, true); LoadData(); RefreshHotkeys(); };
            devCtx.Items.Add(hideItem);
            var hkItem = new MenuItem { Header = "设置快捷键..." };
            hkItem.Click += (s, e) => {
                // Temporarily release all global hotkeys so the dialog can capture conflicting combos
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

        ControlTemplate BuildVolumeSliderTemplate()
        {
            // Fluent slider: thin 3px pill track, a larger 14px round thumb with a subtle
            // dark ring so it reads against the accent fill.
            string xaml =
@"<ControlTemplate TargetType='Slider' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Grid VerticalAlignment='Center' Height='20'>
    <Border Height='3' VerticalAlignment='Center' CornerRadius='2'
            Background='{TemplateBinding Background}'/>
    <Track x:Name='PART_Track'>
      <Track.DecreaseRepeatButton>
        <RepeatButton IsTabStop='False' Focusable='False'
          Command='{x:Static Slider.DecreaseLarge}'>
          <RepeatButton.Template>
            <ControlTemplate TargetType='RepeatButton'>
              <Border Height='3' VerticalAlignment='Center' CornerRadius='2'
                Background='{Binding Foreground, RelativeSource={RelativeSource AncestorType=Slider}}'/>
            </ControlTemplate>
          </RepeatButton.Template>
        </RepeatButton>
      </Track.DecreaseRepeatButton>
      <Track.IncreaseRepeatButton>
        <RepeatButton IsTabStop='False' Focusable='False'
          Command='{x:Static Slider.IncreaseLarge}'>
          <RepeatButton.Template>
            <ControlTemplate TargetType='RepeatButton'>
              <Border Background='Transparent'/>
            </ControlTemplate>
          </RepeatButton.Template>
        </RepeatButton>
      </Track.IncreaseRepeatButton>
      <Track.Thumb>
        <Thumb>
          <Thumb.Template>
            <ControlTemplate TargetType='Thumb'>
              <Grid>
                <Ellipse Width='16' Height='16' Fill='#00000000'/>
                <Ellipse Width='14' Height='14' Fill='White' Stroke='#33000000' StrokeThickness='1'/>
              </Grid>
            </ControlTemplate>
          </Thumb.Template>
        </Thumb>
      </Track.Thumb>
    </Track>
  </Grid>
</ControlTemplate>";
            return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        // A soft Fluent separator: the existing BorderColor, but inset from the edges so it
        // reads as a divider between sections rather than a full-bleed line. No new colour.
        internal static Border MakeDivider()
        {
            return new Border
            {
                Height = 1,
                Background = new SolidColorBrush(BorderColor),
                Margin = new Thickness(2, 12, 2, 12),
                Opacity = 0.6
            };
        }

        // Rounded button template (corner radius 8) — Material-style pill shape for
        // every action button. Background/Border/Content/Padding all bind through.
        static ControlTemplate _roundedBtn;
        static ControlTemplate RoundedButtonTemplate()
        {
            if (_roundedBtn != null) return _roundedBtn;
            string xaml =
@"<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <Border CornerRadius='8' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>
    <ContentPresenter Margin='{TemplateBinding Padding}' HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center' RecognizesAccessKey='True'/>
  </Border>
</ControlTemplate>";
            _roundedBtn = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
            return _roundedBtn;
        }

        // Smooth hover: animate the Background brush colour instead of swapping it
        // instantly. ~180ms matches Material's standard ease for surface state changes.
        static void AnimateButtonBg(Button btn, Color to)
        {
            var b = btn.Background as SolidColorBrush;
            if (b == null) { btn.Background = new SolidColorBrush(to); return; }
            b.BeginAnimation(SolidColorBrush.ColorProperty,
                new System.Windows.Media.Animation.ColorAnimation(to, TimeSpan.FromMilliseconds(180)));
        }

        void UpdateVolumeUI()
        {
            if (_volSlider == null) return;
            _volSliderUpdating = true;
            try { _volSlider.Value = VolumeControl.GetVolume() * 100; if (_volLabel != null) _volLabel.Text = ((int)_volSlider.Value).ToString() + "%"; } catch { }
            _volSliderUpdating = false;
            _muteBtn.Content = VolumeControl.GetMute() ? "\uD83D\uDD07" : "\uD83D\uDD0A";
        }

        // Schedule a few delayed re-reads of the default endpoint volume after a device switch.
        // The kernel needs a beat to (re)bind audio policy, so the first read often returns the
        // *previous* device's value. Polling 3 times in 750 ms catches the new value reliably.
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

        void DoTranslate(string text)
        {
            // Compatibility wrapper: open the dedicated window with this initial text
            OpenTranslateWindow(text);
        }

        TranslateWindow _translateWindow;
        void OpenTranslateWindow(string initialText)
        {
            if (_translateWindow == null || !_translateWindow.IsLoaded)
            {
                _translateWindow = new TranslateWindow { FontFamily = this.FontFamily };
                _translateWindow.Closed += (s, e) => _translateWindow = null;
            }
            _translateWindow.Show();
            _translateWindow.Activate();
            if (!string.IsNullOrEmpty(initialText)) _translateWindow.RunTranslation(initialText);
        }

        void TranslateFromClipboard()
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string txt = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(txt))
                    {
                        OpenTranslateWindow(txt);
                    }
                }
            }
            catch { }
        }

        void UpdateMemoryUI()
        {
            if (_memStatusLabel == null) return;
            try
            {
                var s = MemoryCleaner.GetStatus();
                if (s == null) return;
                double total = s.TotalBytes / 1024.0 / 1024.0 / 1024.0;
                double avail = s.AvailableBytes / 1024.0 / 1024.0 / 1024.0;
                double used = total - avail;
                double cachedGb = s.CachedBytes / 1024.0 / 1024.0 / 1024.0;
                _memStatusLabel.Text = string.Format("已用 {0:0.0} GB / {1:0.0} GB ({2}%) · 已缓存 {3:0.0} GB", used, total, s.MemoryLoadPercent, cachedGb);
            }
            catch { }
        }

        internal void CleanMemory()
        {
            CleanMemory(MemoryCleaner.GetSavedFlags());
        }

        // Clean with explicit flags. Auto-clean passes a mask with the freeze-prone
        // items (full StandbyList purge, ModifiedPageList flush) stripped unless the
        // user opted in via 设置→内存 的"允许自动清理危险项" — silently purging the
        // standby list in the background can stall the whole system.
        internal void CleanMemory(MemoryCleaner.CleanFlags flags)
        {
            if (_memStatusLabel != null) _memStatusLabel.Text = "正在清理...";
            System.Threading.ThreadPool.QueueUserWorkItem(state =>
            {
                MemoryCleaner.CleanResult r = null;
                Exception err = null;
                try { r = MemoryCleaner.CleanAll(flags); }
                catch (Exception ex) { err = ex; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (err != null)
                    {
                        if (_memStatusLabel != null) _memStatusLabel.Text = "清理失败: " + err.Message;
                        return;
                    }
                    if (r != null && _memStatusLabel != null)
                    {
                        double freedMb = r.FreedBytes / 1024.0 / 1024.0;
                        if (freedMb < 1.0 && !AdminUtils.IsAdmin())
                            _memStatusLabel.Text = string.Format("已释放 {0:0} MB · 需管理员权限以启用更多清理项", freedMb);
                        else
                            _memStatusLabel.Text = string.Format("已释放 {0:0} MB", freedMb);
                        Dispatcher.BeginInvoke(new Action(UpdateMemoryUI),
                            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    }
                }));
            });
        }

        public void RestartAutoCleanTimer()
        {
            if (_autoCleanTimer != null) _autoCleanTimer.Stop();
            if (!AppPrefs.GetBool("AutoCleanEnabled", false)) return;
            // Tick every minute; decide each tick whether to clean
            _autoCleanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _autoCleanTimer.Tick += (s, e) => AutoCleanCheck();
            _autoCleanTimer.Start();
        }

        void AutoCleanCheck()
        {
            try
            {
                bool byTime = AppPrefs.GetBool("AutoCleanByTime", true);
                bool byTh = AppPrefs.GetBool("AutoCleanByThreshold", true);
                bool shouldClean = false;
                if (byTime)
                {
                    double mins; AppPrefs.GetDouble("AutoCleanMinutes", out mins);
                    if (mins <= 0) mins = 30;
                    if ((DateTime.Now - _lastCleanTime).TotalMinutes >= mins) shouldClean = true;
                }
                if (!shouldClean && byTh)
                {
                    double th; AppPrefs.GetDouble("AutoCleanThreshold", out th);
                    if (th <= 0) th = 80;
                    var ms = MemoryCleaner.GetStatus();
                    if (ms != null && ms.MemoryLoadPercent >= th) shouldClean = true;
                }
                if (shouldClean)
                {
                    _lastCleanTime = DateTime.Now;
                    var flags = MemoryCleaner.GetSavedFlags();
                    // Auto-clean skips the freeze-prone items (full standby purge,
                    // modified page list flush) unless the user explicitly allows
                    // them — a background standby purge can stall the system.
                    if (!AppPrefs.GetBool("AutoCleanAllowFreezes", false))
                        flags &= ~(MemoryCleaner.CleanFlags.StandbyList | MemoryCleaner.CleanFlags.ModifiedPageList);
                    CleanMemory(flags);
                }
            }
            catch { }
        }

        // Builds the multi-line status text shown in the tray tooltip (plan /
        // device / memory). The tray controller writes it to the NotifyIcon,
        // bypassing the 63-char public limit via reflection.
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

        // Material-style button styling. Three variants share one rounded template:
        //  - primary:  accent fill + white text, hover darkens to AccentHover
        //  - outline:  transparent fill + border, hover fills with a tonal accent tint
        //  - default:  card fill + secondary text, hover lifts to HoverColor (elevation)
        // active overrides the fill to the pressed/accent state (used for the selected
        // power plan / audio device button).
        void StyleButton(Button btn, bool isActive) { StyleButton(btn, isActive, false); }

        void StyleButton(Button btn, bool isActive, bool primary)
        {
            btn.Template = RoundedButtonTemplate();
            if (isActive)
            {
                btn.Background = new SolidColorBrush(ActiveBg);
                btn.Foreground = Brushes.White;
                btn.FontWeight = FontWeights.SemiBold;
                btn.BorderBrush = new SolidColorBrush(AccentColor);
                btn.BorderThickness = new Thickness(1);
                return;
            }
            if (primary)
            {
                // Filled accent button — the main call-to-action (清理/翻译).
                btn.Background = new SolidColorBrush(AccentColor);
                btn.Foreground = Brushes.White;
                btn.FontWeight = FontWeights.SemiBold;
                btn.BorderBrush = new SolidColorBrush(AccentColor);
                btn.BorderThickness = new Thickness(0);
                btn.MouseEnter += (s, e) => AnimateButtonBg(btn, AccentHover);
                btn.MouseLeave += (s, e) => AnimateButtonBg(btn, AccentColor);
            }
            else
            {
                btn.Background = new SolidColorBrush(CardColor);
                btn.Foreground = new SolidColorBrush(TextSecondary);
                btn.FontWeight = FontWeights.Normal;
                btn.BorderBrush = new SolidColorBrush(BorderColor);
                btn.BorderThickness = new Thickness(1);
                btn.MouseEnter += (s, e) => AnimateButtonBg(btn, HoverColor);
                btn.MouseLeave += (s, e) => AnimateButtonBg(btn, CardColor);
            }
        }


        internal void ShowWindow()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            // Honor the user's topmost preference – do NOT force-pin every time we show.
            if (_topmost) { Topmost = false; Topmost = true; }
        }

        // Application shutdown: stop the device watcher, detach SystemEvents
        // hooks, dispose the tray, then exit. Invoked from the tray "退出" item.
        internal void ExitApp()
        {
            if (_deviceWatcher != null) _deviceWatcher.Stop();
            try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= _scaling.OnDisplaySettingsChanged; } catch { }
            try { Microsoft.Win32.SystemEvents.UserPreferenceChanged -= _scaling.OnUserPreferenceChanged; } catch { }
            if (_tray != null) _tray.Dispose();
            Application.Current.Shutdown();
        }


        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == Native.HOTKEY_ID_TRANSLATE)
                {
                    TranslateFromClipboard();
                    handled = true;
                    return IntPtr.Zero;
                }
                if (id == Native.HOTKEY_ID_SCREENSHOT)
                {
                    // Capture on a threadpool thread so the hotkey loop is never
                    // blocked; the toast marshals back to the UI thread itself.
                    System.Threading.ThreadPool.QueueUserWorkItem(_ => ScreenshotService.CaptureForeground());
                    handled = true;
                    return IntPtr.Zero;
                }
                string devName;
                if (_hotkeyMap.TryGetValue(id, out devName))
                {
                    if (AudioDevices.SetDefaultDevice(devName))
                    {
                        _currentDeviceId = null;
                        VolumeControl.Invalidate();
                        LoadData();
                        ScheduleVolumeRefresh();
                    }
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }


        void TrimWorkingSet()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Native.SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        }

        Dictionary<int, string> _hotkeyMap = new Dictionary<int, string>();
        IntPtr _hotkeyHwnd = IntPtr.Zero;

        void UnregisterAllHotkeys()
        {
            if (_hotkeyHwnd == IntPtr.Zero) return;
            foreach (var id in _hotkeyMap.Keys) Native.UnregisterHotKey(_hotkeyHwnd, id);
            _hotkeyMap.Clear();
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_TRANSLATE);
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_SCREENSHOT);
        }

        internal void RefreshHotkeys()
        {
            if (_hotkeyHwnd == IntPtr.Zero) return;
            // Unregister all known IDs
            foreach (var id in _hotkeyMap.Keys) Native.UnregisterHotKey(_hotkeyHwnd, id);
            _hotkeyMap.Clear();
            // Translate hotkey: Ctrl+Shift+T (VK_T = 0x54)
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_TRANSLATE);
            Native.RegisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_TRANSLATE, Native.MOD_CONTROL | Native.MOD_SHIFT, 0x54);
            // Screenshot hotkey: user-bound (stored as Screenshot.Hotkey, same
            // encoding as device hotkeys: hi16 = mods, lo16 = VK). No default.
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_SCREENSHOT);
            int shotEncoded = AppPrefs.GetInt("Screenshot.Hotkey", 0);
            if (shotEncoded != 0)
            {
                int smods = (shotEncoded >> 16) & 0xFFFF;
                uint svk = (uint)(shotEncoded & 0xFFFF);
                uint swinMods = 0;
                if ((smods & 1) != 0) swinMods |= Native.MOD_ALT;
                if ((smods & 2) != 0) swinMods |= Native.MOD_CONTROL;
                if ((smods & 4) != 0) swinMods |= Native.MOD_SHIFT;
                if ((smods & 8) != 0) swinMods |= Native.MOD_WIN;
                Native.RegisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_SCREENSHOT, swinMods, svk);
            }
            int nextId = Native.HOTKEY_ID_BASE;
            foreach (var kv in DevicePrefs.GetAllHotkeys())
            {
                int encoded = kv.Value;
                if (encoded == 0) continue;
                int mods = (encoded >> 16) & 0xFFFF;
                uint vk = (uint)(encoded & 0xFFFF);
                uint winMods = 0;
                if ((mods & 1) != 0) winMods |= Native.MOD_ALT;
                if ((mods & 2) != 0) winMods |= Native.MOD_CONTROL;
                if ((mods & 4) != 0) winMods |= Native.MOD_SHIFT;
                if ((mods & 8) != 0) winMods |= Native.MOD_WIN;
                int id = nextId++;
                if (Native.RegisterHotKey(_hotkeyHwnd, id, winMods, vk))
                    _hotkeyMap[id] = kv.Key;
            }
        }

        void ToggleCollapse(object sender, RoutedEventArgs e)
        {
            SetExpanded(!_isExpanded);
        }

        // Core expand/collapse, reused by the manual button and auto-collapse.
        // Keeps the bottom edge anchored so the window grows/shrinks in place.
        void SetExpanded(bool expanded)
        {
            _isExpanded = expanded;
            // Pin bottom edge so the window collapses upward
            double bottom = Top + ActualHeight;
            if (_isExpanded)
            {
                if (_contentPanel != null) _contentPanel.Visibility = Visibility.Visible;
                // Title bar's bottom corners stay square where it meets the content.
                if (_titleBarBorder != null) _titleBarBorder.CornerRadius = new CornerRadius(10, 10, 0, 0);
                SizeToContent = SizeToContent.Height;
            }
            else
            {
                if (_contentPanel != null) _contentPanel.Visibility = Visibility.Collapsed;
                // Collapsed: only the title bar is visible, so round its bottom
                // corners too — otherwise the square bottom pokes past the outer
                // card's rounded corners and looks like flat bottom corners.
                if (_titleBarBorder != null) _titleBarBorder.CornerRadius = new CornerRadius(10);
                SizeToContent = SizeToContent.Height; // let WPF compute exact title-bar height
                MinHeight = 36;
            }
            // Re-anchor: keep bottom edge fixed. Wait for the next LayoutUpdated when ActualHeight is correct.
            EventHandler reanchor = null;
            reanchor = (xs, xe) =>
            {
                LayoutUpdated -= reanchor;
                double newTop = bottom - ActualHeight;
                var wa = SystemParameters.WorkArea;
                if (newTop < wa.Top) newTop = wa.Top;
                if (newTop + ActualHeight > wa.Bottom) newTop = wa.Bottom - ActualHeight;
                Top = newTop;
            };
            LayoutUpdated += reanchor;
            // Cancel any pending auto-collapse when the user manually expands.
            if (expanded && _autoCollapseTimer != null) _autoCollapseTimer.Stop();
        }

        // ---- Auto-collapse -----------------------------------------------------
        // When enabled, the window collapses after the mouse leaves for a configurable
        // delay, and expands again on hover. The manual collapse button stays in sync.
        DispatcherTimer _autoCollapseTimer;

        void StartAutoCollapse()
        {
            if (!AppPrefs.GetBool("AutoCollapse", true)) return;
            _autoCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(0, AppPrefs.GetInt("AutoCollapseDelay", 8))) };
            _autoCollapseTimer.Tick += (s, e) => { _autoCollapseTimer.Stop(); SetExpanded(false); };
            MouseEnter += (s, e) =>
            {
                if (_autoCollapseTimer != null) _autoCollapseTimer.Stop();
                if (!_isExpanded) SetExpanded(true);
            };
            MouseLeave += (s, e) =>
            {
                if (_autoCollapseTimer != null && AppPrefs.GetBool("AutoCollapse", true))
                {
                    _autoCollapseTimer.Interval = TimeSpan.FromSeconds(Math.Max(0, AppPrefs.GetInt("AutoCollapseDelay", 8)));
                    _autoCollapseTimer.Start();
                }
            };
        }

        // Re-read auto-collapse settings (called after the user changes them).
        internal void RefreshAutoCollapse()
        {
            if (_autoCollapseTimer != null) _autoCollapseTimer.Stop();
            if (!AppPrefs.GetBool("AutoCollapse", true)) return;
            if (_autoCollapseTimer == null) { StartAutoCollapse(); return; }
            _autoCollapseTimer.Interval = TimeSpan.FromSeconds(Math.Max(0, AppPrefs.GetInt("AutoCollapseDelay", 8)));
        }
    }

}
