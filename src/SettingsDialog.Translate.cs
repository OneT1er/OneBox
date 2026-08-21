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
        static ScrollViewer BuildTranslateTab(Window owner, Window dlg, SolidColorBrush fg)
        {
            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(new TextBlock { Text = "百度大模型翻译 API", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            stack.Children.Add(new TextBlock { Text = "在 fanyi-api.baidu.com 开通大模型翻译，控制台 → API Key 管理 创建 API Key。", Foreground = fg, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });

            stack.Children.Add(new TextBlock { Text = "API Key (必填)", Foreground = fg, FontSize = 12, Margin = new Thickness(0, 4, 0, 4) });
            var keyBox = MakeBox(); keyBox.Text = TranslateService.GetKey();
            stack.Children.Add(keyBox);

            stack.Children.Add(new TextBlock { Text = "APPID (可选, 用于 Sign 鉴权兼容)", Foreground = fg, FontSize = 12, Margin = new Thickness(0, 12, 0, 4) });
            var appIdBox = MakeBox(); appIdBox.Text = TranslateService.GetAppId();
            stack.Children.Add(appIdBox);

            stack.Children.Add(new TextBlock { Text = "翻译指令 (可选, 例: 采用意译 / 商务正式语气 / 保持术语原样)", Foreground = fg, FontSize = 12, Margin = new Thickness(0, 12, 0, 4) });
            var instBox = MakeBox(); instBox.Text = TranslateService.GetInstruction();
            stack.Children.Add(instBox);

            // ---- 图片翻译快捷键 ----
            stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(80, 75, 120)), Margin = new Thickness(0, 14, 0, 12), Opacity = 0.6 });
            stack.Children.Add(new TextBlock { Text = "图片翻译快捷键", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
            stack.Children.Add(new TextBlock { Text = "框选屏幕区域 → 自动翻译图中文字为贴合图。", Foreground = fg, FontSize = 11, Margin = new Thickness(0, 0, 0, 8) });

            var itHk = MakeHotkeyRow(owner, dlg, AppPrefs.GetInt("Screenshot.ImageTranslateHotkey", 0), fg, occupiedMessage: "该快捷键已被其他程序占用，OneBox 无法注册。");
            stack.Children.Add(itHk.Row);

            var btns = MakeButtons();
            ((Button)btns.Children[0]).Click += async (s, e) =>
            {
                if (!TranslateService.SetCreds(appIdBox.Text.Trim(), keyBox.Text.Trim(), instBox.Text))
                {
                    string error = TranslateService.GetCredentialError();
                    MessageBox.Show(dlg, string.IsNullOrEmpty(error) ? "翻译凭据保存失败。" : error,
                        "翻译设置", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                if (!TryPersist(dlg, () => AppPrefs.Set(PreferenceKeys.Hotkeys.ImageTranslate, itHk.Value))) return;
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

