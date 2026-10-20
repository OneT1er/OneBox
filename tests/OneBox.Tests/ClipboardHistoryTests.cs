using PowerAudioManager;
using Xunit;

namespace OneBox.Tests;

public sealed class ClipboardHistoryTests
{
    [Theory]
    [InlineData(null, 0u, true)]
    [InlineData(null, 1u, true)]
    [InlineData(17u, 17u, false)]
    [InlineData(17u, 18u, true)]
    [InlineData(17u, 0u, true)]
    public void SequenceGateReadsOnlyAnUnseenNonzeroSequence(uint? lastProcessed, uint current, bool expected)
    {
        Assert.Equal(expected, ClipboardHistory.ShouldReadClipboardSequence(lastProcessed, current));
    }
}
