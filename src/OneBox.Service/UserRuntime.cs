using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using OneBox.Contracts;

namespace OneBox.Service;

internal sealed class UserRuntime
{
    private readonly string _userSid;
    private readonly CancellationTokenSource _stop = new();
    private Task _memoryServerTask;
    private Task _hardwareServerTask;
    private HardwareRelayServer _hardwareServer;

    public UserRuntime(string userSid) => _userSid = userSid;

    public void Start()
    {
        // Keep the server task observed and restart it if an unexpected pipe
        // setup failure escapes the request loop. A faulted fire-and-forget
        // task would otherwise leave this session without memory IPC until
        // the service itself restarted.
        _memoryServerTask = RunMemoryServerAsync(_stop.Token);
        string helperPath = Path.Combine(AppContext.BaseDirectory, ServiceConstants.HardwareExecutable);
        CleanupStaleHardwarePipe(helperPath, _userSid);
        _hardwareServer = new HardwareRelayServer(_userSid, helperPath);
        _hardwareServerTask = RunHardwareServerAsync(_stop.Token);
    }

    private async Task RunMemoryServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await new MemoryPipeServer(_userSid).RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                ServiceLog.Write("memory pipe guardian error: " + ex.Message);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task RunHardwareServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _hardwareServer.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                ServiceLog.Write("hardware relay error: " + ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void CleanupStaleHardwarePipe(string helperPath, string userSid)
    {
        // A helper can outlive a crashed/stopped service. Its named-pipe
        // instance then remains the first endpoint a new GUI connects to,
        // producing rejected subscriptions and misleading "helper running"
        // health checks. Resolve the server PID from this user's pipe and
        // reclaim only the named companion executable (including legacy
        // output directories from an earlier migration).
        for (int attempt = 0; attempt < 8; attempt++)
        {
            using var client = new NamedPipeClientStream(".", PipeNames.ForHardware(userSid),
                PipeDirection.InOut, PipeOptions.None);
            try { client.Connect(250); }
            catch { return; }

            if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out uint pid) || pid == 0)
                return;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                string actualPath = null;
                try { actualPath = process.MainModule?.FileName; } catch { }
                // The pipe name is SID-scoped. Migration can leave a
                // LocalSystem helper from an older output directory behind,
                // so exact-path matching alone would fail to reclaim it. The
                // executable name is the companion identity; never kill an
                // unrelated process merely because it owns a pipe endpoint.
                string actualName = Path.GetFileName(actualPath ?? "");
                if (!string.Equals(actualName, ServiceConstants.HardwareExecutable,
                    StringComparison.OrdinalIgnoreCase))
                {
                    ServiceLog.Write($"hardware pipe belongs to unexpected process pid={pid}; leaving it untouched");
                    return;
                }
                if (!IsLocalSystemProcess(pid))
                {
                    ServiceLog.Write($"hardware pipe companion pid={pid} is not LocalSystem; leaving it untouched");
                    return;
                }

                bool samePath = string.Equals(Path.GetFullPath(actualPath ?? ""), Path.GetFullPath(helperPath),
                    StringComparison.OrdinalIgnoreCase);
                ServiceLog.Write($"stopping stale hardware helper sid={userSid} pid={pid} " +
                    (samePath ? "" : "legacy-path=" + (actualPath ?? "unknown")));
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    ServiceLog.Write("stale hardware helper stop failed: " + ex.Message);
                    return;
                }
                try { process.WaitForExit(2000); } catch { }
            }
            catch (ArgumentException) { return; }
            catch (Exception ex)
            {
                ServiceLog.Write("stale hardware helper inspection failed: " + ex.Message);
                return;
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    private static bool IsLocalSystemProcess(uint processId)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return false;
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out token) || token == IntPtr.Zero) return false;
            using var identity = new WindowsIdentity(token);
            return PipeServerIdentity.IsTrusted(identity.User?.Value);
        }
        catch { return false; }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            CloseHandle(process);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public async Task StopAsync()
    {
        _stop.Cancel();
        _hardwareServer?.Stop();
        Task[] tasks = new[] { _memoryServerTask, _hardwareServerTask }.Where(task => task != null).ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
        _stop.Dispose();
    }

}
