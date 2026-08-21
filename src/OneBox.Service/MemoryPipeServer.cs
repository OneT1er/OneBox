using System.IO.Pipes;
using System.Text.Json;
using OneBox.Contracts;
using OneBox.Windows;

namespace OneBox.Service;

internal sealed class MemoryPipeServer
{
    private readonly string _userSid;
    private readonly FixedWindowRateLimiter _rateLimiter = new(IpcProtocol.MaxRequestsPerSecond, TimeSpan.FromSeconds(1));

    public MemoryPipeServer(string userSid) => _userSid = userSid;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string pipeName = PipeNames.ForCommand(_userSid);
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
                // a faulted task unobserved when a response write failed after the
                // request validation path.
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
                ServiceLog.Write("memory pipe accept failed: " + ex.Message);
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
            ServiceLog.Write("memory connection handler failed: " + ex.Message);
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
                ServiceLog.Write("rejected command client identity");
                return;
            }

            IpcRequest request;
            try { request = await IpcFraming.ReadAsync<IpcRequest>(server, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { ServiceLog.Write("invalid command request: " + ex.Message); return; }

            IpcValidationResult validation = IpcValidator.Validate(request, IpcCommand.CleanMemory, IpcCommand.Ping);
            if (!validation.IsValid)
            {
                await IpcFraming.WriteAsync(server, IpcResponse.Error(request, validation.ErrorCode, validation.ErrorMessage), cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!_rateLimiter.TryAcquire(DateTimeOffset.UtcNow))
            {
                await IpcFraming.WriteAsync(server, IpcResponse.Error(request, IpcErrorCode.RateLimited, "Too many requests."), cancellationToken).ConfigureAwait(false);
                return;
            }
            if (request.Command == IpcCommand.Ping)
            {
                await IpcFraming.WriteAsync(server, IpcResponse.Ok(request, new { alive = true }), cancellationToken).ConfigureAwait(false);
                return;
            }

            CleanMemoryPayload payload;
            try { payload = request.Payload.Deserialize<CleanMemoryPayload>(IpcJson.Options); }
            catch { payload = null; }
            if (payload == null || !PrivilegedMemoryCleaner.AreFlagsValid(payload.Flags))
            {
                await IpcFraming.WriteAsync(server, IpcResponse.Error(request, IpcErrorCode.InvalidPayload, "Invalid memory-clean flags."), cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                ulong freed = await Task.Run(() => PrivilegedMemoryCleaner.Clean(payload.Flags), cancellationToken).ConfigureAwait(false);
                await IpcFraming.WriteAsync(server, IpcResponse.Ok(request, new CleanMemoryResult { FreedBytes = freed }), cancellationToken).ConfigureAwait(false);
                ServiceLog.Write($"memory clean sid={_userSid} flags={payload.Flags} freed={freed}");
            }
            catch (Exception ex)
            {
                ServiceLog.Write("memory clean failed: " + ex.Message);
                await IpcFraming.WriteAsync(server, IpcResponse.Error(request, IpcErrorCode.InternalError, "Memory clean failed."), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
