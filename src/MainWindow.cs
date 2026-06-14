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
        private double _expandedHeight = 520;
        private System.Windows.Forms.NotifyIcon _winFormsTray;
        private bool _topmost = true;
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

        static System.Windows.Media.FontFamily LoadAppFont()
        {
            try
            {
                var path = @"C:\Users\LIUxy\OneDrive\Documents\tools\美化与字体\HarmonyOS-Sans\HarmonyOS Sans\HarmonyOS_Sans_SC\";
                if (System.IO.Directory.Exists(path))
                    return new System.Windows.Media.FontFamily(new Uri(path), "./#HarmonyOS Sans SC");
            }
            catch { }
            return new System.Windows.Media.FontFamily("Microsoft YaHei UI");
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
            _topmost = AppPrefs.GetBool("Topmost", true);
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
            { Left = sl; Top = st; }
            else { Left = screen.Right - Width - 20; Top = screen.Top + 20; }
            BuildUI();
            MouseWheel += (s, e) => { VolumeControl.SetVolume(VolumeControl.GetVolume() + (e.Delta > 0 ? 0.02f : -0.02f)); UpdateVolumeUI(); };
            LoadData();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _refreshTimer.Tick += (s, e) => LoadData();
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
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                try { InitTrayIcon(); } catch { }
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
            }
            catch
            {
            }
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
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(BgColor),
                BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(1)
            };
            _root = new StackPanel();
            var titleBar = new DockPanel
            {
                Background = new SolidColorBrush(Color.FromRgb(34, 32, 50)),
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
            _root.Children.Add(titleBar);

            var contentPanel = new StackPanel { Margin = new Thickness(12, 8, 12, 12) };
            _contentPanel = contentPanel;

            var powerHeader = MakeCollapsibleHeader("电源计划", "icon-power.png", () => _powerSection, AppPrefs.GetBool("UI.PowerCollapsed", false));
            contentPanel.Children.Add(powerHeader);
            _powerSection = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            contentPanel.Children.Add(_powerSection);

            contentPanel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(BorderColor),
                Margin = new Thickness(0, 0, 0, 12)
            });

            var audioHeader = MakeCollapsibleHeader("音频输出", "icon-audio.png", () => _audioSection, AppPrefs.GetBool("UI.AudioCollapsed", false));
            contentPanel.Children.Add(audioHeader);
            _audioSection = new StackPanel();
            contentPanel.Children.Add(_audioSection);

            // Volume row
            var volRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
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

            // Memory section
            contentPanel.Children.Add(new Border {
                Height = 1,
                Background = new SolidColorBrush(BorderColor),
                Margin = new Thickness(0, 12, 0, 12)
            });
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

            // Translate section
            contentPanel.Children.Add(new Border {
                Height = 1,
                Background = new SolidColorBrush(BorderColor),
                Margin = new Thickness(0, 12, 0, 12)
            });
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

            _root.Children.Add(contentPanel);
            mainBorder.Child = _root;
            Content = mainBorder;
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
        void LoadData()
        {
            try { UpdateVolumeUI(); } catch { }
            try { UpdateMemoryUI(); } catch { }
            try { UpdateTrayTooltip(); } catch { }
            try
            {
                _powerPlans = PowerPlanService.GetPowerPlans();
                var active = _powerPlans.Find(p => p.IsActive);
                if (active != null) _currentPlanId = active.Guid;
                _powerSection.Children.Clear();
                if (_powerPlans == null || _powerPlans.Count == 0)
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

            try
            {
                _audioDevices = AudioDevices.GetOutputDevices();
                if (_audioDevices == null) _audioDevices = new List<AudioDeviceInfo>();
                var defaultDev = _audioDevices.Find(d => d.IsDefault);
                if (defaultDev != null) _currentDeviceId = defaultDev.Id;
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
                if (PowerPlanService.SetActivePlan(plan.Guid))
                {
                    _currentPlanId = plan.Guid;
                    LoadData();
                }
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
            string xaml = 
@"<ControlTemplate TargetType='Slider' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Grid VerticalAlignment='Center' Height='20'>
    <Border Height='4' VerticalAlignment='Center' CornerRadius='2'
            Background='{TemplateBinding Background}'/>
    <Track x:Name='PART_Track'>
      <Track.DecreaseRepeatButton>
        <RepeatButton IsTabStop='False' Focusable='False'
          Command='{x:Static Slider.DecreaseLarge}'>
          <RepeatButton.Template>
            <ControlTemplate TargetType='RepeatButton'>
              <Border Height='4' VerticalAlignment='Center' CornerRadius='2'
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
              <Ellipse Width='12' Height='12' Fill='White'/>
            </ControlTemplate>
          </Thumb.Template>
        </Thumb>
      </Track.Thumb>
    </Track>
  </Grid>
</ControlTemplate>";
            return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
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
            btn.MouseEnter += (s, e) => {
                if (!isActive) btn.Background = new SolidColorBrush(HoverColor);
            };
            btn.MouseLeave += (s, e) => {
                if (!isActive) btn.Background = new SolidColorBrush(CardColor);
            };
        }


        private void ShowWindow()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = false; Topmost = true;
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




        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        static extern bool IsWindow(IntPtr hWnd);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr GetModuleHandle(string name);

        const uint NIM_ADD = 0;
        const uint NIM_MODIFY = 1;
        const uint NIM_DELETE = 2;
        const uint NIF_MESSAGE = 1;
        const uint NIF_ICON = 2;
        const uint NIF_TIP = 4;
        const uint IMAGE_ICON = 1;
        const uint LR_LOADFROMFILE = 0x10;
        const uint LR_DEFAULTSIZE = 0x40;
        const int WM_TRAYICON = 0x8001;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

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
                _trayMenu.Items.Insert(_trayMenu.Items.Count - 1, hiddenSub);
                _trayMenu.Opening += (s, e) => {
                    hiddenSub.DropDownItems.Clear();
                    var devs = AudioDevices.GetOutputDevices();
                    bool any = false;
                    foreach (var d in devs) if (d.IsHidden) {
                        any = true;
                        var copy = d;
                        var mi = new System.Windows.Forms.ToolStripMenuItem(d.Name);
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

        IntPtr CreateTrayIconHandle()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                var icoPath = System.IO.Path.Combine(dir, "app.ico");
                if (System.IO.File.Exists(icoPath))
                    return new System.Drawing.Icon(icoPath, 32, 32).Handle;
                var pngPath = System.IO.Path.Combine(dir, "app.png");
                if (System.IO.File.Exists(pngPath))
                    return new System.Drawing.Bitmap(pngPath).GetHicon();
            }
            catch { }
            // Fallback: blank icon
            return new System.Drawing.Bitmap(32, 32).GetHicon();
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_ID_TRANSLATE)
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
            if (msg == WM_TRAYICON)
            {
                int evt = lParam.ToInt32();
                if (evt == 0x0202 || evt == 0x0203) ShowWindow(); // L-click or dblclk
                handled = true;
            }
            return IntPtr.Zero;
        }


        #endregion

        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr hProcess, int min, int max);

        void TrimWorkingSet()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        }
        [DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2;
        const int WM_HOTKEY = 0x0312;
        const int HOTKEY_ID_BASE = 0xB000;
        const int HOTKEY_ID_TRANSLATE = 0xBFFF;
        Dictionary<int, string> _hotkeyMap = new Dictionary<int, string>();
        IntPtr _hotkeyHwnd = IntPtr.Zero;

        void UnregisterAllHotkeys()
        {
            if (_hotkeyHwnd == IntPtr.Zero) return;
            foreach (var id in _hotkeyMap.Keys) UnregisterHotKey(_hotkeyHwnd, id);
            _hotkeyMap.Clear();
            UnregisterHotKey(_hotkeyHwnd, HOTKEY_ID_TRANSLATE);
        }

        void RefreshHotkeys()
        {
            if (_hotkeyHwnd == IntPtr.Zero) return;
            // Unregister all known IDs
            foreach (var id in _hotkeyMap.Keys) UnregisterHotKey(_hotkeyHwnd, id);
            _hotkeyMap.Clear();
            // Translate hotkey: Ctrl+Shift+T (VK_T = 0x54)
            UnregisterHotKey(_hotkeyHwnd, HOTKEY_ID_TRANSLATE);
            RegisterHotKey(_hotkeyHwnd, HOTKEY_ID_TRANSLATE, MOD_CONTROL | 0x4, 0x54);
            int nextId = HOTKEY_ID_BASE;
            foreach (var kv in DevicePrefs.GetAllHotkeys())
            {
                int encoded = kv.Value;
                if (encoded == 0) continue;
                int mods = (encoded >> 16) & 0xFFFF;
                uint vk = (uint)(encoded & 0xFFFF);
                uint winMods = 0;
                if ((mods & 1) != 0) winMods |= MOD_ALT;
                if ((mods & 2) != 0) winMods |= MOD_CONTROL;
                if ((mods & 4) != 0) winMods |= 0x4; // MOD_SHIFT
                if ((mods & 8) != 0) winMods |= 0x8; // MOD_WIN
                int id = nextId++;
                if (RegisterHotKey(_hotkeyHwnd, id, winMods, vk))
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
