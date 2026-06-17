using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
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
    // compared as Version objects (e.g. "v1.2.0" -> 1.2.0). The update flow opens
    // the release page in the browser so the user downloads the new build manually —
    // no in-place replace, which is risky for a running single-exe app.
    public static class UpdateChecker
    {
        // === Configure these for your repo ===
        public const string Owner = "YOUR_GITHUB_USERNAME";
        public const string Repo = "OneBox";
        // Bump this when you cut a new release. Must match the GitHub release tag
        // (e.g. tag "v1.2.0" -> CurrentVersion 1.2.0).
        public static readonly Version CurrentVersion = new Version(1, 0, 0);

        const string ApiUrl = "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";
        const string ReleasesPage = "https://github.com/" + Owner + "/" + Repo + "/releases/latest";

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
                        if (manual) MessageBox.Show(owner, "检查更新失败：" + err.Message + "\n\n你可以直接访问：" + ReleasesPage,
                            "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (info == null)
                    {
                        if (manual) MessageBox.Show(owner, "没有找到任何发布版本。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    bool newer = info.Version != null && info.Version > CurrentVersion;
                    if (newer)
                        ShowUpdateDialog(owner, info);
                    else if (manual)
                        MessageBox.Show(owner, "当前版本 " + CurrentVersion + " 已是最新。", "检查更新",
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
            var ser = new JavaScriptSerializer();
            var dict = ser.DeserializeObject(json) as System.Collections.Generic.Dictionary<string, object>;
            if (dict == null) return null;
            var info = new ReleaseInfo();
            info.TagName = dict.ContainsKey("tag_name") ? dict["tag_name"] as string : null;
            info.Name = dict.ContainsKey("name") ? dict["name"] as string : null;
            info.Body = dict.ContainsKey("body") ? dict["body"] as string : null;
            info.HtmlUrl = dict.ContainsKey("html_url") ? dict["html_url"] as string : ReleasesPage;
            // Tag looks like "v1.2.0" or "1.2.0" — strip leading non-digits.
            if (!string.IsNullOrEmpty(info.TagName))
            {
                var m = Regex.Match(info.TagName, @"(\d+(\.\d+){0,2})");
                if (m.Success)
                {
                    Version v;
                    if (Version.TryParse(m.Groups[1].Value, out v)) info.Version = v;
                }
            }
            return info;
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
                Text = "当前版本 " + CurrentVersion + "，建议更新。",
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
                Height = 180,
                Margin = new Thickness(0, 0, 0, 12),
                Background = new SolidColorBrush(Color.FromRgb(24, 22, 36)),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 218, 245)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120))
            };
            stack.Children.Add(notes);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var download = new Button { Content = "前往下载", Width = 88, Height = 28, FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
            var later = new Button { Content = "以后再说", Width = 88, Height = 28, FontSize = 12 };
            btns.Children.Add(download); btns.Children.Add(later);
            stack.Children.Add(btns);

            dlg.Content = stack;
            download.Click += (s, e) => { try { System.Diagnostics.Process.Start(info.HtmlUrl); } catch { } };
            later.Click += (s, e) => dlg.Close();
            dlg.ShowDialog();
        }
    }
}
