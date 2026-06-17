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
        private List<PowerPlanInfo> _powerPlans;
        private List<AudioDeviceInfo> _audioDevices;
        private string _currentPlanId;
        private string _currentDeviceId;
        private DispatcherTimer _refreshTimer;
        private DispatcherTimer _autoCleanTimer;
        private DateTime _lastCleanTime = DateTime.MinValue;
        private StackPanel _root;
        private StackPanel _powerSection;
        private StackPanel _audioSection;
        private bool _isExpanded = true;
        private System.Windows.Forms.NotifyIcon _winFormsTray;
        private bool _topmost = false;
        private Button _pinBtn;
        private System.Windows.Forms.ToolStripMenuItem _topmostMenuItem;
        private System.Windows.Forms.ToolStripMenuItem _lockMenuItem;
        private bool _lockPosition;
        private System.Windows.Forms.ContextMenuStrip _trayMenu;
        private AudioDevices.DeviceWatcher _deviceWatcher;
        private Slider _volSlider;
        private Button _muteBtn;
        private bool _volSliderUpdating;
        private TextBlock _volLabel;
        private TextBlock _memStatusLabel;
        private StackPanel _contentPanel;

        static readonly Color AccentColor = Color.FromRgb(142, 140, 216);   // 紫影 #8E8CD8
        static readonly Color BgColor = Color.FromRgb(28, 26, 40);          // 深底，与紫影协调
        static readonly Color CardColor = Color.FromRgb(42, 39, 60);        // 卡片
        static readonly Color TextPrimary = Colors.White;
        static readonly Color TextSecondary = Color.FromRgb(190, 188, 220); // 次要文字
        static readonly Color HoverColor = Color.FromRgb(58, 54, 84);       // 悬停
        static readonly Color ActiveBg = Color.FromRgb(110, 105, 200);      // 激活态（紫影偏深）
        static readonly Color BorderColor = Color.FromRgb(80, 75, 120);     // 边框

        static readonly System.Windows.Media.FontFamily AppFont = LoadAppFont();
        public static readonly System.Windows.Media.FontFamily EmojiFont =
            new System.Windows.Media.FontFamily("Segoe UI Symbol, Segoe UI Emoji");

        const string HarmonyFontDir = @"C:\Users\LIUxy\OneDrive\Documents\tools\美化与字体\HarmonyOS-Sans\HarmonyOS Sans\HarmonyOS_Sans_SC\";
        const string FontFileName = "HarmonyOS_Sans_SC_Regular.ttf";

        // Resolve the font directory: prefer a ttf shipped next to the exe (portable),
        // fall back to the developer machine path. Returns null if neither exists.
        static string ResolveFontDir()
        {
            try
            {
                var exeDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(exeDir) && System.IO.File.Exists(System.IO.Path.Combine(exeDir, FontFileName)))
                    return exeDir + System.IO.Path.DirectorySeparatorChar;
            }
            catch { }
            if (System.IO.Directory.Exists(HarmonyFontDir)) return HarmonyFontDir;
            return null;
        }

        static System.Windows.Media.FontFamily LoadAppFont()
        {
            try
            {
                var dir = ResolveFontDir();
                if (dir != null)
                    return new System.Windows.Media.FontFamily(new Uri(dir), "./#HarmonyOS Sans SC");
            }
            catch { }
            return new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        }

        // WinForms (tray menu) counterpart of AppFont: load the same HarmonyOS ttf into a
        // System.Drawing.Font so the right-click menu matches the window font.
        static System.Drawing.Font _trayFont;
        static System.Drawing.Font TrayFont()
        {
            if (_trayFont != null) return _trayFont;
            try
            {
                var dir = ResolveFontDir();
                if (dir != null)
                {
                    var ttf = System.IO.Path.Combine(dir, FontFileName);
                    if (System.IO.File.Exists(ttf))
                    {
                        var pfc = new System.Drawing.Text.PrivateFontCollection();
                        pfc.AddFontFile(ttf);
                        _trayFont = new System.Drawing.Font(pfc.Families[0], 9f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
                        return _trayFont;
                    }
                }
            }
            catch { }
            _trayFont = new System.Drawing.Font("Microsoft YaHei UI", 9f);
            return _trayFont;
        }

        static System.Windows.Media.Imaging.BitmapImage LoadAppImage(string fileName)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                var path = System.IO.Path.Combine(dir, fileName);
                if (!System.IO.File.Exists(path)) return null;
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

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
                // top-right corner of the new work area so the window is actually visible.
                double estW = Width;       // SizeToContent.Height -> Width is fixed at 280
                double estH = 200;         // upper bound estimate before layout runs
                bool offscreen =
                    sl + estW <= screen.Left + 8 || sl >= screen.Right - 8 ||
                    st + 36   <= screen.Top  + 8 || st >= screen.Bottom - 8;
                if (offscreen)
                {
                    Left = screen.Right - estW - 20;
                    Top  = screen.Top + 20;
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
            else { Left = screen.Right - Width - 20; Top = screen.Top + 20; }
            BuildUI();
            MouseWheel += (s, e) => { VolumeControl.SetVolume(VolumeControl.GetVolume() + (e.Delta > 0 ? 0.02f : -0.02f)); UpdateVolumeUI(); };
            LoadData();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _refreshTimer.Tick += (s, e) => { LoadData(); UpdateTrayIcon(); };
            _refreshTimer.Start();
            Closing += (s, ev) => { ev.Cancel = true; Hide(); };
            Loaded += OnLoaded;
            LocationChanged += (s, e) => { if (IsLoaded) { AppPrefs.SetDouble("Left", Left); AppPrefs.SetDouble("Top", Top); } };
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
                try { InitTrayIcon(); } catch { }
                try { UpdateTrayIcon(); } catch { }
                try { ClipboardHistory.Start(); } catch { }
                try { RestartAutoCleanTimer(); } catch { }
                // Register hotkey window hook
                _hotkeyHwnd = hwnd;
                System.Windows.Interop.HwndSource.FromHwnd(hwnd).AddHook(WndProc);
                RefreshHotkeys();
                _deviceWatcher = new AudioDevices.DeviceWatcher();
                _deviceWatcher.OnChange = () => Dispatcher.BeginInvoke(new Action(() => { VolumeControl.Invalidate(); LoadData(); ScheduleVolumeRefresh(); }));
                Dispatcher.BeginInvoke(new Action(() => { try { TrimWorkingSet(); } catch { } }),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                // Re-clamp window into the work area when display config changes (e.g. 4K -> 1080p,
                // monitor unplugged, DPI / scaling change). SystemEvents callbacks fire on a
                // worker thread, so always hop back to the UI dispatcher.
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                // Final safety: after the first layout pass, ActualWidth/ActualHeight are real –
                // re-clamp once so a 4K-saved position never leaves the window off-screen on 1080p.
                Dispatcher.BeginInvoke(new Action(ClampToWorkArea), DispatcherPriority.Loaded);
            }
            catch (Exception ex) { AppLog.Log("OnLoaded", ex); }
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            try { Dispatcher.BeginInvoke(new Action(ClampToWorkArea)); } catch { }
        }

        void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            // DPI / desktop changes can also move the work area on us.
            if (e.Category == Microsoft.Win32.UserPreferenceCategory.Desktop ||
                e.Category == Microsoft.Win32.UserPreferenceCategory.General)
            {
                try { Dispatcher.BeginInvoke(new Action(ClampToWorkArea)); } catch { }
            }
        }

        void ClampToWorkArea()
        {
            try
            {
                var wa = SystemParameters.WorkArea;
                double w = ActualWidth > 0 ? ActualWidth : Width;
                double h = ActualHeight > 0 ? ActualHeight : Height;
                if (double.IsNaN(w) || w <= 0) w = 280;
                if (double.IsNaN(h) || h <= 0) h = 36;
                double left = Left;
                double top = Top;
                bool offscreen = double.IsNaN(left) || double.IsNaN(top)
                    || left + w <= wa.Left + 8 || left >= wa.Right - 8
                    || top + h <= wa.Top + 8  || top >= wa.Bottom - 8;
                if (offscreen)
                {
                    // Lost display – snap back to the top-right of the new primary work area.
                    Left = wa.Right - w - 20;
                    Top  = wa.Top + 20;
                    return;
                }
                if (left + w > wa.Right)  left = wa.Right - w;
                if (top  + h > wa.Bottom) top  = wa.Bottom - h;
                if (left < wa.Left) left = wa.Left;
                if (top  < wa.Top)  top  = wa.Top;
                if (left != Left) Left = left;
                if (top  != Top)  Top  = top;
            }
            catch { }
        }

        void BuildUI()
        {
            var mainBorder = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(BgColor),
                BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(1),
                // Fluent elevation: a soft, depth-less shadow gives the floating card lift
                // without introducing any new palette colour.
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 28,
                    ShadowDepth = 0,
                    Opacity = 0.45,
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
                var dir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                var icoPath = System.IO.Path.Combine(dir, "app.ico");
                if (System.IO.File.Exists(icoPath))
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(icoPath);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    titleIcon.Source = bmp;
                }
            }
            catch { }
            var titleLabel = new TextBlock
            {
                Text = "OneBox",
                Foreground = new SolidColorBrush(TextPrimary),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleStack.Children.Add(titleIcon);
            titleStack.Children.Add(titleLabel);
            var pinBtn = new Button
            {
                Content = _lockPosition ? "\uD83D\uDD12" : "\uD83D\uDD13", FontFamily = EmojiFont,  // 🔒 locked / 🔓 unlocked
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
                pinBtn.Content = _lockPosition ? "\uD83D\uDD12" : "\uD83D\uDD13";
                pinBtn.Foreground = new SolidColorBrush(_lockPosition ? AccentColor : TextSecondary);
                if (_lockMenuItem != null) _lockMenuItem.Checked = _lockPosition;
            };
            _pinBtn = pinBtn;

            var collapseBtn = new Button
            {
                Content = "\u25B2",
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
                Content = "\u2715",
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
                string mem = ""; try { var ms = MemoryCleaner.GetStatus(); if (ms != null) mem = string.Format(System.Environment.NewLine + "内存: {0:0.0}/{1:0.0} GB ({2}%)", (ms.TotalBytes - ms.AvailableBytes) / 1073741824.0, ms.TotalBytes / 1073741824.0, ms.MemoryLoadPercent); } catch { }
                tipBlock.Text = "电源计划: " + plan + System.Environment.NewLine + "音频设备: " + dev + mem;
            };
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
            StyleButton(memBtn, false);
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
            StyleButton(trBtn, false);
            trBtn.Click += (s, e) => OpenTranslateWindow(null);
            contentPanel.Children.Add(trBtn);
            }

            // ---- Launcher bar (4 quick-launch slots) ------------------------------
            BuildLauncherBar(contentPanel);

            // ---- Clipboard-history button -----------------------------------------
            BuildClipboardButton(contentPanel);

            _root.Children.Add(contentPanel);
            mainBorder.Child = _root;
            Content = mainBorder;
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
            BuildUI();
            LoadData();
            Left = left; Top = top;
        }

        // ---- Launcher bar ------------------------------------------------------
        const int LauncherSlots = 4;
        const string LauncherPrefKey = "Launcher.Paths";

        static List<string> LoadLauncherPaths()
        {
            var list = new List<string>();
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PowerAudioManager\App"))
                {
                    if (k != null)
                    {
                        var s = k.GetValue(LauncherPrefKey) as string;
                        if (!string.IsNullOrEmpty(s))
                            foreach (var p in s.Split('|')) if (p.Length > 0) list.Add(p);
                    }
                }
            }
            catch { }
            return list;
        }

        static void SaveLauncherPaths(List<string> paths)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\PowerAudioManager\App"))
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < paths.Count; i++) { if (i > 0) sb.Append('|'); sb.Append(paths[i]); }
                    k.SetValue(LauncherPrefKey, sb.ToString());
                }
            }
            catch { }
        }

        // Extract a small icon from an exe/dll/lnk for the launcher slot.
        static System.Windows.Media.ImageSource ExtractIcon(string path)
        {
            try
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (ico != null)
                {
                    var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        ico.Handle, Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch { }
            return null;
        }

        void BuildLauncherBar(StackPanel contentPanel)
        {
            if (contentPanel.Children.Count > 0) contentPanel.Children.Add(MakeDivider());
            var header = new TextBlock {
                Foreground = new SolidColorBrush(AccentColor), FontSize = 12,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
            header.Inlines.Add(new Run("🚀") { FontFamily = EmojiFont });
            header.Inlines.Add(new Run(" 快捷启动"));
            contentPanel.Children.Add(header);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var paths = LoadLauncherPaths();
            for (int i = 0; i < LauncherSlots; i++)
            {
                string p = i < paths.Count ? paths[i] : null;
                row.Children.Add(MakeLauncherSlot(i, p, paths));
            }
            contentPanel.Children.Add(row);
        }

        Button MakeLauncherSlot(int index, string path, List<string> paths)
        {
            var btn = new Button {
                Width = 44, Height = 44,
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(CardColor),
                BorderBrush = new SolidColorBrush(BorderColor),
                ToolTip = string.IsNullOrEmpty(path) ? "点击添加程序" : path
            };
            if (!string.IsNullOrEmpty(path))
            {
                var img = ExtractIcon(path);
                if (img != null)
                    btn.Content = new System.Windows.Controls.Image { Source = img, Width = 24, Height = 24 };
                else
                    btn.Content = "•";
            }
            else
            {
                btn.Content = "+";
                btn.FontSize = 18;
                btn.Foreground = new SolidColorBrush(TextSecondary);
            }
            btn.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(path))
                {
                    // Pick an executable
                    var dlg = new Microsoft.Win32.OpenFileDialog {
                        Filter = "程序|*.exe;*.lnk|所有文件|*.*",
                        Title = "选择要添加的程序" };
                    if (dlg.ShowDialog() == true)
                    {
                        while (paths.Count <= index) paths.Add("");
                        if (paths.Count > index) paths[index] = dlg.FileName; else paths.Add(dlg.FileName);
                        SaveLauncherPaths(paths);
                        RebuildUI();
                    }
                }
                else
                {
                    try { System.Diagnostics.Process.Start(path); }
                    catch (Exception ex) { AppLog.Log("Launch " + path, ex); }
                }
            };
            btn.MouseRightButtonUp += (s, e) =>
            {
                // Right-click clears the slot
                if (!string.IsNullOrEmpty(path))
                {
                    if (index < paths.Count) paths[index] = "";
                    SaveLauncherPaths(paths);
                    RebuildUI();
                    e.Handled = true;
                }
            };
            return btn;
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

        void LoadData()
        {
            try { UpdateVolumeUI(); } catch { }
            try { UpdateMemoryUI(); } catch { }
            try { UpdateTrayTooltip(); } catch { }
            if (_loading) return;
            _loading = true;
            // powercfg (/list + /getactivescheme) can take 1-3s during a policy refresh —
            // never run it on the UI dispatcher. Gather on a threadpool thread, render on UI.
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                List<PowerPlanInfo> plans = null;
                List<AudioDeviceInfo> devices = null;
                try { plans = PowerPlanService.GetPowerPlans(); } catch { }
                try { devices = AudioDevices.GetOutputDevices(); } catch { }
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
                Text = dev.Name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
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
        Border MakeDivider()
        {
            return new Border
            {
                Height = 1,
                Background = new SolidColorBrush(BorderColor),
                Margin = new Thickness(2, 12, 2, 12),
                Opacity = 0.6
            };
        }

        // Rounded button template (corner radius 6) — the single source of the Fluent pill
        // shape for every action button. Background/Border/Content/Padding all bind through.
        static ControlTemplate _roundedBtn;
        static ControlTemplate RoundedButtonTemplate()
        {
            if (_roundedBtn != null) return _roundedBtn;
            string xaml =
@"<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <Border CornerRadius='6' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>
    <ContentPresenter Margin='{TemplateBinding Padding}' HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center' RecognizesAccessKey='True'/>
  </Border>
</ControlTemplate>";
            _roundedBtn = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
            return _roundedBtn;
        }

        // Smooth Fluent hover: animate the Background brush colour instead of swapping it
        // instantly. Only applied to inactive buttons (active ones keep their accent fill).
        static void AnimateButtonBg(Button btn, Color to)
        {
            var b = btn.Background as SolidColorBrush;
            if (b == null) { btn.Background = new SolidColorBrush(to); return; }
            b.BeginAnimation(SolidColorBrush.ColorProperty,
                new System.Windows.Media.Animation.ColorAnimation(to, TimeSpan.FromMilliseconds(120)));
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
                _memStatusLabel.Text = string.Format("已用 {0:0.0} GB / {1:0.0} GB ({2}%)", used, total, s.MemoryLoadPercent);
            }
            catch { }
        }

        void CleanMemory()
        {
            if (_memStatusLabel != null) _memStatusLabel.Text = "正在清理...";
            System.Threading.ThreadPool.QueueUserWorkItem(state =>
            {
                MemoryCleaner.CleanResult r = null;
                Exception err = null;
                try { r = MemoryCleaner.CleanAll(CleanerSettingsDialog.GetSavedFlags()); }
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
                    CleanMemory();
                }
            }
            catch { }
        }

        void UpdateTrayTooltip()
        {
            if (_winFormsTray == null) return;
            string plan = "(无)", dev = "(无)";
            try { if (_powerPlans != null) { var p = _powerPlans.Find(x => x.IsActive || x.Guid == _currentPlanId); if (p != null) plan = p.Name; } } catch { }
            try { if (_audioDevices != null) { var d = _audioDevices.Find(x => x.IsDefault); if (d != null) dev = d.Name; } } catch { }
            string mem = "";
            try { var ms = MemoryCleaner.GetStatus(); if (ms != null) mem = string.Format(System.Environment.NewLine + "内存: {0:0.0}/{1:0.0} GB ({2}%)", (ms.TotalBytes - ms.AvailableBytes) / 1073741824.0, ms.TotalBytes / 1073741824.0, ms.MemoryLoadPercent); } catch { }
            string txt = "电源计划: " + plan + System.Environment.NewLine + "音频设备: " + dev + mem;
            // Truncate at the WinShell hard limit (127 wchars including null) — but .NET 4 has a stricter
            // 63-char check on the public NotifyIcon.Text setter. Use reflection to bypass it and reach
            // the underlying field, then call UpdateIcon() to refresh the tooltip.
            if (txt.Length > 127) txt = txt.Substring(0, 126) + "…";
            try
            {
                var t = typeof(System.Windows.Forms.NotifyIcon);
                var fld = t.GetField("text", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (fld != null) fld.SetValue(_winFormsTray, txt);
                var added = t.GetField("added", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                bool isAdded = added != null && (bool)added.GetValue(_winFormsTray);
                if (isAdded)
                {
                    var update = t.GetMethod("UpdateIcon", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (update != null) update.Invoke(_winFormsTray, new object[] { true });
                }
            }
            catch
            {
                // Last-resort: stay within the public 63-char limit so we at least see something
                try { _winFormsTray.Text = txt.Length > 63 ? txt.Substring(0, 60) + "…" : txt; } catch { }
            }
        }

        void StyleButton(Button btn, bool isActive)
        {
            btn.Template = RoundedButtonTemplate();
            if (isActive)
            {
                btn.Background = new SolidColorBrush(ActiveBg);
                btn.Foreground = Brushes.White;
                btn.FontWeight = FontWeights.SemiBold;
                btn.BorderBrush = new SolidColorBrush(AccentColor);
                btn.BorderThickness = new Thickness(1);
            }
            else
            {
                btn.Background = new SolidColorBrush(CardColor);
                btn.Foreground = new SolidColorBrush(TextSecondary);
                btn.FontWeight = FontWeights.Normal;
                btn.BorderBrush = new SolidColorBrush(BorderColor);
                btn.BorderThickness = new Thickness(1);
            }
            if (!isActive)
            {
                btn.MouseEnter += (s, e) => AnimateButtonBg(btn, HoverColor);
                btn.MouseLeave += (s, e) => AnimateButtonBg(btn, CardColor);
            }
        }


        private void ShowWindow()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            // Honor the user's topmost preference – do NOT force-pin every time we show.
            if (_topmost) { Topmost = false; Topmost = true; }
        }


        static bool IsAutoStartEnabled()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    return key != null && key.GetValue("OneBox") != null;
                }
            }
            catch { return false; }
        }

        static void ToggleAutoStart(bool enable)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (enable)
                        key.SetValue("OneBox",
                            System.Reflection.Assembly.GetExecutingAssembly().Location);
                    else
                        key.DeleteValue("OneBox", false);
                }
            }
            catch { }
        }

        #region Native Tray Icon

        void InitTrayIcon()
        {
            try
            {
                _winFormsTray = new System.Windows.Forms.NotifyIcon
                {
                    Icon = System.Drawing.Icon.FromHandle(CreateTrayIconHandle()),
                    Text = "OneBox",
                    Visible = true
                };
                _trayMenu = new System.Windows.Forms.ContextMenuStrip();
                _trayMenu.Font = TrayFont(); // HarmonyOS Sans SC — match the window font
                // Match the floating window's dark-purple theme on the WinForms tray menu.
                _trayMenu.RenderMode = System.Windows.Forms.ToolStripRenderMode.ManagerRenderMode;
                _trayMenu.Renderer = new System.Windows.Forms.ToolStripProfessionalRenderer(new OneBoxMenuColorTable());
                _trayMenu.BackColor = System.Drawing.Color.FromArgb(28, 26, 40);   // BgColor
                _trayMenu.ForeColor = System.Drawing.Color.White;                    // TextPrimary
                _trayMenu.ShowImageMargin = false;
                _trayMenu.Padding = new System.Windows.Forms.Padding(2, 4, 2, 4);
                _trayMenu.Items.Add("显示窗口", null, (s, e) => ShowWindow());
                var autoItem = new System.Windows.Forms.ToolStripMenuItem("开机自启") { CheckOnClick = true, Checked = IsAutoStartEnabled() };
                autoItem.Click += (s, e) => ToggleAutoStart(autoItem.Checked);
                _trayMenu.Items.Add(autoItem);
                _trayMenu.Opening += (s, e) => autoItem.Checked = IsAutoStartEnabled();
                _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                _topmostMenuItem = new System.Windows.Forms.ToolStripMenuItem("窗口置顶") { CheckOnClick = true, Checked = _topmost };
                _topmostMenuItem.Click += (s, e) => { _topmost = _topmostMenuItem.Checked; Topmost = _topmost; AppPrefs.SetBool("Topmost", _topmost); };
                _trayMenu.Items.Insert(_trayMenu.Items.Count - 1, _topmostMenuItem);
                _lockMenuItem = new System.Windows.Forms.ToolStripMenuItem("锁定位置") { CheckOnClick = true, Checked = _lockPosition };
                _lockMenuItem.Click += (s, e) => { _lockPosition = _lockMenuItem.Checked; AppPrefs.SetBool("LockPosition", _lockPosition); if (_pinBtn != null) { _pinBtn.Content = _lockPosition ? "\uD83D\uDD12" : "\uD83D\uDD13"; _pinBtn.Foreground = new SolidColorBrush(_lockPosition ? AccentColor : TextSecondary); } };
                _trayMenu.Items.Insert(_trayMenu.Items.Count - 1, _lockMenuItem);
                var hiddenSub = new System.Windows.Forms.ToolStripMenuItem("显示已隐藏设备");
                // Force the submenu drop-down to inherit the dark theme (it does not
                // pick up BackColor/ForeColor from the parent ContextMenuStrip).
                hiddenSub.DropDown.BackColor = System.Drawing.Color.FromArgb(28, 26, 40);
                hiddenSub.DropDown.ForeColor = System.Drawing.Color.White;
                _trayMenu.Items.Insert(_trayMenu.Items.Count - 1, hiddenSub);
                _trayMenu.Opening += (s, e) => {
                    hiddenSub.DropDownItems.Clear();
                    var devs = AudioDevices.GetOutputDevices();
                    bool any = false;
                    foreach (var d in devs) if (d.IsHidden) {
                        any = true;
                        var copy = d;
                        var mi = new System.Windows.Forms.ToolStripMenuItem(d.Name);
                        mi.ForeColor = System.Drawing.Color.White;
                        mi.Click += (ss, ee) => { DevicePrefs.SetHidden(copy.Name, false); LoadData(); };
                        hiddenSub.DropDownItems.Add(mi);
                    }
                    hiddenSub.Visible = any;
                };
                _trayMenu.Items.Insert(_trayMenu.Items.Count - 1, new System.Windows.Forms.ToolStripSeparator());
                _trayMenu.Items.Insert(_trayMenu.Items.Count - 1,
                    new System.Windows.Forms.ToolStripMenuItem("清理内存", null, (s, e) => CleanMemory()));
                _trayMenu.Items.Insert(_trayMenu.Items.Count - 1,
                    new System.Windows.Forms.ToolStripMenuItem("内存清理设置...", null,
                        (ss, ee) => { ShowWindow(); CleanerSettingsDialog.Show(this); }));
                _trayMenu.Items.Insert(_trayMenu.Items.Count - 1,
                    new System.Windows.Forms.ToolStripMenuItem("板块设置...", null,
                        (ss, ee) => { ShowWindow(); ModulesSettingsDialog.Show(this); }));
                _trayMenu.Items.Add("退出", null, (s, e) => {
                    if (_deviceWatcher != null) _deviceWatcher.Stop();
                    try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch { }
                    try { Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; } catch { }
                    if (_winFormsTray != null) { _winFormsTray.Visible = false; _winFormsTray.Dispose(); }
                    Application.Current.Shutdown();
                });
                _winFormsTray.ContextMenuStrip = _trayMenu;
                _winFormsTray.MouseUp += (s, e) => {
                    if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowWindow();
                    else if (e.Button == System.Windows.Forms.MouseButtons.Middle) CleanMemory();
                };
            }
            catch
            {
            }
        }

        // ---- Dynamic tray icon --------------------------------------------------
        // Renders the "闪电" (bolt) logo as a 32x32 bitmap, recoloured by current
        // memory load: green < 60%, amber 60–80%, red > 80%. Called on every refresh
        // tick so the tray reflects live system pressure.
        System.Drawing.Icon _dynIcon;
        int _lastLoadBucket = -1; // -1 forces a redraw on first call

        static System.Drawing.Color LoadColor(uint load)
        {
            if (load >= 80) return System.Drawing.Color.FromArgb(232, 93, 93);   // red
            if (load >= 60) return System.Drawing.Color.FromArgb(240, 180, 80);  // amber
            return System.Drawing.Color.FromArgb(120, 200, 130);                 // green
        }

        System.Drawing.Icon BuildDynamicIcon(uint load)
        {
            var bmp = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                var color = LoadColor(load);
                // Bolt polygon, centred in 32x32.
                var pts = new System.Drawing.PointF[] {
                    new System.Drawing.PointF(19f, 3f),
                    new System.Drawing.PointF(8f, 18f),
                    new System.Drawing.PointF(15f, 18f),
                    new System.Drawing.PointF(13f, 29f),
                    new System.Drawing.PointF(24f, 13f),
                    new System.Drawing.PointF(17f, 13f)
                };
                using (var brush = new System.Drawing.SolidBrush(color))
                    g.FillPolygon(brush, pts);
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 255, 255), 1.2f))
                    g.DrawPolygon(pen, pts);
            }
            var hicon = bmp.GetHicon();
            var icon = System.Drawing.Icon.FromHandle(hicon);
            return icon;
        }

        void UpdateTrayIcon()
        {
            try
            {
                if (_winFormsTray == null) return;
                uint load = 0;
                var s = MemoryCleaner.GetStatus();
                if (s != null) load = s.MemoryLoadPercent;
                int bucket = load >= 80 ? 2 : (load >= 60 ? 1 : 0);
                if (bucket == _lastLoadBucket && _dynIcon != null) return; // unchanged
                _lastLoadBucket = bucket;
                var old = _dynIcon;
                _dynIcon = BuildDynamicIcon(load);
                _winFormsTray.Icon = _dynIcon;
                if (old != null) { try { old.Dispose(); } catch { } }
                UpdateTrayTooltip();
            }
            catch (Exception ex) { AppLog.Log("UpdateTrayIcon", ex); }
        }

        IntPtr CreateTrayIconHandle()
        {
            // Initial icon (green bucket). Subsequent updates go through UpdateTrayIcon.
            try
            {
                if (_dynIcon == null) _dynIcon = BuildDynamicIcon(0);
                return _dynIcon.Handle;
            }
            catch { }
            return new System.Drawing.Bitmap(32, 32).GetHicon();
        }

        // WinForms ProfessionalColorTable that mirrors the WPF floating window's
        // dark-purple palette (BgColor/CardColor/Hover/Accent/Border) so the tray
        // right-click menu looks like the window, not the default light OS menu.
        class OneBoxMenuColorTable : System.Windows.Forms.ProfessionalColorTable
        {
            static readonly System.Drawing.Color Bg     = System.Drawing.Color.FromArgb(28, 26, 40);   // BgColor #1C1A28
            static readonly System.Drawing.Color Title  = System.Drawing.Color.FromArgb(34, 32, 50);   // title bar #222132
            static readonly System.Drawing.Color Card   = System.Drawing.Color.FromArgb(42, 39, 60);   // CardColor #2A273C
            static readonly System.Drawing.Color Hover  = System.Drawing.Color.FromArgb(58, 54, 84);   // HoverColor #3A3654
            static readonly System.Drawing.Color Active = System.Drawing.Color.FromArgb(110, 105, 200);// ActiveBg #6E69C8
            static readonly System.Drawing.Color Accent = System.Drawing.Color.FromArgb(142, 140, 216);// Accent #8E8CD8
            static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(80, 75, 120);  // BorderColor #504F78

            public override System.Drawing.Color MenuBorder { get { return Border; } }
            public override System.Drawing.Color MenuItemBorder { get { return Accent; } }
            public override System.Drawing.Color MenuItemSelected { get { return Hover; } }
            public override System.Drawing.Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override System.Drawing.Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override System.Drawing.Color MenuItemPressedGradientBegin { get { return Active; } }
            public override System.Drawing.Color MenuItemPressedGradientMiddle { get { return Active; } }
            public override System.Drawing.Color MenuItemPressedGradientEnd { get { return Active; } }
            public override System.Drawing.Color MenuStripGradientBegin { get { return Bg; } }
            public override System.Drawing.Color MenuStripGradientEnd { get { return Bg; } }
            public override System.Drawing.Color ToolStripGradientBegin { get { return Title; } }
            public override System.Drawing.Color ToolStripGradientMiddle { get { return Title; } }
            public override System.Drawing.Color ToolStripGradientEnd { get { return Bg; } }
            public override System.Drawing.Color ImageMarginGradientBegin { get { return Card; } }
            public override System.Drawing.Color ImageMarginGradientMiddle { get { return Card; } }
            public override System.Drawing.Color ImageMarginGradientEnd { get { return Card; } }
            public override System.Drawing.Color SeparatorDark { get { return Border; } }
            public override System.Drawing.Color SeparatorLight { get { return Card; } }
            public override System.Drawing.Color CheckBackground { get { return Active; } }
            public override System.Drawing.Color CheckPressedBackground { get { return Active; } }
            public override System.Drawing.Color CheckSelectedBackground { get { return Hover; } }
            public override System.Drawing.Color ButtonSelectedHighlight { get { return Hover; } }
            public override System.Drawing.Color ButtonSelectedGradientBegin { get { return Hover; } }
            public override System.Drawing.Color ButtonSelectedGradientEnd { get { return Hover; } }
            public override System.Drawing.Color ButtonPressedGradientBegin { get { return Active; } }
            public override System.Drawing.Color ButtonPressedGradientMiddle { get { return Active; } }
            public override System.Drawing.Color ButtonPressedGradientEnd { get { return Active; } }
            public override System.Drawing.Color StatusStripGradientBegin { get { return Bg; } }
            public override System.Drawing.Color StatusStripGradientEnd { get { return Bg; } }
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


        #endregion

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
        }

        void RefreshHotkeys()
        {
            if (_hotkeyHwnd == IntPtr.Zero) return;
            // Unregister all known IDs
            foreach (var id in _hotkeyMap.Keys) Native.UnregisterHotKey(_hotkeyHwnd, id);
            _hotkeyMap.Clear();
            // Translate hotkey: Ctrl+Shift+T (VK_T = 0x54)
            Native.UnregisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_TRANSLATE);
            Native.RegisterHotKey(_hotkeyHwnd, Native.HOTKEY_ID_TRANSLATE, Native.MOD_CONTROL | Native.MOD_SHIFT, 0x54);
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
            var btn = (Button)sender;
            // Pin bottom edge so the window collapses upward
            double bottom = Top + ActualHeight;
            _isExpanded = !_isExpanded;
            if (_isExpanded)
            {
                btn.Content = "\u25BC"; // pointing down: click to collapse
                if (_contentPanel != null) _contentPanel.Visibility = Visibility.Visible;
                SizeToContent = SizeToContent.Height;
            }
            else
            {
                btn.Content = "\u25B2"; // pointing up: click to expand
                if (_contentPanel != null) _contentPanel.Visibility = Visibility.Collapsed;
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
        }
    }

}
