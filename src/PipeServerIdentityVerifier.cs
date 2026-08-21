using System;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using OneBox.Contracts;

namespace PowerAudioManager
{
    internal static class PipeServerIdentityVerifier
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint TokenQuery = 0x0008;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        public static void EnsureLocalSystemServer(NamedPipeClientStream pipe)
        {
            if (pipe == null || !pipe.IsConnected)
                throw new InvalidOperationException("管道尚未连接，无法验证服务端身份。");
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId))
                throw WindowsFailure("无法读取管道服务端进程");

            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero) throw WindowsFailure("无法打开管道服务端进程");
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(process, TokenQuery, out token) || token == IntPtr.Zero)
                    throw WindowsFailure("无法读取管道服务端令牌");
                using var identity = new WindowsIdentity(token);
                string sid = identity.User?.Value;
                if (!PipeServerIdentity.IsTrusted(sid))
                    throw new UnauthorizedAccessException($"拒绝非 LocalSystem 管道服务端（PID={processId}, SID={sid ?? "unknown"}）。");
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
                CloseHandle(process);
            }
        }

        private static Win32Exception WindowsFailure(string message) =>
            new(Marshal.GetLastWin32Error(), message);
    }
}
