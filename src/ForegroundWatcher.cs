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
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint command);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder buf, ref uint size);
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint GW_HWNDNEXT = 2;

        /// <summary>
        /// 取当前前台 exe 名（无扩展名）。当 OneBox 自己位于前台时，返回 Z 序中紧邻其后的
        /// 可见应用，避免性能趋势窗口把整段前台历史都记录成 OneBox。
        /// </summary>
        public static string CaptureExeName()
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return null;
                string foreground = GetExeName(hwnd, out uint pid);
                if (pid != (uint)Environment.ProcessId) return foreground;

                // 性能趋势/设置等 OneBox 窗口会抢到焦点。从当前窗口向 Z 序后方寻找第一个
                // 有标题的可见外部窗口，它就是用户打开 OneBox 前正在使用的应用。
                for (IntPtr candidate = GetWindow(hwnd, GW_HWNDNEXT);
                     candidate != IntPtr.Zero;
                     candidate = GetWindow(candidate, GW_HWNDNEXT))
                {
                    if (!IsWindowVisible(candidate) || GetWindowTextLength(candidate) == 0) continue;
                    string name = GetExeName(candidate, out uint candidatePid);
                    if (candidatePid != 0 && candidatePid != (uint)Environment.ProcessId && !string.IsNullOrEmpty(name))
                        return name;
                }
                return null;
            }
            catch { return null; }
        }

        static string GetExeName(IntPtr hwnd, out uint pid)
        {
            pid = 0;
            try
            {
                GetWindowThreadProcessId(hwnd, out pid);
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
