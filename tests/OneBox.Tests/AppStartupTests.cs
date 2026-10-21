using PowerAudioManager;
using Xunit;

namespace OneBox.Tests;

public sealed class AppStartupTests
{
    [Fact]
    public void SoftwareOnly_IsNormalGuiStartupAndNotAVelopackHook()
    {
        string[] args = { "--software-only" };
        Assert.True(App.IsSoftwareOnlyStartup(args));
        Assert.Empty(App.GetStartupArgs(args));
        Assert.True(App.IsRendererDiagnosticStartup(args));
        Assert.False(App.ShouldRetryPendingServiceRepair(args));
        Assert.Equal("--software-only", args[0]);
    }

    [Fact]
    public void DefaultDiagnosticSharesRoutingAndPreservesPendingRepair()
    {
        string[] args = { "--renderer-default" };
        Assert.False(App.IsSoftwareOnlyStartup(args));
        Assert.True(App.IsRendererDiagnosticStartup(args));
        Assert.Empty(App.GetStartupArgs(args));
        Assert.False(App.ShouldRetryPendingServiceRepair(args));
        Assert.True(App.ShouldRetryPendingServiceRepair(System.Array.Empty<string>()));
    }

    [Fact]
    public void OtherStartupArgumentsRemainUnchanged()
    {
        string[][] cases = {
            System.Array.Empty<string>(), new[] { "--audio-denoise-worker" },
            new[] { "--repair-service" }, new[] { "--elevate-autostart", "1" },
            new[] { "--disable-autostart" }, new[] { "--prepare-update" },
            new[] { "--clean-memory", "1" }, new[] { "--launcher", "123" },
            new[] { "--veloapp-updated", "1.9.1" }, new[] { "--repair-service", "--software-only" }
        };
        foreach (string[] args in cases)
        {
            Assert.False(App.IsSoftwareOnlyStartup(args));
            Assert.False(App.IsRendererDiagnosticStartup(args));
            Assert.Equal(args.Length == 0, App.ShouldRetryPendingServiceRepair(args));
            Assert.Same(args, App.GetStartupArgs(args));
        }
    }
}
