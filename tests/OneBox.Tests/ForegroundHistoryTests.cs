using System;
using PowerAudioManager;
using Xunit;

namespace OneBox.Tests;

public sealed class ForegroundHistoryTests : IDisposable
{
    readonly DateTime start = new DateTime(2026, 9, 20, 10, 0, 0);
    public ForegroundHistoryTests() => ForegroundHistory.Clear();
    public void Dispose() => ForegroundHistory.Clear();

    [Fact]
    public void FirstSampleDoesNotFillEarlierHistoryOrUnobservedFuture()
    {
        ForegroundHistory.RecordSample(start, "game");
        Assert.Empty(ForegroundHistory.GetSegments(start.AddHours(-1), start));
        Assert.Empty(ForegroundHistory.GetSegments(start.AddSeconds(2), start.AddHours(1)));
        var segment = Assert.Single(ForegroundHistory.GetSegments(start.AddHours(-1), start.AddHours(1)));
        Assert.Equal(start, segment.Start);
        Assert.Equal(start.AddSeconds(2), segment.End);
    }

    [Fact]
    public void SameAppSamplesMergeOnlyWhileSamplingContinues()
    {
        ForegroundHistory.RecordSample(start, "game");
        ForegroundHistory.RecordSample(start.AddSeconds(2), "game");
        ForegroundHistory.RecordSample(start.AddHours(1), "game");
        var segments = ForegroundHistory.GetSegments(start, start.AddHours(2));
        Assert.Equal(2, segments.Count);
        Assert.Equal(start.AddSeconds(4), segments[0].End);
        Assert.Equal(start.AddHours(1), segments[1].Start);
        Assert.Empty(ForegroundHistory.GetSegments(start.AddMinutes(1), start.AddMinutes(59)));
    }

    [Fact]
    public void UnknownCaptureDoesNotReusePreviousApp()
    {
        ForegroundHistory.RecordSample(start, "game");
        ForegroundHistory.RecordSample(start.AddSeconds(2), null);
        ForegroundHistory.RecordSample(start.AddSeconds(4), "editor");
        Assert.Empty(ForegroundHistory.GetSegments(start.AddSeconds(2), start.AddSeconds(4)));
        Assert.Equal("editor", Assert.Single(ForegroundHistory.GetSegments(start.AddSeconds(4), start.AddSeconds(6))).Exe);
    }

    [Fact]
    public void QueryClipsToBothRequestedBounds()
    {
        ForegroundHistory.RecordSample(start, "game");
        var segment = Assert.Single(ForegroundHistory.GetSegments(start.AddMilliseconds(500), start.AddSeconds(1)));
        Assert.Equal(start.AddMilliseconds(500), segment.Start);
        Assert.Equal(start.AddSeconds(1), segment.End);
    }

    [Fact]
    public void SwitchingAppsTruncatesPreviousSampleWithoutOverlap()
    {
        ForegroundHistory.RecordSample(start, "game");
        ForegroundHistory.RecordSample(start.AddSeconds(1), "OneBox");
        var segments = ForegroundHistory.GetSegments(start, start.AddSeconds(4));
        Assert.Equal(2, segments.Count);
        Assert.Equal(segments[0].End, segments[1].Start);
        Assert.Equal("OneBox", segments[1].Exe);
    }

    [Fact]
    public void ClosingChartDoesNotClearOrStopBackgroundHistory()
    {
        ForegroundHistory.RecordSample(start, "game");
        ForegroundHistory.Release();
        ForegroundHistory.RecordSample(start.AddSeconds(2), "game");
        Assert.Equal(start.AddSeconds(4), Assert.Single(ForegroundHistory.GetSegments(start, start.AddSeconds(10))).End);
    }

    [Fact]
    public void ClockRollbackRemovesOverlappingFutureRecords()
    {
        ForegroundHistory.RecordSample(start, "game");
        ForegroundHistory.RecordSample(start.AddSeconds(10), "browser");
        ForegroundHistory.RecordSample(start.AddSeconds(1), "editor");
        var segments = ForegroundHistory.GetSegments(start, start.AddMinutes(1));
        Assert.Equal(2, segments.Count);
        Assert.Equal(segments[0].End, segments[1].Start);
        Assert.Equal("editor", segments[1].Exe);
    }
}
