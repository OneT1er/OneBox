using System;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading.Tasks;

namespace PowerAudioManager
{
    // Windows Service that launches OneBox GUI in the user's session at logon.
    // Runs as SYSTEM (auto admin); uses session-change detection to start the
    // real GUI process in each interactive logon session. The service itself
    // never shows UI — it just spawns the normal OneBox.exe as the user.
    //
    // Install:   sc create OneBoxSvc binPath= "\"<path>\" --service" start= auto
    // Uninstall: sc delete OneBoxSvc
    public sealed class OneBoxService : ServiceBase
    {
        [DllImport("wtsapi32.dll")] static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
        [DllImport("wtsapi32.dll")] static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr ppBuffer, out uint pBytesReturned);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr p);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool CreateProcessAsUser(IntPtr hToken, string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
        [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);
        [DllImport("userenv.dll", SetLastError = true)] static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        const uint TOKEN_DUPLICATE = 0x0002;
        const uint TOKEN_QUERY = 0x0008;
        const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        const int WTSActive = 0;
        const int WTSConnectState = 6;

        const string SvcName = "OneBoxSvc";

        [StructLayout(LayoutKind.Sequential)] struct STARTUPINFO { public int cb; public IntPtr lpReserved; public IntPtr lpDesktop; public IntPtr lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
        [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        public OneBoxService() { this.ServiceName = SvcName; CanHandleSessionChangeEvent = true; }

        protected override void OnStart(string[] _) { AppLog.Log("Service", "started"); }
        protected override void OnStop() { AppLog.Log("Service", "stopped"); }

        protected override void OnSessionChange(SessionChangeDescription desc)
        {
            base.OnSessionChange(desc);
            if (desc.Reason == SessionChangeReason.SessionLogon)
            {
                AppLog.Log("Service", $"session-logon id={desc.SessionId}");
                // 短暂延迟让会话完全初始化（explorer、desktop）。
                Task.Delay(5000).ContinueWith(_ => LaunchInSession(desc.SessionId));
            }
        }

        void LaunchInSession(int sessionId)
        {
            try
            {
                if (!WTSQueryUserToken((uint)sessionId, out var userToken) || userToken == IntPtr.Zero)
                { AppLog.Log("Service", "WTSQueryUserToken failed"); return; }
                try
                {
                    IntPtr env = IntPtr.Zero;
                    if (!CreateEnvironmentBlock(out env, userToken, false))
                    { AppLog.Log("Service", "CreateEnvironmentBlock failed"); }

                    var si = new STARTUPINFO();
                    si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                    // 附加到用户的默认桌面，这样 GUI 才可见。
                    var desktopPtr = Marshal.StringToHGlobalUni("winsta0\\default");
                    si.lpDesktop = desktopPtr;

                    var exe = Environment.ProcessPath;
                    if (!CreateProcessAsUser(userToken, exe, null, IntPtr.Zero, IntPtr.Zero, false,
                        CREATE_UNICODE_ENVIRONMENT, env, null, ref si, out var pi))
                    {
                        int err = Marshal.GetLastWin32Error();
                        AppLog.Log("Service", $"CreateProcessAsUser failed err={err}");
                    }
                    else
                    {
                        AppLog.Log("Service", $"launched pid={pi.dwProcessId} session={sessionId}");
                        CloseHandle(pi.hProcess);
                        CloseHandle(pi.hThread);
                    }
                    if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
                    Marshal.FreeHGlobal(desktopPtr);
                }
                finally { CloseHandle(userToken); }
            }
            catch (Exception ex) { AppLog.Log("Service", ex); }
        }

        public static void RunService()
        {
            ServiceBase.Run(new OneBoxService());
        }
    }
}
