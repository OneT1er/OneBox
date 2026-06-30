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
                    var dlg = new Microsoft.Win32.OpenFileDialog {
                        Filter = "程序|*.exe;*.lnk|所有文件|*.*",
                        Title = "选择要添加的程序" };
                    if (dlg.ShowDialog() == true)
                    {
                        string resolved = ResolveShortcut(dlg.FileName);
                        paths.Add(resolved);
                        SaveLauncherPaths(paths);
                        requestRebuild();
                    }
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
                if (e.Data.GetDataPresent(DataFormats.FileDrop) ||
                    e.Data.GetDataPresent(DataFormats.Text) ||
                    e.Data.GetDataPresent(DataFormats.StringFormat))
                {
                    e.Effects = DragDropEffects.Copy;
                    btn.BorderBrush = new SolidColorBrush(MainWindow.AccentColor);
                    btn.BorderThickness = new Thickness(2);
                }
                else { e.Effects = DragDropEffects.None; }
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
                string dropped = null;

                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0) dropped = files[0];
                }
                else if (e.Data.GetDataPresent(DataFormats.Text))
                {
                    dropped = e.Data.GetData(DataFormats.Text) as string;
                }
                else if (e.Data.GetDataPresent(DataFormats.StringFormat))
                {
                    dropped = e.Data.GetData(DataFormats.StringFormat) as string;
                }

                if (!string.IsNullOrEmpty(dropped))
                {
                    dropped = dropped.Trim();
                    // 解析 .lnk 目标路径；URL 和文件夹原样保留
                    string resolved = ResolveShortcut(dropped);
                    paths.Add(resolved);
                    SaveLauncherPaths(paths);
                    requestRebuild();
                }
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
                string cacheDir = Path.Combine(Path.GetTempPath(), "OneBoxFavicons");
                Directory.CreateDirectory(cacheDir);
                string cacheFile = Path.Combine(cacheDir, domain + ".ico");

                if (!File.Exists(cacheFile))
                {
                    using (var client = new HttpClient())
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

                        if (bytes != null && bytes.Length >= 100)
                            File.WriteAllBytes(cacheFile, bytes);
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
