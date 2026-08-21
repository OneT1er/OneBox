using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerAudioManager.Commands;

namespace PowerAudioManager
{
    internal static partial class SettingsDialog
    {
        static ScrollViewer BuildClipboardTab(Window owner, Window dlg, SolidColorBrush fg)
        {
            var stack = new StackPanel { Margin = new Thickness(20) };

            stack.Children.Add(new TextBlock { Text = "剪贴板历史快捷键", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
            var clipHk = MakeHotkeyRow(owner, dlg, AppPrefs.GetInt("Clipboard.Hotkey", 0), fg);
            stack.Children.Add(clipHk.Row);
            stack.Children.Add(new TextBlock { Text = "按下快捷键从鼠标位置弹出剪贴板历史。左键复制，右键删除单条。", Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 0) });

            var btns = MakeButtons();
            ((Button)btns.Children[0]).Click += async (s, e) =>
            {
                if (!TryPersist(dlg, () => AppPrefs.Set(PreferenceKeys.Hotkeys.Clipboard, clipHk.Value))) return;
                if (owner is MainWindow mw)
                {
                    var result = await mw.ExecuteCommandAsync(AppCommandId.RuntimeRefreshHotkeys, CommandSource.Settings);
                    if (!result.Success) return;
                }
                dlg.DialogResult = true; dlg.Close();
            };
            ((Button)btns.Children[1]).Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            stack.Children.Add(btns);

            return Scroll(stack);
        }
    }
}

