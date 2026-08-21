using System;
using System.Threading;
using System.Threading.Tasks;
using OneBox.Contracts;

namespace OneBox.Hardware;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
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

    private static string ReadOption(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
