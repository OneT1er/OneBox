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
        static ScrollViewer BuildModulesTab(Window owner, Window dlg, SolidColorBrush fg)
        {
            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(new TextBlock { Text = "悬浮窗板块", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });

            var cbPower = MakeCb("电源计划", "Power");
            var cbAudio = MakeCb("音频输出", "Audio");
            var cbMem = MakeCb("内存清理", "Mem");
            var cbTr = MakeCb("翻译", "Translate");
            var cbLaunch = MakeCb("快捷启动", "Launcher");
            var cbClip = MakeCb("剪贴板历史", "Clipboard");
            var cbGallery = MakeCb("截图文件夹", "Gallery");
            var cbTemp = MakeCb("性能趋势", "Temp");
            stack.Children.Add(cbPower);
            stack.Children.Add(cbAudio);
            stack.Children.Add(cbMem);
            stack.Children.Add(cbTr);
            stack.Children.Add(cbLaunch);
            stack.Children.Add(cbClip);
            stack.Children.Add(cbGallery);
            stack.Children.Add(cbTemp);

            // 音频循环切换快捷键（SoundSwitch 式）：按一下切到下一个可见输出设备并弹提示
            stack.Children.Add(new TextBlock { Text = "音频循环切换快捷键", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 16, 0, 6) });
            var cycleHk = MakeHotkeyRow(owner, dlg, AppPrefs.GetInt("Audio.CycleHotkey", 0), fg);
            stack.Children.Add(cycleHk.Row);

            // 电源计划循环切换快捷键：按一下切到下一个电源计划并弹提示
            stack.Children.Add(new TextBlock { Text = "电源循环切换快捷键", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 16, 0, 6) });
            var pwHk = MakeHotkeyRow(owner, dlg, AppPrefs.GetInt("Power.CycleHotkey", 0), fg);
            stack.Children.Add(pwHk.Row);

            var btns = MakeButtons();
            ((Button)btns.Children[0]).Click += async (s, e) =>
            {
                if (!TryPersist(dlg,
                    () => AppPrefs.Set(PreferenceKeys.Modules.Power, cbPower.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Modules.Audio, cbAudio.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Modules.Memory, cbMem.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Modules.Translate, cbTr.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Modules.Launcher, cbLaunch.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Modules.Clipboard, cbClip.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Modules.Gallery, cbGallery.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Modules.Monitor, cbTemp.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Hotkeys.AudioCycle, cycleHk.Value),
                    () => AppPrefs.Set(PreferenceKeys.Hotkeys.PowerCycle, pwHk.Value))) return;
                if (owner is MainWindow mw)
                {
                    var result = await mw.ExecuteCommandAsync(AppCommandId.RuntimeRebuildModules, CommandSource.Settings);
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

