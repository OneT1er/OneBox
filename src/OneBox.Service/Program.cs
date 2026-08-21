using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace OneBox.Service;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = ServiceConstants.Name);
        builder.Services.AddSingleton<SessionManager>();
        builder.Services.AddHostedService<OneBoxWorker>();
        await builder.Build().RunAsync().ConfigureAwait(false);
    }
}
