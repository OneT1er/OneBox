using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PowerAudioManager
{
    /// <summary>
    /// 轮询前台应用 + 当前电源计划 + 默认音频输出设备，变化时触发事件。
    /// 供 AppProfileService 做被动学习与自动套用。前台 exe 名复用截图模块的
    /// QueryFullProcessImageName 方案（PROCESS_QUERY_LIMITED_INFORMATION，可读提权/UWP 进程，
    /// Process.MainModule 对这些进程抛访问拒绝）。
    /// 回调在 ThreadPool 线程触发，订阅者自行 Dispatcher 切换到 UI 线程。
    /// </summary>
    public static class ForegroundWatcher
    {
        public class Snapshot
        {
            public string ExeName;        // 前台 exe 无扩展名；无前台时为 null
            public string PowerPlanGuid;  // 当前激活电源计划 GUID
            public string AudioDeviceId;  // 默认音频输出端点 Id
            public override string ToString() => $"{ExeName ?? "(none)"} | {PowerPlanGuid} | {AudioDeviceId}";
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder buf, ref uint size);
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        static Timer _timer;
        static readonly object _lock = new object();
        static string _lastExe;     // null = 未初始化，首次 Tick 必触发
        static string _lastPower;
        static string _lastAudio;
        static bool _running;

        /// <summary>前台 exe 切换时触发（ThreadPool 线程）。</summary>
        public static event Action<Snapshot> ForegroundChanged;
        /// <summary>电源计划或默认音频设备变化时触发（ThreadPool 线程）。</summary>
        public static event Action<Snapshot> StateChanged;

        public static bool IsRunning => _running;

        /// <summary>仅取当前前台 exe 名（无扩展名），供大图等轻量查询，不启动后台轮询。</summary>
        public static string CaptureExeName() => GetExeName(GetForegroundWindow());

        /// <summary>拍一张当前状态快照（任意线程可调）。</summary>
        public static Snapshot Capture()
        {
            var s = new Snapshot { ExeName = GetExeName(GetForegroundWindow()) };
            try { s.PowerPlanGuid = PowerPlanService.GetActivePlanGuid() ?? ""; } catch { s.PowerPlanGuid = ""; }
            try
            {
                var dev = AudioDevices.GetOutputDevices()?.Find(d => d.IsDefault);
                s.AudioDeviceId = dev?.Id ?? "";
            }
            catch { s.AudioDeviceId = ""; }
            return s;
        }

        // hwnd -> exe 无扩展名。复用截图模块同款 P/Invoke 与容错。
        static string GetExeName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            try
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0) return null;
                var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return null;
                try
                {
                    var sb = new System.Text.StringBuilder(1024);
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

        public static void Start()
        {
            lock (_lock)
            {
                if (_running) return;
                _running = true;
                _lastExe = null; _lastPower = null; _lastAudio = null; // 强制首次 Tick 触发
                _timer = new Timer(Tick, null, 800, 2000);
            }
            AppLog.Log("FGWatch", "started");
        }

        public static void Stop()
        {
            lock (_lock)
            {
                _running = false;
                _timer?.Dispose();
                _timer = null;
            }
            AppLog.Log("FGWatch", "stopped");
        }

        static void Tick(object state)
        {
            if (!_running) return;
            Snapshot s;
            try { s = Capture(); }
            catch (Exception ex) { AppLog.Log("FGWatch", "capture fail: " + ex.Message); return; }

            bool exeChanged, stateChanged;
            lock (_lock)
            {
                exeChanged = _lastExe == null
                    || !string.Equals(_lastExe, s.ExeName ?? "", StringComparison.OrdinalIgnoreCase);
                stateChanged = _lastPower == null
                    || !string.Equals(_lastPower, s.PowerPlanGuid, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_lastAudio, s.AudioDeviceId, StringComparison.OrdinalIgnoreCase);
                _lastExe = s.ExeName ?? "";
                _lastPower = s.PowerPlanGuid;
                _lastAudio = s.AudioDeviceId;
            }

            if (exeChanged)
            {
                var h = ForegroundChanged;
                try { h?.Invoke(s); } catch (Exception ex) { AppLog.Log("FGWatch", ex); }
            }
            if (stateChanged)
            {
                var h = StateChanged;
                try { h?.Invoke(s); } catch (Exception ex) { AppLog.Log("FGWatch", ex); }
            }
        }
    }
}
