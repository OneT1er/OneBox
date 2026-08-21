using System;
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
    public void GeneralSettings_DoNotApplyAfterPersistenceFailure()
    {
        string dialog = ReadSource("src", "SettingsDialog.cs");
        string command = ReadSource("src", "MainWindow.Commands.cs");
        Assert.Contains("TryPersist", dialog, StringComparison.Ordinal);
        Assert.Contains("if (!TryPersist", ReadSource("src", "SettingsDialog.General.cs"), StringComparison.Ordinal);
        Assert.Contains("previousTopmost", command, StringComparison.Ordinal);
        Assert.Contains("AppPrefs.Set(PreferenceKeys.Window.Topmost, previousTopmost)", command, StringComparison.Ordinal);
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
}
