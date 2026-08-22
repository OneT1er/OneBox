using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Documents;
using System.Windows.Threading;
using System.IO;
using Microsoft.Win32;
using PowerAudioManager.Commands;

namespace PowerAudioManager
{
    public sealed class ExitLifecycleGate
    {
        int _started;

        public bool TryBegin()
        {
            return System.Threading.Interlocked.CompareExchange(ref _started, 1, 0) == 0;
        }

        public bool IsStarted => System.Threading.Volatile.Read(ref _started) != 0;
    }

    // 悬浮窗主窗口。按职责拆分到 partial 文件：
    //   MainWindow.UI / Data / Memory / Monitor / Translate / Hotkeys / Collapse
    public partial class MainWindow : Window
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

        // 温度/性能监控
        private TextBlock _collapsedTempLabel;
        private Panel _metricRow;                // 展开视图的指标行 (WrapPanel)
        private List<TextBlock> _metricValBlocks;  // 每个指标的数值 TextBlock（复用，每秒只改 Text/Foreground）
        private List<string> _metricKeys;          // 上次构建时的 ConfigKey 序列；集合变化才重建结构
        private System.Threading.Timer _tempTimer;

        // 后台数据加载进行中标记。防止多个调用方（设备监视器、刷新定时器、点击）短时间内重复触发 powercfg。
        bool _loading;
        DateTime _loadStartTime;

        TranslateWindow _translateWindow;
        Dictionary<int, string> _hotkeyMap = new Dictionary<int, string>();
        IntPtr _hotkeyHwnd = IntPtr.Zero;
        DispatcherTimer _autoCollapseTimer;
        volatile bool _isExiting;
        readonly ExitLifecycleGate _exitLifecycle = new ExitLifecycleGate();
        System.Windows.Interop.HwndSource _hwndSource;

        // 字体与图片加载统一走 AppResources，避免依赖当前工作目录。
        static FontFamily AppFont { get { return AppResources.AppFont; } }
        static BitmapImage LoadAppImage(string fileName) { return AppResources.LoadAppImage(fileName); }

