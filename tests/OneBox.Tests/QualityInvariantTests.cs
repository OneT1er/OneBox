using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using PowerAudioManager;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    public void PipeIdentityVerification_UsesPipeOwnerInsteadOfOpeningSystemProcess()
    {
        string source = ReadSource("src", "PipeServerIdentityVerifier.cs");
        Assert.Contains("GetSecurityInfo", source, StringComparison.Ordinal);
        Assert.Contains("DangerousAddRef", source, StringComparison.Ordinal);
        Assert.Contains("OwnerSecurityInformation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private static extern IntPtr OpenProcess", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HardwareRuntime_ReclaimsSidScopedStaleCompanionPipe()
    {
        string source = ReadSource("src", "OneBox.Service", "UserRuntime.cs");
        Assert.Contains("CleanupStaleHardwarePipe", source, StringComparison.Ordinal);
        Assert.Contains("GetNamedPipeServerProcessId", source, StringComparison.Ordinal);
        Assert.Contains("ServiceConstants.HardwareExecutable", source, StringComparison.Ordinal);
        Assert.Contains("Kill(entireProcessTree: true)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HardwareRuntime_StopOwnsAndKillsHelperBeforeCancellingGuardian()
    {
        string source = ReadSource("src", "OneBox.Service", "UserRuntime.cs");
        Assert.Contains("_hardwareGate", source, StringComparison.Ordinal);
        Assert.Contains("_stopping", source, StringComparison.Ordinal);
        int stop = source.IndexOf("public async Task StopAsync()", StringComparison.Ordinal);
        int terminate = source.IndexOf("TerminateHardwareProcess(process);", stop, StringComparison.Ordinal);
        int cancel = source.IndexOf("_stop.Cancel();", stop, StringComparison.Ordinal);
        Assert.True(terminate >= 0 && cancel > terminate,
            "StopAsync must terminate the owned helper before cancelling the guardian.");
    }

    [Fact]
    public void HardwareMonitor_StopCancelsBeforeDisposingPipeAndSuppressesExpectedClose()
    {
        string source = ReadSource("src", "HardwareMonitorService.cs");
        Assert.Contains("catch (Exception) when (cancellationToken.IsCancellationRequested)", source, StringComparison.Ordinal);
        int cancel = source.IndexOf("cancellation?.Cancel();", StringComparison.Ordinal);
        int dispose = source.IndexOf("activePipe?.Dispose();", StringComparison.Ordinal);
        Assert.True(cancel >= 0 && dispose > cancel,
            "HardwareMonitorService.Stop must cancel before disposing the active pipe.");
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
    public void StartupServiceCheck_CannotBlockLoadedUiThread()
    {
        string source = ReadSource("src", "MainWindow.cs");
        int taskStart = source.IndexOf("Task.Run", StringComparison.Ordinal);
        int serviceCall = source.IndexOf("EnsureServiceRunning();", StringComparison.Ordinal);
        Assert.True(taskStart >= 0, "service startup must be dispatched off the WPF Loaded handler");
        Assert.True(serviceCall > taskStart, "EnsureServiceRunning must run inside the background task");
        Assert.Contains("OnLoaded done", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayStartup_ForceCreatesVerifiesAndReleasesShellIcon()
    {
        string source = ReadSource("src", "TrayController.cs");
        Assert.Contains("ForceCreate(false)", source, StringComparison.Ordinal);
        Assert.Contains("_tray.IsCreated", source, StringComparison.Ordinal);
        Assert.Contains("StartRetry", source, StringComparison.Ordinal);
        Assert.Contains("StopRetry", source, StringComparison.Ordinal);
        Assert.Contains("MaxTrayRetryAttempts", source, StringComparison.Ordinal);
        Assert.Contains("StopRetry();", source, StringComparison.Ordinal);
        Assert.Contains("_tray?.Dispose()", source, StringComparison.Ordinal);
        Assert.Contains("state changed:", source, StringComparison.Ordinal);
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
    public void SettingsComboBoxes_UseCompleteDarkPopupAndItemTemplate()
    {
        string theme = ReadSource("src", "ThemeTokens.cs");
        string resources = ReadSource("src", "AppResources.cs");
        string metrics = ReadSource("src", "SettingsDialog.Metrics.cs");

        Assert.Contains("CreateDarkComboBoxStyle", resources, StringComparison.Ordinal);
        Assert.Contains("CreateComboBoxTemplate", theme, StringComparison.Ordinal);
        Assert.Contains("CreateComboBoxItemTemplate", theme, StringComparison.Ordinal);
        Assert.Contains("DropDownChrome", theme, StringComparison.Ordinal);
        Assert.Contains("Popup.AllowsTransparencyProperty", theme, StringComparison.Ordinal);
        Assert.Contains("ComboBox.IsDropDownOpenProperty", theme, StringComparison.Ordinal);
        Assert.Contains("Selector.IsSelectedProperty", theme, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.BackgroundProperty, Brush(Card)", theme, StringComparison.Ordinal);
        Assert.Contains("StyleDarkComboBox(typeCombo)", metrics, StringComparison.Ordinal);
        Assert.Contains("StyleDarkComboBox(sensorCombo)", metrics, StringComparison.Ordinal);
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

    [Fact]
    public void TrayMenuTheme_UsesTransparentPopupChromeAndVectorStates()
    {
        string source = ReadSource("src", "TrayController.cs");
        Assert.Contains("CreateTrayMenu", source, StringComparison.Ordinal);
        Assert.Contains("HasDropShadow = false", source, StringComparison.Ordinal);
        Assert.Contains("ContextMenuService.SetHasDropShadow(menu, false)", source, StringComparison.Ordinal);
        Assert.Contains("Popup.AllowsTransparencyProperty, true", source, StringComparison.Ordinal);
        Assert.Contains("CheckMark", source, StringComparison.Ordinal);
        Assert.Contains("Path.StrokeProperty, UiKit.FrozenBrush(UiKit.AccentColor)", source, StringComparison.Ordinal);
        Assert.Contains("CreateTraySeparatorStyle", source, StringComparison.Ordinal);
        Assert.Contains("SubmenuPopup", source, StringComparison.Ordinal);
        // Popup.HasDropShadowProperty is read-only. It cannot be assigned by
        // FrameworkElementFactory; the transparent popup chrome plus the
        // ContextMenuService setting above are the supported no-shadow path.
        Assert.DoesNotContain("Popup.HasDropShadowProperty", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ButtonBase.IsPressedProperty", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayMenuTemplate_CanInstantiateOnStaWithoutReadOnlyPopupPropertyFailure()
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var trayType = typeof(MainWindow).Assembly.GetType("PowerAudioManager.TrayController", throwOnError: true);
                var factory = trayType.GetMethod("CreateTrayMenu", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(factory);
                var menu = Assert.IsType<ContextMenu>(factory.Invoke(null, null));
                Assert.False(menu.HasDropShadow);
                Assert.False(ContextMenuService.GetHasDropShadow(menu));
                var item = new MenuItem { Header = "Template smoke", IsCheckable = true, IsChecked = true };
                menu.Items.Add(item);
                Assert.True(menu.ApplyTemplate());
                Assert.True(item.ApplyTemplate());
            }
            catch (Exception ex)
            {
                failure = ex is TargetInvocationException { InnerException: not null } invocation
                    ? invocation.InnerException
                    : ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
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
