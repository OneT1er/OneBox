using System;
using NAudio.CoreAudioApi;

namespace PowerAudioManager
{
    /// <summary>
    /// Keeps slider notifications from becoming a stream of audio commands.
    /// Programmatic UI synchronization is never accepted, equal values are
    /// ignored, and real user changes are coalesced to at most one command per
    /// interval. The latest value remains pending so the end of a drag is not
    /// lost.
    /// </summary>
    public sealed class AudioVolumeCommandGate
    {
        public const float EqualityEpsilon = 0.001f;
        readonly TimeSpan _minimumInterval;
        float? _lastSent;
        DateTime _lastSentAt;
        bool _hasSent;
        float? _pending;

        public AudioVolumeCommandGate(TimeSpan minimumInterval)
        {
            if (minimumInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumInterval));
            _minimumInterval = minimumInterval;
        }

        public bool HasPending => _pending.HasValue;

        public void CancelPending()
        {
            _pending = null;
        }

        public bool TryAccept(float value, bool programmatic, DateTime now, out float dispatchValue)
        {
            dispatchValue = 0;
            if (programmatic || float.IsNaN(value) || value < 0 || value > 1)
            {
                _pending = null;
                return false;
            }

            value = VolumeControl.Clamp(value);
            if (_lastSent.HasValue && NearlyEqual(_lastSent.Value, value))
            {
                _pending = null;
                return false;
            }

            _pending = value;
            if (_hasSent && now - _lastSentAt < _minimumInterval) return false;
            return TryFlush(now, out dispatchValue);
        }

        public bool TryFlush(DateTime now, out float dispatchValue)
        {
            dispatchValue = 0;
            if (!_pending.HasValue || (_hasSent && now - _lastSentAt < _minimumInterval)) return false;
            dispatchValue = _pending.Value;
            _pending = null;
            _lastSent = dispatchValue;
            _lastSentAt = now;
            _hasSent = true;
            return true;
        }

        static bool NearlyEqual(float left, float right)
        {
            return Math.Abs(left - right) < EqualityEpsilon;
        }
    }

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
                try
                {
                    float target = Clamp(value);
                    // Avoid writing the endpoint when a refresh/retry reports
                    // the value it already has. This is especially important
                    // during device graph notifications.
                    if (Math.Abs(Clamp(endpoint.MasterVolumeLevelScalar) - target) < AudioVolumeCommandGate.EqualityEpsilon)
                        return;
                    endpoint.MasterVolumeLevelScalar = target;
                }
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
