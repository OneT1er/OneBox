using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace PowerAudioManager
{
    // 从 MainWindow 抽出的共享 UI 工具：调色板、按钮样式、矢量图标与分割线。
    // 悬浮窗 / 快捷启动栏 / 托盘菜单 / 设置对话框共用，避免全部挂靠在 MainWindow 上。
    internal static class UiKit
    {
        // 共享调色板：按 Material 层级排列，越底层表面越浅，叠层卡片有视觉深度。
        internal static readonly Color AccentColor = Color.FromRgb(142, 140, 216);   // 紫影 #8E8CD8
        internal static readonly Color BgColor = Color.FromRgb(28, 26, 40);          // 深底
        internal static readonly Color CardColor = Color.FromRgb(42, 39, 60);        // 卡片
        internal static readonly Color TextPrimary = Colors.White;
        internal static readonly Color TextSecondary = Color.FromRgb(190, 188, 220); // 次要文字
        internal static readonly Color ActiveBg = Color.FromRgb(110, 105, 200);      // 激活态
        internal static readonly Color BorderColor = Color.FromRgb(80, 75, 120);     // 边框

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

        // 给按钮打上 MaterialDesign 扁平样式。通过资源键查找，缺失则回退无样式，避免主题异常时按钮变空。
        internal static void ApplyFlatStyle(Button btn)
        {
            var style = Application.Current.TryFindResource("MaterialDesignFlatButton") as Style;
            if (style != null) btn.Style = style;
        }

        // 紧凑图标按钮（标题栏、静音、启动栏）。MaterialDesign 默认 MinWidth=88/MinHeight=36/大 Padding
        // 会撑大按钮导致图标"消失"。本地值覆盖样式设置项，强制 MinWidth/MinHeight=0, Padding=0。
        internal static void ApplyIconButtonStyle(Button btn)
        {
            ApplyFlatStyle(btn);
            btn.MinWidth = 0;
            btn.MinHeight = 0;
            btn.Padding = new Thickness(0);
            btn.HorizontalContentAlignment = HorizontalAlignment.Center;
            btn.VerticalContentAlignment = VerticalAlignment.Center;
        }

        // 标题栏锁定按钮图标。矢量图标不受 MaterialDesign 样式覆盖 FontFamily 影响，emoji 字体会被替换导致消失。
        internal static PackIcon PinIcon(bool locked)
        {
            return new PackIcon { Kind = locked ? PackIconKind.Lock : PackIconKind.LockOpen, Width = 16, Height = 16 };
        }

        internal static PackIcon MuteIcon(bool muted)
        {
            return new PackIcon { Kind = muted ? PackIconKind.VolumeMute : PackIconKind.VolumeHigh, Width = 16, Height = 16 };
        }

        // 彩色矢量图标（设置对话框与悬浮窗指标行复用）
        internal static Image MetricIcon(string iconKey, Color c)
        {
            var brush = new SolidColorBrush(c);
            Geometry geo = iconKey switch
            {
                "cpu"  => Geometry.Parse("M5,1 h6 a1,1 0 0,1 1,1 v1 h1 v2 h-1 v1 h1 v2 h-1 v1 a1,1 0 0,1 -1,1 h-6 a1,1 0 0,1 -1,-1 v-1 h-1 v-2 h1 v-1 h-1 v-2 h1 v-1 a1,1 0 0,1 1,-1 z M6,4 h2 v2 h2 v-2 h-2 z"),
                "gpu"  => Geometry.Parse("M3,2 h10 v6 h-2.5 v1 h-1 v-1 h-1.5 v1 h-1 v-1 h-1.5 z M5,4 h1.5 v2 h-1.5 z M7.5,4 h1.5 v2 h-1.5 z M10,4 h1.5 v2 h-1.5 z"),
                "hot"  => Geometry.Parse("M8,0 l3,3 l-1.5,1.5 l1.5,2.5 l-1.5,2 l-2,-2.5 l-2,1.5 l1,-3 l-2.5,-0.8 l2.5,-1.5 z M8,5 a0.8,0.8 0 1,0 0,1.6 a0.8,0.8 0 1,0 0,-1.6"),
                "vram" => Geometry.Parse("M2,4 h12 v5 a1,1 0 0,1 -1,1 h-10 a1,1 0 0,1 -1,-1 z M3,7 h1.5 v-1.5 h1 v1.5 h2 v-1.5 h1 v1.5 h1.5"),
                "dram" => Geometry.Parse("M2,3 h12 v7 a1,1 0 0,1 -1,1 h-10 a1,1 0 0,1 -1,-1 z M3,5 h2 v-1 h1 v1 h3 v-1 h1 v1 h2"),
                "disk" => Geometry.Parse("M5,2 a4,4 0 1,0 0,8 a4,4 0 1,0 0,-8 z M7,6 a1,1 0 1,0 0,2 a1,1 0 1,0 0,-2 z M5,5 h3 a2,2 0 0,1 0,1 h-3 z"),
                "fan"  => Geometry.Parse("M8,2 a3,3 0 1,0 0,4 l0,-2 a1.5,1.5 0 1,1 0,-2 z M8,6 a0.8,0.8 0 1,0 0,1.6 a0.8,0.8 0 1,0 0,-1.6"),
                "ctrl" => Geometry.Parse("M3,6 h10 v1 h-10 z M7,2 h2 v8 h-2 z M10,4 h3 v1.5 h-3 z"),
                "mb"   => Geometry.Parse("M2,2 h12 v8 h-12 z M4,4 h3 v2 h-3 z M9,4 h3 v2 h-3 z M4,8 h2 v1 h-2 z M7,8 h2 v1 h-2 z"),
                _      => Geometry.Parse("M8,3 a4,4 0 1,0 0,6 a4,4 0 1,0 0,-6"),
            };
            return new Image { Source = new DrawingImage(new GeometryDrawing(brush, null, geo)), Width = 12, Height = 12, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) };
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
