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
using System.Windows.Threading;
using System.IO;
using Microsoft.Win32;

namespace PowerAudioManager
{
    internal static class Native
    {
        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    }

    public class PowerPlanInfo
    {
        public string Guid { get; set; }
        public string Name { get; set; }
        public bool IsActive { get; set; }
    }

    public class AudioDeviceInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsDefault { get; set; }
    }

    public static class PowerPlanService
    {
        public static List<PowerPlanInfo> GetPowerPlans()
        {
            var plans = new List<PowerPlanInfo>();
            try
            {
                var psi = new ProcessStartInfo("powercfg", "/list")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.GetEncoding(936)
                };
                using (var proc = Process.Start(psi))
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    var activeGuid = GetActivePlanGuid();

                    foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!line.Contains("GUID:")) continue;
                        int idx = line.IndexOf("GUID:");
                        if (idx < 0) continue;
                        var rest = line.Substring(idx + 5).Trim();
                        var guid = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
                        var parenStart = line.LastIndexOf('(');
                        var parenEnd = line.LastIndexOf(')');
                        if (parenStart < 0 || parenEnd <= parenStart) continue;
                        var name = line.Substring(parenStart + 1, parenEnd - parenStart - 1);
                        plans.Add(new PowerPlanInfo
                        {
                            Guid = guid,
                            Name = name,
                            IsActive = guid.Equals(activeGuid, StringComparison.OrdinalIgnoreCase)
                        });
                    }
                }
            }
            catch { }
            return plans;
        }

        public static string GetActivePlanGuid()
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", "/getactivescheme")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.GetEncoding(936)
                };
                using (var proc = Process.Start(psi))
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    var idx = output.IndexOf("GUID:");
                    if (idx >= 0)
                    {
                        var sub = output.Substring(idx + 5).Trim();
                        return sub.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    }
                }
            }
            catch { }
            return "";
        }

        public static bool SetActivePlan(string planGuid)
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", "/setactive " + planGuid)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    proc.WaitForExit();
                    return proc.ExitCode == 0;
                }
            }
            catch { return false; }
        }
    }
    public static class AudioDevices
    {
        [DllImport("winmm.dll")]
        private static extern int waveOutGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern int waveOutGetDevCaps(int deviceIndex, ref WAVEOUTCAPS caps, int size);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct WAVEOUTCAPS
        {
            public short wMid; public short wPid; public int vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public int dwFormats; public short wChannels; public short wReserved1; public int dwSupport;
        }

        #region PolicyConfig COM

        [ComImport]
        [Guid("568b9108-44bf-40b4-9006-86afe5b5a620")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfigVista
        {
            int GetMixFormat(string deviceID, IntPtr format);
            int GetDeviceFormat(string deviceID, int defaultFormat, IntPtr format);
            int SetDeviceFormat(string deviceID, IntPtr format, IntPtr endpointFormat);
            int GetProcessingPeriod(string deviceID, int defaultPeriod, out long period, out long minPeriod);
            int SetProcessingPeriod(string deviceID, ref long period);
            int GetShareMode(string deviceID, out int mode);
            int SetShareMode(string deviceID, ref int mode);
            int GetDevicePeriod(string deviceID, int defaultPeriod, out long period, out long minPeriod);
            int SetDevicePeriod(string deviceID, ref long period);
            int SetDefaultEndpoint(string deviceID, int role);
            int SetEndpointVisibility(string deviceID, int visible);
        }

        [ComImport]
        [Guid("294935CE-F637-4E7C-A41B-AB255460B862")]
        private class CPolicyConfigVistaClient { }

        #endregion

        private const int eConsole = 0;
        private const int eMultimedia = 1;
        private const int eCommunications = 2;

        public static List<AudioDeviceInfo> GetOutputDevices()
        {
            var result = new List<AudioDeviceInfo>();
            try
            {
                int numDevs = waveOutGetNumDevs();
                for (int i = 0; i < numDevs; i++)
                {
                    var caps = new WAVEOUTCAPS();
                    int sz = Marshal.SizeOf(typeof(WAVEOUTCAPS));
                    if (waveOutGetDevCaps(i, ref caps, sz) == 0)
                    {
                        string name = !string.IsNullOrEmpty(caps.szPname) ? caps.szPname : ("Output " + (i + 1));
                        result.Add(new AudioDeviceInfo
                        {
                            Id = i.ToString(),
                            Name = name,
                            IsDefault = (i == 0)
                        });
                    }
                }
            }
            catch { }

            if (result.Count == 0)
            {
                result.Add(new AudioDeviceInfo
                {
                    Id = "default",
                    Name = "Default Audio Output",
                    IsDefault = true
                });
            }
            return result;
        }

        public static bool SetDefaultDevice(string deviceName)
        {
            try
            {
                string endpointId = FindEndpointIdByName(deviceName);
                if (string.IsNullOrEmpty(endpointId))
                    return false;

                var config = (IPolicyConfigVista)new CPolicyConfigVistaClient();
                config.SetDefaultEndpoint(endpointId, eConsole);
                config.SetDefaultEndpoint(endpointId, eMultimedia);
                config.SetDefaultEndpoint(endpointId, eCommunications);
                Marshal.ReleaseComObject(config);
                return true;
            }
            catch { }
            return false;
        }

        private static string FindEndpointIdByName(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
                return null;

            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render"))
                {
                    if (key == null) return null;

                    string bestMatchId = null;
                    int bestScore = 0;

                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using (var subKey = key.OpenSubKey(subKeyName))
                        {
                            if (subKey == null) continue;
                            using (var propsKey = subKey.OpenSubKey("Properties"))
                            {
                                if (propsKey == null) continue;

                                string regName = propsKey.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string;
                                if (string.IsNullOrEmpty(regName))
                                    regName = propsKey.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},14") as string;

                                string ifName = propsKey.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string;

                                if (string.IsNullOrEmpty(regName) && string.IsNullOrEmpty(ifName))
                                    continue;

                                int score = 0;

                                if (!string.IsNullOrEmpty(ifName) &&
                                    deviceName.IndexOf(ifName, StringComparison.OrdinalIgnoreCase) >= 0)
                                    score += 100;

                                if (!string.IsNullOrEmpty(regName) &&
                                    deviceName.IndexOf(regName, StringComparison.OrdinalIgnoreCase) >= 0)
                                    score += 10;

                                if (!string.IsNullOrEmpty(regName) &&
                                    regName.IndexOf(deviceName, StringComparison.OrdinalIgnoreCase) >= 0)
                                    score += 5;

                                if (score > 0 && score > bestScore)
                                {
                                    bestScore = score;
                                    bestMatchId = "{0.0.0.00000000}." + subKeyName;
                                }
                            }
                        }
                    }
                    return bestMatchId;
                }
            }
            catch { }
            return null;
        }
    }
    public class MainWindow : Window
    {
        private List<PowerPlanInfo> _powerPlans;
        private List<AudioDeviceInfo> _audioDevices;
        private string _currentPlanId;
        private string _currentDeviceId;
        private DispatcherTimer _refreshTimer;
        private StackPanel _root;
        private StackPanel _powerSection;
        private StackPanel _audioSection;
        private bool _isExpanded = true;
        private double _expandedHeight = 520;
        private System.Windows.Forms.NotifyIcon _winFormsTray;
        private System.Windows.Forms.ContextMenuStrip _trayMenu;

        static readonly Color AccentColor = Color.FromRgb(0, 120, 215);
        static readonly Color BgColor = Color.FromRgb(32, 32, 32);
        static readonly Color CardColor = Color.FromRgb(45, 45, 45);
        static readonly Color TextPrimary = Colors.White;
        static readonly Color TextSecondary = Color.FromRgb(180, 180, 180);
        static readonly Color HoverColor = Color.FromRgb(55, 55, 55);
        static readonly Color ActiveBg = Color.FromRgb(0, 90, 170);
        static readonly Color BorderColor = Color.FromRgb(60, 60, 60);

        public MainWindow()
        {
            Title = "PAM";
            Width = 280;
            Height = _expandedHeight;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            var screen = SystemParameters.WorkArea;
            Left = screen.Right - Width - 20;
            Top = screen.Top + 20;
            BuildUI();
            LoadData();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _refreshTimer.Tick += (s, e) => LoadData();
            _refreshTimer.Start();
            Closing += (s, ev) => { ev.Cancel = true; Hide(); };
            Loaded += OnLoaded;
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
                Dispatcher.BeginInvoke(new Action(() => { try { TrimWorkingSet(); } catch { } }),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            catch
            {
            }
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
                Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                Height = 36,
                LastChildFill = true
            };
            var titleText = new TextBlock
            {
                Text = "  \u26A1 PowerAudio",
                Foreground = new SolidColorBrush(TextPrimary),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            var collapseBtn = new Button
            {
                Content = "\u2212",
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
            titleBar.Children.Add(closeBtn);
            titleBar.Children.Add(collapseBtn);
            titleBar.Children.Add(titleText);
            titleBar.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
            _root.Children.Add(titleBar);

            var contentPanel = new StackPanel { Margin = new Thickness(12, 8, 12, 12) };

            var powerHeader = new TextBlock
            {
                Text = "\u26A1 Power Plan",
                Foreground = new SolidColorBrush(AccentColor),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            contentPanel.Children.Add(powerHeader);
            _powerSection = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            contentPanel.Children.Add(_powerSection);

            contentPanel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(BorderColor),
                Margin = new Thickness(0, 0, 0, 12)
            });

            var audioHeader = new TextBlock
            {
                Text = "\uD83D\uDD0A Audio Output",
                Foreground = new SolidColorBrush(AccentColor),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            contentPanel.Children.Add(audioHeader);
            _audioSection = new StackPanel();
            contentPanel.Children.Add(_audioSection);

            _root.Children.Add(contentPanel);
            mainBorder.Child = _root;
            Content = mainBorder;
        }

        void LoadData()
        {
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
                        Text = "No power plans found",
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
                        Text = "No audio devices found",
                        Foreground = new SolidColorBrush(TextSecondary),
                        FontSize = 11
                    });
                }
                else
                {
                    foreach (var dev in _audioDevices)
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
            var btn = new Button
            {
                Content = dev.Name,
                Tag = dev.Id,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 2, 0, 2),
                FontSize = 12,
                Cursor = Cursors.Hand
            };
            StyleButton(btn, isActive);
            btn.Click += (s, e) =>
            {
                if (AudioDevices.SetDefaultDevice(dev.Name))
                {
                    _currentDeviceId = dev.Id;
                    LoadData();
                }
            };
            return btn;
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
                    return key != null && key.GetValue("PowerAudioManager") != null;
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
                        key.SetValue("PowerAudioManager",
                            System.Reflection.Assembly.GetExecutingAssembly().Location);
                    else
                        key.DeleteValue("PowerAudioManager", false);
                }
            }
            catch { }
        }

        #region Native Tray Icon
        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X; public int Y; }

        static double GetCursorPosX() { POINT p; GetCursorPos(out p); return p.X; }
        static double GetCursorPosY() { POINT p; GetCursorPos(out p); return p.Y; }


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
                    Text = "PowerAudio Manager",
                    Visible = true
                };
                _winFormsTray.MouseUp += (s, e) => {
                    if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowWindow();
                    else if (e.Button == System.Windows.Forms.MouseButtons.Right) ShowTrayContextMenu();
                };
            }
            catch
            {
            }
        }

        IntPtr CreateTrayIconHandle()
        {
            using (var ms = new System.IO.MemoryStream(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAF5SURBVFhH7VYtU8QwEF2JRCKRSCSSn4BEIpEnmEuKORySn4CsrOQnIDF3ybnKk5VImNfMwWS31yYle4o380Q7m/1I3m5C9I+5eFxfkvHXP8S3KlbtCRl/R9bXZNwnWf91gA3ZzT2tPk65i/mAQ+t3A8HG2JF1iz7x2UAV1r8POE+ncZ6MP+eup4FF1rXC4Tx2vU6S0VdeLPieHVXbCx5KAmf2120/SNdOixPCEQsLsvIvPOQvQvW5as8jWvhhfcZDB4R2k4vK85mHDjD+bcC4PNGaAhDH+IQrS9ERS3cljDRZbW7iBPCDG6nSLeIEjifAQOOf4gSW7lYYadI4GyeAWc2NNImCI2A4cCNNQvQCaneA4I6HDoAwpLEC3SsPHYBjOMYwGn0/4rbiC8qy4SFjBDHq3IjYXTGChwCFahyFaL0xwLhkEmLypSAMp044yyGKyKqcI2iiFo7T2KSdeQrQOujfaYFix+q8Z3gu+reDW/Tnumf4VgyqhG+j5MKv7AbBmgAAAABJRU5ErkJggg==")))
            {
                return new System.Drawing.Bitmap(ms).GetHicon();
            }
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_TRAYICON)
            {
                int evt = lParam.ToInt32();
                if (evt == 0x0202 || evt == 0x0203) ShowWindow(); // L-click or dblclk
                else if (evt == 0x0205) ShowTrayContextMenu(); // R-click
                handled = true;
            }
            return IntPtr.Zero;
        }

        void ShowTrayContextMenu()
        {
            // Dispose previous menu if any (after click events have settled)
            var oldMenu = _trayMenu;
            _trayMenu = new System.Windows.Forms.ContextMenuStrip();
            _trayMenu.Items.Add("Show", null, (s, e) => ShowWindow());
            var autoItem = new System.Windows.Forms.ToolStripMenuItem("Auto Start") { CheckOnClick = true, Checked = IsAutoStartEnabled() };
            autoItem.Click += (s, e) => ToggleAutoStart(autoItem.Checked);
            _trayMenu.Items.Add(autoItem);
            _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            _trayMenu.Items.Add("Exit", null, (s, e) => {
                if (_winFormsTray != null) { _winFormsTray.Visible = false; _winFormsTray.Dispose(); }
                Application.Current.Shutdown();
            });
            _trayMenu.Show(System.Windows.Forms.Cursor.Position);
            if (oldMenu != null) {
                // Dispose old menu after WPF dispatcher has processed any pending events
                Dispatcher.BeginInvoke(new Action(() => { try { oldMenu.Dispose(); } catch { } }),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
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
        void ToggleCollapse(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            _isExpanded = !_isExpanded;
            if (_isExpanded)
            {
                btn.Content = "\u25B2";
                Height = _expandedHeight;
                _powerSection.Visibility = Visibility.Visible;
                _audioSection.Visibility = Visibility.Visible;
            }
            else
            {
                btn.Content = "\u25BC";
                Height = 38;
                _powerSection.Visibility = Visibility.Collapsed;
                _audioSection.Visibility = Visibility.Collapsed;
            }
        }
    }

    public class App : Application
    {
        [STAThread]
        public static void Main(string[] args)
        {
            var app = new App();
            var window = new MainWindow();
            window.Show();
            app.Run();
        }
    }
}
