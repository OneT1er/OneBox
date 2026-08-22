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

        // 按文件名加载图片（png/ico）。托盘图标必须保留 UriSource：
        // H.NotifyIcon.Wpf 通过 BitmapImage.UriSource 重新打开图标流，
        // 因此不能把 app.ico 只读成 StreamSource（那会让 UriSource 为空）。
        // 发布目录始终携带 app.ico；清单资源仍作为其他图片的回退。
        public static BitmapImage LoadAppImage(string fileName)
        {
            try
            {
                var dir = Path.GetDirectoryName(Environment.ProcessPath);
                var path = Path.Combine(dir, fileName);
                if (File.Exists(path))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }

                var asm = Assembly.GetExecutingAssembly();
                string resName = "PowerAudioManager." + fileName;
                using (var stream = asm.GetManifestResourceStream(resName))
                {
                    if (stream == null) return null;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    // StreamSource handles non-ASCII install paths consistently;
                    // BitmapImage.UriSource has historically failed for Chinese
                    // directory names on some WPF image codecs.
                    bmp.StreamSource = stream;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch { }
            return null;
        }

        // ---- 对话框按钮样式（共享）------------------------------------
        // Apply the shared flat button style to dialog buttons.
        // 独立窗口（设置/翻译/剪贴板历史/快捷键）与悬浮窗保持统一设计语言。
        //
        // primary = true  → 紫影填充 + 白色文字（主操作，如确定/翻译）
        // primary = false → 透明扁平按钮，暗色文字
        static readonly Color DDim = Color.FromRgb(190, 188, 220);
        static readonly Color DAccent = Color.FromRgb(142, 140, 216);

        public static void StyleDialogButton(Button btn, bool primary)
        {
            // Flat button: transparent secondary action with the shared hover state.
            // 主操作按钮用紫影填充以突出显示；次要按钮保持透明，依赖波纹提供可交互暗示。
            var style = Application.Current.TryFindResource(ThemeTokens.FlatButtonKey) as Style;
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
        // Shared tab surface defaults.
        // 已自动渲染深色紫影标签外观。保留为空方法以便迁移期间现有调用点编译通过。
        public static void StyleDarkTabControl(TabControl tc)
        {
            tc.Background = Brushes.Transparent;
            tc.BorderBrush = Brushes.Transparent;
            tc.Padding = new Thickness(0);
        }

        // Shared combo surface defaults.
        // Apply the complete dark template (closed state, popup, item hover,
        // selection, focus and disabled states).  WPF's default ComboBox
        // template is tied to the Windows light theme and leaves white
        // surfaces behind even when Background/Foreground are set locally.
        public static void StyleDarkComboBox(ComboBox cb)
        {
            if (cb == null) return;
            cb.Style = ThemeTokens.CreateDarkComboBoxStyle();
            cb.Background = new SolidColorBrush(ThemeTokens.Card);
            cb.Foreground = new SolidColorBrush(Color.FromRgb(230, 228, 250));
            cb.BorderBrush = new SolidColorBrush(ThemeTokens.Border);
            cb.BorderThickness = new Thickness(1);
        }

    }
}
