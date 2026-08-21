using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using PowerAudioManager;
using Xunit;

namespace OneBox.Tests;

public sealed class QualityInvariantTests
{
    [Fact]
    public void PreferenceWrites_ReportSuccessAndRemainReadable()
    {
        string key = "Tests.PreferenceWrite." + Guid.NewGuid().ToString("N");
        try
        {
            Assert.True(AppPrefs.SetString(key, "written"));
            Assert.Equal("written", AppPrefs.GetString(key, "missing"));
        }
        finally
        {
            using var registry = Registry.CurrentUser.OpenSubKey(@"Software\PowerAudioManager\App", true);
            registry?.DeleteValue(key, false);
        }
    }

    [Fact]
    public void LauncherFaviconFetch_IsTaskBasedAndNotAsyncVoid()
    {
        string source = ReadSource("src", "LauncherBar.cs");
        Assert.DoesNotContain("async void FetchFavicon", source, StringComparison.Ordinal);
        Assert.Contains("Task FetchFaviconAsync", source, StringComparison.Ordinal);
        Assert.Contains("MaxFaviconBytes", source, StringComparison.Ordinal);
        Assert.Contains("ReadBoundedBytesAsync", source, StringComparison.Ordinal);
        Assert.Contains("ReadBoundedTextAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundedResponseReader_RejectsOversizedBodyBeforeAllocation()
    {
        using var content = new ByteArrayContent(new byte[1024]);
        var error = await Assert.ThrowsAsync<OneBoxHttpException>(() =>
            OneBoxHttp.ReadBoundedBytesAsync(content, 128, CancellationToken.None));
        Assert.Contains("大小限制", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PipeServers_ObserveConnectionHandlerTasks()
    {
        string hardware = ReadSource("src", "OneBox.Hardware", "HardwarePipeServer.cs");
        string service = ReadSource("src", "OneBox.Service", "MemoryPipeServer.cs");
        Assert.DoesNotContain("ContinueWith(_ => handlers.Release()", hardware, StringComparison.Ordinal);
        Assert.DoesNotContain("ContinueWith(_ => handlers.Release()", service, StringComparison.Ordinal);
        Assert.Contains("HandleConnectionAndReleaseAsync", hardware, StringComparison.Ordinal);
        Assert.Contains("HandleConnectionAndReleaseAsync", service, StringComparison.Ordinal);
    }

    [Fact]
    public void ElevatedHelpers_LogWithoutShowingDuplicateDialogs()
    {
        string app = ReadSource("src", "App.cs");
        string autoStart = ReadSource("src", "AutoStartService.cs");
        Assert.Contains("RunCommandLineHelper", app, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox.Show(err", app, StringComparison.Ordinal);
        Assert.Contains("详细原因已写入 OneBox.log", autoStart, StringComparison.Ordinal);
        Assert.Contains("ElevatedHelperPolicy.TimeoutMessage", autoStart, StringComparison.Ordinal);
        Assert.Contains("process.Kill()", autoStart, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneralSettings_DoNotApplyAfterPersistenceFailure()
    {
        string dialog = ReadSource("src", "SettingsDialog.cs");
        string command = ReadSource("src", "MainWindow.Commands.cs");
        Assert.Contains("TryPersist", dialog, StringComparison.Ordinal);
        Assert.Contains("if (!TryPersist", ReadSource("src", "SettingsDialog.General.cs"), StringComparison.Ordinal);
        Assert.Contains("previousTopmost", command, StringComparison.Ordinal);
        Assert.Contains("AppPrefs.Set(PreferenceKeys.Window.Topmost, previousTopmost)", command, StringComparison.Ordinal);
    }

    [Fact]
    public void SolutionBuild_ComposesCompanionProcessesIntoGuiOutput()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        string root = FindRepositoryRoot();
        string bin = Path.Combine(root, "src", "bin", configuration);
        string output = Directory.GetDirectories(bin, "net*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "OneBox.exe")));
        Assert.False(string.IsNullOrEmpty(output), "GUI build output was not found.");

        foreach (string file in new[]
        {
            "OneBox.Service.exe", "OneBox.Service.dll", "OneBox.Service.deps.json",
            "OneBox.Service.runtimeconfig.json", "OneBox.Hardware.exe", "OneBox.Hardware.dll",
            "OneBox.Hardware.deps.json", "OneBox.Hardware.runtimeconfig.json",
            "OneBox.Contracts.dll", "Microsoft.Extensions.Hosting.dll",
            "Microsoft.Extensions.Hosting.WindowsServices.dll", "Microsoft.Extensions.Logging.EventLog.dll",
            "System.Diagnostics.EventLog.dll", "LibreHardwareMonitorLib.dll"
        })
            Assert.True(File.Exists(Path.Combine(output, file)), file);
        Assert.True(new FileInfo(Path.Combine(output, "System.Diagnostics.EventLog.dll")).Length > 100_000,
            "GUI output must contain the win-x64 EventLog implementation, not its ref assembly.");
    }

    [Fact]
    public void ServiceStartupProbe_LoadsTheComposedHostWithoutEventLogProviderFailure()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        string root = FindRepositoryRoot();
        string bin = Path.Combine(root, "src", "bin", configuration);
        string output = Directory.GetDirectories(bin, "net*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "OneBox.exe")));
        Assert.False(string.IsNullOrEmpty(output), "GUI build output was not found.");
        string servicePath = Path.Combine(output, "OneBox.Service.exe");
        Assert.True(File.Exists(servicePath), servicePath);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = servicePath,
            Arguments = "--startup-probe",
            WorkingDirectory = output,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        });
        Assert.NotNull(process);
        bool exited = process.WaitForExit(15000);
        if (!exited)
        {
            try { process.Kill(); } catch { }
        }
        Assert.True(exited, "Service startup probe did not exit promptly.");
        string stderr = process.StandardError.ReadToEnd();
        Assert.Equal(0, process.ExitCode);
        Assert.DoesNotContain("FileNotFoundException", stderr, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] parts)
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Repository source file was not found.", Path.Combine(parts));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
