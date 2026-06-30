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
        public interface IMMDeviceEnumerator2
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr device);
            int GetDevice(string id, out IntPtr device);
            int RegisterEndpointNotificationCallback(IMMNotificationClient client);
            int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        public class MMDeviceEnumerator2 { }

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
            DispatcherTimer _pollTimer;
            string _lastDefaultId = "";
            public DeviceWatcher()
            {
                try
                {
                    _enumerator = (IMMDeviceEnumerator2)new MMDeviceEnumerator2();
                    _enumerator.RegisterEndpointNotificationCallback(this);
                }
                catch { }

                // 双保险：COM 回调在 STA 边界间可能不稳定。
                // 每秒轮询默认设备 ID，变化时触发 OnChange。
                _lastDefaultId = GetCurrentDefaultId();
                _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
                { Interval = TimeSpan.FromSeconds(1) };
                _pollTimer.Tick += (s, e) =>
                {
                    string cur = GetCurrentDefaultId();
                    if (cur != _lastDefaultId)
                    {
                        _lastDefaultId = cur;
                        Fire();
                    }
                };
                _pollTimer.Start();
            }
            public void Stop()
            {
                try { if (_pollTimer != null) _pollTimer.Stop(); } catch { }
                try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
                // 释放 COM 枚举器 RCW，避免等到 GC 才回收。
                try { if (_enumerator != null) { Marshal.ReleaseComObject(_enumerator); _enumerator = null; } } catch { }
            }
            public void OnDeviceStateChanged(string deviceId, int newState) { Fire(); }
            public void OnDeviceAdded(string deviceId) { Fire(); }
            public void OnDeviceRemoved(string deviceId) { Fire(); }
            public void OnDefaultDeviceChanged(int dataFlow, int role, string defaultDeviceId) { Fire(); }
            public void OnPropertyValueChanged(string deviceId, PROPERTYKEY key) { }
            void Fire() { try { if (OnChange != null) OnChange(); } catch { } }

            static string GetCurrentDefaultId()
            {
                object im = null;
                try
                {
                    im = (IMMDeviceEnumerator2)new MMDeviceEnumerator2();
                    IntPtr pDev;
                    if (((IMMDeviceEnumerator2)im).GetDefaultAudioEndpoint(0, 0, out pDev) != 0 || pDev == IntPtr.Zero)
                        return "";
                    var dev = (IMMDeviceForId)Marshal.GetObjectForIUnknown(pDev);
                    try
                    {
                        string id;
                        dev.GetId(out id);
                        return id ?? "";
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(dev);
                        Marshal.Release(pDev);
                    }
                }
                catch (Exception ex) { AppLog.Log("GetCurrentDefaultId", ex); return ""; }
                finally { if (im != null) try { Marshal.ReleaseComObject(im); } catch { } }
            }
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
                            if (state != 1) continue;
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
            catch (Exception ex) { AppLog.Log("GetOutputDevices", ex); }
            if (result.Count == 0)
            {
                result.Add(new AudioDeviceInfo { Id = "default", Name = "默认音频输出", IsDefault = true });
            }
            return result;
        }

        // 通过 COM MMDeviceEnumerator 获取默认音频渲染端点 GUID（Windows 在默认设备变更时写入注册表）。
        // 设备位于 \\MMDevices\\Audio\\Render 下，最可靠的方式是直接用 COM 获取并提取 GUID 部分。
        static string FindDefaultRenderId()
        {
            object im = null;
            try
            {
                im = (IMMDeviceEnumerator2)new MMDeviceEnumerator2();
                IntPtr pDev;
                if (((IMMDeviceEnumerator2)im).GetDefaultAudioEndpoint(0, 0, out pDev) != 0) return null;
                if (pDev == IntPtr.Zero) return null;
                var dev = (IMMDeviceForId)Marshal.GetObjectForIUnknown(pDev);
                try
                {
                    string id;
                    dev.GetId(out id);
                    if (string.IsNullOrEmpty(id)) return null;
                    // id 格式为 "{0.0.0.00000000}.{guid}"，仅返回 guid 部分
                    int dot = id.IndexOf("}.");
                    if (dot >= 0 && dot + 2 < id.Length) return id.Substring(dot + 2);
                    return id;
                }
                finally
                {
                    Marshal.ReleaseComObject(dev);
                    Marshal.Release(pDev);
                }
            }
            catch { return null; }
            finally { if (im != null) try { Marshal.ReleaseComObject(im); } catch { } }
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
            catch (Exception ex) { AppLog.Log("SetDefaultDevice(" + (deviceNameOrId ?? "") + ")", ex); }
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
            catch (Exception ex) { AppLog.Log("FindEndpointIdByName(" + (deviceName ?? "") + ")", ex); }
            return null;
        }
    }

}
