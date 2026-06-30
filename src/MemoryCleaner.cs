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
    public static class MemoryCleaner
    {
        // SE_INCREASE_QUOTA_NAME + SE_PROF_SINGLE_PROCESS_NAME for system-level cleaning
        const int SE_PRIVILEGE_ENABLED = 2;
        const int TOKEN_QUERY = 8;
        const int TOKEN_ADJUST_PRIVILEGES = 32;

        [StructLayout(LayoutKind.Sequential)]
        struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)]
        struct TOKEN_PRIVILEGES { public int Count; public LUID Luid; public int Attr; }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr h, int access, out IntPtr tok);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool LookupPrivilegeValue(string sys, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr tok, bool disable, ref TOKEN_PRIVILEGES newp, int len, IntPtr old, IntPtr ret);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);

        // NtSetSystemInformation classes
        const int SystemMemoryListInformation = 80;
        const int SystemFileCacheInformation = 21;
        const int SystemCombinePhysicalMemoryInformation = 130;
        // Memory list commands
        const int MemoryEmptyWorkingSets = 2;
        const int MemoryFlushModifiedList = 3;
        const int MemoryPurgeStandbyList = 4;
        const int MemoryPurgeLowPriorityStandbyList = 5;

        [DllImport("ntdll.dll")]
        static extern int NtSetSystemInformation(int infoClass, ref int data, int size);
        [DllImport("ntdll.dll")]
        static extern int NtSetSystemInformation(int infoClass, ref SYSTEM_FILECACHE_INFORMATION info, int size);
        [DllImport("ntdll.dll")]
        static extern int NtSetSystemInformation(int infoClass, ref MEMORY_COMBINE_INFORMATION_EX info, int size);

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_FILECACHE_INFORMATION
        {
            public IntPtr CurrentSize;
            public IntPtr PeakSize;
            public uint PageFaultCount;
            public IntPtr MinimumWorkingSet;
            public IntPtr MaximumWorkingSet;
            public IntPtr CurrentSizeIncludingTransitionInPages;
            public IntPtr PeakSizeIncludingTransitionInPages;
            public uint TransitionRePurposeCount;
            public uint Flags;
        }

        // SystemCombinePhysicalMemoryInformation (130) input struct:
        // { HANDLE RegionHandle; ULONG Flags; } — zeroed here to ask the kernel to combine
        // all physical memory lists. Marshalled by ref with its real size so we never hand
        // the kernel a too-large size for a too-small buffer (the old code passed a 4-byte
        // stack int but claimed size 16, over-reading 12 bytes of stack).
        [StructLayout(LayoutKind.Sequential)]
        struct MEMORY_COMBINE_INFORMATION_EX
        {
            public IntPtr RegionHandle;
            public uint Flags;
        }

        static bool EnablePrivilege(string name)
        {
            IntPtr tok;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out tok))
                return false;
            try
            {
                LUID luid;
                if (!LookupPrivilegeValue(null, name, out luid)) return false;
                var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attr = SE_PRIVILEGE_ENABLED };
                return AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(tok); }
        }

        public static long GetTotalPhysicalKb()
        {
            // 用 GlobalMemoryStatusEx（无分配）替代 PerformanceCounter，
            // 后者不释放会泄漏内核性能计数器映射。
            var s = GetStatus();
            return s == null ? 0 : (long)(s.AvailableBytes / 1024);
        }

        public class MemoryStatus
        {
            public ulong TotalBytes;
            public ulong AvailableBytes;
            public uint MemoryLoadPercent;
            public ulong CachedBytes; // 已缓存 = 所有待机列表 + 已修改页列表
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX mse);

        // 已缓存内存 — 匹配任务管理器的 Cached 值。复用长期 PerformanceCounter 实例
        // （按需创建会泄漏内核性能计数器映射，缓存则不会）。
        // 性能陷阱：.NET 8 冷启动时创建这 4 个计数器约需 5s（WMI/COM 初始化 + JIT），
        // 严禁在 UI 线程创建。GetStatus() 在 LoadData 同步序中调用，懒加载首次创建
        // 会冻结悬浮窗约 5s。用 WarmupCounters 在后台预热，就绪前 ReadCachedBytes 返回 0。
        static System.Diagnostics.PerformanceCounter _standbyCore, _standbyNormal, _standbyReserve, _modified;
        static volatile bool _countersReady;
        static readonly object _counterLock = new object();

        // 启动时在后台线程预热，UI 线程无需承担 ~5s 创建开销。可多次调用。
        public static void WarmupCounters()
        {
            if (_countersReady) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    lock (_counterLock)
                    {
                        if (_countersReady) return;
                        _standbyCore = new System.Diagnostics.PerformanceCounter("Memory", "Standby Cache Core Bytes", true);
                        _standbyNormal = new System.Diagnostics.PerformanceCounter("Memory", "Standby Cache Normal Priority Bytes", true);
                        _standbyReserve = new System.Diagnostics.PerformanceCounter("Memory", "Standby Cache Reserve Bytes", true);
                        _modified = new System.Diagnostics.PerformanceCounter("Memory", "Modified Page List Bytes", true);
                        // 预热 NextValue（首次调用约 25ms 初始化），使 UI 线程首次真实读取即时。
                        try { _standbyCore.NextValue(); _standbyNormal.NextValue(); _standbyReserve.NextValue(); _modified.NextValue(); } catch { }
                        _countersReady = true;
                    }
                }
                catch { /* non-fatal: cached bytes just reads 0 */ }
            });
        }

        static ulong ReadCachedBytes()
        {
            try
            {
                if (!_countersReady) return 0; // 后台预热中，不阻塞 UI
                return (ulong)(_standbyCore.NextValue() + _standbyNormal.NextValue() + _standbyReserve.NextValue() + _modified.NextValue());
            }
            catch { return 0; }
        }

        public static MemoryStatus GetStatus()
        {
            var m = new MEMORYSTATUSEX();
            if (!GlobalMemoryStatusEx(m)) return null;
            var s = new MemoryStatus { TotalBytes = m.ullTotalPhys, AvailableBytes = m.ullAvailPhys, MemoryLoadPercent = m.dwMemoryLoad };
            s.CachedBytes = ReadCachedBytes();
            return s;
        }

        public class CleanResult
        {
            public ulong FreedBytes;
            public bool WorkingSetsEmptied;
            public bool StandbyPurged;
            public bool ModifiedFlushed;
            public bool FileCacheReleased;
        }

        [Flags]
        public enum CleanFlags
        {
            None = 0,
            WorkingSet         = 1 << 0,
            SystemFileCache    = 1 << 1,
            ModifiedPageList   = 1 << 2,
            StandbyList        = 1 << 3,  // 含优先级
            StandbyListNoPrio  = 1 << 4,  // 仅低优先级页
            ModifiedFileCache  = 1 << 5,  // MemoryFlushModifiedList 别名
            RegistryCache      = 1 << 6,  // Win8.1+
            CombineMemoryLists = 1 << 7,  // Win10+
            // StandbyList 全清和 ModifiedPageList 刷新可能造成短暂系统卡顿，默认关闭。
            Default = WorkingSet | SystemFileCache | StandbyListNoPrio | ModifiedFileCache | RegistryCache | CombineMemoryLists
        }

        public static CleanResult CleanAll() { return CleanAll(CleanFlags.Default); }

        public static CleanResult CleanAll(CleanFlags flags)
        {
            var before = GetStatus();
            ulong availBefore = before == null ? 0 : before.AvailableBytes;
            var r = new CleanResult();

            EnablePrivilege("SeIncreaseQuotaPrivilege");
            EnablePrivilege("SeProfileSingleProcessPrivilege");

            // 1) 清空所有进程工作集
            if ((flags & CleanFlags.WorkingSet) != 0)
            {
                int cmd = MemoryEmptyWorkingSets;
                bool nt = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;
                if (!nt)
                {
                    // 非管理员回退：遍历可访问进程逐一 EmptyWorkingSet。
                    int succeeded = 0;
                    try
                    {
                        foreach (var p in System.Diagnostics.Process.GetProcesses())
                        {
                            try { if (EmptyWorkingSet(p.Handle)) succeeded++; }
                            catch { }
                            finally { try { p.Dispose(); } catch { } }
                        }
                    }
                    catch { }
                    r.WorkingSetsEmptied = succeeded > 0;
                }
                else
                {
                    r.WorkingSetsEmptied = true;
                }
            }

            // 2) 释放系统文件缓存
            if ((flags & CleanFlags.SystemFileCache) != 0)
            {
                try
                {
                    var fci = new SYSTEM_FILECACHE_INFORMATION { MinimumWorkingSet = (IntPtr)(-1), MaximumWorkingSet = (IntPtr)(-1) };
                    r.FileCacheReleased = NtSetSystemInformation(SystemFileCacheInformation, ref fci, Marshal.SizeOf(typeof(SYSTEM_FILECACHE_INFORMATION))) == 0;
                }
                catch { }
            }

            // 3) 刷新已修改页列表（回写脏页使其可回收）
            if ((flags & CleanFlags.ModifiedPageList) != 0)
            {
                int cmd = MemoryFlushModifiedList;
                r.ModifiedFlushed = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;
            }

            // 4) 待机列表（含优先级，移除全部待机页）
            if ((flags & CleanFlags.StandbyList) != 0)
            {
                int cmd = MemoryPurgeStandbyList;
                r.StandbyPurged = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;
            }

            // 4b) 待机列表（仅低优先级页）
            if ((flags & CleanFlags.StandbyListNoPrio) != 0)
            {
                int cmd = MemoryPurgeLowPriorityStandbyList;
                r.StandbyPurged = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0 || r.StandbyPurged;
            }

            // 5) 已修改文件缓存
            if ((flags & CleanFlags.ModifiedFileCache) != 0 && !r.ModifiedFlushed)
            {
                int cmd = MemoryFlushModifiedList;
                r.ModifiedFlushed = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;
            }

            // 6) 注册表缓存 (Win8.1+)
            if ((flags & CleanFlags.RegistryCache) != 0 && AdminUtils.RealOsVersion() >= new Version(6, 3))
            {
                try
                {
                    int cmd = 0;
                    NtSetSystemInformation(155, ref cmd, sizeof(int));
                }
                catch { }
            }

            // 7) 合并内存列表 (Win10+)
            if ((flags & CleanFlags.CombineMemoryLists) != 0 && AdminUtils.RealOsVersion().Major >= 10)
            {
                try
                {
                    var info = new MEMORY_COMBINE_INFORMATION_EX(); // zeroed: combine all lists
                    NtSetSystemInformation(SystemCombinePhysicalMemoryInformation, ref info, Marshal.SizeOf(typeof(MEMORY_COMBINE_INFORMATION_EX)));
                }
                catch { }
            }


            try { EmptyWorkingSet(GetCurrentProcess()); } catch { }
            System.Threading.Thread.Sleep(500);
            var after = GetStatus();
            if (after != null && after.AvailableBytes > availBefore)
                r.FreedBytes = after.AvailableBytes - availBefore;
            return r;
        }

        public static CleanFlags GetSavedFlags()
        {
            var f = CleanFlags.None;
            if (AppPrefs.GetBool("Clean.WorkingSet", true)) f |= CleanFlags.WorkingSet;
            if (AppPrefs.GetBool("Clean.SystemFileCache", true)) f |= CleanFlags.SystemFileCache;
            if (AppPrefs.GetBool("Clean.ModifiedPageList", false)) f |= CleanFlags.ModifiedPageList;
            if (AppPrefs.GetBool("Clean.StandbyList", false)) f |= CleanFlags.StandbyList;
            if (AppPrefs.GetBool("Clean.StandbyListNoPrio", true)) f |= CleanFlags.StandbyListNoPrio;
            if (AppPrefs.GetBool("Clean.ModifiedFileCache", true)) f |= CleanFlags.ModifiedFileCache;
            if (AppPrefs.GetBool("Clean.RegistryCache", true)) f |= CleanFlags.RegistryCache;
            if (AppPrefs.GetBool("Clean.CombineMemoryLists", true)) f |= CleanFlags.CombineMemoryLists;
            if (f == CleanFlags.None) f = CleanFlags.Default;
            return f;
        }
    }

}
