using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PowerAudioManager
{
    // 检查 GitHub Releases 有无新版本。启动时静默检查，仅发现新版本时才提醒；
    // 托盘"检查更新..."手动检查总会反馈（包括"已是最新"）。
    //
    // 版本号用 Version 对象比较（如 tag "v1.2.0" → 1.2.0）。发布附带 OneBox.exe 时，
    // 下载到随机名临时文件，通过批处理等待进程退出后覆盖锁定 exe 再重启 — 原地自替换。
    // 临时文件用 Path.GetRandomFileName() 防本地 squatting/TOCTOU 竞争。
    //
    // 完整性警告：下载走 HTTPS 但无独立签名/哈希校验，信任完全依赖 GitHub 仓库本身。
    // 若仓库或发布流水线被攻破，恶意 exe 将被自动安装。已知待办：代码签名 + 嵌入公钥校验。
    public static class UpdateChecker
    {
        public const string Owner = "OneT1er";
        public const string Repo = "OneBox";
        // 发版时升级此值，需与 GitHub release tag 一致。
        public static readonly Version CurrentVersion = new Version(1, 4, 3);

        const string ApiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        const string ReleasesPage = $"https://github.com/{Owner}/{Repo}/releases/latest";

        // 从工作线程调用，显示 UI 前切换到 UI 线程。
        public static void CheckAsync(Window owner, bool manual)
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                ReleaseInfo info = null;
                Exception err = null;
                try { info = FetchLatest(); }
                catch (Exception ex) { err = ex; }

                if (owner == null) return;
                owner.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (err != null)
                    {
                        AppLog.Log("UpdateChecker", err);
                        if (manual) MessageBox.Show(owner, $"检查更新失败：{err.Message}\n\n你可以直接访问：{ReleasesPage}",
                            "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (info == null)
                    {
                        if (manual) MessageBox.Show(owner, "没有找到任何发布版本。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    bool newer = info.Version != null && info.Version > CurrentVersion;
                    AppLog.Log("UpdateChecker", $"latest={(info.Version == null ? info.TagName : info.Version.ToString())} current={CurrentVersion} newer={newer}");
                    if (newer)
                        ShowUpdateDialog(owner, info);
                    else if (manual)
                        MessageBox.Show(owner, $"当前版本 {CurrentVersion} 已是最新。", "检查更新",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    // 静默启动检查：已是最新时不做任何操作。
                }));
            });
        }

        class ReleaseInfo
        {
            public Version Version;
            public string TagName;
            public string Name;
            public string Body;      // 发布说明 (markdown)
            public string HtmlUrl;
            public string ExeDownloadUrl; // OneBox.exe 资产直链（如有）
        }

        static ReleaseInfo FetchLatest()
        {
            // GitHub 要求 TLS 1.2+。旧 .NET Framework 4 默认 SSL3/TLS1.0 会报"未能创建 SSL/TLS 安全通道"。
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
            var req = (HttpWebRequest)WebRequest.Create(ApiUrl);
            req.Method = "GET";
            req.Timeout = 10000;
            req.ReadWriteTimeout = 10000;
            req.UserAgent = "OneBox-Updater"; // GitHub API 要求 User-Agent
            req.Accept = "application/json";
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                var json = sr.ReadToEnd();
                return ParseRelease(json);
            }
        }

        static ReleaseInfo ParseRelease(string json)
        {
            GitHubRelease rel;
            try { rel = JsonSerializer.Deserialize<GitHubRelease>(json, _jsonOpt); }
            catch { return null; }
            if (rel == null) return null;
            var info = new ReleaseInfo();
            info.TagName = rel.TagName;
            info.Name = rel.Name;
            info.Body = rel.Body;
            info.HtmlUrl = string.IsNullOrEmpty(rel.HtmlUrl) ? ReleasesPage : rel.HtmlUrl;
            // 在发布资产中查找 OneBox.exe 下载链接。
            if (rel.Assets != null)
            {
                foreach (var a in rel.Assets)
                {
                    if (a != null && string.Equals(a.Name, "onebox.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        info.ExeDownloadUrl = a.BrowserDownloadUrl;
                        break;
                    }
                }
            }
            // Tag 格式如 "v1.2.0" — 提取数字版本号。
            if (!string.IsNullOrEmpty(info.TagName))
            {
                var m = Regex.Match(info.TagName, @"(\d+(\.\d+){0,2})");
                if (m.Success)
                {
                    if (Version.TryParse(m.Groups[1].Value, out var v)) info.Version = v;
                }
            }
            return info;
        }

        static readonly JsonSerializerOptions _jsonOpt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        class GitHubRelease
        {
            [JsonPropertyName("tag_name")] public string TagName { get; set; }
            [JsonPropertyName("name")] public string Name { get; set; }
            [JsonPropertyName("body")] public string Body { get; set; }
            [JsonPropertyName("html_url")] public string HtmlUrl { get; set; }
            [JsonPropertyName("assets")] public System.Collections.Generic.List<GitHubAsset> Assets { get; set; }
        }

        class GitHubAsset
        {
            [JsonPropertyName("name")] public string Name { get; set; }
            [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; }
        }

        static void ShowUpdateDialog(Window owner, ReleaseInfo info)
        {
            var dlg = new Window
            {
                Title = "发现新版本",
                Width = 460, Height = 400,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(28, 26, 40)),
                FontFamily = owner.FontFamily
            };
            var stack = new StackPanel { Margin = new Thickness(20) };
            var fg = new SolidColorBrush(Color.FromRgb(190, 188, 220));

            var titleTb = new TextBlock
            {
                Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            titleTb.Inlines.Add(new Run("OneBox " + (info.Version != null ? info.Version.ToString() : info.TagName) + " 已发布"));
            stack.Children.Add(titleTb);

            stack.Children.Add(new TextBlock
            {
                Text = $"当前版本 {CurrentVersion}，建议更新。",
                Foreground = fg, FontSize = 12, Margin = new Thickness(0, 0, 0, 12)
            });

            stack.Children.Add(new TextBlock { Text = "更新内容：", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
            var notes = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Text = string.IsNullOrEmpty(info.Body) ? (info.Name ?? "(无说明)") : info.Body,
                FontSize = 11,
                Height = 150,
                Margin = new Thickness(0, 0, 0, 12),
                Background = new SolidColorBrush(Color.FromRgb(24, 22, 36)),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 218, 245)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120))
            };
            stack.Children.Add(notes);

            var progress = new TextBlock
            {
                Foreground = fg, FontSize = 11, Margin = new Thickness(0, 0, 0, 8),
                Text = string.IsNullOrEmpty(info.ExeDownloadUrl) ? "提示：此 Release 未附带 OneBox.exe，将打开网页手动下载。" : ""
            };
            stack.Children.Add(progress);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            // 有 exe 直链则提供应用内更新，否则回退到浏览器打开发布页。
            string updateLabel = string.IsNullOrEmpty(info.ExeDownloadUrl) ? "前往下载" : "立即更新";
            var download = new Button { Content = updateLabel, Width = 88, Height = 28, FontSize = 12, Margin = new Thickness(0, 0, 8, 0), IsEnabled = true };
            var later = new Button { Content = "以后再说", Width = 88, Height = 28, FontSize = 12 };
            btns.Children.Add(download); btns.Children.Add(later);
            stack.Children.Add(btns);

            dlg.Content = stack;
            bool updateFailed = false; // 下载失败后按钮改为浏览器打开
            download.Click += (s, e) =>
            {
                // 下载失败后按钮变为"前往下载"，点击打开浏览器。
                if (updateFailed || string.IsNullOrEmpty(info.ExeDownloadUrl))
                {
                    try { System.Diagnostics.Process.Start(info.HtmlUrl); } catch { }
                    return;
                }
                download.IsEnabled = false;
                later.IsEnabled = false;
                progress.Text = "正在下载...";
                ThreadPool.QueueUserWorkItem(state =>
                {
                    string tmpExe = null;
                    Exception err = null;
                    try { tmpExe = DownloadExe(info.ExeDownloadUrl, bytes => {
                        owner.Dispatcher.BeginInvoke(new Action(() => {
                            progress.Text = "正在下载... " + (bytes / 1024) + " KB";
                        }));
                    }); }
                    catch (Exception ex) { err = ex; }

                    owner.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (err != null)
                        {
                            AppLog.Log("UpdateChecker download", err);
                            progress.Text = $"下载失败：{err.Message}。可点“前往下载”用浏览器下载。";
                            download.Content = "前往下载";
                            download.IsEnabled = true;
                            later.IsEnabled = true;
                            updateFailed = true; // 下次点击打开浏览器
                            return;
                        }
                        progress.Text = "下载完成，即将安装并重启...";
                        // 移交更新批处理并退出。
                        try { LaunchUpdaterAndExit(tmpExe); }
                        catch (Exception ex2) { AppLog.Log("UpdateChecker install", ex2); progress.Text = $"安装失败：{ex2.Message}"; }
                    }));
                });
            };
            later.Click += (s, e) => dlg.Close();
            dlg.ShowDialog();
        }

        // 下载新 exe 到临时文件，progressCallback 回调已下载字节数。
        static string DownloadExe(string url, Action<long> progressCallback)
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            req.UserAgent = "OneBox-Updater";
            req.AllowAutoRedirect = true; // GitHub 资产链接重定向到 S3
            // 随机临时文件名，防止本地用户 squat/swap（预测名字可在读取前交换文件）。
            string tmp = Path.Combine(Path.GetTempPath(), $"OneBox_{Path.GetRandomFileName()}.exe");
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var rs = resp.GetResponseStream())
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                var buf = new byte[8192];
                long total = 0;
                int n;
                while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                {
                    fs.Write(buf, 0, n);
                    total += n;
                    if (progressCallback != null) progressCallback(total);
                }
            }
            return tmp;
        }

        // 写批处理：等待当前进程退出 → 覆盖运行中 exe → 重新启动。然后关闭应用让覆盖生效。
        static void LaunchUpdaterAndExit(string downloadedExePath)
        {
            string currentExe = Environment.ProcessPath;
            string currentDir = Path.GetDirectoryName(currentExe);
            // 随机批处理名，防本地攻击者预置恶意 .bat。
            string batPath = Path.Combine(Path.GetTempPath(), $"OneBox_{Path.GetRandomFileName()}.bat");
            // 等待当前进程退出（exe 被其锁定）。
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine(":wait");
            sb.AppendLine($"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
            sb.AppendLine("if not errorlevel 1 (");
            sb.AppendLine("  timeout /t 1 /nobreak >nul");
            sb.AppendLine("  goto wait");
            sb.AppendLine(")");
            // 覆盖 exe，重试数次以防残留锁定。
            sb.AppendLine("set /a tries=0");
            sb.AppendLine(":copy");
            sb.AppendLine($"copy /Y \"{downloadedExePath}\" \"{currentExe}\" >nul 2>&1");
            sb.AppendLine("if errorlevel 1 (");
            sb.AppendLine("  set /a tries+=1");
            sb.AppendLine("  if %tries% LSS 10 (");
            sb.AppendLine("    timeout /t 1 /nobreak >nul");
            sb.AppendLine("    goto copy");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine($"del /f /q \"{downloadedExePath}\" >nul 2>&1");
            sb.AppendLine($"start \"\" \"{currentExe}\"");
            sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
            File.WriteAllText(batPath, sb.ToString(), Encoding.GetEncoding(936)); // GBK 确保 cmd 正确处理 chcp 路径

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = batPath,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            System.Diagnostics.Process.Start(psi);

            // 退出应用使批处理能替换 exe。
            System.Windows.Application.Current.Shutdown();
        }
    }
}
