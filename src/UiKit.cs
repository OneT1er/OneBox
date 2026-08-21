using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerAudioManager
{
    // 从 MainWindow 抽出的共享 UI 工具：调色板、按钮样式、矢量图标与分割线。
    // 悬浮窗 / 快捷启动栏 / 托盘菜单 / 设置对话框共用，避免全部挂靠在 MainWindow 上。
    internal static class UiKit
    {
        // 共享调色板：按 Material 层级排列，越底层表面越浅，叠层卡片有视觉深度。
        internal static readonly Color AccentColor = ThemeTokens.Accent;
        internal static readonly Color BgColor = ThemeTokens.Background;
        internal static readonly Color CardColor = ThemeTokens.Card;
        internal static readonly Color TextPrimary = ThemeTokens.PrimaryText;
        internal static readonly Color TextSecondary = ThemeTokens.SecondaryText;
        internal static readonly Color ActiveBg = ThemeTokens.Active;
        internal static readonly Color BorderColor = ThemeTokens.Border;

        // 按颜色缓存冻结画笔：温度行每秒刷新频繁，复用冻结 SolidColorBrush 避免每秒 new 一批 Freezable。
        static readonly Dictionary<Color, SolidColorBrush> _frozenBrushes = new Dictionary<Color, SolidColorBrush>();
        internal static SolidColorBrush FrozenBrush(Color c)
        {
            if (!_frozenBrushes.TryGetValue(c, out var b)) { b = new SolidColorBrush(c); b.Freeze(); _frozenBrushes[c] = b; }
            return b;
        }

        // 柔和的 Fluent 风格分割线：使用边框色但留左右边距，作为区块间分隔。
        internal static Border MakeDivider()
        {
            return new Border
            {
                Height = 1,
                Background = new SolidColorBrush(BorderColor),
                Margin = new Thickness(2, 12, 2, 12),
                Opacity = 0.6
            };
        }

        // Apply the shared local button style.  A local style keeps every window
        // consistent even when the optional visual package is absent.
        internal static void ApplyFlatStyle(Button btn)
        {
            var style = Application.Current?.TryFindResource(ThemeTokens.FlatButtonKey) as Style;
            if (style != null) btn.Style = style;
            else
            {
                btn.Background = Brushes.Transparent;
                btn.BorderBrush = Brushes.Transparent;
            }
        }

        // Compact icon button (title bar, mute, launcher).  Keep the hit target
        // at least 28px while the vector itself remains on the 16px grid.
        internal static void ApplyIconButtonStyle(Button btn)
        {
            ApplyFlatStyle(btn);
            btn.MinWidth = Math.Max(btn.MinWidth, 28);
            btn.MinHeight = Math.Max(btn.MinHeight, 28);
            btn.Padding = new Thickness(0);
            btn.HorizontalContentAlignment = HorizontalAlignment.Center;
            btn.VerticalContentAlignment = VerticalAlignment.Center;
        }

        // All app icons come from the compiled vector catalog.
        internal static FrameworkElement PinIcon(bool locked, Brush brush = null)
            => IconCatalog.CreateElement(locked ? IconKey.Lock : IconKey.Unlock, 16, brush);

        internal static FrameworkElement MuteIcon(bool muted, Brush brush = null)
            => IconCatalog.CreateElement(muted ? IconKey.Mute : IconKey.Audio, 16, brush);

        // 彩色矢量图标（设置对话框与悬浮窗指标行复用）
        internal static FrameworkElement MetricIcon(string iconKey, Color c)
        {
            IconKey key = iconKey switch
            {
                "cpu" => IconKey.Cpu, "gpu" => IconKey.Gpu, "hot" => IconKey.Hot,
                "vram" => IconKey.Vram, "dram" => IconKey.Dram, "disk" => IconKey.Disk,
                "fan" => IconKey.Fan, "ctrl" => IconKey.Control, "mb" => IconKey.Motherboard,
                _ => IconKey.DefaultMetric
            };
            var view = IconCatalog.CreateElement(key, 14, new SolidColorBrush(c));
            view.VerticalAlignment = VerticalAlignment.Center;
            view.Margin = new Thickness(0, 1, 0, 0);
            return view;
        }

        internal static Color MetricIconColorByKey(string iconKey)
        {
            return iconKey switch
            {
                "cpu"  => Color.FromRgb(255, 140, 60),
                "gpu"  => Color.FromRgb(100, 210, 100),
                "hot"  => Color.FromRgb(255, 70, 50),
                "vram" => Color.FromRgb(180, 140, 255),
                "dram" => Color.FromRgb(140, 200, 255),
                "disk" => Color.FromRgb(200, 180, 140),
                "fan"  => Color.FromRgb(80, 180, 220),
                "ctrl" => Color.FromRgb(220, 200, 80),
                "mb"   => Color.FromRgb(160, 220, 160),
                _      => Color.FromRgb(255, 180, 80),
            };
        }
    }
}
