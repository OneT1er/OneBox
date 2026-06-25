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
                    FileName = Environment.ProcessPath,
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

}
