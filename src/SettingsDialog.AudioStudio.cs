using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerAudioManager.AudioStudio;
using PowerAudioManager.Commands;

namespace PowerAudioManager;

internal static partial class SettingsDialog
{
    static FrameworkElement BuildAudioStudioTab(Window owner, Window dlg, SolidColorBrush fg)
    {
        var studio = StudioController.Instance;
        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock { Text = "麦克风工作室", Foreground = Brushes.White,
            FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(new TextBlock { Text = "设备、降噪、音乐和监听在工作室窗口调整。这里管理启动行为和全局快捷键。",
            Foreground = fg, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 14) });
        var open = new Button { Content = "打开麦克风工作室", Height = 30, Margin = new Thickness(0, 0, 0, 16) };
        AppResources.StyleDialogButton(open, false);
        open.Click += (_, _) => { if (owner is MainWindow main) studio.Show(main); };
        stack.Children.Add(open);

        var auto = new CheckBox { Content = "OneBox 启动后自动共享", Foreground = Brushes.White,
            IsChecked = studio.Settings.AutoStartAudio, Margin = new Thickness(0, 4, 0, 10) };
        var updates = new CheckBox { Content = "空闲时自动更新 OneBox", Foreground = Brushes.White,
            IsChecked = studio.Settings.AutoUpdate, Margin = new Thickness(0, 4, 0, 14) };
        stack.Children.Add(auto); stack.Children.Add(updates);
        var languageRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        languageRow.Children.Add(new TextBlock { Text = "工作室语言", Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var language = new ComboBox { Width = 120, ItemsSource = new[] { "中文", "English" },
            SelectedItem = studio.Settings.Language == "en" ? "English" : "中文" };
        AppResources.StyleDarkComboBox(language);
        languageRow.Children.Add(language); stack.Children.Add(languageRow);
        stack.Children.Add(new TextBlock { Text = "全局快捷键", Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 9) });
        var keys = new[] { "Denoise", "Eq", "Music", "Explode", "Monitor" };
        var names = new[] { "降噪", "EQ", "BGM", "炸麦", "监听" };
        var rows = new HotkeyRow[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            string preference = "AudioStudio.Hotkey." + keys[i];
            stack.Children.Add(new TextBlock { Text = names[i], Foreground = fg, FontSize = 12,
                Margin = new Thickness(0, 5, 0, 3) });
            rows[i] = MakeHotkeyRow(owner, dlg, AppPrefs.GetInt(preference, 0), fg);
            stack.Children.Add(rows[i].Row);
        }
        var actions = MakeButtons();
        ((Button)actions.Children[0]).Click += (_, _) =>
        {
            try
            {
                var values = rows.Select(x => x.Value).Where(x => x != 0).ToArray();
                if (values.Distinct().Count() != values.Length)
                    throw new InvalidOperationException("麦克风工作室的快捷键不能重复。");
                for (int i = 0; i < keys.Length; i++)
                {
                    string preference = "AudioStudio.Hotkey." + keys[i];
                    if (rows[i].Value != 0 && rows[i].Value != AppPrefs.GetInt(preference, 0) &&
                        HotkeyDefinitions.All.Any(x => x.PreferenceKey != preference && HotkeyDefinitions.ResolveEncoded(x) == rows[i].Value))
                        throw new InvalidOperationException(names[i] + " 快捷键已被占用。");
                }
                var settings = studio.Settings.Copy();
                settings.AutoStartAudio = auto.IsChecked == true; settings.AutoUpdate = updates.IsChecked == true;
                settings.Language = (string)language.SelectedItem == "English" ? "en" : "zh";
                studio.Apply(settings);
                for (int i = 0; i < keys.Length; i++)
                    if (!AppPrefs.SetInt("AudioStudio.Hotkey." + keys[i], rows[i].Value))
                        throw new InvalidOperationException("保存快捷键失败。");
                (owner as MainWindow)?.RefreshHotkeys();
                dlg.DialogResult = true; dlg.Close();
            }
            catch (Exception ex) { MessageBox.Show(dlg, ex.Message, "OneBox 设置", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        ((Button)actions.Children[1]).Click += (_, _) => { dlg.DialogResult = false; dlg.Close(); };
        stack.Children.Add(actions);
        return Scroll(stack);
    }
}
