using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace PowerAudioManager
{
    /// <summary>
    /// 进程类别（决策树特征之一，按 exe 名白名单归类）。
    /// </summary>
    public enum AppCategory { Other = 0, Game = 1, Creative = 2, VideoConf = 3 }

    /// <summary>
    /// 阶段1：特征采集器。后台 1s 定时采集一张数据快照，供学习引擎记录样本与实时推理。
    /// 只负责准确、稳定地提供数据，不写任何规则。CPU 走原生 GetSystemTimes（无 UAC、无 5s 预热、
    /// 无 PerformanceCounter 依赖），GPU 走 Windows 性能计数器 \GPU Engine(*engtype_3D)\Utilization Percentage
    /// 求和（Win10+，非管理员可用，实例随进程增减故 15s 刷新一次缓存）。全屏/电池/时间/进程类别同理轻量。
    /// 回调在 ThreadPool 线程触发，订阅者自行 Dispatcher 切到 UI 线程。
    /// </summary>
    public static class FeatureCollector
    {
        public class Snapshot
        {
            public DateTime Time;
            public float CpuLoad;          // 0-100
            public float GpuLoad;          // 0-100；-1 表示不可用（无 GPU 计数器/读失败）
            public bool Fullscreen;
            public bool OnBattery;         // true=用电池，false=接电源
            public float Hour;             // 0-24（带分钟小数）
            public AppCategory Category;
            public string ExeName;         // 前台 exe 无扩展名

            public int CategoryIndex => (int)Category;
            public string CategoryName => Category.ToString();
        }

        static Timer _timer;
        static readonly object _lock = new object();
        static bool _running;
        static int _intervalMs = 1000;

        public static bool IsRunning => _running;
        public static int IntervalMs
        {
            get => _intervalMs;
            set => _intervalMs = Math.Max(500, value);
        }

        /// <summary>每次采集完成触发（ThreadPool 线程）。</summary>
        public static event Action<Snapshot> Sampled;

        // ---- CPU：GetSystemTimes 差分 ----
        [StructLayout(LayoutKind.Sequential)]
        struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }
        [DllImport("kernel32.dll", SetLastError = false)]
        static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);
        static ulong _lastIdle, _lastKernel, _lastUser;
        static bool _cpuInited;

        // ---- GPU：性能计数器 ----
        // GPU Engine 计数器实例名形如 pid_xxxx_..._engtype_3D，随进程增减。缓存计数器列表，15s 刷新。
        static readonly List<PerformanceCounter> _gpuCounters = new List<PerformanceCounter>();
        static DateTime _gpuRefreshAt;
        static bool _gpuTried;            // 是否尝试过初始化（避免无 GPU 系统每秒重试）
        static bool _gpuAvailable = true; // false=确定无该计数器类别，停止尝试

        // ---- 全屏检测 ----
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        const uint MONITOR_DEFAULTTONEAREST = 2;

        public static void Start()
        {
            lock (_lock)
            {
                if (_running) return;
                _running = true;
                _timer = new Timer(Tick, null, 800, _intervalMs);
            }
            AppLog.Log("FeatCol", $"started interval={_intervalMs}ms");
        }

        public static void Stop()
        {
            lock (_lock)
            {
                _running = false;
                _timer?.Dispose();
                _timer = null;
            }
            lock (_gpuCounters)
            {
                foreach (var c in _gpuCounters) try { c.Dispose(); } catch { }
                _gpuCounters.Clear();
                _gpuTried = false; _gpuAvailable = true;
            }
            AppLog.Log("FeatCol", "stopped");
        }

        static void Tick(object state)
        {
            if (!_running) return;
            try
            {
                var s = Capture();
                var h = Sampled;
                try { h?.Invoke(s); } catch (Exception ex) { AppLog.Log("FeatCol", ex); }
            }
            catch (Exception ex) { AppLog.Log("FeatCol", "capture fail: " + ex.Message); }
        }

        /// <summary>拍一张当前特征快照（任意线程可调，线程安全）。</summary>
        public static Snapshot Capture()
        {
            var s = new Snapshot { Time = DateTime.Now };

            // 前台 exe：复用 ForegroundWatcher 的轻量捕获（仅 GetForegroundWindow + QueryFullProcessImageName，
            // 不读电源/音频——那两者走 powercfg/注册表较重，由 ForegroundWatcher 的 2s 状态轮询负责）。
            s.ExeName = ForegroundWatcher.CaptureExeName() ?? "";

            s.CpuLoad = ReadCpu();
            s.GpuLoad = ReadGpu();
            s.Fullscreen = IsForegroundFullscreen(s.ExeName);
            s.OnBattery = IsOnBattery();
            s.Hour = (float)s.Time.TimeOfDay.TotalHours; // 0-24
            s.Category = ClassifyExe(s.ExeName);
            return s;
        }

        // ---- CPU ----
        static float ReadCpu()
        {
            try
            {
                GetSystemTimes(out var idle, out var kernel, out var user);
                ulong idleNow = Ft(idle), kerNow = Ft(kernel), usrNow = Ft(user);
                if (!_cpuInited) { _lastIdle = idleNow; _lastKernel = kerNow; _lastUser = usrNow; _cpuInited = true; return 0; }
                ulong idleD = idleNow - _lastIdle, kerD = kerNow - _lastKernel, usrD = usrNow - _lastUser;
                ulong totalD = kerD + usrD;
                _lastIdle = idleNow; _lastKernel = kerNow; _lastUser = usrNow;
                if (totalD == 0) return 0;
                float cpu = 100f * (1f - (float)idleD / totalD);
                if (cpu < 0) cpu = 0; if (cpu > 100) cpu = 100;
                return cpu;
            }
            catch { return 0; }
        }
        static ulong Ft(FILETIME f) => ((ulong)f.dwHighDateTime << 32) | f.dwLowDateTime;

        // ---- GPU ----
        static float ReadGpu()
        {
            if (!_gpuAvailable) return -1;
            lock (_gpuCounters)
            {
                try
                {
                    if (!_gpuTried || (DateTime.Now - _gpuRefreshAt).TotalSeconds > 15)
                    {
                        RefreshGpuCounters();
                        _gpuRefreshAt = DateTime.Now;
                    }
                    if (_gpuCounters.Count == 0) return -1;
                    float sum = 0;
                    foreach (var c in _gpuCounters)
                    {
                        try { sum += c.NextValue(); } catch { } // 实例可能在缓存后被销毁，逐个容错
                    }
                    if (sum < 0) sum = 0; if (sum > 100) sum = 100;
                    return sum;
                }
                catch { return -1; }
            }
        }

        static void RefreshGpuCounters()
        {
            // 调用者持 _gpuCounters 锁
            foreach (var c in _gpuCounters) try { c.Dispose(); } catch { }
            _gpuCounters.Clear();
            _gpuTried = true;
            try
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                string[] insts = cat.GetInstanceNames();
                foreach (var inst in insts)
                {
                    if (inst == null || !inst.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
                    try { _gpuCounters.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true)); }
                    catch { }
                }
                if (_gpuCounters.Count > 0)
                    AppLog.Log("FeatCol", $"gpu counters={_gpuCounters.Count}");
            }
            catch (Exception ex) { AppLog.Log("FeatCol", "gpu cat fail: " + ex.Message); _gpuAvailable = false; }
        }

        // ---- 全屏 ----
        static bool IsForegroundFullscreen(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            // 排除自身与资源管理器：悬浮窗/桌面占满屏幕不算沉浸式全屏。
            if (exe.Equals("OneBox", StringComparison.OrdinalIgnoreCase)) return false;
            if (exe.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;
                if (!GetWindowRect(hwnd, out var wr)) return false;
                IntPtr hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(hmon, ref mi)) return false;
                var m = mi.rcMonitor;
                int ww = wr.Right - wr.Left, wh = wr.Bottom - wr.Top;
                int mw = m.Right - m.Left, mh = m.Bottom - m.Top;
                if (mw <= 0 || mh <= 0) return false;
                // 窗口几乎铺满整块显示器（容差 4px）视为全屏。
                return ww >= mw - 4 && wh >= mh - 4
                    && wr.Left <= m.Left + 4 && wr.Top <= m.Top + 4;
            }
            catch { return false; }
        }

        // ---- 电池 ----
        static bool IsOnBattery()
        {
            try
            {
                var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                return ps.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Online;
            }
            catch { return false; }
        }

        // ---- 进程类别白名单 ----
        static readonly HashSet<string> _games = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 常见游戏
            "valorant","leagueclient","league of legends","csgo","cs2","dota2","genshinimpact","yuanshen",
            "minecraft","minecraftlauncher","overwatch","apex_legends","r5apex","fortnite","fortniteclient-win64-shipping",
            "rustclient","tslgame","tslgame_wx","gtav","playgtav","gta5","rdr2","cyberpunk2077","witcher3",
            "hoi4","eu4","stellaris","ck3","citiesskylines","eldenring","sekiro","darksoulsiii","bg3","baldursgate3",
            "hogwartslegacy","ffxiv","ffxiv_dx11","world of warcraft","hearthstone","diablo iv","diabloiv",
            "team fortress 2","tf2","starcraft","sc2","warframe","destiny2","r6vegas2","rainbowsix","r6game",
            "gmod","csgo.exe","wallpaper64","genshinimpactcloudgame","sf6","streetfighter6","tekken8","mortal kombat 1",
            "honkaisr","starrail","zenlesszonezero","wuthering waves","kqlwbi",
            // 启动器/平台
            "steam","steamwebhelper","epicgameslauncher","epicgames","origin","eadesktop","ea app","uplay",
            "ubisoftconnect","battle.net","galaxyclient","riotclientservices","riotclient","bethesdanetlauncher",
            "battle.net.exe","weigame","qlauncher","rockstar games launcher","socialclub",
        };
        static readonly HashSet<string> _creative = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "adobe premiere pro","premiere","adobe premiere pro 2024","adobe premiere pro 2025","afterfx","adobe after effects",
            "photoshop","adobe photoshop 2024","adobe photoshop 2025","illustrator","indesign","audition","adobe media encoder",
            "mediaencoder","blender","maya","3dsmax","3dsmaxwindowsx64","cinema4d","c4d","davinciresolve","vegas","vegas140",
            "vegas200","vegas365","lightroom","adobe lightroom","lightroom cc","unrealeditor","unreal editor","unity",
            "substancepainter","substance3dpainter","substance designer","houdini","houdinifx","zbrush","keyshot","fusion360",
            "octanerender","redshift","vray","nuke","davinci resolve","resolve","hitfilm","shotcut","kdenlive","obs64","obs32",
        };
        static readonly HashSet<string> _videoconf = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "zoom","teams","ms-teams","msteams","skype","discord","slack","lark","feishu","voovmeeting","wemeet",
            "wemeetapp","tencentmeeting","dingtalk","webex","gotomeeting","bluejeans","ringcentral","teams classic",
            "zoomoutlookplugin","mta","腾讯会议","飞书","微信","wechat","trillian","teamstalker",
        };

        public static AppCategory ClassifyExe(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return AppCategory.Other;
            // 用户自定义游戏进程（注册表 Learn.CustomGames，分号分隔）
            var custom = AppPrefs.GetString("Learn.CustomGames", "");
            if (!string.IsNullOrWhiteSpace(custom))
            {
                foreach (var part in custom.Split(';', ',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (exe.Equals(part.Trim(), StringComparison.OrdinalIgnoreCase)) return AppCategory.Game;
                }
            }
            if (_games.Contains(exe)) return AppCategory.Game;
            if (_creative.Contains(exe)) return AppCategory.Creative;
            if (_videoconf.Contains(exe)) return AppCategory.VideoConf;
            return AppCategory.Other;
        }
    }
}
