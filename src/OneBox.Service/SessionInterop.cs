using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace OneBox.Service;

internal sealed record InteractiveSession(int SessionId, string UserSid);

internal static class SessionInterop
{
    private const uint MaximumAllowed = 0x02000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int ActiveState = 0;
    private const int ConnectedState = 1;
    private const uint KeyRead = 0x20019;

    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ImpersonateLoggedOnUser(IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool RevertToSelf();
    [DllImport("advapi32.dll")] private static extern int RegOpenCurrentUser(uint desiredAccess, out SafeRegistryHandle key);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string applicationName, string commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo { public int SessionId; public IntPtr StationName; public int State; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags;
        public short ShowWindow, ReservedLength;
        public IntPtr Reserved2, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process; public IntPtr Thread; public int ProcessId; public int ThreadId; }

    public static IReadOnlyList<InteractiveSession> EnumerateActiveSessions()
    {
        var result = new List<InteractiveSession>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr memory, out int count) || memory == IntPtr.Zero) return result;
        try
        {
            int size = Marshal.SizeOf<WtsSessionInfo>();
            for (int index = 0; index < count; index++)
            {
                WtsSessionInfo session = Marshal.PtrToStructure<WtsSessionInfo>(IntPtr.Add(memory, index * size));
                if (session.SessionId == 0 || (session.State != ActiveState && session.State != ConnectedState)) continue;
                string sid = ReadUserSid(session.SessionId);
                if (!string.IsNullOrEmpty(sid)) result.Add(new InteractiveSession(session.SessionId, sid));
            }
        }
        finally { WTSFreeMemory(memory); }
        return result;
    }

    private static string ReadUserSid(int sessionId)
    {
        if (!WTSQueryUserToken((uint)sessionId, out IntPtr token) || token == IntPtr.Zero) return null;
        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.User?.Value;
        }
        catch { return null; }
        finally { CloseHandle(token); }
    }

    public static bool IsServiceAutoStartEnabled(int sessionId)
    {
        if (!WTSQueryUserToken((uint)sessionId, out IntPtr token) || token == IntPtr.Zero) return false;
        try
        {
            if (!ImpersonateLoggedOnUser(token)) return false;
            try
            {
                if (RegOpenCurrentUser(KeyRead, out SafeRegistryHandle currentUser) != 0 || currentUser.IsInvalid)
                    return false;
                using (currentUser)
                using (RegistryKey root = RegistryKey.FromHandle(currentUser))
                using (RegistryKey key = root.OpenSubKey(@"Software\PowerAudioManager\App"))
                {
                    string enabledValue = key?.GetValue("AutoStart.Enabled") as string;
                    bool enabled = enabledValue != "0"; // missing key keeps legacy service installs enabled
                    string methodValue = key?.GetValue("AutoStart.LastMethod") as string;
                    int method = int.TryParse(methodValue, out int parsed) ? parsed : 3;
                    return enabled && method == 3;
                }
            }
            finally { RevertToSelf(); }
        }
        catch { return false; }
        finally { CloseHandle(token); }
    }

    public static bool LaunchGui(int sessionId, string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            ServiceLog.Write("GUI executable missing: " + executablePath);
            return false;
        }
        if (!WTSQueryUserToken((uint)sessionId, out IntPtr userToken) || userToken == IntPtr.Zero)
        {
            ServiceLog.Write($"GUI launch could not query user token session={sessionId} error={Marshal.GetLastWin32Error()}");
            return false;
        }
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        ProcessInformation process = default;
        try
        {
            if (!DuplicateTokenEx(userToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken) || primaryToken == IntPtr.Zero)
            {
                ServiceLog.Write($"GUI launch could not duplicate token session={sessionId} error={Marshal.GetLastWin32Error()}");
                return false;
            }
            bool hasEnvironment = CreateEnvironmentBlock(out environment, primaryToken, false);
            if (!hasEnvironment) environment = IntPtr.Zero;
            uint creationFlags = hasEnvironment ? CreateUnicodeEnvironment : 0;
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            string directory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
            if (!CreateProcessAsUser(primaryToken, null, $"\"{executablePath}\"", IntPtr.Zero, IntPtr.Zero, false,
                creationFlags, environment, directory, ref startup, out process))
            {
                ServiceLog.Write($"GUI launch failed session={sessionId} error={Marshal.GetLastWin32Error()}");
                return false;
            }
            ServiceLog.Write($"GUI launched session={sessionId} pid={process.ProcessId}");
            return true;
        }
        catch (Exception ex)
        {
            ServiceLog.Write("GUI launch error: " + ex.Message);
            return false;
        }
        finally
        {
            if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }
}
