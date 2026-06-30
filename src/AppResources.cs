using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerAudioManager
{
    // 共享资源访问：应用字体（从系统已安装字体中选择）和内嵌图片。
    // 字体不再打包进 exe —— 使用用户在设置中选择的字体（默认 Microsoft YaHei UI）。
    internal static class AppResources
    {
        const string DefaultFontName = "Microsoft YaHei UI";
        const string FontPrefKey = "App.FontFamily";

        // 用户选择的系统字体对应的 WPF FontFamily（保存的名称缺失或无效时回退到 Microsoft YaHei UI）。
        static FontFamily _cachedAppFont;
        static string _cachedFontName;

        public static FontFamily AppFont
        {
            get
            {
                var name = AppPrefs.GetString(FontPrefKey, DefaultFontName);
                if (_cachedAppFont == null || _cachedFontName != name)
                {
                    _cachedAppFont = ResolveFont(name);
                    _cachedFontName = name;
                }
                return _cachedAppFont;
            }
        }

        // 重新读取字体（用户在设置中更改后调用），返回新 FontFamily 供宿主应用到窗口。
        public static FontFamily ReloadFont()
        {
            _cachedAppFont = null;
            _cachedFontName = null;
            return AppFont;
        }

        // 构建 FontFamily，若名称无法解析为已安装字体族则回退到默认值。
        static FontFamily ResolveFont(string name)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var ff = new FontFamily(name);
                    // 验证字体族是否真的已安装。
                    foreach (var f in Fonts.SystemFontFamilies)
                        if (string.Equals(f.Source, name, StringComparison.OrdinalIgnoreCase))
                            return ff;
                }
            }
            catch { }
            return new FontFamily(DefaultFontName);
        }

        public static readonly FontFamily EmojiFont =
            new FontFamily("Segoe UI Symbol, Segoe UI Emoji");

        // WinForms 托盘菜单对应的 AppFont：用同一字体名称构建 System.Drawing.Font，保持右键菜单风格一致。
        static System.Drawing.Font _trayFont;
        static string _trayFontName;
        public static System.Drawing.Font TrayFont()
        {
            var name = AppPrefs.GetString(FontPrefKey, DefaultFontName);
            if (_trayFont != null && _trayFontName == name) return _trayFont;
            if (_trayFont != null) { try { _trayFont.Dispose(); } catch { } _trayFont = null; }
            try
            {
                // 传给 WinForms 前验证字体族是否已安装；未知名称在 WinForms 中也会静默回退，但这里显式处理更安全。
                bool installed = false;
                using (var col = new System.Drawing.Text.InstalledFontCollection())
                    foreach (var f in col.Families)
                        if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                        { installed = true; break; }
                _trayFont = new System.Drawing.Font(installed ? name : DefaultFontName, 9f);
            }
            catch { _trayFont = new System.Drawing.Font(DefaultFontName, 9f); }
            _trayFontName = name;
            return _trayFont;
        }

        // 按文件名加载内嵌图片（png/ico）：优先使用嵌入式清单资源（无需外部文件），
        // 回退到 exe 旁的外部文件。返回已冻结的 BitmapSource，不可用时返回 null。
        public static BitmapImage LoadAppImage(string fileName)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string resName = "PowerAudioManager." + fileName;
                using (var stream = asm.GetManifestResourceStream(resName))
                {
                    if (stream != null)
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = stream;
                        bmp.EndInit();
                        bmp.Freeze();
                        return bmp;
                    }
                }
                var dir = Path.GetDirectoryName(Environment.ProcessPath);
                var path = Path.Combine(dir, fileName);
                if (File.Exists(path))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(path);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch { }
            return null;
        }

        // ---- 对话框按钮样式（共享）------------------------------------
        // 将 MaterialDesign 扁平按钮样式应用到对话框按钮。
        // 独立窗口（设置/翻译/剪贴板历史/快捷键）与悬浮窗保持统一设计语言。
        //
        // primary = true  → 紫影填充 + 白色文字（主操作，如确定/翻译）
        // primary = false → 透明扁平按钮，暗色文字
        static readonly Color DDim = Color.FromRgb(190, 188, 220);
        static readonly Color DAccent = Color.FromRgb(142, 140, 216);

        public static void StyleDialogButton(Button btn, bool primary)
        {
            // MaterialDesign 扁平按钮：透明填充 + 悬浮波纹，无投影阴影（保持对话框扁平卡片风格）。
            // 主操作按钮用紫影填充以突出显示；次要按钮保持透明，依赖波纹提供可交互暗示。
            var style = Application.Current.TryFindResource("MaterialDesignFlatButton") as Style;
            if (style != null) btn.Style = style;
            if (primary)
            {
                btn.Background = new SolidColorBrush(DAccent);
                btn.Foreground = Brushes.White;
                btn.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                btn.Foreground = new SolidColorBrush(DDim);
            }
        }

        // ---- 深色 TabControl 样式（共享）----------------------------------
        // 现在是空操作：MaterialDesign 的隐式 TabControl/TabItem 样式（来自全局 MaterialDesign3 默认值）
        // 已自动渲染深色紫影标签外观。保留为空方法以便迁移期间现有调用点编译通过。
        public static void StyleDarkTabControl(TabControl tc)
        {
            tc.Background = Brushes.Transparent;
            tc.BorderBrush = Brushes.Transparent;
            tc.Padding = new Thickness(0);
        }

        // 现在是空操作：MaterialDesign 的隐式 ComboBox 样式（来自全局 MaterialDesign3 默认值）
        // 已自动渲染深色紫影下拉框。保留为空方法以便迁移期间现有调用点编译通过。
        public static void StyleDarkComboBox(ComboBox cb)
        {
        }

    }
}
