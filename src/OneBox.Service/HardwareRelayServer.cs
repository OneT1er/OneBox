using System.Diagnostics;
using System.IO.Pipes;
using OneBox.Contracts;
using OneBox.Windows;

namespace OneBox.Service;

// The GUI always connects to this service-owned pipe. A hardware process exists
// only for the lifetime of a validated subscription.
internal sealed class HardwareRelayServer
{
    private readonly string _userSid;
    private readonly string _helperPath;
    private readonly object _processGate = new();
    private readonly HashSet<Process> _processes = new();
    private readonly FixedWindowRateLimiter _rateLimiter =
        new(IpcProtocol.MaxRequestsPerSecond, TimeSpan.FromSeconds(1));
    private bool _stopping;

    public HardwareRelayServer(string userSid, string helperPath)
    {
        _userSid = userSid;
        _helperPath = helperPath;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var security = SecurePipe.CreateSecurity(_userSid);
        var handlers = new SemaphoreSlim(IpcProtocol.MaxConcurrentConnections,
            IpcProtocol.MaxConcurrentConnections);
        while (!cancellationToken.IsCancellationRequested)
        {
            await handlers.WaitAsync(cancellationToken).ConfigureAwait(false);
            NamedPipeServerStream server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(PipeNames.ForHardware(_userSid),
                    PipeDirection.InOut, IpcProtocol.MaxConcurrentConnections,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleAndReleaseAsync(server, handlers, cancellationToken);
                server = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                handlers.Release();
                break;
            }
            catch (Exception ex)
            {
                handlers.Release();
                ServiceLog.Write("hardware pipe accept failed: " + ex.Message);
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
            finally { server?.Dispose(); }
        }
    }

    private async Task HandleAndReleaseAsync(NamedPipeServerStream server,
        SemaphoreSlim handlers, CancellationToken cancellationToken)
    {
        try { await HandleAsync(server, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { ServiceLog.Write("hardware connection failed: " + ex.Message); }
        finally { handlers.Release(); }
    }

    private async Task HandleAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using (server)
        {
            if (!SecurePipe.IsExpectedClient(server, _userSid))
            {
                ServiceLog.Write("rejected hardware client identity");
                return;
            }

            IpcRequest request;
            try { request = await IpcFraming.ReadAsync<IpcRequest>(server, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { ServiceLog.Write("invalid hardware subscribe request: " + ex.Message); return; }
            IpcValidationResult validation = IpcValidator.Validate(request, IpcCommand.SubscribeHardware);
            if (!validation.IsValid)
            {
                await IpcFraming.WriteAsync(server, IpcResponse.Error(request, validation.ErrorCode,
                    validation.ErrorMessage), cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!_rateLimiter.TryAcquire(DateTimeOffset.UtcNow))
            {
                await IpcFraming.WriteAsync(server, IpcResponse.Error(request, IpcErrorCode.RateLimited,
                    "Too many subscription requests."), cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!File.Exists(_helperPath))
            {
                await IpcFraming.WriteAsync(server, IpcResponse.Error(request, IpcErrorCode.ServiceUnavailable,
                    "Hardware helper is unavailable."), cancellationToken).ConfigureAwait(false);
                return;
            }

            Process process;
            lock (_processGate)
            {
                if (_stopping) return;
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = _helperPath,
                    Arguments = "--stdio",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                }) ?? throw new InvalidOperationException("Hardware helper did not start.");
                _processes.Add(process);
            }
            ServiceLog.Write($"hardware helper started on subscription sid={_userSid} pid={process.Id}");
            try
            {
                await IpcFraming.WriteAsync(process.StandardInput.BaseStream, request, cancellationToken)
                    .ConfigureAwait(false);
                while (server.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    IpcResponse response = await IpcFraming.ReadAsync<IpcResponse>(
                        process.StandardOutput.BaseStream, cancellationToken, TimeSpan.FromSeconds(75))
                        .ConfigureAwait(false);
                    await IpcFraming.WriteAsync(server, response, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_processGate) _processes.Remove(process);
                Terminate(process);
                process.Dispose();
                ServiceLog.Write("hardware helper stopped after subscription ended sid=" + _userSid);
            }
        }
    }

    public void Stop()
    {
        lock (_processGate)
        {
            _stopping = true;
            foreach (var process in _processes) Terminate(process);
        }
    }

    private static void Terminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        try { process.WaitForExit(2000); } catch { }
    }
}
