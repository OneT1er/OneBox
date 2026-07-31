using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerAudioManager
{
    internal static partial class SettingsDialog
    {
        static ScrollViewer BuildModulesTab(Window owner, Window dlg, SolidColorBrush fg)
        {
            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(new TextBlock { Text = "勾选要在悬浮窗中显示的板块：", Foreground = Brushes.White, FontSize = 13, Margin = new Thickness(0, 0, 0, 12) });

            var cbPower = MakeCb("电源计划", "Power");
            var cbAudio = MakeCb("音频控制", "Audio");
            var cbMem = MakeCb("内存清理", "Mem");
            var cbTr = MakeCb("翻译", "Translate");
            var cbLaunch = MakeCb("快捷启动栏", "Launcher");
            var cbClip = MakeCb("剪贴板历史", "Clipboard");
            var cbGallery = MakeCb("截图图库", "Gallery");
            var cbTemp = MakeCb("温度监控", "Temp");
            stack.Children.Add(cbPower);
            stack.Children.Add(cbAudio);
            stack.Children.Add(cbMem);
            stack.Children.Add(cbTr);
            stack.Children.Add(cbLaunch);
            stack.Children.Add(cbClip);
            stack.Children.Add(cbGallery);
            stack.Children.Add(cbTemp);
            stack.Children.Add(new TextBlock { Text = "隐藏后悬浮窗立即刷新；托盘菜单与全局快捷键不受影响。", Foreground = fg, FontSize = 10, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap });

            // 音频循环切换快捷键（SoundSwitch 式）：按一下切到下一个可见输出设备并弹提示
            stack.Children.Add(new TextBlock { Text = "音频循环切换快捷键", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 16, 0, 6) });
            var cycleHk = MakeHotkeyRow(owner, dlg, AppPrefs.GetInt("Audio.CycleHotkey", 0), fg);
            stack.Children.Add(cycleHk.Row);
            stack.Children.Add(new TextBlock { Text = "按下快捷键在可见输出设备间循环切换（跳过已隐藏的），右下角弹提示。", Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap });

            // 电源计划循环切换快捷键：按一下切到下一个电源计划并弹提示
            stack.Children.Add(new TextBlock { Text = "电源循环切换快捷键", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 16, 0, 6) });
            var pwHk = MakeHotkeyRow(owner, dlg, AppPrefs.GetInt("Power.CycleHotkey", 0), fg);
            stack.Children.Add(pwHk.Row);
            stack.Children.Add(new TextBlock { Text = "按下快捷键在所有电源计划间循环切换，右下角弹提示。", Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap });

            var btns = MakeButtons();
            ((Button)btns.Children[0]).Click += (s, e) =>
            {
                AppPrefs.SetBool("UI.ShowPower", cbPower.IsChecked == true);
                AppPrefs.SetBool("UI.ShowAudio", cbAudio.IsChecked == true);
                AppPrefs.SetBool("UI.ShowMem", cbMem.IsChecked == true);
                AppPrefs.SetBool("UI.ShowTranslate", cbTr.IsChecked == true);
                AppPrefs.SetBool("UI.ShowLauncher", cbLaunch.IsChecked == true);
                AppPrefs.SetBool("UI.ShowClipboard", cbClip.IsChecked == true);
                AppPrefs.SetBool("UI.ShowGallery", cbGallery.IsChecked == true);
                AppPrefs.SetBool("UI.ShowTemp", cbTemp.IsChecked == true);
                AppPrefs.SetInt("Audio.CycleHotkey", cycleHk.Value);
                AppPrefs.SetInt("Power.CycleHotkey", pwHk.Value);
                if (owner is MainWindow) ((MainWindow)owner).RefreshHotkeys();
                if (owner is MainWindow) ((MainWindow)owner).RebuildUI();
                dlg.DialogResult = true; dlg.Close();
            };
            ((Button)btns.Children[1]).Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            stack.Children.Add(btns);

            return Scroll(stack);
        }
    }
}

