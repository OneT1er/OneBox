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
using MaterialDesignThemes.Wpf;

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
        private System.Threading.Timer _initLoadTimer;
        private DateTime _lastCleanTime = DateTime.MinValue;
        internal WindowScaling _scaling;
        private StackPanel _root;
        private StackPanel _powerSection;
        private StackPanel _audioSection;
        private bool _isExpanded = true;
        private bool _collapsedManually; // 通过按钮收起时为 true（非自动收起）
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

        // 共享调色板，内部可见供 LauncherBar / TrayController 等复用。
        // 按 Material 层级排列：越底层表面越浅，叠层卡片有视觉深度。
        internal static readonly Color AccentColor = Color.FromRgb(142, 140, 216);   // 紫影 #8E8CD8
        internal static readonly Color AccentHover = Color.FromRgb(126, 122, 210);   // 强调色悬停
        internal static readonly Color BgColor = Color.FromRgb(28, 26, 40);          // 深底
        internal static readonly Color CardColor = Color.FromRgb(42, 39, 60);        // 卡片
        internal static readonly Color HoverColor = Color.FromRgb(58, 54, 84);       // 悬停
        internal static readonly Color TextPrimary = Colors.White;
        internal static readonly Color TextSecondary = Color.FromRgb(190, 188, 220); // 次要文字
        internal static readonly Color ActiveBg = Color.FromRgb(110, 105, 200);      // 激活态
        internal static readonly Color BorderColor = Color.FromRgb(80, 75, 120);     // 边框
        internal static readonly Color AccentRipple = Color.FromRgb(80, 76, 150);    // 强调按钮叠色

        static System.Windows.Media.FontFamily AppFont { get { return AppResources.AppFont; } }
        static System.Windows.Media.FontFamily EmojiFont { get { return AppResources.EmojiFont; } }
        static System.Windows.Media.Imaging.BitmapImage LoadAppImage(string fileName) { return AppResources.LoadAppImage(fileName); }

        public MainWindow()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
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
                // 保存的位置在屏幕外（如 4K 拖到后换 1080p 启动）时，回到右下角确保可见。
                double estW = Width;
                double estH = 200;
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
            // LoadData() 推迟到 OnLoaded 异步执行，避免 GetStatus() 首次创建 PerformanceCounter（~300ms）阻塞构造函数。
            AppLog.Log("Startup", "ctor done " + sw.ElapsedMilliseconds + "ms");
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _refreshTimer.Tick += (s, e) => { LoadData(); if (_tray != null) _tray.UpdateIcon(); try { _scaling.ApplyScaling(); _scaling.Reposition(); } catch { } };
            _refreshTimer.Start();
            // 2s 轮询分辨率/DPI 变化。SystemEvents.DisplaySettingsChanged 在 DPI 切换时不可靠，直接监视屏幕宽度。
            _screenPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _screenPoll.Tick += (s, e) => { try { if (_scaling != null) _scaling.ApplyScaling(); } catch { } };
            _screenPoll.Start();
            Closing += (s, ev) => { ev.Cancel = true; Hide(); };
            Loaded += OnLoaded;
            LocationChanged += (s, e) => { if (IsLoaded) SavePosition(); };
        }

        // 保存窗口绝对位置。固定位置无条件：切换分辨率不移动窗口，仅完全离开屏幕时拉回。
        void SavePosition()
        {
            try
            {
                AppPrefs.SetDouble("Left", Left);
                AppPrefs.SetDouble("Top", Top);
            }
            catch { }
        }

        // 启动时微调：首次显示时把超出工作区的部分夹回屏幕内。后续分辨率变化不再调用——遵守固定位置。
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
                // 完全离开屏幕（如外接显示器被拔掉），重置到右下角确保可操作。
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
            AppLog.Log("App", "OnLoaded start, admin=" + AdminUtils.IsAdmin());
            try { AppLog.Log("Startup", "process->OnLoaded " + (int)(System.DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds + "ms"); } catch { }
            var sw = System.Diagnostics.Stopwatch.StartNew();
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
                // UpdateIcon 调用 MemoryCleaner.GetStatus() 首次创建 PerformanceCounter ~400ms，推迟到 Idle 执行避免阻塞 OnLoaded。
                Dispatcher.BeginInvoke(new Action(() => { try { if (_tray != null) _tray.UpdateIcon(); } catch { } }),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                try { ClipboardHistory.Start(); } catch { }
                try { UpdateChecker.CheckAsync(this, false); } catch { }
                try { RestartAutoCleanTimer(); } catch { }
                try { StartAutoCollapse(); } catch { }
                _hotkeyHwnd = hwnd;
                System.Windows.Interop.HwndSource.FromHwnd(hwnd).AddHook(WndProc);
                RefreshHotkeys();
                _deviceWatcher = new AudioDevices.DeviceWatcher();
                _deviceWatcher.OnChange = () => Dispatcher.BeginInvoke(new Action(() => { VolumeControl.Invalidate(); LoadData(); ScheduleVolumeRefresh(); }));
                Dispatcher.BeginInvoke(new Action(() => { try { TrimWorkingSet(); } catch { } }),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                _scaling = new WindowScaling(this, () => _mainBorder);
                // 显示配置变化时重新夹回工作区。SystemEvents 回调在工作线程触发，必须跳回 UI 派发器。
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += _scaling.OnDisplaySettingsChanged;
                Microsoft.Win32.SystemEvents.UserPreferenceChanged += _scaling.OnUserPreferenceChanged;
                _scaling.ApplyScaling();
                // 首次布局后夹回屏幕内（仅启动时，后续分辨率变化遵守固定位置）。
                Dispatcher.BeginInvoke(new Action(EnsureFullyVisible), DispatcherPriority.Loaded);
                // 窗口显示后延迟加载电源计划/设备/内存，避免 PerformanceCounter 初始化（~300ms）阻塞首帧。
                // 原先用 ApplicationIdle，但 .NET 8 冷启动时派发器数秒不进入 Idle，导致 ~6s 延迟。
                // 改用 threading timer 确定性地 50ms 后触发，通过 BeginInvoke 回到 UI 线程。
                _initLoadTimer = new System.Threading.Timer(_ =>
                {
                    Dispatcher.BeginInvoke(new Action(() => { try { LoadData(); } catch (Exception ex) { AppLog.Log("Startup LoadData", ex); } }));
                }, null, 50, System.Threading.Timeout.Infinite);
                AppLog.Log("Startup", "OnLoaded done " + sw.ElapsedMilliseconds + "ms");
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
                // Material 层级阴影 (dp2)：宽柔低透明度投影，悬浮卡片无硬边。
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
            var titleIcon = new System.Windows.Controls.Image
            {
                Width = 16, Height = 16,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            try
            {
                // 优先使用嵌入资源，回退到外部 app.ico。
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
                Content = PinIcon(_lockPosition),
                Width = 28, Height = 28,
                Foreground = new SolidColorBrush(_lockPosition ? AccentColor : TextSecondary),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "切换锁定窗口位置"
            };
            ApplyIconButtonStyle(pinBtn);
            pinBtn.Click += (s, e) =>
            {
                _lockPosition = !_lockPosition;
                AppPrefs.SetBool("LockPosition", _lockPosition);
                pinBtn.Content = PinIcon(_lockPosition);
                pinBtn.Foreground = new SolidColorBrush(_lockPosition ? AccentColor : TextSecondary);
                if (_tray != null) _tray.SetLockChecked(_lockPosition);
            };
            _pinBtn = pinBtn;
            var collapseBtn = new Button
            {
                Content = new PackIcon { Kind = PackIconKind.ChevronUp, Width = 16, Height = 16 },
                Width = 28, Height = 28,
                Foreground = new SolidColorBrush(TextSecondary),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            ApplyIconButtonStyle(collapseBtn);
            collapseBtn.Click += ToggleCollapse;
            var closeBtn = new Button
            {
                Content = new PackIcon { Kind = PackIconKind.Close, Width = 16, Height = 16 },
                Width = 28, Height = 28,
                Foreground = new SolidColorBrush(TextSecondary),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            ApplyIconButtonStyle(closeBtn);
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

            // 板块可见性（用户可在设置中隐藏）。每个板块自带头部分割线（第一个除外），隐藏不会遗留孤立分割线。
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

            var volRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
            _muteBtn = new Button {
                Content = MuteIcon(false),
                Width = 28, Height = 28,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(TextSecondary),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 6, 0)
            };
            ApplyIconButtonStyle(_muteBtn);
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
                Margin = new Thickness(4, 0, 4, 0)
            };
            // MaterialDesign slider: 紫影滑块与轨道自动来自主题主色。
            var sliderStyle = Application.Current.TryFindResource("MaterialDesign3.MaterialDesignSlider") as Style;
            if (sliderStyle != null) _volSlider.Style = sliderStyle;
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

            if (ModuleVisible("Launcher")) LauncherBar.Build(contentPanel, RebuildUI);

            if (ModuleVisible("Clipboard")) BuildClipboardButton(contentPanel);

            if (ModuleVisible("Gallery")) BuildGalleryButton(contentPanel);

            _root.Children.Add(contentPanel);

            _mainBorder.Child = _root;
            Content = _mainBorder;
        }

        // 板块可见性默认全开。键值缺失返回 true，首次运行显示全部板块。
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

        // 板块可见性变更后重建整个悬浮窗，保持位置不变。
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

        // 重新加载用户选择的字体并重建 UI，设置中换字体后调用。
        internal void ApplyFont()
        {
            FontFamily = AppResources.ReloadFont();
            RebuildUI();
        }

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

        void BuildGalleryButton(StackPanel contentPanel)
        {
            var gContent = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(TextSecondary) };
            gContent.Inlines.Add(new Run("🖼") { FontFamily = EmojiFont });
            gContent.Inlines.Add(new Run("  截图图库"));
            var gBtn = new Button {
                Content = gContent,
                Padding = new Thickness(10, 6, 10, 6),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 6, 0, 0),
                ToolTip = "查看已保存的截图"
            };
            StyleButton(gBtn, false);
            gBtn.Click += (s, e) => ScreenshotGallery.Show(this);
            contentPanel.Children.Add(gBtn);
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
                arrow.Text = nowCollapsed ? "\u25B6" : "\u25BC";
                AppPrefs.SetBool(prefKey, nowCollapsed);
            };
            return dock;
        }
        // 后台数据加载进行中标记。防止多个调用方（设备监视器、刷新定时器、点击）短时间内重复触发 powercfg。
        bool _loading;
        DateTime _loadStartTime;

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


        // 柔和的 Fluent 风格分割线：使用边框色但留左右边距，作为区块间分隔。
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

        void UpdateVolumeUI()
        {
            if (_volSlider == null) return;
            _volSliderUpdating = true;
            try { _volSlider.Value = VolumeControl.GetVolume() * 100; if (_volLabel != null) _volLabel.Text = ((int)_volSlider.Value).ToString() + "%"; } catch { }
            _volSliderUpdating = false;
            _muteBtn.Content = MuteIcon(VolumeControl.GetMute());
        }

        // 设备切换后内核需要时间重新绑定音频策略，首次读取常返回旧设备的值。750ms 内轮询 3 次捕获新值。
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

        // 图片翻译流程：框选截图（UI 线程）→ 百度图片 API（线程池）→ 结果窗口（UI 线程）。
        void HandleImageTranslateHotkey()
        {
            byte[] png = null;
            try { png = RegionCaptureService.CaptureRegion(); }
            catch (Exception ex) { AppLog.Log("ImageTranslate capture", ex); ImageTranslateWindow.Show(this, null, null, "框选截图失败: " + ex.Message); return; }
            if (png == null) return; // cancelled or empty
            string from = AppPrefs.GetString("Translate.From", "auto");
            string to = AppPrefs.GetString("Translate.To", "zh");
            byte[] pngCaptured = png;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                ImageTranslateService.ImageResult res = null;
                try { res = ImageTranslateService.Translate(pngCaptured, from, to); }
                catch (Exception ex) { res = new ImageTranslateService.ImageResult { Error = ex.Message }; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ImageTranslateWindow.Show(this, res.PasteImage, res.Dst, res.Error);
                }));
            });
        }

        public void TranslateClipboardImage()
        {
            try
            {
                if (!System.Windows.Forms.Clipboard.ContainsImage()) { ImageTranslateWindow.Show(this, null, null, "剪贴板里没有图片"); return; }
                using (var img = System.Windows.Forms.Clipboard.GetImage())
                {
                    if (img == null) { ImageTranslateWindow.Show(this, null, null, "剪贴板里没有图片"); return; }
                    using (var ms = new System.IO.MemoryStream())
                    {
                        img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        byte[] png = ms.ToArray();
                        string from = AppPrefs.GetString("Translate.From", "auto");
                        string to = AppPrefs.GetString("Translate.To", "zh");
                        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                        {
                            ImageTranslateService.ImageResult res = null;
                            try { res = ImageTranslateService.Translate(png, from, to); }
                            catch (Exception ex) { res = new ImageTranslateService.ImageResult { Error = ex.Message }; }
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                ImageTranslateWindow.Show(this, res.PasteImage, res.Dst, res.Error);
                            }));
                        });
                    }
                }
            }
            catch (Exception ex) { AppLog.Log("ImageTranslate clipboard", ex); }
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

        // 带显式标志清理。自动清理会移除可能导致卡顿的项（StandbyList 全量清除、ModifiedPageList 刷盘），
        // 除非用户在设置中勾选"允许自动清理危险项"——后台静默清除 standby 列表可能让系统卡死。
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
                        AppLog.Log("MemoryClean", "error: " + err.Message);
                        return;
                    }
                    if (r != null && _memStatusLabel != null)
                    {
                        double freedMb = r.FreedBytes / 1024.0 / 1024.0;
                        if (freedMb < 1.0 && !AdminUtils.IsAdmin())
                            _memStatusLabel.Text = string.Format("已释放 {0:0} MB · 需管理员权限以启用更多清理项", freedMb);
                        else
                            _memStatusLabel.Text = string.Format("已释放 {0:0} MB", freedMb);
                        AppLog.Log("MemoryClean", "freed=" + (int)freedMb + "MB flags=" + flags);
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
            // 每分钟滴答一次，每次判断是否需要清理。
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
                    // 自动清理跳过可能导致卡顿的项，除非用户明确允许——后台 standby 清除可能让系统停滞。
                    if (!AppPrefs.GetBool("AutoCleanAllowFreezes", false))
                        flags &= ~(MemoryCleaner.CleanFlags.StandbyList | MemoryCleaner.CleanFlags.ModifiedPageList);
                    AppLog.Log("AutoClean", "triggered, flags=" + flags);
                    CleanMemory(flags);
                }
            }
            catch { }
        }

        // 托盘提示多行状态文本（计划/设备/内存），通过反射绕过 WinForms 63 字符限制写入 NotifyIcon。
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

        // Material 风格按钮：三种变体共用一个圆角模板。
        // primary=强调填充、default=卡片填充、isActive=激活态填充（选中电源计划/音频设备）。
        void StyleButton(Button btn, bool isActive) { StyleButton(btn, isActive, false); }

        // 应用 MaterialDesign 扁平按钮样式（透明底 + 悬停涟漪 + 无阴影）。
        // 重要：悬浮窗 AllowsTransparency 下凸起按钮阴影渲染异常，必须用扁平样式。
        void StyleButton(Button btn, bool isActive, bool primary)
        {
            ApplyFlatStyle(btn);
            if (isActive)
            {
                btn.Background = new SolidColorBrush(ActiveBg);
                btn.Foreground = Brushes.White;
                btn.FontWeight = FontWeights.SemiBold;
                return;
            }
            if (primary)
            {
                btn.Background = new SolidColorBrush(AccentColor);
                btn.Foreground = Brushes.White;
                btn.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                btn.Background = new SolidColorBrush(CardColor);
                btn.Foreground = new SolidColorBrush(TextSecondary);
                btn.FontWeight = FontWeights.Normal;
            }
        }

        // 给按钮打上 MaterialDesign 扁平样式。通过资源键查找，缺失则回退无样式，避免主题异常时按钮变空。
        internal static void ApplyFlatStyle(Button btn)
        {
            var style = Application.Current.TryFindResource("MaterialDesignFlatButton") as Style;
            if (style != null) btn.Style = style;
        }

        // 紧凑图标按钮（标题栏、静音、启动栏）。MaterialDesign 默认 MinWidth=88/MinHeight=36/大 Padding
        // 会撑大按钮导致图标"消失"。本地值覆盖样式设置项，强制 MinWidth/MinHeight=0, Padding=0。
        internal static void ApplyIconButtonStyle(Button btn)
        {
            ApplyFlatStyle(btn);
            btn.MinWidth = 0;
            btn.MinHeight = 0;
            btn.Padding = new Thickness(0);
            btn.HorizontalContentAlignment = HorizontalAlignment.Center;
            btn.VerticalContentAlignment = VerticalAlignment.Center;
        }

        // 标题栏锁定按钮图标。矢量图标不受 MaterialDesign 样式覆盖 FontFamily 影响，emoji 字体会被替换导致消失。
        internal static PackIcon PinIcon(bool locked)
        {
            return new PackIcon { Kind = locked ? PackIconKind.Lock : PackIconKind.LockOpen, Width = 16, Height = 16 };
        }

        internal static PackIcon MuteIcon(bool muted)
        {
            return new PackIcon { Kind = muted ? PackIconKind.VolumeMute : PackIconKind.VolumeHigh, Width = 16, Height = 16 };
        }


        internal void ShowWindow()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            // 仅在用户设置了置顶时刷新 Topmost，不强制置顶。
            if (_topmost) { Topmost = false; Topmost = true; }
        }

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
                    AppLog.Log("Hotkey", "translate (Ctrl+Shift+T) triggered");
                    TranslateFromClipboard();
                    handled = true;
                    return IntPtr.Zero;
                }
                if (id == Native.HOTKEY_ID_SCREENSHOT)
                {
                    AppLog.Log("Hotkey", "screenshot triggered");
                    // 线程池执行截图避免阻塞热键循环，Toast 内部会回到 UI 线程。
                    System.Threading.ThreadPool.QueueUserWorkItem(_ => ScreenshotService.CaptureForeground());
                    handled = true;
                    return IntPtr.Zero;
                }
                if (id == Native.HOTKEY_ID_CLIPBOARD)
                {
                    AppLog.Log("Hotkey", "clipboard history triggered");
                    Native.POINT pt;
                    Native.GetCursorPos(out pt);
                    ClipboardHistoryPanel.ShowAt(this, pt.X, pt.Y);
                    handled = true;
                    return IntPtr.Zero;
                }
                if (id == Native.HOTKEY_ID_IMAGE_TRANSLATE)
                {
                    AppLog.Log("Hotkey", "image translate (region capture) triggered");
                    HandleImageTranslateHotkey();
                    handled = true;
                    return IntPtr.Zero;
                }
                string devName;
                if (_hotkeyMap.TryGetValue(id, out devName))
                {
                    AppLog.Log("Hotkey", "switch audio device: " + devName);
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
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_CLIPBOARD);
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_IMAGE_TRANSLATE);
        }

        // 测试快捷键组合能否注册。用临时 ID 注册再立即注销，供设置对话框即时反馈某个组合是否被占用。
        internal bool TestHotkey(int encoded)
        {
            if (_hotkeyHwnd == IntPtr.Zero) return true;
            if (encoded == 0) return true;
            int mods = (encoded >> 16) & 0xFFFF;
            uint vk = (uint)(encoded & 0xFFFF);
            uint winMods = 0;
            if ((mods & 1) != 0) winMods |= Native.MOD_ALT;
            if ((mods & 2) != 0) winMods |= Native.MOD_CONTROL;
            if ((mods & 4) != 0) winMods |= Native.MOD_SHIFT;
            if ((mods & 8) != 0) winMods |= Native.MOD_WIN;
            int testId = 0xBE00; // 临时测试 ID，实际热键不会用
            Native.UnregisterHotKey(_hotkeyHwnd, testId);
            bool ok = Native.RegisterHotKey(_hotkeyHwnd, testId, winMods, vk);
            Native.UnregisterHotKey(_hotkeyHwnd, testId);
            return ok;
        }

        internal void RefreshHotkeys()
        {
            if (_hotkeyHwnd == IntPtr.Zero) return;
            foreach (var id in _hotkeyMap.Keys) Native.UnregisterHotKey(_hotkeyHwnd, id);
            _hotkeyMap.Clear();
            // 翻译快捷键：固定 Ctrl+Shift+T
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_TRANSLATE);
            Native.RegisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_TRANSLATE, Native.MOD_CONTROL | Native.MOD_SHIFT, 0x54);
            // 截图快捷键：用户自定义（Screenshot.Hotkey，编码同设备热键：hi16=修饰键, lo16=VK）
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
            // 剪贴板历史快捷键：用户自定义（Clipboard.Hotkey）
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_CLIPBOARD);
            int clipEncoded = AppPrefs.GetInt("Clipboard.Hotkey", 0);
            if (clipEncoded != 0)
            {
                int cmods = (clipEncoded >> 16) & 0xFFFF;
                uint cvk = (uint)(clipEncoded & 0xFFFF);
                uint cwinMods = 0;
                if ((cmods & 1) != 0) cwinMods |= Native.MOD_ALT;
                if ((cmods & 2) != 0) cwinMods |= Native.MOD_CONTROL;
                if ((cmods & 4) != 0) cwinMods |= Native.MOD_SHIFT;
                if ((cmods & 8) != 0) cwinMods |= Native.MOD_WIN;
                Native.RegisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_CLIPBOARD, cwinMods, cvk);
            }
            // 图片翻译快捷键：用户自定义（Screenshot.ImageTranslateHotkey）
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_IMAGE_TRANSLATE);
            int itEnc = AppPrefs.GetInt("Screenshot.ImageTranslateHotkey", 0);
            if (itEnc != 0)
            {
                int imods = (itEnc >> 16) & 0xFFFF;
                uint ivk = (uint)(itEnc & 0xFFFF);
                uint iwinMods = 0;
                if ((imods & 1) != 0) iwinMods |= Native.MOD_ALT;
                if ((imods & 2) != 0) iwinMods |= Native.MOD_CONTROL;
                if ((imods & 4) != 0) iwinMods |= Native.MOD_SHIFT;
                if ((imods & 8) != 0) iwinMods |= Native.MOD_WIN;
                Native.RegisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_IMAGE_TRANSLATE, iwinMods, ivk);
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
            SetExpanded(!_isExpanded, true);
        }

        // 展开/折叠核心逻辑。manual=true 表示用户点击按钮触发，auto 计时器传 false。
        // 手动折叠后悬停不展开，除非开启"AutoExpandAfterManual"。
        void SetExpanded(bool expanded) { SetExpanded(expanded, false); }
        void SetExpanded(bool expanded, bool manual)
        {
            _isExpanded = expanded;
            if (manual && !expanded) _collapsedManually = true;
            if (expanded) _collapsedManually = false;
            // 固定底边使窗口向上折叠
            double bottom = Top + ActualHeight;
            if (_isExpanded)
            {
                if (_contentPanel != null) _contentPanel.Visibility = Visibility.Visible;
                // 展开时标题栏下角保持直角与内容区衔接。
                if (_titleBarBorder != null) _titleBarBorder.CornerRadius = new CornerRadius(10, 10, 0, 0);
                SizeToContent = SizeToContent.Height;
            }
            else
            {
                if (_contentPanel != null) _contentPanel.Visibility = Visibility.Collapsed;
                // 折叠时仅标题栏可见，下角也圆角匹配外层卡片，避免方角超出圆角弧边。
                if (_titleBarBorder != null) _titleBarBorder.CornerRadius = new CornerRadius(10);
                SizeToContent = SizeToContent.Height;
                MinHeight = 36;
            }
            // 重锚定：保持底边固定，等 LayoutUpdated 后 ActualHeight 正确再调整。
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
            // 手动展开时取消待执行的自动折叠。
            if (expanded && _autoCollapseTimer != null) _autoCollapseTimer.Stop();
        }

        // 自动折叠：鼠标离开延迟后折叠，悬停展开。手动折叠按钮保持同步。
        DispatcherTimer _autoCollapseTimer;

        void StartAutoCollapse()
        {
            if (!AppPrefs.GetBool("AutoCollapse", true)) return;
            _autoCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(0, AppPrefs.GetInt("AutoCollapseDelay", 8))) };
            _autoCollapseTimer.Tick += (s, e) => { _autoCollapseTimer.Stop(); SetExpanded(false, false); };
            MouseEnter += (s, e) =>
            {
                if (_autoCollapseTimer != null) _autoCollapseTimer.Stop();
                if (!_isExpanded)
                {
                    // 手动折叠后悬停不展开，除非开启"手动折叠后也自动展开"。
                    if (_collapsedManually && !AppPrefs.GetBool("AutoExpandAfterManual", false)) return;
                    SetExpanded(true);
                }
            };
            MouseLeave += (s, e) =>
            {
                // 手动折叠的窗口不再自动折叠（已折叠且用户希望保持折叠）。
                if (_collapsedManually) return;
                if (_autoCollapseTimer != null && AppPrefs.GetBool("AutoCollapse", true))
                {
                    _autoCollapseTimer.Interval = TimeSpan.FromSeconds(Math.Max(0, AppPrefs.GetInt("AutoCollapseDelay", 8)));
                    _autoCollapseTimer.Start();
                }
            };
        }

        // 重读自动折叠设置（用户修改设置后调用）。
        internal void RefreshAutoCollapse()
        {
            if (_autoCollapseTimer != null) _autoCollapseTimer.Stop();
            if (!AppPrefs.GetBool("AutoCollapse", true)) return;
            if (_autoCollapseTimer == null) { StartAutoCollapse(); return; }
            _autoCollapseTimer.Interval = TimeSpan.FromSeconds(Math.Max(0, AppPrefs.GetInt("AutoCollapseDelay", 8)));
        }
    }

}
