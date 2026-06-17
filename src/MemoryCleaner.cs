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
            try { var c = new System.Diagnostics.PerformanceCounter("Memory", "Available KBytes"); return (long)c.NextValue(); } catch { return 0; }
        }

        public class MemoryStatus
        {
            public ulong TotalBytes;
            public ulong AvailableBytes;
            public uint MemoryLoadPercent;
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

        public static MemoryStatus GetStatus()
        {
            var m = new MEMORYSTATUSEX();
            if (!GlobalMemoryStatusEx(m)) return null;
            return new MemoryStatus { TotalBytes = m.ullTotalPhys, AvailableBytes = m.ullAvailPhys, MemoryLoadPercent = m.dwMemoryLoad };
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
            StandbyList        = 1 << 3,  // priority
            StandbyListNoPrio  = 1 << 4,  // low-priority pages only
            ModifiedFileCache  = 1 << 5,  // alias of MemoryFlushModifiedList
            RegistryCache      = 1 << 6,  // win8.1+
            CombineMemoryLists = 1 << 7,  // win10+
            Default = WorkingSet | SystemFileCache | StandbyListNoPrio | ModifiedFileCache | RegistryCache
        }

        public static CleanResult CleanAll() { return CleanAll(CleanFlags.Default); }

        public static CleanResult CleanAll(CleanFlags flags)
        {
            var before = GetStatus();
            ulong availBefore = before == null ? 0 : before.AvailableBytes;
            var r = new CleanResult();

            EnablePrivilege("SeIncreaseQuotaPrivilege");
            EnablePrivilege("SeProfileSingleProcessPrivilege");

            // 1) Empty all process working sets
            if ((flags & CleanFlags.WorkingSet) != 0)
            {
                int cmd = MemoryEmptyWorkingSets;
                bool nt = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;
                if (!nt)
                {
                    // Non-admin fallback: walk every accessible process and EmptyWorkingSet each.
                    // EmptyWorkingSet is a per-process API and works without admin for user-owned processes.
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

            // 2) System file cache release
            if ((flags & CleanFlags.SystemFileCache) != 0)
            {
                try
                {
                    var fci = new SYSTEM_FILECACHE_INFORMATION { MinimumWorkingSet = (IntPtr)(-1), MaximumWorkingSet = (IntPtr)(-1) };
                    r.FileCacheReleased = NtSetSystemInformation(SystemFileCacheInformation, ref fci, Marshal.SizeOf(typeof(SYSTEM_FILECACHE_INFORMATION))) == 0;
                }
                catch { }
            }

            // 3) Modified page list flush (writes dirty pages back, allowing them to become available)
            if ((flags & CleanFlags.ModifiedPageList) != 0)
            {
                int cmd = MemoryFlushModifiedList;
                r.ModifiedFlushed = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;
            }

            // 4) Standby list (priority-aware: removes ALL standby pages)
            if ((flags & CleanFlags.StandbyList) != 0)
            {
                int cmd = MemoryPurgeStandbyList;
                r.StandbyPurged = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;
            }

            // 4b) Standby list (without priority) - only low-priority pages
            if ((flags & CleanFlags.StandbyListNoPrio) != 0)
            {
                int cmd = MemoryPurgeLowPriorityStandbyList;
                r.StandbyPurged = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0 || r.StandbyPurged;
            }

            // 5) Modified file cache (same NT call: FlushModifiedList)
            if ((flags & CleanFlags.ModifiedFileCache) != 0 && !r.ModifiedFlushed)
            {
                int cmd = MemoryFlushModifiedList;
                r.ModifiedFlushed = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;
            }

            // 6) Registry cache (Win8.1+) - SystemRegistryReconciliationInformation = 155
            if ((flags & CleanFlags.RegistryCache) != 0 && AdminUtils.RealOsVersion() >= new Version(6, 3))
            {
                try
                {
                    int cmd = 0;
                    NtSetSystemInformation(155, ref cmd, sizeof(int));
                }
                catch { }
            }

            // 7) Combine memory lists (Win10+) - SystemCombinePhysicalMemoryInformation = 130
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
    }

}
