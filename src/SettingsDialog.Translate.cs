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
        static FrameworkElement BuildTranslateTab(Window owner, Window dlg, SolidColorBrush fg)
        {
            var stack = NewPage("翻译");

            // ---- 百度大模型翻译 API ----
            // API Key 默认以密码框遮挡，眼睛按钮切换明文；保存时总是读取当前可见的输入框。
            var keyBox = MakeBox(); keyBox.Text = TranslateService.GetKey();
            keyBox.FontFamily = UiKit.MonoFont;
            keyBox.Visibility = Visibility.Collapsed;
            var keyPwd = new PasswordBox
            {
                Password = keyBox.Text, MinHeight = 30, FontSize = 12, Padding = new Thickness(8, 5, 8, 5),
                Background = UiKit.FrozenBrush(UiKit.BgColor), Foreground = UiKit.FrozenBrush(UiKit.TextPrimary),
                CaretBrush = UiKit.FrozenBrush(UiKit.TextPrimary), BorderBrush = UiKit.FrozenBrush(UiKit.BorderStrongColor),
                BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center
            };
            System.Windows.Automation.AutomationProperties.SetName(keyPwd, "API Key");
            System.Windows.Automation.AutomationProperties.SetName(keyBox, "API Key");
            var eye = new Button
            {
                Width = 30, Height = 30, ToolTip = "显示密钥",
                Content = UiKit.Glyph(UiKit.Glyphs.Eye, 14, UiKit.FrozenBrush(UiKit.TextSecondary))
            };
            UiKit.ApplyIconButtonStyle(eye);
            System.Windows.Automation.AutomationProperties.SetName(eye, "显示密钥");
            eye.Margin = new Thickness(6, 0, 0, 0);
            eye.Click += (_, _) =>
            {
                bool reveal = keyBox.Visibility != Visibility.Visible;
                if (reveal) keyBox.Text = keyPwd.Password; else keyPwd.Password = keyBox.Text;
                keyBox.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
                keyPwd.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
                eye.ToolTip = reveal ? "隐藏密钥" : "显示密钥";
            };
            string CurrentKey() => keyBox.Visibility == Visibility.Visible ? keyBox.Text : keyPwd.Password;
            var keyRow = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(eye, Dock.Right);
            keyRow.Children.Add(eye);
            var keyHost = new Grid();
            keyHost.Children.Add(keyPwd);
            keyHost.Children.Add(keyBox);
            keyRow.Children.Add(keyHost);

            var appIdBox = MakeBox(); appIdBox.Text = TranslateService.GetAppId();
            System.Windows.Automation.AutomationProperties.SetName(appIdBox, "APPID");
            var instBox = MakeBox(); instBox.Text = TranslateService.GetInstruction();
            instBox.AcceptsReturn = true; instBox.TextWrapping = TextWrapping.Wrap;
            instBox.MinHeight = 56; instBox.VerticalContentAlignment = VerticalAlignment.Top;
            System.Windows.Automation.AutomationProperties.SetName(instBox, "翻译指令");

            StackPanel Field(string label, UIElement input, string trailing = null, Color? trailingColor = null)
            {
                var field = new StackPanel { Margin = new Thickness(12, 10, 12, 0) };
                var caption = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                caption.Children.Add(UiKit.Text(label, 11, UiKit.TextSecondary));
                if (trailing != null)
                {
                    var t = UiKit.Text(" · " + trailing, 11, trailingColor ?? UiKit.TextSecondary);
                    caption.Children.Add(t);
                }
                field.Children.Add(caption);
                field.Children.Add(input);
                return field;
            }
            var apiFields = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            apiFields.Children.Add(Field("API Key", keyRow, "必填", UiKit.DangerColor));
            apiFields.Children.Add(Field("APPID（可选）", appIdBox));
            apiFields.Children.Add(Field("翻译指令（可选）", instBox));
            stack.Children.Add(UiKit.GroupCaption("百度大模型翻译 API"));
            stack.Children.Add(UiKit.Card(apiFields, new Thickness(0)));
            stack.Children.Add(new Border { Height = 14 });

            // ---- 图片翻译快捷键 ----
            var itHk = MakeHotkeyRow(owner, dlg, AppPrefs.GetInt("Screenshot.ImageTranslateHotkey", 0), fg, occupiedMessage: "该快捷键已被其他程序占用，OneBox 无法注册。");
            stack.Children.Add(UiKit.SettingsGroup("快捷键",
                UiKit.SettingsRow("翻译剪贴板文本", UiKit.KeyChip("Ctrl+Shift+T"), "全局快捷键，固定"),
                HotkeySettingsRow("框选图片翻译", itHk)));

            var btns = MakeButtons();
            ((Button)btns.Children[0]).Click += async (s, e) =>
            {
                if (!TranslateService.SetCreds(appIdBox.Text.Trim(), CurrentKey().Trim(), instBox.Text))
                {
                    string error = TranslateService.GetCredentialError();
                    OneBoxMessage.Error(dlg, "翻译设置", string.IsNullOrEmpty(error) ? "翻译凭据保存失败。" : error);
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

