using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PowerAudioManager
{
    // 快捷启动栏：最多 8 个槽位（WrapPanel 自动换行），支持 exe/快捷方式/文件夹/URL。
    // 注册表 Launcher.Paths（'|' 分隔）持久化；只显示已填槽位 + 1 个空占位。
    internal static class LauncherBar
    {
        const int MaxSlots = 8;
        const string LauncherPrefKey = "Launcher.Paths";

        // requestRebuild 在槽位路径变更时回调，宿主据此重新渲染图标
        public static void Build(StackPanel contentPanel, Action requestRebuild)
        {
            if (contentPanel.Children.Count > 0) contentPanel.Children.Add(MainWindow.MakeDivider());
            var header = new TextBlock {
                Foreground = new SolidColorBrush(MainWindow.AccentColor), FontSize = 12,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
            header.Inlines.Add(new Run("🚀") { FontFamily = AppResources.EmojiFont });
            header.Inlines.Add(new Run(" 快捷启动"));
            contentPanel.Children.Add(header);
            var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            var paths = LoadLauncherPaths();
            // 已填槽位 + 1 个空占位，不超过 MaxSlots
            int shown = Math.Min(MaxSlots, paths.Count + 1);
            if (shown < 1) shown = 1;
            for (int i = 0; i < shown; i++)
            {
                string p = i < paths.Count ? paths[i] : null;
                wrap.Children.Add(MakeLauncherSlot(i, p, paths, requestRebuild));
            }
            contentPanel.Children.Add(wrap);
        }

        static List<string> LoadLauncherPaths()
        {
            var list = new List<string>();
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PowerAudioManager\App"))
                {
                    if (k != null)
                    {
                        var s = k.GetValue(LauncherPrefKey) as string;
                        if (!string.IsNullOrEmpty(s))
                            foreach (var p in s.Split('|')) if (p.Length > 0) list.Add(p);
                    }
                }
            }
            catch { }
            return list;
        }

        static void SaveLauncherPaths(List<string> paths)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\PowerAudioManager\App"))
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < paths.Count; i++) { if (i > 0) sb.Append('|'); sb.Append(paths[i]); }
                    k.SetValue(LauncherPrefKey, sb.ToString());
                }
            }
            catch { }
        }

        static bool IsUrl(string s)
        {
            return s != null && (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                              || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        static bool IsFolder(string s)
        {
            try { return !string.IsNullOrEmpty(s) && Directory.Exists(s); }
            catch { return false; }
        }

        // 从 exe/dll/lnk 提取图标，URL 和文件夹返回 null（另外处理）
        static ImageSource ExtractIcon(string path)
        {
            try
            {
                if (IsUrl(path) || IsFolder(path)) return null;
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (ico != null)
                {
                    var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        ico.Handle, Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch { }
            return null;
        }

        static Button MakeLauncherSlot(int index, string path, List<string> paths, Action requestRebuild)
        {
            var btn = new Button {
                Width = 44, Height = 44,
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(MainWindow.CardColor),
                BorderBrush = new SolidColorBrush(MainWindow.BorderColor),
                ToolTip = string.IsNullOrEmpty(path) ? "拖入程序 / 快捷方式 / 文件夹 / URL" : path,
                AllowDrop = true
            };
            MainWindow.ApplyIconButtonStyle(btn);

            if (!string.IsNullOrEmpty(path))
            {
                var img = ExtractIcon(path);
                if (img != null)
                {
                    btn.Content = new System.Windows.Controls.Image { Source = img, Width = 24, Height = 24 };
                }
                else if (IsUrl(path))
                {
                    btn.Content = "🌐";
                    btn.FontSize = 20;
                    btn.ToolTip = path;
                    FetchFavicon(path, btn);
                }
                else if (IsFolder(path))
                {
                    btn.Content = "📁";
                    btn.FontSize = 20;
                    btn.ToolTip = path;
                }
                else
                {
                    btn.Content = "•";
                }
            }
            else
            {
                btn.Content = "+";
                btn.FontSize = 18;
                btn.Foreground = new SolidColorBrush(MainWindow.TextSecondary);
            }

            btn.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(path))
                {
                    // 弹菜单选择添加类型：程序 / 文件夹 / 网页
                    var menu = new ContextMenu();
                    var miExe = new MenuItem { Header = "程序 / 快捷方式..." };
                    var miFolder = new MenuItem { Header = "文件夹..." };
                    var miUrl = new MenuItem { Header = "网页 (URL)..." };
                    miExe.Click += (_, _) => AddProgram(paths, requestRebuild);
                    miFolder.Click += (_, _) => AddFolder(paths, requestRebuild);
                    miUrl.Click += (_, _) => AddUrl(btn, paths, requestRebuild);
                    menu.Items.Add(miExe);
                    menu.Items.Add(miFolder);
                    menu.Items.Add(miUrl);
                    menu.PlacementTarget = btn;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.IsOpen = true;
                }
                else
                {
                    try
                    {
                        var psi = new ProcessStartInfo(path) { UseShellExecute = true };
                        Process.Start(psi);
                    }
                    catch (Exception ex) { AppLog.Log("Launch " + path, ex); }
                }
            };

            btn.MouseRightButtonUp += (s, e) =>
            {
                if (!string.IsNullOrEmpty(path))
                {
                    paths.RemoveAt(index);
                    SaveLauncherPaths(paths);
                    requestRebuild();
                    e.Handled = true;
                }
            };

            btn.DragEnter += (s, e) =>
            {
                if (LauncherBar.HasDropData(e.Data))
                {
                    e.Effects = DragDropEffects.Copy;
                    btn.BorderBrush = new SolidColorBrush(MainWindow.AccentColor);
                    btn.BorderThickness = new Thickness(2);
                }
                else { e.Effects = DragDropEffects.None; }
                e.Handled = true;
            };
            btn.DragOver += (s, e) =>
            {
                e.Effects = LauncherBar.HasDropData(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            btn.DragLeave += (s, e) =>
            {
                btn.BorderBrush = new SolidColorBrush(MainWindow.BorderColor);
                btn.BorderThickness = new Thickness(1);
                e.Handled = true;
            };
            btn.Drop += (s, e) =>
            {
                btn.BorderBrush = new SolidColorBrush(MainWindow.BorderColor);
                btn.BorderThickness = new Thickness(1);
                string dropped = ExtractDropped(e);
                if (!string.IsNullOrEmpty(dropped)) AddDropped(dropped, requestRebuild);
                e.Handled = true;
            };
            return btn;
        }

        // 抓取网站 favicon（先试 /favicon.ico，再解析 HTML <link rel=icon>），缓存到 %TEMP%\OneBoxFavicons
        static async void FetchFavicon(string url, Button btn)
        {
            try
            {
                var uri = new Uri(url);
                string domain = uri.Host;
                AppLog.Log("Favicon", "fetch " + url);
                string cacheDir = Path.Combine(Path.GetTempPath(), "OneBoxFavicons");
                Directory.CreateDirectory(cacheDir);
                string cacheFile = Path.Combine(cacheDir, domain + ".ico");

                if (!File.Exists(cacheFile))
                {
                    using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }, true))
                    {
                        client.Timeout = TimeSpan.FromSeconds(5);
                        byte[] bytes = null;

                        // 1) 先试标准 /favicon.ico
                        try { bytes = await client.GetByteArrayAsync($"{uri.Scheme}://{domain}/favicon.ico"); }
                        catch { }

                        // 2) 再解析 HTML <link rel="icon" href="...">
                        if (bytes == null || bytes.Length < 100)
                        {
                            try
                            {
                                var html = await client.GetStringAsync($"{uri.Scheme}://{domain}/");
                                var match = System.Text.RegularExpressions.Regex.Match(html,
                                    @"<link[^>]+rel=[""'](?:shortcut\s+)?icon[""'][^>]+href=[""']([^""']+)",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (match.Success)
                                {
                                    var href = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
                                    var iconUri = new Uri(new Uri($"{uri.Scheme}://{domain}/"), href);
                                    try { bytes = await client.GetByteArrayAsync(iconUri); } catch { }
                                }
                            }
                            catch { }
                        }

                        AppLog.Log("Favicon", "got " + domain + ": " + (bytes?.Length ?? 0) + " bytes");
                        if (bytes != null && bytes.Length >= 100)
                        {
                            File.WriteAllBytes(cacheFile, bytes);
                            AppLog.Log("Favicon", "saved " + domain + ": " + bytes.Length + " bytes");
                        }
                        else
                            return; // 未找到图标 → 保持 🌐
                    }
                }

                // 回到 UI 线程加载缓存图标
                btn.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bmp.StreamSource = new FileStream(cacheFile, FileMode.Open, FileAccess.Read);
                        bmp.DecodePixelWidth = 24;
                        bmp.EndInit();
                        bmp.Freeze();
                        btn.Content = new Image { Source = bmp, Width = 24, Height = 24 };
                    }
                    catch { /* keep 🌐 */ }
                });
            }
            catch { /* network error → keep 🌐 */ }
        }

        // 解析 .lnk 快捷方式到目标路径；非快捷方式或解析失败回退原路径。
        // 使用反射后期绑定（避免添加 dynamic / Microsoft.CSharp 依赖）
        public static bool HasDropData(IDataObject data)
        {
            return data.GetDataPresent(DataFormats.FileDrop)
                || data.GetDataPresent("UniformResourceLocator")
                || data.GetDataPresent("text/x-moz-url")
                || data.GetDataPresent(DataFormats.Text)
                || data.GetDataPresent(DataFormats.StringFormat);
        }

        public static string ExtractDropped(DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0 && !string.IsNullOrEmpty(files[0])) return files[0];
                }
                // 浏览器拖 URL 的几种格式
                string[] urlFormats = { "UniformResourceLocator", "text/x-moz-url", DataFormats.Text, DataFormats.StringFormat };
                foreach (var fmt in urlFormats)
                {
                    if (!e.Data.GetDataPresent(fmt)) continue;
                    var data = e.Data.GetData(fmt);
                    string s = null;
                    if (data is string str) s = str;
                    else if (data is Stream st)
                    {
                        using (st)
                        using (var reader = new StreamReader(st, fmt == "UniformResourceLocator" ? Encoding.Unicode : Encoding.UTF8, true))
                            s = reader.ReadToEnd();
                    }
                    if (string.IsNullOrEmpty(s)) continue;
                    s = s.TrimEnd('\0').Trim();
                    var parts = s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;
                    var line = parts[0].Trim();
                    if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        return line;
                }
            }
            catch { }
            return null;
        }

        public static void AddDropped(string dropped, Action requestRebuild)
        {
            if (string.IsNullOrEmpty(dropped)) return;
            dropped = dropped.Trim();
            string resolved = ResolveShortcut(dropped);
            var paths = LoadLauncherPaths();
            paths.Add(resolved);
            SaveLauncherPaths(paths);
            requestRebuild();
        }

        static void AddProgram(List<string> paths, Action requestRebuild)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "程序|*.exe;*.lnk|所有文件|*.*", Title = "选择要添加的程序" };
            if (dlg.ShowDialog() == true)
            {
                paths.Add(ResolveShortcut(dlg.FileName));
                SaveLauncherPaths(paths);
                requestRebuild();
            }
        }

        static void AddFolder(List<string> paths, Action requestRebuild)
        {
            try
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "选择要添加的文件夹", ShowNewFolderButton = false };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
                {
                    paths.Add(dlg.SelectedPath);
                    SaveLauncherPaths(paths);
                    requestRebuild();
                }
            }
            catch (Exception ex) { AppLog.Log("AddFolder", ex); }
        }

        static void AddUrl(Button btn, List<string> paths, Action requestRebuild)
        {
            string url = PromptUrl(btn);
            if (!string.IsNullOrEmpty(url))
            {
                url = url.Trim();
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    url = "https://" + url;
                paths.Add(url);
                SaveLauncherPaths(paths);
                requestRebuild();
            }
        }

        static string PromptUrl(Button owner)
        {
            string result = null;
            var dlg = new Window
            {
                Title = "添加网页", Width = 360, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(owner),
                Background = new SolidColorBrush(Color.FromRgb(28, 26, 40)), ResizeMode = ResizeMode.NoResize
            };
            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(new TextBlock { Text = "输入网址（如 https://github.com）", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) });
            var tb = new TextBox { FontSize = 12, Padding = new Thickness(6, 4, 6, 4) };
            stack.Children.Add(tb);
            var ok = new Button { Content = "添加", Height = 28, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(20, 0, 20, 0), Margin = new Thickness(0, 12, 0, 0) };
            AppResources.StyleDialogButton(ok, true);
            ok.Click += (_, _) => { result = tb.Text?.Trim(); dlg.DialogResult = true; dlg.Close(); };
            stack.Children.Add(ok);
            dlg.Content = stack;
            tb.Focus();
            dlg.ShowDialog();
            return result;
        }

        static string ResolveShortcut(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            // URL 和文件夹原样返回
            if (IsUrl(path) || IsFolder(path)) return path;
            try
            {
                if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return path;
                object shell = Activator.CreateInstance(shellType);
                try
                {
                    object sc = shellType.InvokeMember("CreateShortcut",
                        BindingFlags.InvokeMethod, null, shell, new object[] { path });
                    if (sc == null) return path;
                    try
                    {
                        object target = sc.GetType().InvokeMember("TargetPath",
                            BindingFlags.GetProperty, null, sc, null);
                        string t = target as string;
                        if (!string.IsNullOrEmpty(t)) return t;
                    }
                    finally { try { Marshal.ReleaseComObject(sc); } catch { } }
                }
                finally { try { Marshal.ReleaseComObject(shell); } catch { } }
            }
            catch (Exception ex) { AppLog.Log("ResolveShortcut " + path, ex); }
            return path;
        }
    }
}
