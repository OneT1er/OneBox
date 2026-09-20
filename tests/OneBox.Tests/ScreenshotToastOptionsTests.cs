using System.Drawing;
using PowerAudioManager;
using Xunit;

namespace OneBox.Tests;

public sealed class ScreenshotToastOptionsTests
{
    [Theory]
    [InlineData(ScreenshotToastPosition.BottomRight, 668, 692)]
    [InlineData(ScreenshotToastPosition.BottomLeft, 12, 692)]
    [InlineData(ScreenshotToastPosition.TopRight, 668, 8)]
    [InlineData(ScreenshotToastPosition.TopLeft, 12, 8)]
    internal void PlacesNotificationAtChosenCorner(ScreenshotToastPosition position, int x, int y)
    {
        var options = new ScreenshotToastOptions { Position = position };
        Assert.Equal(new Point(x, y), options.GetOrigin(new Rectangle(0, 0, 1000, 900), 320, 200));
    }

    [Fact]
    public void NegativeMonitorCoordinatesAndTaskbarWorkAreaAreRespected()
    {
        var options = new ScreenshotToastOptions();
        Assert.Equal(new Point(-332, 832), options.GetOrigin(new Rectangle(-1920, 0, 1920, 1040), 320, 200));
    }

    [Fact]
    public void OversizedNotificationIsAnchoredWithinSmallWorkArea()
    {
        Assert.Equal(new Point(20, 30), new ScreenshotToastOptions().GetOrigin(new Rectangle(20, 30, 100, 80), 320, 200));
    }

    [Fact]
    public void InvalidSavedPreferencesFallBackToSafeValues()
    {
        var options = new ScreenshotToastOptions { Position = (ScreenshotToastPosition)99, DurationSeconds = -1 }.Normalize();
        Assert.Equal(ScreenshotToastPosition.BottomRight, options.Position);
        Assert.Equal(1, options.DurationSeconds);
        Assert.Equal(30, (options with { DurationSeconds = 999 }).Normalize().DurationSeconds);
        Assert.True(new ScreenshotToastOptions().ExcludeFromCapture);
        Assert.False(new ScreenshotToastOptions().Compact);
        Assert.Equal(5, new ScreenshotToastOptions().DurationSeconds);
    }
}
