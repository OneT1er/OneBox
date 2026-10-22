using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PowerAudioManager
{
    internal static partial class SettingsDialog
    {
        static FrameworkElement BuildAboutTab(Window owner, Window dlg, SolidColorBrush fg)
        {
            var stack = new StackPanel { Margin = new Thickness(8, 4, 16, 12) };
            var heading = new DockPanel { LastChildFill = true };
            var logo = IconCatalog.CreateElement(IconKey.Brand, 44, UiKit.FrozenBrush(ThemeTokens.Accent));
            logo.Margin = new Thickness(0, 0, 16, 0);
            DockPanel.SetDock(logo, Dock.Left);
            heading.Children.Add(logo);
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(UiKit.Text("OneBox", 18, UiKit.TextPrimary, FontWeights.SemiBold));
            name.Children.Add(UiKit.Text("版本 " + UpdateChecker.CurrentVersion + " · Windows 桌面悬浮工具箱", 12, UiKit.TextSecondary));
            heading.Children.Add(name);
            stack.Children.Add(UiKit.Card(heading, new Thickness(20)));

            const string repository = "https://github.com/OneT1er/OneBox";
            var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 14) };
            var updateBtn = UiKit.MakeButton("检查更新", ButtonVariant.Primary);
            updateBtn.Click += async (_, _) =>
            {
                if (owner is not MainWindow mw) return;
                updateBtn.IsEnabled = false;
                try { await mw.ExecuteCommandAsync(PowerAudioManager.Commands.AppCommandId.UpdateCheck, PowerAudioManager.Commands.CommandSource.Settings, new PowerAudioManager.Commands.UpdateCheckPayload(true)); }
                catch (Exception ex) { AppLog.Log("About.UpdateCheck", ex); }
                finally { updateBtn.IsEnabled = true; }
            };
            updateBtn.IsEnabled = owner is MainWindow;
            var githubBtn = UiKit.MakeButton("GitHub 主页", ButtonVariant.Secondary);
            githubBtn.Margin = new Thickness(8, 0, 0, 0);
            githubBtn.ToolTip = repository;
            githubBtn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(repository) { UseShellExecute = true }); }
                catch (Exception ex) { AppLog.Log("About.GitHub", ex); OneBoxMessage.Error(dlg, "OneBox", "无法打开浏览器，请通过 GitHub 地址访问。\n" + repository); }
            };
            links.Children.Add(updateBtn);
            links.Children.Add(githubBtn);
            stack.Children.Add(links);

            stack.Children.Add(UiKit.GroupCaption("更新记录"));
            var notes = new StackPanel();
            try
            {
                using var stream = typeof(SettingsDialog).Assembly.GetManifestResourceStream("PowerAudioManager.CHANGELOG.md");
                if (stream == null) throw new FileNotFoundException("Embedded changelog is missing.");
                using var reader = new StreamReader(stream);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("# ")) continue;
                    bool version = line.StartsWith("## ");
                    bool section = line.StartsWith("### ");
                    string text = line.TrimStart('#', ' ').Replace("**", "").Replace("`", "");
                    if (text.StartsWith("- ")) text = "• " + text.Substring(2);
                    notes.Children.Add(new TextBlock {
                        Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 18,
                        Foreground = UiKit.FrozenBrush(version ? UiKit.TextPrimary : UiKit.TextSecondary), FontSize = version ? 12 : 11,
                        FontWeight = version || section ? FontWeights.SemiBold : FontWeights.Normal,
                        Margin = new Thickness(0, version ? (notes.Children.Count == 0 ? 0 : 14) : section ? 8 : 3, 0, 3)
                    });
                }
            }
            catch (Exception ex)
            {
                AppLog.Log("About.Changelog", ex);
                notes.Children.Add(new TextBlock { Text = "更新记录暂不可用，可通过 GitHub 查看。", Foreground = fg, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            }
            stack.Children.Add(UiKit.Card(notes, new Thickness(14, 12, 14, 12)));
            var actions = new StackPanel { Tag = "SettingsActions", Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var close = new Button { Content = "关闭", Width = 80, Height = 32, FontSize = 12 };
            AppResources.StyleDialogButton(close, false);
            close.Click += (_, _) => dlg.Close();
            actions.Children.Add(close); stack.Children.Add(actions);
            return Scroll(stack);
        }
    }
}
