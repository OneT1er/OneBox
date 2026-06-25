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
    // Checks GitHub Releases for a newer version. Startup does a silent background
    // check and only nags the user if a new version exists; the tray "检查更新..."
    // menu item does a manual check that always reports back (including "已是最新").
    //
    // Configure Owner/Repo below to match your GitHub repository. Version tags are
    // compared as Version objects (e.g. "v1.2.0" -> 1.2.0). When a release ships a
    // OneBox.exe asset, the update flow downloads it to a randomly-named temp file
    // and hands off to a temp batch that waits for this process to exit, overwrites
    // the locked exe, then relaunches — an in-place self-replace. Temp files use
    // Path.GetRandomFileName() rather than predictable names to avoid local
    // squatting / TOCTOU races.
    //
    // INTEGRITY: the download is fetched over HTTPS from GitHub but is NOT
    // independently verified (no signature/hash checked against a trusted source).
    // Trust therefore rests entirely on the GitHub repository itself — if the repo
    // or its release pipeline is compromised, a malicious exe would be installed
    // automatically. Hardening this (code-signing with an embedded public key) is
    // a known follow-up.
    public static class UpdateChecker
    {
        // === Configure these for your repo ===
        public const string Owner = "OneT1er";
        public const string Repo = "OneBox";
        // Bump this when you cut a new release. Must match the GitHub release tag
        // (e.g. tag "v1.2.0" -> CurrentVersion 1.2.0).
        public static readonly Version CurrentVersion = new Version(1, 3, 0);

        const string ApiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        const string ReleasesPage = $"https://github.com/{Owner}/{Repo}/releases/latest";

        // Fired from a worker thread; marshals to the UI thread before showing UI.
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
                    // Silent startup check: do nothing when up to date.
                }));
            });
        }

        class ReleaseInfo
        {
            public Version Version;
            public string TagName;
            public string Name;
            public string Body;      // release notes (markdown)
            public string HtmlUrl;
            public string ExeDownloadUrl; // direct URL to OneBox.exe asset, if present
        }

        static ReleaseInfo FetchLatest()
        {
            // GitHub requires TLS 1.2+. The legacy .NET Framework 4 runtime defaults
            // to SSL3/TLS1.0, which fails with "未能创建 SSL/TLS 安全通道". Force TLS 1.2.
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
            var req = (HttpWebRequest)WebRequest.Create(ApiUrl);
            req.Method = "GET";
            req.Timeout = 10000;
            req.ReadWriteTimeout = 10000;
            req.UserAgent = "OneBox-Updater"; // GitHub API requires a User-Agent
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
            // Find the OneBox.exe asset download URL among the release's assets.
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
            // Tag looks like "v1.2.0" or "1.2.0" — strip leading non-digits.
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
            // If we have a direct exe download URL, offer in-app update; otherwise fall
            // back to opening the release page in the browser.
            string updateLabel = string.IsNullOrEmpty(info.ExeDownloadUrl) ? "前往下载" : "立即更新";
            var download = new Button { Content = updateLabel, Width = 88, Height = 28, FontSize = 12, Margin = new Thickness(0, 0, 8, 0), IsEnabled = true };
            var later = new Button { Content = "以后再说", Width = 88, Height = 28, FontSize = 12 };
            btns.Children.Add(download); btns.Children.Add(later);
            stack.Children.Add(btns);

            dlg.Content = stack;
            bool updateFailed = false; // set true if download fails -> button opens browser instead
            download.Click += (s, e) =>
            {
                // After a failed download, the button becomes "前往下载" and opens the browser.
                if (updateFailed || string.IsNullOrEmpty(info.ExeDownloadUrl))
                {
                    try { System.Diagnostics.Process.Start(info.HtmlUrl); } catch { }
                    return;
                }
                // Disable buttons and run the download on a background thread.
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
                            updateFailed = true; // next click opens the browser
                            return;
                        }
                        progress.Text = "下载完成，即将安装并重启...";
                        // Hand off to the updater batch and exit.
                        try { LaunchUpdaterAndExit(tmpExe); }
                        catch (Exception ex2) { AppLog.Log("UpdateChecker install", ex2); progress.Text = $"安装失败：{ex2.Message}"; }
                    }));
                });
            };
            later.Click += (s, e) => dlg.Close();
            dlg.ShowDialog();
        }

        // Downloads the new exe to a temp file. progressCallback receives byte count
        // as it streams. Returns the temp file path.
        static string DownloadExe(string url, Action<long> progressCallback)
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            req.UserAgent = "OneBox-Updater";
            req.AllowAutoRedirect = true; // GitHub asset URLs redirect to S3
            // Random temp name (predictable names let a local user squat/swap the
            // file before we read it back).
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

        // Writes a batch file that: waits for the current OneBox process to exit,
        // copies the downloaded exe over the running one, then relaunches it.
        // Then starts that batch and shuts the app down so the copy can succeed
        // (a running exe is locked and cannot be overwritten).
        static void LaunchUpdaterAndExit(string downloadedExePath)
        {
            string currentExe = Environment.ProcessPath;
            string currentDir = Path.GetDirectoryName(currentExe);
            // Random batch name so a local attacker can't pre-place a malicious
            // OneBox_updater.bat in temp.
            string batPath = Path.Combine(Path.GetTempPath(), $"OneBox_{Path.GetRandomFileName()}.bat");
            // PID list of the current process AND any child processes that might
            // hold the exe open. We wait on the current PID at minimum.
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            // Wait until the current OneBox exits (it holds a lock on the exe).
            sb.AppendLine(":wait");
            sb.AppendLine($"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
            sb.AppendLine("if not errorlevel 1 (");
            sb.AppendLine("  timeout /t 1 /nobreak >nul");
            sb.AppendLine("  goto wait");
            sb.AppendLine(")");
            // Overwrite the exe. Retry a few times in case of a lingering lock.
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
            // Clean up the temp download.
            sb.AppendLine($"del /f /q \"{downloadedExePath}\" >nul 2>&1");
            // Relaunch the new version.
            sb.AppendLine($"start \"\" \"{currentExe}\"");
            // Self-delete this batch.
            sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
            File.WriteAllText(batPath, sb.ToString(), Encoding.GetEncoding(936)); // GBK so cmd renders the chcp path fine; ASCII-safe regardless

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = batPath,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            System.Diagnostics.Process.Start(psi);

            // Exit the app so the batch can replace the exe.
            System.Windows.Application.Current.Shutdown();
        }
    }
}
