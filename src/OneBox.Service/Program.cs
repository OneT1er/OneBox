using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OneBox.Service;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            HostApplicationBuilder builder = CreateBuilder(args);
            if (Array.IndexOf(args, "--startup-probe") >= 0)
            {
                using IHost probe = builder.Build();
                ServiceLog.Write("startup probe succeeded");
                return 0;
            }

            await builder.Build().RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            ServiceLog.Write("fatal startup/runtime error: " + ex);
            return 1;
        }
    }

    private static HostApplicationBuilder CreateBuilder(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = ServiceConstants.Name);
        // The service owns its diagnostic file and does not consume ILogger.
        // Removing the default EventLog provider prevents LocalSystem startup
        // from depending on an optional provider before ServiceLog is ready.
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<SessionManager>();
        builder.Services.AddHostedService<OneBoxWorker>();
        return builder;
    }
}
