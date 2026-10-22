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
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using OneBox.Contracts;

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

        // 已缓存内存 — 匹配任务管理器的 Cached 值（所有待机列表 + 已修改页列表）。
        // 不再在 GUI 进程里创建 PerformanceCounter：那套库冷启动约 5s，并常驻
        // 计数器注册表数据与内核映射。改为：
        //   1) 管理员 GUI：自己用 NtQuerySystemInformation(80) 读取（需
        //      SeProfileSingleProcessPrivilege，管理员令牌持有）；
        //   2) 普通 GUI：经已有的 OneBoxSvc 命令管道发 MemoryListStatus；
        //   3) 都不可用：GetPerformanceInfo 的系统缓存页数兜底（偏小）。
        // GetStatus() 在 UI 线程调用，因此读取永不阻塞：返回最近一次结果，
        // 过期（>2s）时在后台刷新。未请求（板块隐藏）时返回 0，与旧行为一致。
        static readonly TimeSpan CachedRefreshInterval = TimeSpan.FromSeconds(2);
        static readonly TimeSpan ServiceRetryDelay = TimeSpan.FromSeconds(60);
        static long _cachedBytes;
        static long _cachedAtTicks;
        static long _serviceRetryAtTicks;
        static int _refreshInFlight;
        static int _profilePrivilegeTried;
        static volatile bool _cachedRequested;
        // Elevation is fixed for the process lifetime; avoid building a
        // WindowsPrincipal every refresh.
        static readonly Lazy<bool> _isAdmin = new Lazy<bool>(AdminUtils.IsAdmin);

        [DllImport("ntdll.dll")]
        static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

        [StructLayout(LayoutKind.Sequential)]
        struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
                KernelTotal, KernelPaged, KernelNonpaged, PageSize;
            public uint HandleCount, ProcessCount, ThreadCount;
        }
        [DllImport("psapi.dll", SetLastError = true)]
        static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, int size);

        // 旧名称保留给调用方：现在只是开启/关闭缓存值的后台刷新，不再创建计数器。
        public static void WarmupCounters()
        {
            _cachedRequested = true;
            RequestCachedRefresh();
        }

        public static void ReleaseCounters()
        {
            _cachedRequested = false;
            Interlocked.Exchange(ref _cachedBytes, 0);
            Interlocked.Exchange(ref _cachedAtTicks, 0);
        }

        static ulong ReadCachedBytes()
        {
            if (!_cachedRequested) return 0;
            long at = Interlocked.Read(ref _cachedAtTicks);
            if (at == 0 || DateTime.UtcNow.Ticks - at >= CachedRefreshInterval.Ticks) RequestCachedRefresh();
            return (ulong)Interlocked.Read(ref _cachedBytes);
        }

        static void RequestCachedRefresh()
        {
            if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ulong? value = QueryCachedBytes();
                    if (value.HasValue && _cachedRequested)
                    {
                        Interlocked.Exchange(ref _cachedBytes, (long)value.Value);
                        Interlocked.Exchange(ref _cachedAtTicks, DateTime.UtcNow.Ticks);
                    }
                }
                catch { /* non-fatal: cached bytes keeps its last value */ }
                finally { Interlocked.Exchange(ref _refreshInFlight, 0); }
            });
        }

        static ulong? QueryCachedBytes()
        {
            if (_isAdmin.Value)
            {
                var direct = QueryMemoryListsDirect();
                if (direct != null) return direct.CachedBytes;
            }
            if (DateTime.UtcNow.Ticks >= Interlocked.Read(ref _serviceRetryAtTicks))
            {
                var viaService = QueryMemoryListsFromService();
                if (viaService != null) return viaService.CachedBytes;
                Interlocked.Exchange(ref _serviceRetryAtTicks, DateTime.UtcNow.Add(ServiceRetryDelay).Ticks);
            }
            return QuerySystemCacheFallback();
        }

        static MemoryListStatusResult QueryMemoryListsDirect()
        {
            if (Interlocked.Exchange(ref _profilePrivilegeTried, 1) == 0)
                EnablePrivilege("SeProfileSingleProcessPrivilege");
            int fieldCount = MemoryListLayout.FieldCount;
            int length = fieldCount * IntPtr.Size;
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (NtQuerySystemInformation(MemoryListLayout.InformationClass, buffer, length, out _) != 0) return null;
                var fields = new ulong[fieldCount];
                for (int i = 0; i < fieldCount; i++)
                    fields[i] = (ulong)(nuint)Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                return MemoryListLayout.FromPageCounts(fields, (ulong)Environment.SystemPageSize);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // Same identity rules as the clean request: connect to the per-user
        // command pipe and refuse any server that is not the LocalSystem
        // service.  Short timeouts: this is a status read, not an action.
        static MemoryListStatusResult QueryMemoryListsFromService()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query))
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                using (var client = new NamedPipeClientStream(".", PipeNames.ForCommand(identity.User?.Value), PipeDirection.InOut,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation))
                {
                    client.ConnectAsync(1000, timeout.Token).GetAwaiter().GetResult();
                    PipeServerIdentityVerifier.EnsureLocalSystemServer(client);
                    var request = IpcRequest.Create(IpcCommand.MemoryListStatus, new { });
                    IpcFraming.WriteAsync(client, request, timeout.Token).GetAwaiter().GetResult();
                    IpcResponse response = IpcFraming.ReadAsync<IpcResponse>(client, timeout.Token).GetAwaiter().GetResult();
                    if (response == null || response.Version != IpcProtocol.Version || response.RequestId != request.RequestId
                        || response.Command != IpcCommand.MemoryListStatus || !response.Success)
                        return null;
                    return response.ReadResult<MemoryListStatusResult>();
                }
            }
            catch { return null; }
        }

        static ulong? QuerySystemCacheFallback()
        {
            int size = Marshal.SizeOf<PERFORMANCE_INFORMATION>();
            var info = new PERFORMANCE_INFORMATION { cb = (uint)size };
            if (!GetPerformanceInfo(out info, size)) return null;
            return (ulong)info.SystemCache * (ulong)info.PageSize;
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
            // None 是用户明确的选择，不得提升为默认清理，也不应触发权限、休眠或工作集操作。
            if (!HasSelectedAreas(flags)) return new CleanResult();

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

        public static bool HasSelectedAreas(CleanFlags flags)
        {
            const CleanFlags all = CleanFlags.WorkingSet | CleanFlags.SystemFileCache |
                CleanFlags.ModifiedPageList | CleanFlags.StandbyList | CleanFlags.StandbyListNoPrio |
                CleanFlags.ModifiedFileCache | CleanFlags.RegistryCache | CleanFlags.CombineMemoryLists;
            return (flags & all) != CleanFlags.None;
        }

        public static CleanFlags ComposeFlags(
            bool workingSet,
            bool systemFileCache,
            bool modifiedPageList,
            bool standbyList,
            bool standbyListNoPriority,
            bool modifiedFileCache,
            bool registryCache,
            bool combineMemoryLists)
        {
            var flags = CleanFlags.None;
            if (workingSet) flags |= CleanFlags.WorkingSet;
            if (systemFileCache) flags |= CleanFlags.SystemFileCache;
            if (modifiedPageList) flags |= CleanFlags.ModifiedPageList;
            if (standbyList) flags |= CleanFlags.StandbyList;
            if (standbyListNoPriority) flags |= CleanFlags.StandbyListNoPrio;
            if (modifiedFileCache) flags |= CleanFlags.ModifiedFileCache;
            if (registryCache) flags |= CleanFlags.RegistryCache;
            if (combineMemoryLists) flags |= CleanFlags.CombineMemoryLists;
            return flags;
        }

        public static CleanFlags GetSavedFlags()
        {
            return ComposeFlags(
                AppPrefs.GetBool("Clean.WorkingSet", true),
                AppPrefs.GetBool("Clean.SystemFileCache", true),
                AppPrefs.GetBool("Clean.ModifiedPageList", false),
                AppPrefs.GetBool("Clean.StandbyList", false),
                AppPrefs.GetBool("Clean.StandbyListNoPrio", true),
                AppPrefs.GetBool("Clean.ModifiedFileCache", true),
                AppPrefs.GetBool("Clean.RegistryCache", true),
                AppPrefs.GetBool("Clean.CombineMemoryLists", true));
        }
    }

}
