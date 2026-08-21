using PowerAudioManager;
using Xunit;

namespace OneBox.Tests;

public sealed class HotkeyCaptureDialogTests
{
    [Fact]
    public void Format_ReturnsNoHotkeyForZero()
    {
        Assert.Equal("(无)", HotkeyCaptureDialog.Format(0));
    }

    [Fact]
    public void Format_UsesStableModifierOrder()
    {
        // Ctrl + Shift + T: modifier bits 1=Alt, 2=Ctrl, 4=Shift, 8=Win.
        var encoded = (2 << 16) | (4 << 16) | 0x54;

        Assert.Equal("Ctrl+Shift+T", HotkeyCaptureDialog.Format(encoded));
    }
}
