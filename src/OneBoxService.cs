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
        [DllImport("wtsapi32.dll")] static extern bool WTSEnumerateSessions(IntPtr hServer, int Reserved, int Version, out IntPtr ppSessionInfo, out int pCount);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr p);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
        [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessAsUser(IntPtr hToken, string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
        [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);
        [DllImport("userenv.dll", SetLastError = true)] static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out IntPtr phNewToken);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);

        const uint MAXIMUM_ALLOWED = 0x02000000;
        const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        const int SecurityImpersonation = 2;
        const int TokenPrimary = 1;
        const int TokenLinkedToken = 19;  // 用户的 UAC 提权令牌（管理员才有）
        const int WTS_CONNECTSTATE_ACTIVE = 0;
        const int WTS_CONNECTSTATE_CONNECTED = 1;

        const string SvcName = "OneBoxSvc";

        [StructLayout(LayoutKind.Sequential)] struct STARTUPINFO { public int cb; public IntPtr lpReserved; public IntPtr lpDesktop; public IntPtr lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
        [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
        [StructLayout(LayoutKind.Sequential)] struct WTS_SESSION_INFO { public int SessionId; public IntPtr pWinStationName; public int State; }

        public OneBoxService() { this.ServiceName = SvcName; CanHandleSessionChangeEvent = true; }

        protected override void OnStart(string[] _)
        {
            AppLog.Log("Service", "started");
            // 主动扫描所有已登录的活跃会话并启动 GUI（OnSessionChange 只在会话变化时触发，
            // 服务重启/重新安装时不会收到已有会话的事件）。
            EnumerateAndLaunch();
        }
        protected override void OnStop() { AppLog.Log("Service", "stopped"); }

        void EnumerateAndLaunch()
        {
            try
            {
                if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var ppSessionInfo, out int count) || ppSessionInfo == IntPtr.Zero)
                { AppLog.Log("Service", "WTSEnumerateSessions failed"); return; }
                try
                {
                    int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                    for (int i = 0; i < count; i++)
                    {
                        var si = (WTS_SESSION_INFO)Marshal.PtrToStructure(IntPtr.Add(ppSessionInfo, i * size), typeof(WTS_SESSION_INFO));
                        // 仅对活跃/已连接的用户会话（非 session 0）启动 GUI
                        if (si.SessionId == 0) continue;
                        if (si.State != WTS_CONNECTSTATE_ACTIVE && si.State != WTS_CONNECTSTATE_CONNECTED) continue;
                        AppLog.Log("Service", $"found session id={si.SessionId} state={si.State}");
                        Task.Delay(3000).ContinueWith(_ => LaunchInSession(si.SessionId));
                    }
                }
                finally { WTSFreeMemory(ppSessionInfo); }
            }
            catch (Exception ex) { AppLog.Log("Service", ex); }
        }

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
                    // 获取用户的 UAC 提权令牌（TokenLinkedToken）。
                    // 管理员用户有过滤令牌（普通权限）和链接令牌（完整管理员）。
                    // SYSTEM 服务可直接使用链接令牌启动进程，无需 UAC 弹窗。
                    IntPtr adminToken = IntPtr.Zero;
                    int infoLen = 0;
                    GetTokenInformation(userToken, TokenLinkedToken, IntPtr.Zero, 0, out infoLen);
                    if (infoLen > 0)
                    {
                        IntPtr buf = Marshal.AllocHGlobal(infoLen);
                        try
                        {
                            if (GetTokenInformation(userToken, TokenLinkedToken, buf, infoLen, out _))
                            {
                                adminToken = Marshal.ReadIntPtr(buf);
                                // 复制为主令牌供 CreateProcessAsUser 使用
                                if (!DuplicateTokenEx(adminToken, MAXIMUM_ALLOWED, IntPtr.Zero,
                                    SecurityImpersonation, TokenPrimary, out var dup))
                                { AppLog.Log("Service", "DuplicateTokenEx(admin) failed"); }
                                else
                                {
                                    CloseHandle(adminToken);
                                    adminToken = dup;
                                }
                            }
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }

                    if (adminToken != IntPtr.Zero)
                    {
                        AppLog.Log("Service", "using elevated admin token (no UAC)");
                        try { LaunchWithToken(adminToken); }
                        finally { CloseHandle(adminToken); }
                    }
                    else
                    {
                        // 回退：用户不是管理员，用普通 token
                        AppLog.Log("Service", "no admin token, using user token");
                        if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, IntPtr.Zero,
                            SecurityImpersonation, TokenPrimary, out var dupToken) || dupToken == IntPtr.Zero)
                        { AppLog.Log("Service", "DuplicateTokenEx failed"); return; }
                        try { LaunchWithToken(dupToken); }
                        finally { CloseHandle(dupToken); }
                    }
                }
                finally { CloseHandle(userToken); }
            }
            catch (Exception ex) { AppLog.Log("Service", ex); }
        }

        void LaunchWithToken(IntPtr token)
        {
            IntPtr env = IntPtr.Zero;
            try
            {
                if (!CreateEnvironmentBlock(out env, token, false))
                { AppLog.Log("Service", "CreateEnvironmentBlock failed"); }

                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                // 不指定 lpDesktop！设为 "winsta0\\default" 会导致进程因
                // STATUS_DLL_INIT_FAILED (0xC0000142) 崩溃。
                // 系统会根据用户 token 自动选择正确的交互桌面。

                var exe = Environment.ProcessPath;
                var exeDir = System.IO.Path.GetDirectoryName(exe);
                AppLog.Log("Service", $"launching: {exe}");
                if (!CreateProcessAsUser(token, null, $"\"{exe}\"",
                    IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT, env, exeDir, ref si, out var pi))
                {
                    int err = Marshal.GetLastWin32Error();
                    AppLog.Log("Service", $"CreateProcessAsUser failed err={err}");
                }
                else
                {
                    AppLog.Log("Service", $"launched pid={pi.dwProcessId}");
                    // 等待 8 秒检查进程退出码，确认是否正常存活
                    uint waitResult = WaitForSingleObject(pi.hProcess, 8000);
                    if (waitResult == 0)
                    {
                        if (GetExitCodeProcess(pi.hProcess, out uint exitCode))
                            AppLog.Log("Service", $"pid={pi.dwProcessId} exited code={exitCode}");
                    }
                    CloseHandle(pi.hProcess);
                    CloseHandle(pi.hThread);
                }
            }
            catch (Exception ex) { AppLog.Log("Service", ex); }
            finally { if (env != IntPtr.Zero) DestroyEnvironmentBlock(env); }
        }

        public static void RunService()
        {
            ServiceBase.Run(new OneBoxService());
        }
    }
}
