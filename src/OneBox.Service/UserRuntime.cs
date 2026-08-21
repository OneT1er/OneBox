using System.Diagnostics;

namespace OneBox.Service;

internal sealed class UserRuntime
{
    private readonly string _userSid;
    private readonly CancellationTokenSource _stop = new();
    private Task _memoryServerTask;
    private Task _hardwareGuardianTask;
    private Process _hardwareProcess;

    public UserRuntime(string userSid) => _userSid = userSid;

    public void Start()
    {
        // Keep the server task observed and restart it if an unexpected pipe
        // setup failure escapes the request loop. A faulted fire-and-forget
        // task would otherwise leave this session without memory IPC until
        // the service itself restarted.
        _memoryServerTask = RunMemoryServerAsync(_stop.Token);
        _hardwareGuardianTask = GuardHardwareAsync(_stop.Token);
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

    private async Task GuardHardwareAsync(CancellationToken cancellationToken)
    {
        string helperPath = Path.Combine(AppContext.BaseDirectory, ServiceConstants.HardwareExecutable);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!File.Exists(helperPath))
                {
                    ServiceLog.Write("hardware helper missing: " + helperPath);
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                _hardwareProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = helperPath,
                    Arguments = $"--user-sid {_userSid}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                });
                if (_hardwareProcess == null) throw new InvalidOperationException("Hardware helper did not start.");
                ServiceLog.Write($"hardware helper started sid={_userSid} pid={_hardwareProcess.Id}");
                await _hardwareProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) break;
                ServiceLog.Write($"hardware helper exited sid={_userSid}; restarting in 3s");
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                ServiceLog.Write("hardware guardian error: " + ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _hardwareProcess?.Dispose();
                _hardwareProcess = null;
            }
        }
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        try { if (_hardwareProcess is { HasExited: false }) _hardwareProcess.Kill(entireProcessTree: true); } catch { }
        Task[] tasks = new[] { _memoryServerTask, _hardwareGuardianTask }.Where(task => task != null).ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
        _stop.Dispose();
    }
}
