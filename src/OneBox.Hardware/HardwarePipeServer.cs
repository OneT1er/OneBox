using System;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OneBox.Contracts;
using OneBox.Windows;

namespace OneBox.Hardware;

internal sealed class HardwarePipeServer
{
    private readonly string _userSid;
    private readonly HardwareCollector _collector;
    private readonly FixedWindowRateLimiter _rateLimiter = new(IpcProtocol.MaxRequestsPerSecond, TimeSpan.FromSeconds(1));

    public HardwarePipeServer(string userSid, HardwareCollector collector)
    {
        _userSid = userSid;
        _collector = collector;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string pipeName = PipeNames.ForHardware(_userSid);
        var security = SecurePipe.CreateSecurity(_userSid);
        var handlers = new SemaphoreSlim(IpcProtocol.MaxConcurrentConnections, IpcProtocol.MaxConcurrentConnections);
        while (!cancellationToken.IsCancellationRequested)
        {
            await handlers.WaitAsync(cancellationToken).ConfigureAwait(false);
            NamedPipeServerStream server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, IpcProtocol.MaxConcurrentConnections,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                // Observe the handler task explicitly. ContinueWith-only release left
                // a faulted task unobserved when a write or disposal failed outside the
                // request loop's guarded sections.
                _ = HandleConnectionAndReleaseAsync(server, handlers, cancellationToken);
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
                HardwareLog.Write("accept failed: " + ex.Message);
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
            finally { server?.Dispose(); }
        }
    }

    private async Task HandleConnectionAndReleaseAsync(NamedPipeServerStream server,
        SemaphoreSlim handlers, CancellationToken cancellationToken)
    {
        try
        {
            await HandleConnectionAsync(server, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            HardwareLog.Write("connection handler failed: " + ex.Message);
        }
        finally
        {
            handlers.Release();
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using (server)
        {
            if (!SecurePipe.IsExpectedClient(server, _userSid))
            {
                HardwareLog.Write("rejected hardware client identity");
                return;
            }
            IpcRequest request;
            try { request = await IpcFraming.ReadAsync<IpcRequest>(server, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { HardwareLog.Write("invalid subscribe request: " + ex.Message); return; }

            IpcValidationResult validation = IpcValidator.Validate(request, IpcCommand.SubscribeHardware);
            if (!validation.IsValid)
            {
                await IpcFraming.WriteAsync(server, IpcResponse.Error(request, validation.ErrorCode, validation.ErrorMessage), cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!_rateLimiter.TryAcquire(DateTimeOffset.UtcNow))
            {
                await IpcFraming.WriteAsync(server, IpcResponse.Error(request, IpcErrorCode.RateLimited, "Too many subscription requests."), cancellationToken).ConfigureAwait(false);
                return;
            }

            HardwareSubscribePayload payload;
            try { payload = request.Payload.Deserialize<HardwareSubscribePayload>(IpcJson.Options) ?? new HardwareSubscribePayload(); }
            catch
            {
                await IpcFraming.WriteAsync(server, IpcResponse.Error(request, IpcErrorCode.InvalidPayload, "Invalid subscription payload."), cancellationToken).ConfigureAwait(false);
                return;
            }
            int interval = Math.Clamp(payload.MinimumIntervalMilliseconds, 500, 60000);
            while (server.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    HardwareSnapshot snapshot = _collector.ReadSnapshot();
                    await IpcFraming.WriteAsync(server, IpcResponse.Ok(request, snapshot, IpcCommand.HardwareSnapshot), cancellationToken).ConfigureAwait(false);
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex) { HardwareLog.Write("connection ended: " + ex.Message); break; }
            }
        }
    }
}
