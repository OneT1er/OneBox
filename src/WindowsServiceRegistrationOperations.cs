using System;
using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using OneBox.Contracts;

namespace PowerAudioManager;

internal sealed class WindowsServiceRegistrationOperations : IServiceRegistrationOperations
{
    private const string ServiceName = "OneBoxSvc";

    public bool IsInstalled
    {
        get
        {
            try
            {
                using var controller = new ServiceController(ServiceName);
                _ = controller.Status;
                return true;
            }
            catch { return false; }
        }
    }

    public string ImagePath
    {
        get
        {
            try
            {
                using RegistryKey key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
                return key?.GetValue("ImagePath") as string;
            }
            catch { return null; }
        }
    }

    public int StopIfRunning()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            if (controller.Status == ServiceControllerStatus.Stopped) return 0;
            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            return controller.Status == ServiceControllerStatus.Stopped ? 0 : -2;
        }
        catch { return -1; }
    }

    public int Create(string executablePath) => RunSc($"create \"{ServiceName}\" binPath= \"\\\"{executablePath}\\\"\" start= auto");

    public int Configure(string executablePath) => RunSc($"config \"{ServiceName}\" binPath= \"\\\"{executablePath}\\\"\" start= auto");

    public int StartIfStopped()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            if (controller.Status == ServiceControllerStatus.Running) return 0;
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
            return controller.Status == ServiceControllerStatus.Running ? 0 : -2;
        }
        catch { return -1; }
    }

    private static int RunSc(string arguments)
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process == null) return -1;
            if (!process.WaitForExit(10000)) return -2;
            return process.ExitCode;
        }
        catch { return -1; }
    }
}
