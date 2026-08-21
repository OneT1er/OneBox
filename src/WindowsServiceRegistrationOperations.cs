using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using Microsoft.Win32;
using OneBox.Contracts;

namespace PowerAudioManager;

internal sealed class WindowsServiceRegistrationOperations : IServiceRegistrationOperations
{
    private const string ServiceName = "OneBoxSvc";

    public string LastError { get; private set; }

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
            if (controller.Status == ServiceControllerStatus.Stopped) return 0;
            LastError = "ServiceController stop timed out after 10 seconds.";
            return -2;
        }
        catch (Exception ex)
        {
            LastError = $"ServiceController stop failed: {ex.GetType().Name}: {ex.Message}";
            return -1;
        }
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
            if (controller.Status == ServiceControllerStatus.Running) return 0;
            LastError = "ServiceController start timed out after 10 seconds.";
            return -2;
        }
        catch (Exception ex)
        {
            LastError = $"ServiceController start failed: {ex.GetType().Name}: {ex.Message}";
            return -1;
        }
    }

    private int RunSc(string arguments)
    {
        LastError = null;
        try
        {
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process == null)
            {
                LastError = "sc.exe did not return a process handle.";
                return -1;
            }
            if (!process.WaitForExit(10000))
            {
                LastError = $"sc.exe timed out after 10 seconds (arguments: {arguments}).";
                return -2;
            }
            int exitCode = process.ExitCode;
            if (exitCode != 0)
                LastError = $"sc.exe returned exit {exitCode} (arguments: {arguments}).";
            return exitCode;
        }
        catch (Exception ex)
        {
            LastError = $"sc.exe launch failed: {ex.GetType().Name}: {ex.Message}";
            return -1;
        }
    }
}
