using System;
using System.Drawing;
using PowerAudioManager.Commands;

namespace PowerAudioManager
{
    internal enum ScreenshotToastPosition { BottomRight, BottomLeft, TopRight, TopLeft }

    internal sealed record ScreenshotToastOptions
    {
        public ScreenshotToastPosition Position { get; init; } = ScreenshotToastPosition.BottomRight;
        public bool Compact { get; init; }
        public int DurationSeconds { get; init; } = 5;
        public bool ExcludeFromCapture { get; init; } = true;

        public static ScreenshotToastOptions Load() => new ScreenshotToastOptions
        {
            Position = (ScreenshotToastPosition)AppPrefs.Get(PreferenceKeys.Screenshot.ToastPosition),
            Compact = AppPrefs.Get(PreferenceKeys.Screenshot.ToastCompact),
            DurationSeconds = AppPrefs.Get(PreferenceKeys.Screenshot.ToastDurationSeconds),
            ExcludeFromCapture = AppPrefs.Get(PreferenceKeys.Screenshot.ToastExcludeFromCapture)
        }.Normalize();

        public ScreenshotToastOptions Normalize() => this with
        {
            Position = Enum.IsDefined(Position) ? Position : ScreenshotToastPosition.BottomRight,
            DurationSeconds = Math.Clamp(DurationSeconds, 1, 30)
        };

        // 所有输入和输出均为物理像素，使用显示器工作区以避开任务栏。
        public Point GetOrigin(Rectangle area, int width, int height)
        {
            var position = Normalize().Position;
            bool left = position is ScreenshotToastPosition.BottomLeft or ScreenshotToastPosition.TopLeft;
            bool top = position is ScreenshotToastPosition.TopLeft or ScreenshotToastPosition.TopRight;
            int maxX = Math.Max(area.Left, area.Right - width);
            int maxY = Math.Max(area.Top, area.Bottom - height);
            return new Point(Math.Clamp(left ? area.Left + 12 : area.Right - width - 12, area.Left, maxX),
                Math.Clamp(top ? area.Top + 8 : area.Bottom - height - 8, area.Top, maxY));
        }
    }
}
