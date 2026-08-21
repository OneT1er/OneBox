using System;
using NAudio.CoreAudioApi;

namespace PowerAudioManager
{
    public interface IAudioEndpointVolumeSession
    {
        float Volume { get; set; }
        bool Mute { get; set; }
    }

    /// <summary>
    /// Small, platform-free cache/recovery decision layer. Production uses the
    /// NAudio implementation below; tests can inject a fake endpoint to verify
    /// device disappearance and recovery without requiring an audio device.
    /// </summary>
    public sealed class AudioVolumeSession
    {
        readonly Func<IAudioEndpointVolumeSession> _factory;
        IAudioEndpointVolumeSession _current;

        public AudioVolumeSession(Func<IAudioEndpointVolumeSession> factory) { _factory = factory; }

        IAudioEndpointVolumeSession Endpoint => _current ?? (_current = _factory?.Invoke());

        public float GetVolume()
        {
            try { return VolumeControl.Clamp(Endpoint?.Volume ?? 0); }
            catch { _current = null; return 0; }
        }

        public bool TrySetVolume(float value)
        {
            try { var endpoint = Endpoint; if (endpoint == null) return false; endpoint.Volume = VolumeControl.Clamp(value); return true; }
            catch { _current = null; return false; }
        }

        public bool TryGetMute(out bool muted)
        {
            try { var endpoint = Endpoint; if (endpoint == null) { muted = false; return false; } muted = endpoint.Mute; return true; }
            catch { _current = null; muted = false; return false; }
        }

        public bool TrySetMute(bool muted)
        {
            try { var endpoint = Endpoint; if (endpoint == null) return false; endpoint.Mute = muted; return true; }
            catch { _current = null; return false; }
        }

        public void Invalidate() { _current = null; }
    }

    public static class VolumeControl
    {
        static readonly object Sync = new object();
        static MMDeviceEnumerator _enumerator;
        static MMDevice _device;
        static AudioEndpointVolume _endpoint;

        static AudioEndpointVolume GetEndpoint()
        {
            lock (Sync)
            {
                if (_endpoint != null) return _endpoint;
                try
                {
                    _enumerator = _enumerator ?? new MMDeviceEnumerator();
                    if (!_enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Console, out _device))
                        return null;
                    _endpoint = _device.AudioEndpointVolume;
                    return _endpoint;
                }
                catch (Exception ex)
                {
                    AppLog.Log("Get audio endpoint volume", ex);
                    InvalidateCore();
                    return null;
                }
            }
        }

        public static void Invalidate()
        {
            lock (Sync) InvalidateCore();
        }

        static void InvalidateCore()
        {
            try { _endpoint?.Dispose(); } catch (Exception ex) { AppLog.Log("Dispose audio volume", ex); }
            try { _device?.Dispose(); } catch (Exception ex) { AppLog.Log("Dispose audio device", ex); }
            _endpoint = null;
            _device = null;
            // The enumerator is cheap and can be recreated after a device graph
            // reset; retaining it is useful between normal UI refreshes.
        }

        public static void Shutdown()
        {
            lock (Sync)
            {
                InvalidateCore();
                try { _enumerator?.Dispose(); } catch (Exception ex) { AppLog.Log("Dispose audio enumerator", ex); }
                _enumerator = null;
            }
        }

        public static float GetVolume()
        {
            lock (Sync)
            {
                var endpoint = GetEndpoint();
                if (endpoint == null) return 0;
                try { return Clamp(endpoint.MasterVolumeLevelScalar); }
                catch (Exception ex) { AppLog.Log("Get volume", ex); InvalidateCore(); return 0; }
            }
        }

        public static void SetVolume(float value)
        {
            lock (Sync)
            {
                var endpoint = GetEndpoint();
                if (endpoint == null) return;
                try { endpoint.MasterVolumeLevelScalar = Clamp(value); }
                catch (Exception ex) { AppLog.Log("Set volume", ex); InvalidateCore(); }
            }
        }

        public static bool GetMute()
        {
            lock (Sync)
            {
                var endpoint = GetEndpoint();
                if (endpoint == null) return false;
                try { return endpoint.Mute; }
                catch (Exception ex) { AppLog.Log("Get mute", ex); InvalidateCore(); return false; }
            }
        }

        public static void SetMute(bool muted)
        {
            lock (Sync)
            {
                var endpoint = GetEndpoint();
                if (endpoint == null) return;
                try { endpoint.Mute = muted; }
                catch (Exception ex) { AppLog.Log("Set mute", ex); InvalidateCore(); }
            }
        }

        public static float Clamp(float value)
        {
            if (float.IsNaN(value)) return 0;
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }
    }
}
