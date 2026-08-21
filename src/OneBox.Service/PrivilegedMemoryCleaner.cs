using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OneBox.Service;

internal static class PrivilegedMemoryCleaner
{
    private const int PrivilegeEnabled = 2;
    private const int TokenQuery = 8;
    private const int TokenAdjustPrivileges = 32;
    private const int SystemMemoryListInformation = 80;
    private const int SystemFileCacheInformationClass = 21;
    private const int SystemCombinePhysicalMemoryInformation = 130;
    private const int MemoryEmptyWorkingSets = 2;
    private const int MemoryFlushModifiedList = 3;
    private const int MemoryPurgeStandbyList = 4;
    private const int MemoryPurgeLowPriorityStandbyList = 5;
    private const int AllFlags = 0xff;

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public int Count; public Luid Luid; public int Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemFileCacheInformation
    {
        public IntPtr CurrentSize, PeakSize;
        public uint PageFaultCount;
        public IntPtr MinimumWorkingSet, MaximumWorkingSet, CurrentIncludingTransition, PeakIncludingTransition;
        public uint TransitionPurposeCount, Flags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryCombineInformation { public IntPtr RegionHandle; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatus
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatus>();
        public uint Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtended;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfo
    {
        public int Size, Major, Minor, Build, Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
        public short ServicePackMajor, ServicePackMinor, SuiteMask;
        public byte ProductType, Reserved;
    }

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool LookupPrivilegeValue(string system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disable, ref TokenPrivileges privileges, int length, IntPtr oldState, IntPtr returnLength);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);
    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int infoClass, ref int data, int size);
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int infoClass, ref SystemFileCacheInformation data, int size);
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int infoClass, ref MemoryCombineInformation data, int size);
    [DllImport("ntdll.dll")] private static extern int RtlGetVersion(ref OsVersionInfo version);

    public static bool AreFlagsValid(int flags) => flags > 0 && (flags & ~AllFlags) == 0;

    public static ulong Clean(int flags)
    {
        if (!AreFlagsValid(flags)) throw new ArgumentOutOfRangeException(nameof(flags));
        ulong before = AvailableMemory();
        EnablePrivilege("SeIncreaseQuotaPrivilege");
        EnablePrivilege("SeProfileSingleProcessPrivilege");

        if ((flags & (1 << 0)) != 0)
        {
            int command = MemoryEmptyWorkingSets;
            if (NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int)) != 0)
            {
                foreach (Process process in Process.GetProcesses())
                {
                    try { EmptyWorkingSet(process.Handle); } catch { }
                    finally { process.Dispose(); }
                }
            }
        }
        if ((flags & (1 << 1)) != 0)
        {
            var cache = new SystemFileCacheInformation { MinimumWorkingSet = (IntPtr)(-1), MaximumWorkingSet = (IntPtr)(-1) };
            NtSetSystemInformation(SystemFileCacheInformationClass, ref cache, Marshal.SizeOf<SystemFileCacheInformation>());
        }
        if ((flags & ((1 << 2) | (1 << 5))) != 0)
        {
            int command = MemoryFlushModifiedList;
            NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        }
        if ((flags & (1 << 3)) != 0)
        {
            int command = MemoryPurgeStandbyList;
            NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        }
        if ((flags & (1 << 4)) != 0)
        {
            int command = MemoryPurgeLowPriorityStandbyList;
            NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        }
        Version os = RealOsVersion();
        if ((flags & (1 << 6)) != 0 && os >= new Version(6, 3))
        {
            int command = 0;
            NtSetSystemInformation(155, ref command, sizeof(int));
        }
        if ((flags & (1 << 7)) != 0 && os.Major >= 10)
        {
            var combine = new MemoryCombineInformation();
            NtSetSystemInformation(SystemCombinePhysicalMemoryInformation, ref combine, Marshal.SizeOf<MemoryCombineInformation>());
        }
        try { EmptyWorkingSet(GetCurrentProcess()); } catch { }
        Thread.Sleep(500);
        ulong after = AvailableMemory();
        return after > before ? after - before : 0;
    }

    private static ulong AvailableMemory()
    {
        var status = new MemoryStatus();
        return GlobalMemoryStatusEx(status) ? status.AvailablePhysical : 0;
    }

    private static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out IntPtr token)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, name, out Luid luid)) return false;
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = PrivilegeEnabled };
            return AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(token); }
    }

    private static Version RealOsVersion()
    {
        var info = new OsVersionInfo { Size = Marshal.SizeOf<OsVersionInfo>() };
        return RtlGetVersion(ref info) == 0 ? new Version(info.Major, info.Minor, info.Build) : Environment.OSVersion.Version;
    }
}
