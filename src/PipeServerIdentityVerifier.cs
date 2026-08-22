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
        // A medium-integrity desktop process is intentionally not allowed to
        // open a LocalSystem service process, even with
        // PROCESS_QUERY_LIMITED_INFORMATION (ERROR_ACCESS_DENIED).  Do not
        // use the server PID as an authentication mechanism.  The named pipe
        // object itself is owned by LocalSystem and has a protected ACL; that
        // is the credential we can verify from the client side.
        private const uint OwnerSecurityInformation = 0x00000001;
        private const int SeKernelObject = 6;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint GetSecurityInfo(
            IntPtr handle, int objectType, uint securityInfo,
            out IntPtr ownerSid, IntPtr groupSid, IntPtr dacl,
            IntPtr sacl, out IntPtr securityDescriptor);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static void EnsureLocalSystemServer(NamedPipeClientStream pipe)
        {
            if (pipe == null || !pipe.IsConnected)
                throw new InvalidOperationException("管道尚未连接，无法验证服务端身份。");
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId))
                throw WindowsFailure("无法读取管道服务端进程");

            // GetNamedPipeServerProcessId is retained only for diagnostics. A
            // normal user cannot OpenProcess() a LocalSystem service, so using
            // that PID here rejects every legitimate connection.
            IntPtr ownerSid = IntPtr.Zero;
            IntPtr securityDescriptor = IntPtr.Zero;
            bool addedRef = false;
            try
            {
                pipe.SafePipeHandle.DangerousAddRef(ref addedRef);
                uint result = GetSecurityInfo(pipe.SafePipeHandle.DangerousGetHandle(), SeKernelObject,
                    OwnerSecurityInformation, out ownerSid, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, out securityDescriptor);
                if (result != 0)
                    throw new Win32Exception((int)result, $"无法读取管道服务端安全描述符（PID={processId}）");
                if (ownerSid == IntPtr.Zero)
                    throw new InvalidOperationException($"管道服务端未提供所有者（PID={processId}）。");

                string sid = new SecurityIdentifier(ownerSid).Value;
                if (!PipeServerIdentity.IsTrusted(sid))
                    throw new UnauthorizedAccessException($"拒绝非 LocalSystem 管道服务端（PID={processId}, owner={sid}）。");
            }
            finally
            {
                if (securityDescriptor != IntPtr.Zero) LocalFree(securityDescriptor);
                if (addedRef) pipe.SafePipeHandle.DangerousRelease();
            }
        }

        private static Win32Exception WindowsFailure(string message) =>
            new(Marshal.GetLastWin32Error(), message);
    }
}
