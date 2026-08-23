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
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PowerAudioManager.Commands;

namespace PowerAudioManager
{
    public sealed class LauncherCapacityResult
    {
        public List<string> Paths { get; } = new List<string>();
        public int Added { get; internal set; }
        public int Rejected { get; internal set; }
    }

    public static class LauncherPolicy
    {
        public const int MaxSlots = 8;
        internal const int MaxFaviconBytes = 512 * 1024;

        public static LauncherCapacityResult AddWithinLimit(IEnumerable<string> existing, IEnumerable<string> candidates)
        {
            var result = new LauncherCapacityResult();
            if (existing != null)
            {
                foreach (string item in existing)
                {
                    if (result.Paths.Count >= MaxSlots) break;
                    if (!string.IsNullOrWhiteSpace(item)) result.Paths.Add(item.Trim());
                }
            }
            if (candidates == null) return result;
            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                if (result.Paths.Count >= MaxSlots) { result.Rejected++; continue; }
                result.Paths.Add(candidate.Trim());
                result.Added++;
            }
            return result;
        }

        public static bool IsCurrentSlot(IReadOnlyList<string> paths, int index, string expectedPath)
        {
            return paths != null && index >= 0 && index < paths.Count &&
                string.Equals(paths[index], expectedPath, StringComparison.Ordinal);
        }
    }

    // 快捷启动栏：最多 8 个槽位（WrapPanel 自动换行），支持 exe/快捷方式/文件夹/URL。
    // 注册表 Launcher.Paths（'|' 分隔）持久化；只显示已填槽位 + 1 个空占位。
    internal static class LauncherBar
    {
        const int MaxSlots = LauncherPolicy.MaxSlots;
        const string LauncherPrefKey = "Launcher.Paths";

        // requestRebuild 在槽位路径变更时回调，宿主据此重新渲染图标
        public static void Build(StackPanel contentPanel, Action requestRebuild, MainWindow commandOwner = null)
        {
            if (contentPanel.Children.Count > 0) contentPanel.Children.Add(UiKit.MakeDivider());
            var header = new TextBlock {
                Foreground = new SolidColorBrush(UiKit.AccentColor), FontSize = 12,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
            header.Inlines.Add(new Run("快捷启动"));
            contentPanel.Children.Add(header);
            var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            var paths = LoadLauncherPaths();
            // 已填槽位 + 1 个空占位，不超过 MaxSlots
            int shown = Math.Min(MaxSlots, paths.Count + 1);
            if (shown < 1) shown = 1;
            for (int i = 0; i < shown; i++)
            {
                string p = i < paths.Count ? paths[i] : null;
                wrap.Children.Add(MakeLauncherSlot(i, p, paths, requestRebuild, commandOwner));
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
                            foreach (var p in s.Split('|'))
                            {
                                if (list.Count >= MaxSlots) break;
                                if (p.Length > 0) list.Add(p);
                            }
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
                    int count = Math.Min(paths.Count, MaxSlots);
                    for (int i = 0; i < count; i++) { if (i > 0) sb.Append('|'); sb.Append(paths[i]); }
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
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
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

        static Button MakeLauncherSlot(int index, string path, List<string> paths, Action requestRebuild,
            MainWindow commandOwner)
        {
            var btn = new Button {
                Width = 44, Height = 44,
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(UiKit.CardColor),
                BorderBrush = new SolidColorBrush(UiKit.BorderColor),
                ToolTip = string.IsNullOrEmpty(path) ? "拖入程序 / 快捷方式 / 文件夹 / URL" : path,
                AllowDrop = true,
                Tag = path
            };
            AutomationProperties.SetName(btn, string.IsNullOrEmpty(path) ? "添加快捷项" : path);
            UiKit.ApplyIconButtonStyle(btn);

            if (!string.IsNullOrEmpty(path))
            {
                var img = ExtractIcon(path);
                if (img != null)
                {
                    btn.Content = new System.Windows.Controls.Image { Source = img, Width = 24, Height = 24 };
                }
                else if (IsUrl(path))
                {
                    btn.Content = IconCatalog.CreateElement(IconKey.Url, 20, UiKit.FrozenBrush(UiKit.AccentColor));
                    btn.ToolTip = path;
                    // The fetch is deliberately detached from UI construction, but the
                    // Task-returning method observes every failure internally so an
                    // abandoned network request cannot become an unhandled exception.
                    _ = FetchFaviconAsync(path, index, btn);
                }
                else if (IsFolder(path))
                {
                    btn.Content = IconCatalog.CreateElement(IconKey.Folder, 20, UiKit.FrozenBrush(UiKit.AccentColor));
                    btn.ToolTip = path;
                }
                else
                {
                    btn.Content = IconCatalog.CreateElement(IconKey.Warning, 20, UiKit.FrozenBrush(UiKit.TextSecondary));
                }
            }
            else
            {
                btn.Content = IconCatalog.CreateElement(IconKey.Add, 20, UiKit.FrozenBrush(UiKit.TextSecondary));
                btn.Foreground = new SolidColorBrush(UiKit.TextSecondary);
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
                    miExe.Click += (_, _) => AddProgram(commandOwner, requestRebuild);
                    miFolder.Click += (_, _) => AddFolder(commandOwner, requestRebuild);
                    miUrl.Click += (_, _) => AddUrl(btn, commandOwner, requestRebuild);
                    menu.Items.Add(miExe);
                    menu.Items.Add(miFolder);
                    menu.Items.Add(miUrl);
                    menu.PlacementTarget = btn;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.IsOpen = true;
                }
                else
                {
                    if (commandOwner != null)
                        _ = commandOwner.ExecuteCommandAsync(AppCommandId.LauncherLaunch,
                            CommandSource.Launcher, new LauncherLaunchPayload(index));
                    else LaunchAt(index);
                }
            };

            btn.MouseRightButtonUp += (s, e) =>
            {
                if (!string.IsNullOrEmpty(path))
                {
                    if (commandOwner != null)
                        _ = commandOwner.ExecuteCommandAsync(AppCommandId.LauncherRemove,
                            CommandSource.Launcher, new LauncherRemovePayload(index));
                    else RemoveAt(index, requestRebuild);
                    e.Handled = true;
                }
            };

            btn.DragEnter += (s, e) =>
            {
                if (LauncherBar.HasDropData(e.Data))
                {
                    e.Effects = DragDropEffects.Copy;
                    btn.BorderBrush = new SolidColorBrush(UiKit.AccentColor);
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
                btn.BorderBrush = new SolidColorBrush(UiKit.BorderColor);
                btn.BorderThickness = new Thickness(1);
                e.Handled = true;
            };
            btn.Drop += (s, e) =>
            {
                btn.BorderBrush = new SolidColorBrush(UiKit.BorderColor);
                btn.BorderThickness = new Thickness(1);
                var dropped = ExtractDroppedItems(e.Data);
                if (dropped.Count > 0)
                {
                    if (commandOwner != null)
                        _ = commandOwner.ExecuteCommandAsync(AppCommandId.LauncherAdd,
                            CommandSource.Launcher, new LauncherAddPayload(dropped));
                    else AddDropped(dropped, btn, requestRebuild);
                }
                e.Handled = true;
            };
            return btn;
        }

        // 抓取网站 favicon（先试 /favicon.ico，再解析 HTML <link rel=icon>），缓存到 %TEMP%\OneBoxFavicons
        static async System.Threading.Tasks.Task FetchFaviconAsync(string url, int slotIndex, Button btn)
        {
            try
            {
                var uri = new Uri(url);
                string domain = uri.Host;
                AppLog.Log("Favicon", "fetch " + url);
                string cacheDir = Path.Combine(Path.GetTempPath(), "OneBoxFavicons");
                Directory.CreateDirectory(cacheDir);
                string cacheFile = Path.Combine(cacheDir, domain.Replace(':', '_') + ".ico");

                if (!File.Exists(cacheFile))
                {
                    using var cancellation = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
                    byte[] bytes = null;

                    // 1) 先试标准 /favicon.ico
                    try { bytes = await DownloadBytesAsync(new Uri($"{uri.Scheme}://{domain}/favicon.ico"), cancellation.Token); }
                    catch (Exception ex) { AppLog.Log("Favicon direct", ex); }

                    // 2) 再解析 HTML <link rel="icon" href="...">
                    if (bytes == null || bytes.Length < 100)
                    {
                        try
                        {
                            string html = await DownloadTextAsync(new Uri($"{uri.Scheme}://{domain}/"), cancellation.Token);
                            var match = System.Text.RegularExpressions.Regex.Match(html,
                                @"<link[^>]+rel=[""'](?:shortcut\s+)?icon[""'][^>]+href=[""']([^""']+)",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (match.Success)
                            {
                                var href = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
                                var iconUri = new Uri(new Uri($"{uri.Scheme}://{domain}/"), href);
                                bytes = await DownloadBytesAsync(iconUri, cancellation.Token);
                            }
                        }
                        catch (Exception ex) { AppLog.Log("Favicon html", ex); }
                    }

                    AppLog.Log("Favicon", "got " + domain + ": " + (bytes?.Length ?? 0) + " bytes");
                    if (bytes != null && bytes.Length >= 100)
                    {
                        File.WriteAllBytes(cacheFile, bytes);
                        AppLog.Log("Favicon", "saved " + domain + ": " + bytes.Length + " bytes");
                    }
                    else return;
                }

                // 回到 UI 线程加载缓存图标
                _ = btn.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (!LauncherPolicy.IsCurrentSlot(LoadLauncherPaths(), slotIndex, url)) return;
                        if (!string.Equals(btn.Tag as string, url, StringComparison.Ordinal)) return;
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        using (var stream = new FileStream(cacheFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            bmp.StreamSource = stream;
                            bmp.DecodePixelWidth = 24;
                            bmp.EndInit();
                        }
                        bmp.Freeze();
                        btn.Content = new Image { Source = bmp, Width = 24, Height = 24 };
                    }
                    catch (Exception ex) { AppLog.Log("Favicon render", ex); }
                }));
            }
            catch (Exception ex) { AppLog.Log("Favicon", ex); }
        }

        public static CommandResult AddPaths(IReadOnlyList<string> candidates, Action requestRebuild)
        {
            if (candidates == null || candidates.Count == 0)
                return CommandResult.Fail(CommandErrorCode.InvalidPayload, "没有可添加的快捷启动项。");
            var resolved = new List<string>();
            foreach (var candidate in candidates)
                if (!string.IsNullOrWhiteSpace(candidate)) resolved.Add(ResolveShortcut(candidate.Trim()));
            var capacity = LauncherPolicy.AddWithinLimit(LoadLauncherPaths(), resolved);
            if (capacity.Added > 0)
            {
                SaveLauncherPaths(capacity.Paths);
                requestRebuild?.Invoke();
            }
            if (capacity.Rejected > 0)
                return CommandResult.Fail(CommandErrorCode.Rejected,
                    $"快捷启动最多 8 项，有 {capacity.Rejected} 项未添加。", capacity);
            return capacity.Added > 0
                ? CommandResult.Ok(capacity)
                : CommandResult.Fail(CommandErrorCode.Rejected, "快捷启动项未添加。", capacity);
        }

        public static CommandResult RemoveAt(int index, Action requestRebuild)
        {
            var paths = LoadLauncherPaths();
            if (index < 0 || index >= paths.Count)
                return CommandResult.Fail(CommandErrorCode.InvalidPayload, "快捷启动位置无效。");
            paths.RemoveAt(index);
            SaveLauncherPaths(paths);
            requestRebuild?.Invoke();
            return CommandResult.Ok();
        }

        public static CommandResult LaunchAt(int index)
        {
            var paths = LoadLauncherPaths();
            if (index < 0 || index >= paths.Count)
                return CommandResult.Fail(CommandErrorCode.InvalidPayload, "快捷启动位置无效。");
            Process.Start(new ProcessStartInfo(paths[index]) { UseShellExecute = true });
            return CommandResult.Ok();
        }

        static async System.Threading.Tasks.Task<byte[]> DownloadBytesAsync(Uri uri, System.Threading.CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("OneBox/" + ApplicationVersion.Value);
            using var response = await OneBoxHttp.Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await OneBoxHttp.ReadBoundedBytesAsync(response.Content,
                LauncherPolicy.MaxFaviconBytes, cancellationToken).ConfigureAwait(false);
        }

        static async System.Threading.Tasks.Task<string> DownloadTextAsync(Uri uri, System.Threading.CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("OneBox/" + ApplicationVersion.Value);
            using var response = await OneBoxHttp.Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await OneBoxHttp.ReadBoundedTextAsync(response.Content,
                LauncherPolicy.MaxFaviconBytes, cancellationToken).ConfigureAwait(false);
        }

        // 解析 .lnk 快捷方式到目标路径；非快捷方式或解析失败回退原路径。
        // 使用反射后期绑定（避免添加 dynamic / Microsoft.CSharp 依赖）
        public static bool HasDropData(IDataObject data)
        {
            return data != null && (data.GetDataPresent(DataFormats.FileDrop)
                || data.GetDataPresent("UniformResourceLocator")
                || data.GetDataPresent("text/x-moz-url")
                || data.GetDataPresent(DataFormats.Text)
                || data.GetDataPresent(DataFormats.StringFormat));
        }

        public static string ExtractDropped(DragEventArgs e)
        {
            var items = e == null ? new List<string>() : ExtractDroppedItems(e.Data);
            return items.Count == 0 ? null : items[0];
        }

        public static List<string> ExtractDroppedItems(IDataObject data)
        {
            var result = new List<string>();
            if (data == null) return result;
            try
            {
                if (data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null)
                        foreach (string file in files)
                            if (!string.IsNullOrWhiteSpace(file)) result.Add(file);
                    return result;
                }
                // 浏览器拖 URL 的几种格式
                string[] urlFormats = { "UniformResourceLocator", "text/x-moz-url", DataFormats.Text, DataFormats.StringFormat };
                foreach (var fmt in urlFormats)
                {
                    if (!data.GetDataPresent(fmt)) continue;
                    var value = data.GetData(fmt);
                    string s = null;
                    if (value is string str) s = str;
                    else if (value is Stream st)
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
                    {
                        result.Add(line);
                        return result;
                    }
                }
            }
            catch (Exception ex) { AppLog.Log("Launcher drop", ex); }
            return result;
        }

        public static bool AddDropped(string dropped, Action requestRebuild)
        {
            if (string.IsNullOrWhiteSpace(dropped)) return false;
            dropped = dropped.Trim();
            string resolved = ResolveShortcut(dropped);
            var paths = LoadLauncherPaths();
            var result = LauncherPolicy.AddWithinLimit(paths, new[] { resolved });
            if (result.Added == 0) return false;
            SaveLauncherPaths(result.Paths);
            requestRebuild?.Invoke();
            return true;
        }

        static void AddDropped(IEnumerable<string> dropped, Button owner, Action requestRebuild)
        {
            var resolved = new List<string>();
            foreach (string item in dropped)
                if (!string.IsNullOrWhiteSpace(item)) resolved.Add(ResolveShortcut(item.Trim()));
            var result = LauncherPolicy.AddWithinLimit(LoadLauncherPaths(), resolved);
            if (result.Added > 0)
            {
                SaveLauncherPaths(result.Paths);
                requestRebuild?.Invoke();
            }
            if (result.Rejected > 0) ShowCapacityMessage(owner, result.Rejected);
        }

        static void AddProgram(MainWindow commandOwner, Action requestRebuild)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "程序|*.exe;*.lnk|所有文件|*.*", Title = "选择要添加的程序" };
            if (dlg.ShowDialog() == true)
            {
                if (commandOwner != null)
                    _ = commandOwner.ExecuteCommandAsync(AppCommandId.LauncherAdd, CommandSource.Launcher,
                        new LauncherAddPayload(new[] { dlg.FileName }));
                else AddToExisting(LoadLauncherPaths(), ResolveShortcut(dlg.FileName), null, requestRebuild);
            }
        }

        static void AddFolder(MainWindow commandOwner, Action requestRebuild)
        {
            try
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "选择要添加的文件夹", ShowNewFolderButton = false };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
                {
                    if (commandOwner != null)
                        _ = commandOwner.ExecuteCommandAsync(AppCommandId.LauncherAdd, CommandSource.Launcher,
                            new LauncherAddPayload(new[] { dlg.SelectedPath }));
                    else AddToExisting(LoadLauncherPaths(), dlg.SelectedPath, null, requestRebuild);
                }
            }
            catch (Exception ex) { AppLog.Log("AddFolder", ex); }
        }

        static void AddUrl(Button btn, MainWindow commandOwner, Action requestRebuild)
        {
            string url = PromptUrl(btn);
            if (!string.IsNullOrEmpty(url))
            {
                url = url.Trim();
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    url = "https://" + url;
                if (commandOwner != null)
                    _ = commandOwner.ExecuteCommandAsync(AppCommandId.LauncherAdd, CommandSource.Launcher,
                        new LauncherAddPayload(new[] { url }));
                else AddToExisting(LoadLauncherPaths(), url, btn, requestRebuild);
            }
        }

        static void AddToExisting(List<string> paths, string candidate, Button owner, Action requestRebuild)
        {
            var result = LauncherPolicy.AddWithinLimit(paths, new[] { candidate });
            if (result.Added > 0)
            {
                paths.Clear();
                paths.AddRange(result.Paths);
                SaveLauncherPaths(paths);
                requestRebuild?.Invoke();
            }
            if (result.Rejected > 0) ShowCapacityMessage(owner, result.Rejected);
        }

        static void ShowCapacityMessage(Button owner, int rejected)
        {
            string message = rejected == 1 ? "快捷启动栏最多只能添加 8 项。" : $"快捷启动栏最多只能添加 8 项，已有 {rejected} 项未添加。";
            var window = owner == null ? Application.Current?.MainWindow : Window.GetWindow(owner);
            if (window != null)
                MessageBox.Show(window, message, "快捷启动栏", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(message, "快捷启动栏", MessageBoxButton.OK, MessageBoxImage.Information);
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

