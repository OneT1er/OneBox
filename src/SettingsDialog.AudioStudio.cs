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
        var stack = NewPage("OneMic");

        var auto = Switch("OneBox 启动后自动共享", studio.Settings.AutoStartAudio);
        stack.Children.Add(UiKit.SettingsGroup(null,
            UiKit.SettingsRow("OneBox 启动后自动共享", auto, "悬浮窗的 OneMic 图标可打开音频工作台")));

        var keys = new[] { "Denoise", "Eq", "Music", "Explode", "Monitor" };
        var names = new[] { "降噪", "EQ", "BGM", "炸麦", "监听" };
        var labels = new[] { "切换降噪", "切换均衡器", "切换 BGM", "炸麦音效", "切换监听" };
        var rows = new HotkeyRow[keys.Length];
        var keyRows = new UIElement[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            string preference = "AudioStudio.Hotkey." + keys[i];
            rows[i] = MakeHotkeyRow(owner, dlg, AppPrefs.GetInt(preference, 0), fg);
            keyRows[i] = HotkeySettingsRow(labels[i], rows[i]);
        }
        stack.Children.Add(UiKit.SettingsGroup("全局快捷键", keyRows));
        stack.Children.Add(Hint("音效板每条音效的快捷键在 OneMic 窗口里设置；Ctrl+Shift+0 停止音效。", -8, 14));
        var actions = MakeButtons();
        ((Button)actions.Children[0]).Click += (_, _) =>
        {
            try
            {
                var values = rows.Select(x => x.Value).Where(x => x != 0).ToArray();
                if (values.Distinct().Count() != values.Length)
                    throw new InvalidOperationException("OneMic 的快捷键不能重复。");
                for (int i = 0; i < keys.Length; i++)
                {
                    string preference = "AudioStudio.Hotkey." + keys[i];
                    if (rows[i].Value != 0 && (rows[i].Value == StudioSoundpadHotkeys.StopEncoded ||
                        studio.Settings.SoundEffects.Any(x => x.Hotkey == rows[i].Value)))
                        throw new InvalidOperationException(names[i] + " 快捷键与音效板冲突。");
                    if (rows[i].Value != 0 && rows[i].Value != AppPrefs.GetInt(preference, 0) &&
                        HotkeyDefinitions.All.Any(x => x.PreferenceKey != preference && HotkeyDefinitions.ResolveEncoded(x) == rows[i].Value))
                        throw new InvalidOperationException(names[i] + " 快捷键已被占用。");
                }
                var settings = studio.Settings.Copy();
                settings.AutoStartAudio = auto.IsChecked == true;
                studio.Apply(settings);
                for (int i = 0; i < keys.Length; i++)
                    if (!AppPrefs.SetInt("AudioStudio.Hotkey." + keys[i], rows[i].Value))
                        throw new InvalidOperationException("保存快捷键失败。");
                (owner as MainWindow)?.RefreshHotkeys();
                dlg.DialogResult = true; dlg.Close();
            }
            catch (Exception ex) { OneBoxMessage.Error(dlg, "OneBox 设置", ex.Message); }
        };
        ((Button)actions.Children[1]).Click += (_, _) => { dlg.DialogResult = false; dlg.Close(); };
        stack.Children.Add(actions);
        return Scroll(stack);
    }
}
