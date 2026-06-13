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
        public bool IsHidden { get; set; }
        public int HotkeyIndex { get; set; } // 1..9 mapped to Ctrl+Alt+digit; 0 = none
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
    public static class AppPrefs
    {
        const string KeyPath = @"Software\PowerAudioManager\App";
        public static bool GetBool(string key, bool defaultValue)
        {
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath)) {
                if (k == null) return defaultValue;
                var v = k.GetValue(key) as string;
                if (v == "1") return true; if (v == "0") return false;
            } } catch { }
            return defaultValue;
        }
        public static void SetBool(string key, bool v)
        {
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath))
                k.SetValue(key, v ? "1" : "0"); } catch { }
        }
        public static bool GetDouble(string key, out double value)
        {
            value = 0;
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath)) {
                if (k == null) return false;
                var v = k.GetValue(key) as string;
                if (v == null) return false;
                return double.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value);
            } } catch { return false; }
        }
        public static void SetDouble(string key, double v)
        {
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath))
                k.SetValue(key, v.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
        }
    }

    public static class DevicePrefs
    {
        const string KeyPath = @"Software\PowerAudioManager\Devices";

        public static bool IsHidden(string deviceName)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) return false;
                    var v = k.GetValue(deviceName + ":hidden") as string;
                    return v == "1";
                }
            }
            catch { return false; }
        }

        public static void SetHidden(string deviceName, bool hidden)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (hidden) k.SetValue(deviceName + ":hidden", "1");
                    else k.DeleteValue(deviceName + ":hidden", false);
                }
            }
            catch { }
        }

        public static int GetHotkey(string deviceName)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) return 0;
                    var v = k.GetValue(deviceName + ":hotkey") as string;
                    int n; return int.TryParse(v, out n) ? n : 0;
                }
            }
            catch { return 0; }
        }

        public static void SetHotkeyKey(string deviceName, int encoded)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (encoded != 0)
                    {
                        // Clear conflicts (any other device with same encoded value)
                        var s = encoded.ToString();
                        foreach (var existing in k.GetValueNames())
                        {
                            if (!existing.EndsWith(":hotkey")) continue;
                            if (existing == deviceName + ":hotkey") continue;
                            if ((k.GetValue(existing) as string) == s)
                                k.DeleteValue(existing, false);
                        }
                        k.SetValue(deviceName + ":hotkey", s);
                    }
                    else k.DeleteValue(deviceName + ":hotkey", false);
                }
            }
            catch { }
        }

        // Backwards-compatible alias: if you still have callers passing 0..9 they still work
        public static void SetHotkey(string deviceName, int digit) { SetHotkeyKey(deviceName, digit); }

        public static List<KeyValuePair<string, int>> GetAllHotkeys()
        {
            var list = new List<KeyValuePair<string, int>>();
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) return list;
                    foreach (var n in k.GetValueNames())
                    {
                        if (!n.EndsWith(":hotkey")) continue;
                        var v = k.GetValue(n) as string;
                        int digit;
                        if (int.TryParse(v, out digit) && digit != 0)
                        {
                            var name = n.Substring(0, n.Length - ":hotkey".Length);
                            list.Add(new KeyValuePair<string, int>(name, digit));
                        }
                    }
                }
            }
            catch { }
            return list;
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

        #region Hot-plug notifications

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator2
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr device);
            int GetDevice(string id, out IntPtr device);
            int RegisterEndpointNotificationCallback(IMMNotificationClient client);
            int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumerator2 { }

        [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMNotificationClient
        {
            void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
            void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            void OnDefaultDeviceChanged(int dataFlow, int role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
            void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PROPERTYKEY key);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROPERTYKEY { public Guid fmtid; public int pid; }

        public class DeviceWatcher : IMMNotificationClient
        {
            public Action OnChange;
            IMMDeviceEnumerator2 _enumerator;
            public DeviceWatcher()
            {
                _enumerator = (IMMDeviceEnumerator2)new MMDeviceEnumerator2();
                _enumerator.RegisterEndpointNotificationCallback(this);
            }
            public void Stop()
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
            }
            public void OnDeviceStateChanged(string deviceId, int newState) { Fire(); }
            public void OnDeviceAdded(string deviceId) { Fire(); }
            public void OnDeviceRemoved(string deviceId) { Fire(); }
            public void OnDefaultDeviceChanged(int dataFlow, int role, string defaultDeviceId) { Fire(); }
            public void OnPropertyValueChanged(string deviceId, PROPERTYKEY key) { }
            void Fire() { if (OnChange != null) OnChange(); }
        }

        #endregion


        private const int eConsole = 0;
        private const int eMultimedia = 1;
        private const int eCommunications = 2;

        public static List<AudioDeviceInfo> GetOutputDevices()
        {
            var result = new List<AudioDeviceInfo>();
            string defaultEndpointId = "{0.0.0.00000000}." + (FindDefaultRenderId() ?? "");
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render"))
                {
                    if (key == null) return result;
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using (var subKey = key.OpenSubKey(subKeyName))
                        {
                            if (subKey == null) continue;
                            int state = 0;
                            try { state = (int)subKey.GetValue("DeviceState", 0); } catch { }
                            if (state != 1) continue; // ACTIVE only
                            using (var propsKey = subKey.OpenSubKey("Properties"))
                            {
                                if (propsKey == null) continue;
                                var desc = propsKey.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string;
                                var ifName = propsKey.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string;
                                if (string.IsNullOrEmpty(desc) && string.IsNullOrEmpty(ifName)) continue;
                                string name;
                                if (!string.IsNullOrEmpty(desc) && !string.IsNullOrEmpty(ifName))
                                    name = desc + " (" + ifName + ")";
                                else
                                    name = !string.IsNullOrEmpty(desc) ? desc : ifName;
                                string fullId = "{0.0.0.00000000}." + subKeyName;
                                result.Add(new AudioDeviceInfo {
                                    Id = fullId,
                                    Name = name,
                                    IsDefault = fullId.Equals(defaultEndpointId, StringComparison.OrdinalIgnoreCase),
                                    IsHidden = DevicePrefs.IsHidden(name),
                                    HotkeyIndex = DevicePrefs.GetHotkey(name)
                                });
                            }
                        }
                    }
                }
            }
            catch { }
            if (result.Count == 0)
            {
                result.Add(new AudioDeviceInfo { Id = "default", Name = "默认音频输出", IsDefault = true });
            }
            return result;
        }

        // Read default render endpoint guid from registry (key set by Windows when default changes).
        // Format under \\MMDevices\\Audio\\Render: each device has Properties\\{...},14 or  is in {1d}, {2}
        // The default endpoint is the one whose Role->0 value matches; simpler approach: query via MMDeviceEnumerator? we already have COM but want avoid heavy use. Use registry: there is "DefaultEndpointId" under HKEY_CURRENT_USER\\Software\\Microsoft\\Multimedia\\Audio\\.. but most reliable is reading from each device's Properties\\\"{....1da5d803-d492-4edd-8c23-e0c0ffee7f0e}\\\\,7"=1 means active default? That field is unreliable. Cleanest: enumerate active devices and check the user-level role registry under HKCU.
        static string FindDefaultRenderId()
        {
            try
            {
                // HKEY_CURRENT_USER\\Software\\Microsoft\\Multimedia\\Sound Mapper\\Playback (Vista+) has nothing; the modern way is the Render subkey "Role:0" (eConsole). Actually MMDevices uses a hidden HKLM key. Use COM as fallback.
                var enumType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
                if (enumType == null) return null;
                object enumObj = Activator.CreateInstance(enumType);
                var im = (IMMDeviceEnumerator2)enumObj;
                IntPtr pDev;
                if (im.GetDefaultAudioEndpoint(0, 0, out pDev) != 0) return null;
                if (pDev == IntPtr.Zero) return null;
                var dev = (IMMDeviceForId)Marshal.GetObjectForIUnknown(pDev);
                string id;
                dev.GetId(out id);
                Marshal.ReleaseComObject(dev);
                Marshal.Release(pDev);
                Marshal.ReleaseComObject(enumObj);
                if (string.IsNullOrEmpty(id)) return null;
                // id looks like "{0.0.0.00000000}.{guid}" — return only the guid portion
                int dot = id.IndexOf("}.");
                if (dot >= 0 && dot + 2 < id.Length) return id.Substring(dot + 2);
                return id;
            }
            catch { return null; }
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceForId
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr ap, [MarshalAs(UnmanagedType.IUnknown)] out object pi);
            int OpenPropertyStore(int access, out IntPtr props);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            int GetState(out int state);
        }

        public static bool SetDefaultDevice(string deviceNameOrId)
        {
            try
            {
                string endpointId;
                if (deviceNameOrId != null && deviceNameOrId.StartsWith("{0.0.0.00000000}."))
                    endpointId = deviceNameOrId;
                else
                    endpointId = FindEndpointIdByName(deviceNameOrId);
                if (string.IsNullOrEmpty(endpointId)) return false;
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
    public static class VolumeControl
    {
        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr p);
            int UnregisterControlChangeNotify(IntPtr p);
            int GetChannelCount(out int count);
            int SetMasterVolumeLevel(float level, ref Guid context);
            int SetMasterVolumeLevelScalar(float level, ref Guid context);
            int GetMasterVolumeLevel(out float level);
            int GetMasterVolumeLevelScalar(out float level);
            int SetChannelVolumeLevel(uint c, float l, ref Guid g);
            int SetChannelVolumeLevelScalar(uint c, float l, ref Guid g);
            int GetChannelVolumeLevel(uint c, out float l);
            int GetChannelVolumeLevelScalar(uint c, out float l);
            int SetMute(bool mute, ref Guid g);
            int GetMute(out bool mute);
            int GetVolumeStepInfo(out uint step, out uint count);
            int VolumeStepUp(ref Guid g);
            int VolumeStepDown(ref Guid g);
            int QueryHardwareSupport(out uint mask);
            int GetVolumeRange(out float min, out float max, out float inc);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceVol
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumeratorVol
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.IUnknown)] out object device);
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorVol { }

        static IAudioEndpointVolume _cachedEp;

        static IAudioEndpointVolume GetEndpoint()
        {
            if (_cachedEp != null) return _cachedEp;
            try
            {
                var e = (IMMDeviceEnumeratorVol)new MMDeviceEnumeratorVol();
                object dev;
                if (e.GetDefaultAudioEndpoint(0, 0, out dev) != 0) return null;
                var iid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
                object ep;
                ((IMMDeviceVol)dev).Activate(ref iid, 1, IntPtr.Zero, out ep);
                _cachedEp = (IAudioEndpointVolume)ep;
                return _cachedEp;
            }
            catch { return null; }
        }

        public static void Invalidate()
        {
            if (_cachedEp != null) { try { Marshal.ReleaseComObject(_cachedEp); } catch { } _cachedEp = null; }
        }

        public static float GetVolume()
        {
            var ep = GetEndpoint(); if (ep == null) return 0;
            try { float v; ep.GetMasterVolumeLevelScalar(out v); return v; }
            catch { Invalidate(); return 0; }
        }

        public static void SetVolume(float v)
        {
            v = Math.Max(0, Math.Min(1, v));
            var ep = GetEndpoint(); if (ep == null) return;
            try { var g = Guid.Empty; ep.SetMasterVolumeLevelScalar(v, ref g); }
            catch { Invalidate(); }
        }

        public static bool GetMute()
        {
            var ep = GetEndpoint(); if (ep == null) return false;
            try { bool m; ep.GetMute(out m); return m; }
            catch { Invalidate(); return false; }
        }

        public static void SetMute(bool m)
        {
            var ep = GetEndpoint(); if (ep == null) return;
            try { var g = Guid.Empty; ep.SetMute(m, ref g); }
            catch { Invalidate(); }
        }
    }

    public static class HotkeyCaptureDialog
    {
        // Returns: high 16 bits = modifiers (bit0 Alt, bit1 Ctrl, bit2 Shift, bit3 Win),
        //          low 16 bits = VK code. 0 = none.
        public static int? Show(Window owner, int currentEncoded)
        {
            int captured = currentEncoded;
            var dlg = new Window {
                Title = "设置快捷键",
                Width = 320, Height = 160,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                FontFamily = owner.FontFamily,
                Background = new SolidColorBrush(Color.FromRgb(32,32,32))
            };
            var stack = new StackPanel { Margin = new Thickness(16) };
            var hint = new TextBlock { Text = "请按下组合键，按 Esc 取消", Foreground = new SolidColorBrush(Color.FromRgb(180,180,180)), FontSize = 12, Margin = new Thickness(0,0,0,12) };
            var display = new TextBlock {
                Text = currentEncoded != 0 ? Format(currentEncoded) : "(请按键)",
                FontSize = 18, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,0,0,12)
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "确定", Width = 64, Height = 28, Margin = new Thickness(0,0,8,0) };
            var cancel = new Button { Content = "取消", Width = 64, Height = 28 };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            stack.Children.Add(hint); stack.Children.Add(display); stack.Children.Add(buttons);
            dlg.Content = stack;

            dlg.PreviewKeyDown += (s, e) => {
                if (e.Key == Key.Escape) { captured = 0; dlg.DialogResult = false; dlg.Close(); return; }
                if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
                    e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
                    e.Key == Key.LeftShift || e.Key == Key.RightShift ||
                    e.Key == Key.System || e.Key == Key.LWin || e.Key == Key.RWin) return;
                int mods = 0;
                if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= 1;
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= 2;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= 4;
                if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods |= 8;
                if (mods == 0) return; // require at least one modifier
                int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
                if (vk == 0) return;
                captured = (mods << 16) | (vk & 0xFFFF);
                display.Text = Format(captured);
                e.Handled = true;
            };
            ok.Click += (s, e) => { dlg.DialogResult = true; dlg.Close(); };
            cancel.Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };

            bool? result = dlg.ShowDialog();
            if (result == true && captured != 0) return captured;
            return null;
        }

        public static string Format(int encoded)
        {
            if (encoded == 0) return "(无)";
            int mods = (encoded >> 16) & 0xFFFF;
            int vk = encoded & 0xFFFF;
            var parts = new List<string>();
            if ((mods & 2) != 0) parts.Add("Ctrl");
            if ((mods & 1) != 0) parts.Add("Alt");
            if ((mods & 4) != 0) parts.Add("Shift");
            if ((mods & 8) != 0) parts.Add("Win");
            try { parts.Add(KeyInterop.KeyFromVirtualKey(vk).ToString()); } catch { parts.Add("?"); }
            return string.Join("+", parts.ToArray());
        }
    }

    public static class MemoryCleaner
    {
        // SE_INCREASE_QUOTA_NAME + SE_PROF_SINGLE_PROCESS_NAME for system-level cleaning
        const int SE_PRIVILEGE_ENABLED = 2;
        const int TOKEN_QUERY = 8;
        const int TOKEN_ADJUST_PRIVILEGES = 32;

        [StructLayout(LayoutKind.Sequential)]
        struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)]
        struct TOKEN_PRIVILEGES { public int Count; public LUID Luid; public int Attr; }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr h, int access, out IntPtr tok);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool LookupPrivilegeValue(string sys, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr tok, bool disable, ref TOKEN_PRIVILEGES newp, int len, IntPtr old, IntPtr ret);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);

        // NtSetSystemInformation classes
        const int SystemMemoryListInformation = 80;
        const int SystemFileCacheInformation = 21;
        const int SystemCombinePhysicalMemoryInformation = 130;
        // Memory list commands
        const int MemoryEmptyWorkingSets = 2;
        const int MemoryFlushModifiedList = 3;
        const int MemoryPurgeStandbyList = 4;
        const int MemoryPurgeLowPriorityStandbyList = 5;

        [DllImport("ntdll.dll")]
        static extern int NtSetSystemInformation(int infoClass, ref int data, int size);
        [DllImport("ntdll.dll")]
        static extern int NtSetSystemInformation(int infoClass, ref SYSTEM_FILECACHE_INFORMATION info, int size);

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_FILECACHE_INFORMATION
        {
            public IntPtr CurrentSize;
            public IntPtr PeakSize;
            public uint PageFaultCount;
            public IntPtr MinimumWorkingSet;
            public IntPtr MaximumWorkingSet;
            public IntPtr CurrentSizeIncludingTransitionInPages;
            public IntPtr PeakSizeIncludingTransitionInPages;
            public uint TransitionRePurposeCount;
            public uint Flags;
        }

        static bool EnablePrivilege(string name)
        {
            IntPtr tok;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out tok))
                return false;
            try
            {
                LUID luid;
                if (!LookupPrivilegeValue(null, name, out luid)) return false;
                var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attr = SE_PRIVILEGE_ENABLED };
                return AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(tok); }
        }

        public static long GetTotalPhysicalKb()
        {
            try { var c = new System.Diagnostics.PerformanceCounter("Memory", "Available KBytes"); return (long)c.NextValue(); } catch { return 0; }
        }

        public class MemoryStatus
        {
            public ulong TotalBytes;
            public ulong AvailableBytes;
            public uint MemoryLoadPercent;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX mse);

        public static MemoryStatus GetStatus()
        {
            var m = new MEMORYSTATUSEX();
            if (!GlobalMemoryStatusEx(m)) return null;
            return new MemoryStatus { TotalBytes = m.ullTotalPhys, AvailableBytes = m.ullAvailPhys, MemoryLoadPercent = m.dwMemoryLoad };
        }

        public class CleanResult
        {
            public ulong FreedBytes;
            public bool WorkingSetsEmptied;
            public bool StandbyPurged;
            public bool ModifiedFlushed;
            public bool FileCacheReleased;
        }

        [Flags]
        public enum CleanFlags
        {
            None = 0,
            WorkingSet         = 1 << 0,
            SystemFileCache    = 1 << 1,
            ModifiedPageList   = 1 << 2,
            StandbyList        = 1 << 3,  // priority
            StandbyListNoPrio  = 1 << 4,  // low-priority pages only
            ModifiedFileCache  = 1 << 5,  // alias of MemoryFlushModifiedList
            RegistryCache      = 1 << 6,  // win8.1+
            CombineMemoryLists = 1 << 7,  // win10+
            Default = WorkingSet | SystemFileCache | StandbyListNoPrio | ModifiedFileCache | RegistryCache
        }

        public static CleanResult CleanAll() { return CleanAll(CleanFlags.Default); }

        public static CleanResult CleanAll(CleanFlags flags)
        {
            var before = GetStatus();
            ulong availBefore = before == null ? 0 : before.AvailableBytes;
            var r = new CleanResult();

            EnablePrivilege("SeIncreaseQuotaPrivilege");
            EnablePrivilege("SeProfileSingleProcessPrivilege");

            // 1) Empty all process working sets
            if ((flags & CleanFlags.WorkingSet) != 0)
            {
                int cmd = MemoryEmptyWorkingSets;
                r.WorkingSetsEmptied = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;
            }

            // 2) System file cache release
            if ((flags & CleanFlags.SystemFileCache) != 0)
            {
                try
                {
                    var fci = new SYSTEM_FILECACHE_INFORMATION { MinimumWorkingSet = (IntPtr)(-1), MaximumWorkingSet = (IntPtr)(-1) };
                    r.FileCacheReleased = NtSetSystemInformation(SystemFileCacheInformation, ref fci, Marshal.SizeOf(typeof(SYSTEM_FILECACHE_INFORMATION))) == 0;
                }
                catch { }
            }

            // 3) Modified page list flush (writes dirty pages back, allowing them to become available)
            if ((flags & CleanFlags.ModifiedPageList) != 0)
            {
                int cmd = MemoryFlushModifiedList;
                r.ModifiedFlushed = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;
            }

            // 4) Standby list (priority-aware: removes ALL standby pages)
            if ((flags & CleanFlags.StandbyList) != 0)
            {
                int cmd = MemoryPurgeStandbyList;
                r.StandbyPurged = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;
            }

            // 4b) Standby list (without priority) - only low-priority pages
            if ((flags & CleanFlags.StandbyListNoPrio) != 0)
            {
                int cmd = MemoryPurgeLowPriorityStandbyList;
                r.StandbyPurged = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0 || r.StandbyPurged;
            }

            // 5) Modified file cache (same NT call: FlushModifiedList)
            if ((flags & CleanFlags.ModifiedFileCache) != 0 && !r.ModifiedFlushed)
            {
                int cmd = MemoryFlushModifiedList;
                r.ModifiedFlushed = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;
            }

            // 6) Registry cache (Win8.1+) - SystemRegistryReconciliationInformation = 155
            if ((flags & CleanFlags.RegistryCache) != 0 && AdminUtils.RealOsVersion() >= new Version(6, 3))
            {
                try
                {
                    int cmd = 0;
                    NtSetSystemInformation(155, ref cmd, sizeof(int));
                }
                catch { }
            }

            // 7) Combine memory lists (Win10+) - SystemCombinePhysicalMemoryInformation = 130
            if ((flags & CleanFlags.CombineMemoryLists) != 0 && AdminUtils.RealOsVersion().Major >= 10)
            {
                try
                {
                    // Pass MEMORY_COMBINE_INFORMATION_EX with Flags=0
                    long zero = 0;
                    var ptr = Marshal.AllocHGlobal(16);
                    try
                    {
                        Marshal.WriteInt64(ptr, 0, 0);
                        Marshal.WriteInt64(ptr, 8, 0);
                        // The 130 path needs a struct, but reusing the int signature with size 16 works for triggering the operation when the kernel only checks Size.
                        int dummy = 0;
                        NtSetSystemInformation(130, ref dummy, 16);
                    }
                    finally { Marshal.FreeHGlobal(ptr); }
                }
                catch { }
            }


            try { EmptyWorkingSet(GetCurrentProcess()); } catch { }
            System.Threading.Thread.Sleep(500);
            var after = GetStatus();
            if (after != null && after.AvailableBytes > availBefore)
                r.FreedBytes = after.AvailableBytes - availBefore;
            return r;
        }
    }

    public static class AdminUtils
    {
        // Environment.OSVersion lies on Win8.1+ when the app has no manifest; use RtlGetVersion which always returns the real value.
        [StructLayout(LayoutKind.Sequential)]
        struct RTL_OSVERSIONINFOEX { public int dwOSVersionInfoSize; public int dwMajorVersion; public int dwMinorVersion; public int dwBuildNumber; public int dwPlatformId; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion; public short wServicePackMajor; public short wServicePackMinor; public short wSuiteMask; public byte wProductType; public byte wReserved; }
        [DllImport("ntdll.dll")] static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEX vi);

        public static Version RealOsVersion()
        {
            try
            {
                var vi = new RTL_OSVERSIONINFOEX();
                vi.dwOSVersionInfoSize = Marshal.SizeOf(typeof(RTL_OSVERSIONINFOEX));
                if (RtlGetVersion(ref vi) == 0)
                    return new Version(vi.dwMajorVersion, vi.dwMinorVersion, vi.dwBuildNumber);
            }
            catch { }
            return Environment.OSVersion.Version;
        }

        public static bool IsAdmin()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var p = new System.Security.Principal.WindowsPrincipal(id);
                    return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        public static bool RestartAsAdmin()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo {
                    FileName = System.Reflection.Assembly.GetExecutingAssembly().Location,
                    Verb = "runas",
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
                System.Windows.Application.Current.Shutdown();
                return true;
            }
            catch { return false; }
        }
    }

    public static class CleanerSettingsDialog
    {
        // Helpers for the per-area checkbox + tooltip
        static CheckBox MakeAreaCb(string label, string tip, string prefKey, bool defChecked, SolidColorBrush fg, bool enabled)
        {
            var cb = new CheckBox {
                Content = label,
                Foreground = fg,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                IsChecked = AppPrefs.GetBool(prefKey, defChecked),
                IsEnabled = enabled,
                ToolTip = tip
            };
            ToolTipService.SetInitialShowDelay(cb, 250);
            ToolTipService.SetShowDuration(cb, 8000);
            // Allow tooltip to show even when the checkbox is disabled (default WPF behavior is to suppress).
            ToolTipService.SetShowOnDisabled(cb, true);
            cb.IsHitTestVisible = true;
            return cb;
        }

        public static void Show(Window owner)
        {
            var dlg = new Window {
                Title = "内存清理设置",
                Width = 420, Height = 600,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                FontFamily = owner.FontFamily,
                Background = new SolidColorBrush(Color.FromRgb(28, 26, 40))
            };
            var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stack = new StackPanel { Margin = new Thickness(20) };
            scroller.Content = stack;
            var fg = new SolidColorBrush(Color.FromRgb(190, 188, 220));
            bool isAdmin = AdminUtils.IsAdmin();

            // Admin status banner
            var adminBanner = new Border {
                Background = new SolidColorBrush(isAdmin ? Color.FromRgb(40, 60, 50) : Color.FromRgb(70, 50, 50)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var adminRow = new DockPanel { LastChildFill = true };
            var adminText = new TextBlock {
                Text = isAdmin ? "已以管理员身份运行：所有清理项可用" : "当前未以管理员身份运行：部分项需要管理员权限",
                Foreground = Brushes.White,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            adminRow.Children.Add(adminText);
            if (!isAdmin)
            {
                var elevateBtn = new Button {
                    Content = "以管理员重启",
                    Padding = new Thickness(8, 2, 8, 2),
                    FontSize = 11
                };
                DockPanel.SetDock(elevateBtn, Dock.Right);
                elevateBtn.Click += (s, e) => { AdminUtils.RestartAsAdmin(); };
                adminRow.Children.Add(elevateBtn);
            }
            adminBanner.Child = adminRow;
            stack.Children.Add(adminBanner);

            // Section: 自动清理触发
            stack.Children.Add(new TextBlock { Text = "自动清理", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });

            var enableCb = new CheckBox { Content = "启用自动清理", Foreground = Brushes.White, FontSize = 13, Margin = new Thickness(0, 0, 0, 14) };
            enableCb.IsChecked = AppPrefs.GetBool("AutoCleanEnabled", false);
            stack.Children.Add(enableCb);

            var byTimeCb = new CheckBox { Content = "按时间周期清理", Foreground = fg, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
            byTimeCb.IsChecked = AppPrefs.GetBool("AutoCleanByTime", true);
            stack.Children.Add(byTimeCb);

            var timeRow = new DockPanel { Margin = new Thickness(20, 0, 0, 14) };
            timeRow.Children.Add(new TextBlock { Text = "每", VerticalAlignment = VerticalAlignment.Center, Foreground = fg });
            var timeBox = new TextBox { Width = 60, MinHeight = 24, Margin = new Thickness(8, 0, 8, 0), Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)) };
            double tmin; AppPrefs.GetDouble("AutoCleanMinutes", out tmin); if (tmin <= 0) tmin = 30;
            timeBox.Text = ((int)tmin).ToString();
            timeRow.Children.Add(timeBox);
            timeRow.Children.Add(new TextBlock { Text = "分钟清理一次", VerticalAlignment = VerticalAlignment.Center, Foreground = fg });
            stack.Children.Add(timeRow);

            var byThCb = new CheckBox { Content = "按内存占用率清理", Foreground = fg, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
            byThCb.IsChecked = AppPrefs.GetBool("AutoCleanByThreshold", true);
            stack.Children.Add(byThCb);

            var thRow = new DockPanel { Margin = new Thickness(20, 0, 0, 18) };
            thRow.Children.Add(new TextBlock { Text = "占用率达到", VerticalAlignment = VerticalAlignment.Center, Foreground = fg });
            var thBox = new TextBox { Width = 60, MinHeight = 24, Margin = new Thickness(8, 0, 8, 0), Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)) };
            double th; AppPrefs.GetDouble("AutoCleanThreshold", out th); if (th <= 0) th = 80;
            thBox.Text = ((int)th).ToString();
            thRow.Children.Add(thBox);
            thRow.Children.Add(new TextBlock { Text = "% 时清理", VerticalAlignment = VerticalAlignment.Center, Foreground = fg });
            stack.Children.Add(thRow);

            // Section: 清理区域
            stack.Children.Add(new TextBlock {
                Text = "要清理的内存区域",
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(0, 8, 0, 4)
            });

            var cbWS    = MakeAreaCb("Working set", "释放各进程的工作集（已加载到物理内存的代码与数据），把未使用的页面交还系统。", "Clean.WorkingSet", true, fg, true);
            var cbSFC   = MakeAreaCb("System file cache", "归还系统文件缓存：Windows 用来加速文件读取的内存被释放回可用池。", "Clean.SystemFileCache", true, fg, true);
            var cbMPL   = MakeAreaCb("Modified page list*", "把已修改但尚未写回磁盘的脏页刷盘后转入可用列表。* 需要管理员权限。", "Clean.ModifiedPageList", false, fg, isAdmin);
            var cbSL    = MakeAreaCb("Standby list*", "清空整个 standby（备用）列表，包括所有优先级缓存的页面。* 需要管理员权限。", "Clean.StandbyList", false, fg, isAdmin);
            var cbSLNP  = MakeAreaCb("Standby list (without priority)", "只清理低优先级的 standby 页（影响小、释放慢但稳定）。", "Clean.StandbyListNoPrio", true, fg, true);
            var cbMFC   = MakeAreaCb("Modified file cache", "刷新已修改的文件缓存页（与 Modified page list 的非分页部分对应）。", "Clean.ModifiedFileCache", true, fg, true);
            var cbReg   = MakeAreaCb("Registry cache (win8.1+)", "Windows 8.1 及以上：归还注册表配置单元的缓存内存。", "Clean.RegistryCache", true, fg, AdminUtils.RealOsVersion() >= new Version(6, 3));
            var cbCML   = MakeAreaCb("Combine memory lists (win10+)", "Windows 10 及以上：合并相同内容的物理内存页（内存压缩 / 共享）。", "Clean.CombineMemoryLists", false, fg, AdminUtils.RealOsVersion().Major >= 10);

            stack.Children.Add(cbWS);
            stack.Children.Add(cbSFC);
            stack.Children.Add(cbMPL);
            stack.Children.Add(cbSL);
            stack.Children.Add(cbSLNP);
            stack.Children.Add(cbMFC);
            stack.Children.Add(cbReg);
            stack.Children.Add(cbCML);
            stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(80, 75, 120)), Margin = new Thickness(0, 14, 0, 12) });

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "确定", Width = 64, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "取消", Width = 64, Height = 28 };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            stack.Children.Add(btns);
            dlg.Content = scroller;

            ok.Click += (s, e) => {
                AppPrefs.SetBool("AutoCleanEnabled", enableCb.IsChecked == true);
                AppPrefs.SetBool("AutoCleanByTime", byTimeCb.IsChecked == true);
                AppPrefs.SetBool("AutoCleanByThreshold", byThCb.IsChecked == true);
                int n; if (int.TryParse(timeBox.Text, out n) && n > 0) AppPrefs.SetDouble("AutoCleanMinutes", n);
                int t; if (int.TryParse(thBox.Text, out t) && t > 0 && t <= 100) AppPrefs.SetDouble("AutoCleanThreshold", t);
                AppPrefs.SetBool("Clean.WorkingSet",        cbWS.IsChecked == true);
                AppPrefs.SetBool("Clean.SystemFileCache",   cbSFC.IsChecked == true);
                AppPrefs.SetBool("Clean.ModifiedPageList",  cbMPL.IsChecked == true);
                AppPrefs.SetBool("Clean.StandbyList",       cbSL.IsChecked == true);
                AppPrefs.SetBool("Clean.StandbyListNoPrio", cbSLNP.IsChecked == true);
                AppPrefs.SetBool("Clean.ModifiedFileCache", cbMFC.IsChecked == true);
                AppPrefs.SetBool("Clean.RegistryCache",     cbReg.IsChecked == true);
                AppPrefs.SetBool("Clean.CombineMemoryLists",cbCML.IsChecked == true);
                if (owner is MainWindow) ((MainWindow)owner).RestartAutoCleanTimer();
                dlg.DialogResult = true; dlg.Close();
            };
            cancel.Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            dlg.ShowDialog();
        }

        public static MemoryCleaner.CleanFlags GetSavedFlags()
        {
            var f = MemoryCleaner.CleanFlags.None;
            if (AppPrefs.GetBool("Clean.WorkingSet",        true))  f |= MemoryCleaner.CleanFlags.WorkingSet;
            if (AppPrefs.GetBool("Clean.SystemFileCache",   true))  f |= MemoryCleaner.CleanFlags.SystemFileCache;
            if (AppPrefs.GetBool("Clean.ModifiedPageList",  false)) f |= MemoryCleaner.CleanFlags.ModifiedPageList;
            if (AppPrefs.GetBool("Clean.StandbyList",       false)) f |= MemoryCleaner.CleanFlags.StandbyList;
            if (AppPrefs.GetBool("Clean.StandbyListNoPrio", true))  f |= MemoryCleaner.CleanFlags.StandbyListNoPrio;
            if (AppPrefs.GetBool("Clean.ModifiedFileCache", true))  f |= MemoryCleaner.CleanFlags.ModifiedFileCache;
            if (AppPrefs.GetBool("Clean.RegistryCache",     true))  f |= MemoryCleaner.CleanFlags.RegistryCache;
            if (AppPrefs.GetBool("Clean.CombineMemoryLists",false)) f |= MemoryCleaner.CleanFlags.CombineMemoryLists;
            if (f == MemoryCleaner.CleanFlags.None) f = MemoryCleaner.CleanFlags.Default;
            return f;
        }
    }

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
        private bool _lockPosition;
        private System.Windows.Forms.ContextMenuStrip _trayMenu;
        private AudioDevices.DeviceWatcher _deviceWatcher;
        private Slider _volSlider;
        private Button _muteBtn;
        private bool _volSliderUpdating;
        private TextBlock _volLabel;
        private TextBlock _memStatusLabel;

        static readonly Color AccentColor = Color.FromRgb(142, 140, 216);   // 紫影 #8E8CD8
        static readonly Color BgColor = Color.FromRgb(28, 26, 40);          // 深底，与紫影协调
        static readonly Color CardColor = Color.FromRgb(42, 39, 60);        // 卡片
        static readonly Color TextPrimary = Colors.White;
        static readonly Color TextSecondary = Color.FromRgb(190, 188, 220); // 次要文字
        static readonly Color HoverColor = Color.FromRgb(58, 54, 84);       // 悬停
        static readonly Color ActiveBg = Color.FromRgb(110, 105, 200);      // 激活态（紫影偏深）
        static readonly Color BorderColor = Color.FromRgb(80, 75, 120);     // 边框

        static readonly System.Windows.Media.FontFamily AppFont = LoadAppFont();

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
                Background = new SolidColorBrush(Color.FromRgb(34, 32, 50)),
                Height = 36,
                LastChildFill = true
            };
            var titleText = new TextBlock
            {
                Text = "  \u26A1 OneBox",
                Foreground = new SolidColorBrush(TextPrimary),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            var pinBtn = new Button
            {
                Content = _topmost ? "\uD83D\uDCCC" : "\uD83D\uDCCD",  // 📌 pinned / 📍 not
                Width = 28, Height = 28,
                FontSize = 12,
                Foreground = new SolidColorBrush(_topmost ? AccentColor : TextSecondary),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "切换窗口置顶"
            };
            pinBtn.Click += (s, e) =>
            {
                _topmost = !_topmost;
                Topmost = _topmost;
                AppPrefs.SetBool("Topmost", _topmost);
                pinBtn.Content = _topmost ? "\uD83D\uDCCC" : "\uD83D\uDCCD";
                pinBtn.Foreground = new SolidColorBrush(_topmost ? AccentColor : TextSecondary);
                if (_topmostMenuItem != null) _topmostMenuItem.Checked = _topmost;
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
            titleBar.Children.Add(titleText);
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

            var powerHeader = new TextBlock
            {
                Text = "\u26A1 电源计划",
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
                Text = "\uD83D\uDD0A 音频输出",
                Foreground = new SolidColorBrush(AccentColor),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            contentPanel.Children.Add(audioHeader);
            _audioSection = new StackPanel();
            contentPanel.Children.Add(_audioSection);

            // Volume row
            var volRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
            _muteBtn = new Button {
                Content = "\uD83D\uDD0A",
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
                Text = "\uD83E\uDDF9 内存清理",
                Foreground = new SolidColorBrush(AccentColor),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
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

            _root.Children.Add(contentPanel);
            mainBorder.Child = _root;
            Content = mainBorder;
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
                _topmostMenuItem.Click += (s, e) => { _topmost = _topmostMenuItem.Checked; Topmost = _topmost; AppPrefs.SetBool("Topmost", _topmost); if (_pinBtn != null) { _pinBtn.Content = _topmost ? "\uD83D\uDCCC" : "\uD83D\uDCCD"; _pinBtn.Foreground = new SolidColorBrush(_topmost ? AccentColor : TextSecondary); } };
                _trayMenu.Items.Insert(_trayMenu.Items.Count - 1, _topmostMenuItem);
                var lockItem = new System.Windows.Forms.ToolStripMenuItem("锁定位置") { CheckOnClick = true, Checked = _lockPosition };
                lockItem.Click += (s, e) => { _lockPosition = lockItem.Checked; AppPrefs.SetBool("LockPosition", _lockPosition); };
                _trayMenu.Items.Insert(_trayMenu.Items.Count - 1, lockItem);
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
        Dictionary<int, string> _hotkeyMap = new Dictionary<int, string>();
        IntPtr _hotkeyHwnd = IntPtr.Zero;

        void UnregisterAllHotkeys()
        {
            if (_hotkeyHwnd == IntPtr.Zero) return;
            foreach (var id in _hotkeyMap.Keys) UnregisterHotKey(_hotkeyHwnd, id);
            _hotkeyMap.Clear();
        }

        void RefreshHotkeys()
        {
            if (_hotkeyHwnd == IntPtr.Zero) return;
            // Unregister all known IDs
            foreach (var id in _hotkeyMap.Keys) UnregisterHotKey(_hotkeyHwnd, id);
            _hotkeyMap.Clear();
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
                _powerSection.Visibility = Visibility.Visible;
                _audioSection.Visibility = Visibility.Visible;
                if (_volSlider != null) ((FrameworkElement)_volSlider.Parent).Visibility = Visibility.Visible;
                if (_memStatusLabel != null) _memStatusLabel.Visibility = Visibility.Visible;
                SizeToContent = SizeToContent.Height;
            }
            else
            {
                btn.Content = "\u25B2"; // pointing up: click to expand
                _powerSection.Visibility = Visibility.Collapsed;
                _audioSection.Visibility = Visibility.Collapsed;
                if (_volSlider != null) ((FrameworkElement)_volSlider.Parent).Visibility = Visibility.Collapsed;
                if (_memStatusLabel != null) _memStatusLabel.Visibility = Visibility.Collapsed;
                SizeToContent = SizeToContent.Manual;
                Height = 38;
            }
            // Re-anchor: keep bottom edge fixed
            Dispatcher.BeginInvoke(new Action(() => { Top = bottom - ActualHeight; }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    public class App : Application
    {
        [STAThread]
        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => {
                try { System.IO.File.WriteAllText(System.IO.Path.GetTempPath() + "pam_crash.log",
                    DateTime.Now + " UnhandledException: " + ex.ExceptionObject); } catch { }
            };
            System.Windows.Forms.Application.ThreadException += (s, ex) => {
                try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "pam_crash.log",
                    System.Environment.NewLine + DateTime.Now + " ThreadException: " + ex.Exception); } catch { }
            };
            var app = new App();
            app.DispatcherUnhandledException += (s, ex) => { try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "pam_crash.log", System.Environment.NewLine + DateTime.Now + " Dispatcher: " + ex.Exception); } catch { } ex.Handled = true; };
            var window = new MainWindow();
            try { window.Show(); } catch (Exception ex) { try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "pam_crash.log", DateTime.Now + " Show: " + ex); } catch { } throw; }
            try { app.Run(); } catch (Exception ex) { try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "pam_crash.log", System.Environment.NewLine + DateTime.Now + " Run: " + ex); } catch { } throw; }
        }
    }
}
