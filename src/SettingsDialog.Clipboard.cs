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
        static FrameworkElement BuildClipboardTab(Window owner, Window dlg, SolidColorBrush fg)
        {
            var stack = NewPage("剪贴板");

            var clipHk = MakeHotkeyRow(owner, dlg, AppPrefs.GetInt("Clipboard.Hotkey", 0), fg);
            var clearHistory = UiKit.MakeButton("清空历史", ButtonVariant.Danger);
            clearHistory.Height = 30;
            clearHistory.Click += (_, _) =>
            {
                if (!OneBoxMessage.Confirm(dlg, "清空剪贴板历史？", "已保存的文本和图片记录会被删除，且无法恢复。", "清空", danger: true)) return;
                try { ClipboardHistory.Clear(); }
                catch (Exception ex) { AppLog.Log("Settings.ClipboardClear", ex); OneBoxMessage.Error(dlg, "OneBox 设置", "清空剪贴板历史失败。"); }
            };
            stack.Children.Add(UiKit.SettingsGroup(null,
                HotkeySettingsRow("打开剪贴板历史", clipHk),
                UiKit.SettingsRow("保留最近 20 条", clearHistory, "文本和图片都在本机加密保存")));

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

