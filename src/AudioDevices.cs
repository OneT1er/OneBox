using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace PowerAudioManager
{
    /// <summary>Pure device projection used by the NAudio adapter and tests.</summary>
    public sealed class AudioDeviceCandidate
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsActive { get; set; }
    }

    public static class AudioDevicePolicy
    {
        public static List<AudioDeviceInfo> Project(
            IEnumerable<AudioDeviceCandidate> candidates,
            string defaultId,
            Func<string, bool> isHidden = null,
            Func<string, int> hotkey = null)
        {
            var result = new List<AudioDeviceInfo>();
            if (candidates == null) return result;
            foreach (var candidate in candidates)
            {
                if (candidate == null || !candidate.IsActive || string.IsNullOrWhiteSpace(candidate.Id)) continue;
                string name = string.IsNullOrWhiteSpace(candidate.Name) ? "未命名音频设备" : candidate.Name;
                result.Add(new AudioDeviceInfo
                {
                    Id = candidate.Id,
                    Name = name,
                    IsDefault = string.Equals(candidate.Id, defaultId, StringComparison.OrdinalIgnoreCase),
                    IsHidden = isHidden != null && isHidden(name),
                    HotkeyIndex = hotkey == null ? 0 : hotkey(name)
                });
            }
            return result;
        }
    }

    public enum AudioDeviceRole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    public static class AudioDefaultEndpointPolicy
    {
        public static bool Apply(string endpointId, Func<AudioDeviceRole, int> setRole)
        {
            if (string.IsNullOrWhiteSpace(endpointId) || setRole == null) return false;
            int console = Invoke(setRole, AudioDeviceRole.Console);
            int multimedia = Invoke(setRole, AudioDeviceRole.Multimedia);
            int communications = Invoke(setRole, AudioDeviceRole.Communications);
            return console == 0 && multimedia == 0 && communications == 0;
        }

        static int Invoke(Func<AudioDeviceRole, int> setRole, AudioDeviceRole role)
        {
            try { return setRole(role); }
            catch (Exception ex) { AppLog.Log("Set default audio role " + role, ex); return int.MinValue; }
        }
    }

    /// <summary>
    /// Coalesces bursts from the Core Audio notification client. The callback is
    /// always drained by the WPF dispatcher, never by the COM callback thread.
    /// </summary>
    public sealed class AudioNotificationGate : IDisposable
    {
        readonly Action _callback;
        int _queued;
        int _stopped;

        public AudioNotificationGate(Action callback) { _callback = callback; }

        public bool TryQueue()
        {
            if (System.Threading.Volatile.Read(ref _stopped) != 0) return false;
            return System.Threading.Interlocked.Exchange(ref _queued, 1) == 0;
        }

        public void Drain()
        {
            if (System.Threading.Volatile.Read(ref _stopped) != 0) return;
            if (System.Threading.Interlocked.Exchange(ref _queued, 0) == 1)
            {
                try { _callback?.Invoke(); }
                catch (Exception ex) { AppLog.Log("Audio notification callback", ex); }
            }
        }

        public void Dispose()
        {
            System.Threading.Interlocked.Exchange(ref _stopped, 1);
            System.Threading.Interlocked.Exchange(ref _queued, 0);
        }
    }

    public static class AudioDevices
    {
        public sealed class DeviceWatcher : IDisposable
        {
            readonly Dispatcher _dispatcher;
            readonly DispatcherTimer _debounceTimer;
            readonly AudioNotificationGate _gate;
            MMDeviceEnumerator _enumerator;
            MMDeviceNotificationClient _notificationClient;
            bool _stopped;

            public Action OnChange;

            public DeviceWatcher()
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                _gate = new AudioNotificationGate(() => OnChange?.Invoke());
                _debounceTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
                { Interval = TimeSpan.FromMilliseconds(150) };
                _debounceTimer.Tick += OnDebounceTick;
                try
                {
                    _enumerator = new MMDeviceEnumerator();
                    // NAudio owns the Core Audio notification adapter and unregisters
                    // it when the returned client is disposed.
                    _notificationClient = _enumerator.CreateNotificationClient(true);
                    _notificationClient.DeviceStateChanged += OnDeviceChanged;
                    _notificationClient.DeviceAdded += OnDeviceChanged;
                    _notificationClient.DeviceRemoved += OnDeviceChanged;
                    _notificationClient.DefaultDeviceChanged += OnDefaultDeviceChanged;
                    _notificationClient.PropertyValueChanged += OnDeviceChanged;
                }
                catch (Exception ex)
                {
                    AppLog.Log("Audio watcher create", ex);
                    Dispose();
                }
            }

            void OnDeviceChanged(object sender, EventArgs args) => QueueChange();
            void OnDefaultDeviceChanged(object sender, DefaultDeviceChangedEventArgs args) => QueueChange();

            void QueueChange()
            {
                if (_stopped || !_gate.TryQueue()) return;
                try
                {
                    if (_dispatcher.CheckAccess())
                    {
                        _debounceTimer.Stop();
                        _debounceTimer.Start();
                    }
                    else
                    {
                        _dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (_stopped) return;
                            _debounceTimer.Stop();
                            _debounceTimer.Start();
                        }));
                    }
                }
                catch (Exception ex) { AppLog.Log("Audio watcher schedule", ex); }
            }

            void OnDebounceTick(object sender, EventArgs e)
            {
                _debounceTimer.Stop();
                _gate.Drain();
            }

            public void Stop() => Dispose();

            public void Dispose()
            {
                if (_stopped) return;
                _stopped = true;
                try { _debounceTimer.Stop(); } catch { }
                _gate.Dispose();
                if (_notificationClient != null)
                {
                    try
                    {
                        _notificationClient.DeviceStateChanged -= OnDeviceChanged;
                        _notificationClient.DeviceAdded -= OnDeviceChanged;
                        _notificationClient.DeviceRemoved -= OnDeviceChanged;
                        _notificationClient.DefaultDeviceChanged -= OnDefaultDeviceChanged;
                        _notificationClient.PropertyValueChanged -= OnDeviceChanged;
                    }
                    catch (Exception ex) { AppLog.Log("Audio watcher unsubscribe", ex); }
                    try { _notificationClient.Dispose(); } catch (Exception ex) { AppLog.Log("Audio watcher dispose", ex); }
                    _notificationClient = null;
                }
                if (_enumerator != null)
                {
                    try { _enumerator.Dispose(); } catch (Exception ex) { AppLog.Log("Audio enumerator dispose", ex); }
                    _enumerator = null;
                }
            }
        }

        public static List<AudioDeviceInfo> GetOutputDevices()
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                using (var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    string defaultId = null;
                    try
                    {
                        if (enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Console, out var endpoint))
                        {
                            using (endpoint) defaultId = endpoint.ID;
                        }
                    }
                    catch (Exception ex) { AppLog.Log("Get default audio endpoint", ex); }

                    var candidates = new List<AudioDeviceCandidate>();
                    foreach (var endpoint in endpoints)
                    {
                        using (endpoint)
                        {
                            candidates.Add(new AudioDeviceCandidate
                            {
                                Id = endpoint.ID,
                                Name = endpoint.FriendlyName,
                                IsActive = endpoint.State == DeviceState.Active
                            });
                        }
                    }
                    var result = AudioDevicePolicy.Project(candidates, defaultId,
                        DevicePrefs.IsHidden, DevicePrefs.GetHotkey);
                    if (result.Count > 0) return result;
                }
            }
            catch (Exception ex) { AppLog.Log("GetOutputDevices", ex); }

            // An empty list is the real no-device state; the UI renders its
            // existing empty-state text and commands can return NotAvailable.
            return new List<AudioDeviceInfo>();
        }

        /// <summary>
        /// Windows exposes no supported managed API for changing the default
        /// endpoint. This isolated adapter is the only remaining PolicyConfig COM
        /// interop; enumeration and volume control are entirely NAudio-based.
        /// </summary>
        public static bool SetDefaultDevice(string deviceNameOrId)
        {
            MMDevice device = null;
            try
            {
                device = FindDevice(deviceNameOrId);
                if (device == null) return false;
                object policyObject = null;
                try
                {
                    policyObject = new CPolicyConfigClient();
                    var policy = (IPolicyConfig)policyObject;
                    return AudioDefaultEndpointPolicy.Apply(device.ID,
                        role => policy.SetDefaultEndpoint(device.ID, (int)role));
                }
                finally
                {
                    if (policyObject != null)
                        try { Marshal.ReleaseComObject(policyObject); } catch (Exception ex) { AppLog.Log("PolicyConfig release", ex); }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log("SetDefaultDevice(" + (deviceNameOrId ?? "") + ")", ex);
                return false;
            }
            finally { device?.Dispose(); }
        }

        static MMDevice FindDevice(string deviceNameOrId)
        {
            if (string.IsNullOrWhiteSpace(deviceNameOrId) || deviceNameOrId == "default") return null;
            var enumerator = new MMDeviceEnumerator();
            try
            {
                if (deviceNameOrId.StartsWith("{", StringComparison.Ordinal))
                {
                    try
                    {
                        var exact = enumerator.GetDevice(deviceNameOrId);
                        enumerator.Dispose();
                        return exact;
                    }
                    catch { }
                }
                using (enumerator)
                using (var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    string bestId = null;
                    int bestScore = 0;
                    foreach (var endpoint in endpoints)
                    {
                        using (endpoint)
                        {
                            int score = string.Equals(endpoint.ID, deviceNameOrId, StringComparison.OrdinalIgnoreCase)
                                ? 1000
                                : endpoint.FriendlyName?.IndexOf(deviceNameOrId, StringComparison.OrdinalIgnoreCase) >= 0 ? 100 : 0;
                            if (score > bestScore)
                            {
                                bestId = endpoint.ID;
                                bestScore = score;
                            }
                        }
                    }
                    // Re-open after the collection and its temporary endpoint
                    // wrappers are disposed; the caller owns this independent RCW.
                    return bestId == null ? null : enumerator.GetDevice(bestId);
                }
            }
            catch (Exception ex)
            {
                enumerator.Dispose();
                AppLog.Log("Find audio endpoint", ex);
                return null;
            }
        }

        // Minimal undocumented PolicyConfig COM boundary; no endpoint enumeration
        // or volume operations belong here.
        [ComImport, Guid("568b9108-44bf-40b4-9006-86afe5b5a620"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPolicyConfig
        {
            [PreserveSig]
            int GetMixFormat(string deviceID, IntPtr format);
            [PreserveSig]
            int GetDeviceFormat(string deviceID, int defaultFormat, IntPtr format);
            [PreserveSig]
            int SetDeviceFormat(string deviceID, IntPtr format, IntPtr endpointFormat);
            [PreserveSig]
            int GetProcessingPeriod(string deviceID, int defaultPeriod, out long period, out long minPeriod);
            [PreserveSig]
            int SetProcessingPeriod(string deviceID, ref long period);
            [PreserveSig]
            int GetShareMode(string deviceID, out int mode);
            [PreserveSig]
            int SetShareMode(string deviceID, ref int mode);
            [PreserveSig]
            int GetDevicePeriod(string deviceID, int defaultPeriod, out long period, out long minPeriod);
            [PreserveSig]
            int SetDevicePeriod(string deviceID, ref long period);
            [PreserveSig]
            int SetDefaultEndpoint(string deviceID, int role);
            [PreserveSig]
            int SetEndpointVisibility(string deviceID, int visible);
        }

        [ComImport, Guid("294935CE-F637-4E7C-A41B-AB255460B862")]
        class CPolicyConfigClient { }
    }
}
