using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PowerAudioManager
{
    /// <summary>
    /// admin 悬浮窗里的快捷启动容器（HwndHost）：创建子 HWND，启动普通权限 --launcher 进程
    /// SetParent 嵌入。拖放由普通进程处理，绕过 admin UIPI。
    /// </summary>
    public class LauncherHost : HwndHost
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyWindow(IntPtr hwnd);

        IntPtr _hwnd;
        System.Diagnostics.Process _proc;

        protected override HandleRef BuildWindowCore(HandleRef parent)
        {
            int w = (int)ActualWidth; if (w < 200) w = 280;
            int h = (int)ActualHeight; if (h < 50) h = 70;
            _hwnd = CreateWindowEx(0, "Static", "", 0x40000000 /*WS_CHILD*/, 0, 0, w, h, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            try
            {
                _proc = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath,
                        Arguments = "--launcher " + _hwnd.ToInt64(),
                        UseShellExecute = false
                    }
                };
                _proc.Start();
                AppLog.Log("LauncherHost", $"started --launcher parent={_hwnd.ToInt64()}");
            }
            catch (Exception ex) { AppLog.Log("LauncherHost", ex); }
            return new HandleRef(this, _hwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            try { _proc?.Kill(); } catch { }
            DestroyWindow(hwnd.Handle);
        }
    }
}
