using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PowerAudioManager
{
    // The icon set is deliberately compiled into the WPF assembly.  This keeps
    // rendering crisp on PerMonitorV2 displays and means a published folder does
    // not have to carry a loose SVG directory at runtime.
    public enum IconKey
    {
        Brand, Power, Audio, Mute, Lock, Unlock, ChevronRight, ChevronDown,
        ChevronUp, Close, Performance, MemoryClean, Translate, Clipboard,
        Gallery, Launcher, Url, Folder, Add, Edit, Delete, Settings, Modules,
        Dashboard, Temperature, Capture, Error, Success, Warning, Cpu, Gpu,
        Hot, Vram, Dram, Disk, Fan, Control, Motherboard, DefaultMetric
    }

    public static class IconCatalog
    {
        static readonly IReadOnlyDictionary<IconKey, string> Paths =
            new ReadOnlyDictionary<IconKey, string>(new Dictionary<IconKey, string>
        {
            [IconKey.Brand] = "M4,7.5 L12,4 L20,7.5 L20,16.5 L12,20 L4,16.5 Z M4,7.5 L12,11 L20,7.5 M12,11 L12,20 M8,8.9 L8,13.2 M16,8.9 L16,13.2 M8,13.2 L16,13.2",
            [IconKey.Power] = "M12,3 L12,11 M7.2,5.8 A8,8 0 1 0 16.8,5.8",
            [IconKey.Audio] = "M4,10 L8,10 L13,6 L13,18 L8,14 L4,14 Z M16,9.2 A4,4 0 0 1 16,14.8 M18.7,6.8 A7.5,7.5 0 0 1 18.7,17.2",
            [IconKey.Mute] = "M4,10 L8,10 L13,6 L13,18 L8,14 L4,14 Z M4,4 L20,20",
            [IconKey.Lock] = "M5,10 L19,10 L19,20 L5,20 Z M8,10 L8,7 A4,4 0 0 1 16,7 L16,10 M12,14 L12,17",
            [IconKey.Unlock] = "M5,10 L19,10 L19,20 L5,20 Z M8,10 L8,7 A4,4 0 0 1 15.2,4.6 M12,14 L12,17",
            [IconKey.ChevronRight] = "M9,5 L16,12 L9,19",
            [IconKey.ChevronDown] = "M5,9 L12,16 L19,9",
            [IconKey.ChevronUp] = "M5,15 L12,8 L19,15",
            [IconKey.Close] = "M6,6 L18,18 M18,6 L6,18",
            [IconKey.Performance] = "M4,19 L4,5 M4,19 L20,19 M6,15 L10,11 L13,13 L18,7 M15,7 L18,7 L18,10",
            [IconKey.MemoryClean] = "M7,7 L17,7 L16,20 L8,20 Z M5,7 L19,7 M10,4 L14,4 M10,11 L10,16 M14,11 L14,16 M18,4 L19,5 M20,3 L20,5 M21,5 L19,5",
            [IconKey.Translate] = "M4,5 L13,5 M8.5,3 L8.5,5 M6,5 C6.7,9 9,11.5 12.5,13 M6.5,9.5 C7.9,8.5 9.2,7.1 10.1,5 M14,21 L20,21 L17,13 Z M15.2,18 L18.8,18",
            [IconKey.Clipboard] = "M5,5 L19,5 L19,21 L5,21 Z M9,5 L9,3 L15,3 L15,5 M8,10 L16,10 M8,14 L14,14 M8,18 L12,18",
            [IconKey.Gallery] = "M4,5 L20,5 L20,19 L4,19 Z M9,10 A1.3,1.3 0 1 0 9,10.1 M6,17 L10,13 L13,16 L15,14 L19,17",
            [IconKey.Launcher] = "M4,6 L20,6 L20,16 L4,16 Z M7,9 L10,9 L10,13 L7,13 M14,9 L17,9 L17,13 L14,13 M8,16 L8,19 M16,16 L16,19 M4,10 L2,10 M20,10 L22,10",
            [IconKey.Url] = "M13,3 C16.5,3.8 18.8,6.4 19.5,10 L12.7,16.8 L8.2,12.3 Z M8.2,13.8 L5,14.5 L7.5,16.3 L8.2,19.4 L10,16.9 M13,9.5 L13.1,9.5 M4,20 L7,17",
            [IconKey.Folder] = "M3.5,7.5 L9.5,7.5 L11.2,9.5 L20.5,9.5 L20.5,18.2 A2,2 0 0 1 18.5,20.2 L5.5,20.2 A2,2 0 0 1 3.5,18.2 Z M3.5,7.5 L3.5,6.5 A2,2 0 0 1 5.5,4.5 L9.5,4.5 L11.2,6.5 L16.5,6.5",
            [IconKey.Add] = "M4,4 L20,4 L20,20 L4,20 Z M12,8 L12,16 M8,12 L16,12",
            [IconKey.Edit] = "M5,16 L4.2,20 L8.2,19.2 L19,8.4 A2.1,2.1 0 0 0 16,5.4 Z M14.5,6.5 L17.5,9.5 M5,20 L8,17",
            [IconKey.Delete] = "M6,7 L18,7 M10,4 L14,4 M8,7 L9,20 L15,20 L16,7 M10,11 L10,16 M14,11 L14,16",
            [IconKey.Settings] = "M12,4 A8,8 0 1 0 12,20 A8,8 0 1 0 12,4 M12,8 A4,4 0 1 0 12,16 A4,4 0 1 0 12,8",
            [IconKey.Modules] = "M4,4 L10,4 L10,10 L4,10 Z M14,4 L20,4 L20,10 L14,10 Z M4,14 L10,14 L10,20 L4,20 Z M14,14 L20,14 L20,20 L14,20 Z",
            [IconKey.Dashboard] = "M4,15 A8,8 0 1 1 20,15 L20,18 L4,18 Z M12,14 L16,10 M7,18 L7.1,18 M12,18 L12.1,18 M17,18 L17.1,18",
            [IconKey.Temperature] = "M10,14.5 L10,6 A2,2 0 1 1 14,6 L14,14.5 A5,5 0 1 1 10,14.5 Z M12,10 L12,17 M12,19 L12.1,19",
            [IconKey.Capture] = "M8,4 L5,4 A1,1 0 0 0 4,5 L4,8 M16,4 L19,4 A1,1 0 0 1 20,5 L20,8 M8,20 L5,20 A1,1 0 0 1 4,19 L4,16 M16,20 L19,20 A1,1 0 0 0 20,19 L20,16 M8,8 L16,8 L16,16 L8,16 Z",
            [IconKey.Error] = "M12,4 A8,8 0 1 0 12,20 A8,8 0 1 0 12,4 M12,8 L12,13 M12,16 L12.1,16",
            [IconKey.Success] = "M12,4 A8,8 0 1 0 12,20 A8,8 0 1 0 12,4 M8,12 L10.5,14.5 L16,9",
            [IconKey.Warning] = "M12,4 L21,20 L3,20 Z M12,9 L12,14 M12,17 L12.1,17",
            [IconKey.Cpu] = "M7,7 L17,7 L17,17 L7,17 Z M9,10 L15,10 L15,14 L9,14 Z M4,9 L7,9 M4,12 L7,12 M4,15 L7,15 M17,9 L20,9 M17,12 L20,12 M17,15 L20,15 M9,4 L9,7 M12,4 L12,7 M15,4 L15,7 M9,17 L9,20 M12,17 L12,20 M15,17 L15,20",
            [IconKey.Gpu] = "M4,6 L20,6 L20,16 L4,16 Z M7,9 L10,9 L10,13 L7,13 M14,9 L17,9 L17,13 L14,13 M8,16 L8,19 M16,16 L16,19 M4,10 L2,10 M20,10 L22,10",
            [IconKey.Hot] = "M13,3 C14,6 12,7 12,9 C12,10 13,11 14,11 C15.5,11 16,9.6 16,8.2 C18.1,10.2 19,12.3 19,15.1 A7,7 0 1 1 5,15.1 C5,12.5 6.3,10 8.8,8.3 C8.6,10.4 9.4,11.4 10.5,11.4 C11.9,11.4 12.3,9.7 12.3,8.2 C10.3,5.8 11.5,4.3 13,3 Z",
            [IconKey.Vram] = "M4,7 L20,7 L20,17 L4,17 Z M7,10 L9,10 L9,14 L7,14 M11,10 L13,10 L13,14 L11,14 M15,10 L17,10 L17,14 L15,14 M8,17 L8,20 M12,17 L12,20 M16,17 L16,20",
            [IconKey.Dram] = "M5,5 L19,5 L19,19 L5,19 Z M8,8 L11,8 L11,11 L8,11 M13,8 L16,8 L16,11 L13,11 M8,13 L11,13 L11,16 L8,16 M13,13 L16,13 L16,16 L13,16",
            [IconKey.Disk] = "M12,4 A8,8 0 1 0 12,20 A8,8 0 1 0 12,4 M12,10 A2,2 0 1 0 12,14 A2,2 0 1 0 12,10 M12,4 L12,7 M5.5,7.5 L7.7,9.7",
            [IconKey.Fan] = "M12,12 A2,2 0 1 0 12,12.1 M12,10 C11,6 13,4 15,5 C17,6 16,9 13,11 M14,12 C18,11 20,13 19,15 C18,17 15,16 13,13 M12,14 C13,18 11,20 9,19 C7,18 8,15 11,13 M10,12 C6,13 4,11 5,9 C6,7 9,8 11,11",
            [IconKey.Control] = "M5,6 L19,6 M5,12 L19,12 M5,18 L19,18 M9,6 A2,2 0 1 0 9,6.1 M15,12 A2,2 0 1 0 15,12.1 M11,18 A2,2 0 1 0 11,18.1",
            [IconKey.Motherboard] = "M4,4 L20,4 L20,20 L4,20 Z M7,7 L12,7 L12,12 L7,12 Z M15,7 L18,7 L18,9 L15,9 Z M15,13 L17,13 M7,15 L9,15 L9,17 M12,12 L17,17",
            [IconKey.DefaultMetric] = "M12,6 A6,6 0 1 0 12,18 A6,6 0 1 0 12,6 M12,11 A1,1 0 1 0 12,13 A1,1 0 1 0 12,11"
        });

        static readonly IReadOnlyDictionary<IconKey, string> Names =
            new ReadOnlyDictionary<IconKey, string>(new Dictionary<IconKey, string>
        {
            [IconKey.Brand] = "OneBox 工具箱", [IconKey.Power] = "电源计划", [IconKey.Audio] = "音频输出",
            [IconKey.Mute] = "静音", [IconKey.Lock] = "锁定窗口位置", [IconKey.Unlock] = "解除窗口位置锁定",
            [IconKey.ChevronRight] = "展开", [IconKey.ChevronDown] = "折叠", [IconKey.ChevronUp] = "上移",
            [IconKey.Close] = "关闭", [IconKey.Performance] = "性能趋势", [IconKey.MemoryClean] = "内存清理",
            [IconKey.Translate] = "翻译", [IconKey.Clipboard] = "剪贴板历史", [IconKey.Gallery] = "截图图库",
            [IconKey.Launcher] = "快捷启动", [IconKey.Url] = "打开网页", [IconKey.Folder] = "打开文件夹",
            [IconKey.Add] = "添加快捷项", [IconKey.Edit] = "编辑指标", [IconKey.Delete] = "删除指标",
            [IconKey.Settings] = "常规设置", [IconKey.Modules] = "模块设置", [IconKey.Dashboard] = "性能仪表",
            [IconKey.Temperature] = "温度监控", [IconKey.Capture] = "截图取景框", [IconKey.Error] = "错误",
            [IconKey.Success] = "成功", [IconKey.Warning] = "警告", [IconKey.Cpu] = "处理器", [IconKey.Gpu] = "显卡",
            [IconKey.Hot] = "温度", [IconKey.Vram] = "显存", [IconKey.Dram] = "内存", [IconKey.Disk] = "硬盘",
            [IconKey.Fan] = "风扇", [IconKey.Control] = "风扇控制", [IconKey.Motherboard] = "主板", [IconKey.DefaultMetric] = "指标"
        });

        static readonly IReadOnlyDictionary<IconKey, Geometry> Geometries = BuildGeometryCache();

        static IReadOnlyDictionary<IconKey, Geometry> BuildGeometryCache()
        {
            var result = new Dictionary<IconKey, Geometry>();
            foreach (var pair in Paths)
            {
                var geometry = Geometry.Parse(pair.Value);
                geometry.Freeze();
                result.Add(pair.Key, geometry);
            }
            return new ReadOnlyDictionary<IconKey, Geometry>(result);
        }

        public static IEnumerable<IconKey> Keys => Paths.Keys;
        public static string AutomationName(IconKey key) => Names[key];

        public static Geometry GetGeometry(IconKey key)
            => Geometries[key];

        internal static PathView Create(IconKey key, double size = 16, Brush brush = null)
        {
            var path = new Path
            {
                Data = GetGeometry(key), Width = size, Height = size,
                Stretch = Stretch.Uniform, Fill = null,
                Stroke = brush ?? UiKit.FrozenBrush(UiKit.TextSecondary),
                StrokeThickness = size >= 20 ? 1.7 : 1.8,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false
            };
            return new PathView(path);
        }

        internal static FrameworkElement CreateElement(IconKey key, double size = 16, Brush brush = null)
            => Create(key, size, brush).Element;

        internal static Button ConfigureIconButton(Button button, IconKey key, string tooltip, double size = 16)
        {
            button.Content = CreateElement(key, size, button.Foreground);
            button.ToolTip = tooltip;
            AutomationProperties.SetName(button, tooltip);
            button.MinWidth = Math.Max(button.MinWidth, 28);
            button.MinHeight = Math.Max(button.MinHeight, 28);
            button.Padding = new Thickness(0);
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;
            return button;
        }

        internal sealed class PathView
        {
            internal Path Element { get; }
            internal PathView(Path element) => Element = element;
        }
    }
}