        public MainWindow()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            InitializeCommands();
            _topmost = AppPrefs.Get(PreferenceKeys.Window.Topmost);
            _lockPosition = AppPrefs.Get(PreferenceKeys.Window.LockPosition);
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
            MouseWheel += (s, e) => _ = ExecuteCommandAsync(AppCommandId.AudioSetVolume,
                CommandSource.MainWindow, new AudioVolumePayload(Math.Clamp(
                    VolumeControl.GetVolume() + (e.Delta > 0 ? 0.02f : -0.02f), 0, 1)));
            // LoadData() 推迟到 OnLoaded 异步执行，避免 GetStatus() 首次创建 PerformanceCounter（~300ms）阻塞构造函数。
            AppLog.Log("Startup", "ctor done " + sw.ElapsedMilliseconds + "ms");
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _refreshTimer.Tick += (s, e) => { LoadData(); if (_tray != null) _tray.UpdateIcon(); try { _scaling.ApplyScaling(); _scaling.Reposition(); } catch { } };
            _refreshTimer.Start();
            // 2s 轮询分辨率/DPI 变化。SystemEvents.DisplaySettingsChanged 在 DPI 切换时不可靠，直接监视屏幕宽度。
            _screenPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _screenPoll.Tick += (s, e) => { try { if (_scaling != null) _scaling.ApplyScaling(); } catch { } };
            _screenPoll.Start();
            Closing += OnWindowClosing;
            Loaded += OnLoaded;
            LocationChanged += (s, e) => { if (IsLoaded) SavePosition(); };
        }

        void SavePosition()
        {
            try
            {
                AppPrefs.SetDouble("Left", Left);
                AppPrefs.SetDouble("Top", Top);
            }
            catch { }
        }

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
                _ = RunStartupUpdateCheckAsync();
                try { RestartAutoCleanTimer(); } catch { }
                try { StartAutoCollapse(); } catch { }
                _hotkeyHwnd = hwnd;
                _hwndSource = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                _hwndSource?.AddHook(WndProc);
                RefreshHotkeys();
                _deviceWatcher = new AudioDevices.DeviceWatcher();
                _deviceWatcher.OnChange = () =>
                {
                    if (_isExiting || Dispatcher.HasShutdownStarted) return;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_isExiting) return;
                        VolumeControl.Invalidate(); LoadData(); ScheduleVolumeRefresh();
                    }));
                };
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
                // 原先用 ApplicationIdle，但 .NET 10 冷启动时派发器数秒不进入 Idle，导致 ~6s 延迟。
                // 改用 threading timer 确定性地 50ms 后触发，通过 BeginInvoke 回到 UI 线程。
                _initLoadTimer = new System.Threading.Timer(_ =>
                {
                    if (_isExiting || Dispatcher.HasShutdownStarted) return;
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { LoadData(); } catch (Exception ex) { AppLog.Log("Startup LoadData", ex); }
                        }));
                    }
                    catch (Exception ex) { AppLog.Log("Startup LoadData dispatch", ex); }
                }, null, 50, System.Threading.Timeout.Infinite);
                // 服务状态检查可能触发 runas/UAC。绝不能在 Loaded UI 线程同步等待，
                // 否则 UAC 被用户延后或关闭时，整个窗口（包括托盘和性能指标）都会冻结。
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    if (_isExiting) return;
                    try { EnsureServiceRunning(); }
                    catch (Exception ex) { AppLog.Log("Service", "background startup check: " + ex.Message); }
                });
                // 温度监控启动（后台初始化硬件传感器）
                try { StartTempMonitor(); } catch { }
                AppLog.Log("Startup", "OnLoaded done " + sw.ElapsedMilliseconds + "ms");
            }
            catch (Exception ex) { AppLog.Log("OnLoaded", ex); }
        }

        public static bool ModuleVisible(string module)
        {
            switch (module)
            {
                case "Power": return AppPrefs.Get(PreferenceKeys.Modules.Power);
                case "Audio": return AppPrefs.Get(PreferenceKeys.Modules.Audio);
                case "Mem": return AppPrefs.Get(PreferenceKeys.Modules.Memory);
                case "Translate": return AppPrefs.Get(PreferenceKeys.Modules.Translate);
                case "Launcher": return AppPrefs.Get(PreferenceKeys.Modules.Launcher);
                case "Clipboard": return AppPrefs.Get(PreferenceKeys.Modules.Clipboard);
                case "Gallery": return AppPrefs.Get(PreferenceKeys.Modules.Gallery);
                case "Temp": return AppPrefs.Get(PreferenceKeys.Modules.Monitor);
                default: return true;
            }
        }

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
            if (IsLoaded) RestartTempTimer();
            Left = left; Top = top;
        }

        internal void ApplyFont()
        {
            FontFamily = AppResources.ReloadFont();
            RebuildUI();
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
            if (!_exitLifecycle.TryBegin()) return;
            _isExiting = true;
            // Prevent a trailing throttled slider value from being dispatched
            // while the window and audio endpoint are shutting down.
            try { CancelPendingVolumeCommand(); } catch { }
            try { _refreshTimer?.Stop(); } catch { }
            try { _screenPoll?.Stop(); } catch { }
            try { _autoCleanTimer?.Stop(); } catch { }
            try { _autoCollapseTimer?.Stop(); } catch { }
            var initTimer = System.Threading.Interlocked.Exchange(ref _initLoadTimer, null);
            try { initTimer?.Dispose(); } catch { }
            try { StopTempMonitor(); } catch { }
            try { ScreenshotService.Shutdown(); } catch { }
            try { ClipboardHistory.Stop(); } catch { }
            try { UnregisterAllHotkeys(); } catch { }
            try { _hwndSource?.RemoveHook(WndProc); } catch { }
            _hwndSource = null;
            _hotkeyHwnd = IntPtr.Zero;
            try { PerfHistory.Save(); } catch { }
            try { ForegroundHistory.Save(); } catch { }
            try { ForegroundHistory.Stop(); } catch { }
            try { _deviceWatcher?.Stop(); } catch { }
            _deviceWatcher = null;
            try { VolumeControl.Shutdown(); } catch { }
            if (_scaling != null)
            {
                try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= _scaling.OnDisplaySettingsChanged; } catch { }
                try { Microsoft.Win32.SystemEvents.UserPreferenceChanged -= _scaling.OnUserPreferenceChanged; } catch { }
            }
            try { _translateWindow?.Close(); } catch { }
            _translateWindow = null;
            try { _tray?.Dispose(); } catch { }
            _tray = null;
            Application.Current?.Shutdown();
        }

        void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (_isExiting)
            {
                e.Cancel = false;
                return;
            }
            e.Cancel = true;
            Hide();
        }

        void TrimWorkingSet()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Native.SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        }
    }
}

