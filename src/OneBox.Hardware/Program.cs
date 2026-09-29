using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OneBox.Contracts;

namespace OneBox.Hardware;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (Array.Exists(args, arg => string.Equals(arg, "--stdio", StringComparison.OrdinalIgnoreCase)))
            return await RunStandardStreamAsync().ConfigureAwait(false);

        string userSid = ReadOption(args, "--user-sid");
        try { _ = PipeNames.NormalizeSid(userSid); }
        catch (Exception ex)
        {
            HardwareLog.Write("invalid arguments: " + ex.Message);
            return 2;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
        try
        {
            using var collector = new HardwareCollector();
            collector.Start();
            HardwareLog.Write("started for " + userSid);
            await new HardwarePipeServer(userSid, collector).RunAsync(stop.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception ex)
        {
            HardwareLog.Write("fatal: " + ex);
            return 1;
        }
    }

    private static async Task<int> RunStandardStreamAsync()
    {
        using var stop = new CancellationTokenSource();
        try
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            IpcRequest request = await IpcFraming.ReadAsync<IpcRequest>(input, stop.Token).ConfigureAwait(false);
            IpcValidationResult validation = IpcValidator.Validate(request, IpcCommand.SubscribeHardware);
            if (!validation.IsValid)
            {
                await IpcFraming.WriteAsync(output, IpcResponse.Error(request, validation.ErrorCode,
                    validation.ErrorMessage), stop.Token).ConfigureAwait(false);
                return 2;
            }
            HardwareSubscribePayload payload;
            try { payload = request.Payload.Deserialize<HardwareSubscribePayload>(IpcJson.Options) ?? new(); }
            catch
            {
                await IpcFraming.WriteAsync(output, IpcResponse.Error(request, IpcErrorCode.InvalidPayload,
                    "Invalid subscription payload."), stop.Token).ConfigureAwait(false);
                return 2;
            }
            int interval = Math.Clamp(payload.MinimumIntervalMilliseconds, 500, 60000);
            using var collector = new HardwareCollector();
            collector.Start();
            HardwareLog.Write("collector started for active subscription");
            while (true)
            {
                HardwareSnapshot snapshot = collector.ReadSnapshot();
                await IpcFraming.WriteAsync(output, IpcResponse.Ok(request, snapshot,
                    IpcCommand.HardwareSnapshot), stop.Token).ConfigureAwait(false);
                await Task.Delay(interval, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex)
        {
            HardwareLog.Write("stdio subscription ended: " + ex);
            return 1;
        }
    }

    private static string ReadOption(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
