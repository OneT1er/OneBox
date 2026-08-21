using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerAudioManager;
using Xunit;

namespace OneBox.Tests;

public sealed class MemoryCleanerPolicyTests
{
    [Fact]
    public void ComposeFlags_AllDisabled_RemainsNone()
    {
        var flags = MemoryCleaner.ComposeFlags(false, false, false, false, false, false, false, false);

        Assert.Equal(MemoryCleaner.CleanFlags.None, flags);
        Assert.False(MemoryCleaner.HasSelectedAreas(flags));
    }

    [Fact]
    public void ComposeFlags_DefaultSelection_MatchesDefault()
    {
        var flags = MemoryCleaner.ComposeFlags(true, true, false, false, true, true, true, true);

        Assert.Equal(MemoryCleaner.CleanFlags.Default, flags);
    }

    [Fact]
    public void CleanAll_None_IsAnImmediateNoOp()
    {
        var result = MemoryCleaner.CleanAll(MemoryCleaner.CleanFlags.None);

        Assert.Equal(0UL, result.FreedBytes);
        Assert.False(result.WorkingSetsEmptied);
        Assert.False(result.StandbyPurged);
        Assert.False(result.ModifiedFlushed);
        Assert.False(result.FileCacheReleased);
    }
}

public sealed class MonitorLifecyclePolicyTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    public void CollectionRequiresEnabledModuleAndLiveApplication(bool enabled, bool exiting, bool expected)
    {
        Assert.Equal(expected, MonitorLifecyclePolicy.ShouldCollect(enabled, exiting));
    }
}

public sealed class LauncherPolicyTests
{
    [Fact]
    public void EmptyLauncher_AcceptsExactlyEightItems()
    {
        var result = LauncherPolicy.AddWithinLimit(Array.Empty<string>(), Enumerable.Range(0, 10).Select(i => "item" + i));

        Assert.Equal(8, result.Paths.Count);
        Assert.Equal(8, result.Added);
        Assert.Equal(2, result.Rejected);
    }

    [Fact]
    public void SevenExistingItems_AcceptsOneAndRejectsRemainder()
    {
        var result = LauncherPolicy.AddWithinLimit(Enumerable.Range(0, 7).Select(i => "old" + i), new[] { "a", "b", "c" });

        Assert.Equal(8, result.Paths.Count);
        Assert.Equal(1, result.Added);
        Assert.Equal(2, result.Rejected);
    }

    [Fact]
    public void FullLauncher_RejectsNewItem()
    {
        var result = LauncherPolicy.AddWithinLimit(Enumerable.Range(0, 8).Select(i => "old" + i), new[] { "new" });

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Rejected);
    }

    [Fact]
    public void ExistingOverflow_IsClampedToEight()
    {
        var result = LauncherPolicy.AddWithinLimit(Enumerable.Range(0, 12).Select(i => "old" + i), null);

        Assert.Equal(8, result.Paths.Count);
    }

    [Fact]
    public void EmptyCandidates_DoNotConsumeCapacity()
    {
        var result = LauncherPolicy.AddWithinLimit(new[] { "old" }, new[] { "", "  ", "new" });

        Assert.Equal(new[] { "old", "new" }, result.Paths);
        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Rejected);
    }

    [Fact]
    public void SlotIdentity_RejectsDeletedOrMovedSlot()
    {
        var paths = new[] { "first", "second" };

        Assert.True(LauncherPolicy.IsCurrentSlot(paths, 1, "second"));
        Assert.False(LauncherPolicy.IsCurrentSlot(paths, 0, "second"));
        Assert.False(LauncherPolicy.IsCurrentSlot(paths, 2, "second"));
    }
}

public sealed class CaptureCoordinateMapperTests
{
    [Theory]
    [InlineData(1.00, 10, 20, 110, 220, 10, 20, 100, 200)]
    [InlineData(1.25, 8, 16, 88, 96, 10, 20, 100, 100)]
    [InlineData(1.50, 10, 20, 110, 120, 15, 30, 150, 150)]
    [InlineData(2.00, 10, 20, 110, 120, 20, 40, 200, 200)]
    public void MapsCommonDpiScalesToPhysicalPixels(
        double scale, double x1, double y1, double x2, double y2,
        int expectedX, int expectedY, int expectedWidth, int expectedHeight)
    {
        var screen = new CapturePixelRect(0, 0, 4000, 3000);

        var result = CaptureCoordinateMapper.MapDipSelection(x1, y1, x2, y2, 0, 0, scale, scale, screen);

        Assert.Equal(expectedX, result.X);
        Assert.Equal(expectedY, result.Y);
        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
    }

