using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PowerAudioManager
{
    /// <summary>
    /// 轻量前台应用 exe 名捕获：复用 QueryFullProcessImageName 方案（PROCESS_QUERY_LIMITED_INFORMATION，
    /// 可读提权/UWP 进程，Process.MainModule 对这些进程抛访问拒绝）。
    /// 供性能图表前台标注（ForegroundHistory）等轻量查询，不启动后台轮询。
    /// </summary>
    public static class ForegroundWatcher
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder buf, ref uint size);
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// <summary>取当前前台 exe 名（无扩展名）；无前台或读取失败返回 null。不启动后台轮询。</summary>
        public static string CaptureExeName()
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return null;
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0) return null;
                var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return null;
                try
                {
                    var sb = new StringBuilder(1024);
                    uint size = 1024;
                    if (!QueryFullProcessImageName(h, 0, sb, ref size)) return null;
                    string name = sb.ToString();
                    if (string.IsNullOrEmpty(name)) return null;
                    return Path.GetFileNameWithoutExtension(name);
                }
                finally { CloseHandle(h); }
            }
            catch { return null; }
        }
    }
}
