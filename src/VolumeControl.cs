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

}
