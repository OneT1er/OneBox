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
        static FrameworkElement BuildAboutTab(Window dlg, SolidColorBrush fg)
        {
            var stack = new StackPanel { Margin = new Thickness(20) };
            var heading = new StackPanel { Orientation = Orientation.Horizontal };
            heading.Children.Add(IconCatalog.CreateElement(IconKey.Brand, 32, UiKit.FrozenBrush(ThemeTokens.Accent)));
            var name = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            name.Children.Add(new TextBlock { Text = "OneBox", Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.SemiBold });
            name.Children.Add(new TextBlock { Text = "版本 " + UpdateChecker.CurrentVersion, Foreground = fg, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
            heading.Children.Add(name); stack.Children.Add(heading);
            stack.Children.Add(new TextBlock { Text = "Windows 桌面悬浮工具箱", Foreground = fg, FontSize = 11, Margin = new Thickness(0, 12, 0, 8) });
            const string repository = "https://github.com/OneT1er/OneBox";
            var link = new Hyperlink(new Run(repository)) { NavigateUri = new Uri(repository), Foreground = UiKit.FrozenBrush(ThemeTokens.Accent) };
            link.RequestNavigate += (_, e) =>
            {
                try { Process.Start(new ProcessStartInfo(repository) { UseShellExecute = true }); }
                catch (Exception ex) { AppLog.Log("About.GitHub", ex); MessageBox.Show(dlg, "无法打开浏览器，请通过 GitHub 地址访问。", "OneBox"); }
                e.Handled = true;
            };
            var linkText = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
            linkText.Inlines.Add(link); stack.Children.Add(linkText);
            stack.Children.Add(new TextBlock { Text = "更新记录", Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
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
                    stack.Children.Add(new TextBlock {
                        Text = text, TextWrapping = TextWrapping.Wrap,
                        Foreground = version ? Brushes.White : fg, FontSize = version ? 12 : 11,
                        FontWeight = version || section ? FontWeights.SemiBold : FontWeights.Normal,
                        Margin = new Thickness(0, version ? 14 : section ? 8 : 3, 0, 3)
                    });
                }
            }
            catch (Exception ex)
            {
                AppLog.Log("About.Changelog", ex);
                stack.Children.Add(new TextBlock { Text = "更新记录暂不可用，可通过 GitHub 查看。", Foreground = fg, TextWrapping = TextWrapping.Wrap });
            }
            var actions = new StackPanel { Tag = "SettingsActions", Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var close = new Button { Content = "关闭", Width = 72, Height = 28, FontSize = 12 };
            AppResources.StyleDialogButton(close, false);
            close.Click += (_, _) => dlg.Close();
            actions.Children.Add(close); stack.Children.Add(actions);
            return Scroll(stack);
        }
    }
}