    [Fact]
    public void MapsLeftHandNegativeCoordinateMonitor()
    {
        var screen = new CapturePixelRect(-1920, 0, 5760, 2160);

        var result = CaptureCoordinateMapper.MapDipSelection(0, 0, 800, 400, -1920, 0, 1.25, 1.25, screen);

        Assert.Equal(-1920, result.X);
        Assert.Equal(0, result.Y);
        Assert.Equal(1000, result.Width);
        Assert.Equal(500, result.Height);
    }

    [Fact]
    public void ReverseDragIsNormalizedAndClamped()
    {
        var screen = new CapturePixelRect(-100, -50, 200, 100);

        var result = CaptureCoordinateMapper.MapDipSelection(150, 100, -50, -100, -100, -50, 1, 1, screen);

        Assert.Equal(-100, result.X);
        Assert.Equal(-50, result.Y);
        Assert.Equal(150, result.Width);
        Assert.Equal(100, result.Height);
    }
}

public sealed class ScreenshotConcurrencyTests
{
    [Fact]
    public async Task Gate_SerializesConcurrentCaptures()
    {
        var gate = new ScreenshotConcurrencyGate();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maximum = 0;

        Task<int> first = gate.RunAsync(async token =>
        {
            int now = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, now);
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(token);
            Interlocked.Decrement(ref active);
            return 1;
        }, TimeSpan.FromSeconds(2), CancellationToken.None);
        await firstEntered.Task;

        Task<int> second = gate.RunAsync(async token =>
        {
            int now = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, now);
            await Task.Yield();
            Interlocked.Decrement(ref active);
            return 2;
        }, TimeSpan.FromSeconds(2), CancellationToken.None);
        await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.Equal(1, maximum);
        releaseFirst.SetResult();

        Assert.Equal(new[] { 1, 2 }, await Task.WhenAll(first, second));
        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task Gate_TimeoutCancelsLongCapture()
    {
        var gate = new ScreenshotConcurrencyGate();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.RunAsync(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }, TimeSpan.FromMilliseconds(20), CancellationToken.None));
    }

    [Fact]
    public async Task Gate_HonorsCallerCancellation()
    {
        var gate = new ScreenshotConcurrencyGate();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.RunAsync(
            _ => Task.FromResult(0), TimeSpan.FromSeconds(1), cancellation.Token));
    }
}

public sealed class GameBarCaptureMatcherTests
{
    [Fact]
    public void Matcher_PairsPngAndJxrWithSameBaseName()
    {
        DateTime trigger = DateTime.UtcNow;
        var candidates = new[]
        {
            new CaptureFileCandidate { Path = @"C:\Captures\shot.png", LastWriteUtc = trigger.AddMilliseconds(100) },
            new CaptureFileCandidate { Path = @"C:\Captures\shot.jxr", LastWriteUtc = trigger.AddMilliseconds(200) }
        };

        var match = GameBarCaptureMatcher.Select(candidates, new HashSet<string>(), trigger);

        Assert.EndsWith("shot.png", match.PngPath);
        Assert.EndsWith("shot.jxr", match.JxrPath);
    }

    [Fact]
    public void Matcher_RejectsFilesPresentBeforeTrigger()
    {
        DateTime trigger = DateTime.UtcNow;
        string old = @"C:\Captures\old.png";
        var candidates = new[] { new CaptureFileCandidate { Path = old, LastWriteUtc = trigger } };

        var match = GameBarCaptureMatcher.Select(candidates, new HashSet<string> { old }, trigger);

        Assert.Null(match.PngPath);
        Assert.Null(match.JxrPath);
    }

    [Fact]
    public void Matcher_DoesNotPairDifferentCaptureNames()
    {
        DateTime trigger = DateTime.UtcNow;
        var candidates = new[]
        {
            new CaptureFileCandidate { Path = @"C:\Captures\first.png", LastWriteUtc = trigger.AddMilliseconds(100) },
            new CaptureFileCandidate { Path = @"C:\Captures\other.jxr", LastWriteUtc = trigger.AddMilliseconds(200) }
        };

        var match = GameBarCaptureMatcher.Select(candidates, new HashSet<string>(), trigger);

        Assert.EndsWith("first.png", match.PngPath);
        Assert.Null(match.JxrPath);
    }
}

public sealed class ExitLifecycleGateTests
{
    [Fact]
    public void Gate_AllowsExactlyOneConcurrentExit()
    {
        var gate = new ExitLifecycleGate();

        int winners = Enumerable.Range(0, 32).AsParallel().Count(_ => gate.TryBegin());

        Assert.Equal(1, winners);
        Assert.True(gate.IsStarted);
    }
}
