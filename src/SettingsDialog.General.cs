using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerAudioManager.Commands;
using OneBox.Contracts;

namespace PowerAudioManager
{
    internal static partial class SettingsDialog
    {
        static FrameworkElement BuildGeneralTab(Window owner, Window dlg, SolidColorBrush fg, SolidColorBrush lightText)
        {
            var stack = NewPage("常规");

            // ---- 外观 ----
            var fontCombo = new ComboBox { Width = 190, FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center };
            AppResources.StyleDarkComboBox(fontCombo);
            System.Windows.Automation.AutomationProperties.SetName(fontCombo, "界面字体");
            string currentFont = AppPrefs.GetString("App.FontFamily", "Microsoft YaHei UI");
            foreach (var fam in System.Windows.Media.Fonts.SystemFontFamilies.OrderBy(f => f.Source))
                fontCombo.Items.Add(fam.Source);
            fontCombo.SelectedItem = currentFont;
            // 实时预览：将选中的字体应用到示例文本（显示在行描述位置）。
            var fontRow = UiKit.SettingsRow("界面字体", fontCombo, "OneBox 预览 1234");
            TextBlock preview = null;
            if (fontRow.Children.Count > 0 && fontRow.Children[fontRow.Children.Count - 1] is StackPanel fontTexts && fontTexts.Children.Count > 1)
                preview = fontTexts.Children[1] as TextBlock;
            if (preview != null) preview.FontFamily = new FontFamily(currentFont);
            fontCombo.SelectionChanged += (s, e) =>
            {
                var sel = fontCombo.SelectedItem as string;
                if (!string.IsNullOrEmpty(sel) && preview != null) preview.FontFamily = new FontFamily(sel);
            };

            // 窗口缩放：1080p=100%（4K 同样），小屏自动缩小；手动 80%–200%。
            double curScale = 1.0;
            AppPrefs.GetDouble("WindowScale.Factor", out curScale);
            if (curScale < 0.8 || curScale > 2.0) curScale = 0; // 0 = auto
            bool isAuto = curScale == 0;
            var mwForScale = owner as MainWindow;
            double autoScaleShown = 1.0;
            try { if (mwForScale != null && mwForScale._scaling != null) autoScaleShown = mwForScale._scaling.AutoScale; } catch { }

            var scaleSlider = new Slider { Minimum = 80, Maximum = 200, Value = isAuto ? 100 : (int)(curScale * 100), TickFrequency = 5, IsSnapToTickEnabled = true, Width = 110, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(scaleSlider, "窗口缩放");
            var scalePctLabel = new TextBlock { Text = isAuto ? "自动" : $"{(int)(curScale * 100)}%", Foreground = fg, FontSize = 12, Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 10, 0) };
            var scaleAutoCb = DarkCheck("自动", isAuto);
            scaleAutoCb.Checked += (_, _) => { scaleSlider.IsEnabled = false; scalePctLabel.Text = "自动"; };
            scaleAutoCb.Unchecked += (_, _) => { scaleSlider.IsEnabled = true; scalePctLabel.Text = $"{(int)scaleSlider.Value}%"; };
            scaleSlider.ValueChanged += (_, _) => { if (scaleSlider.IsEnabled) scalePctLabel.Text = $"{(int)scaleSlider.Value}%"; };
            scaleSlider.IsEnabled = !isAuto;
            var scaleControl = new StackPanel { Orientation = Orientation.Horizontal };
            scaleControl.Children.Add(scaleSlider);
            scaleControl.Children.Add(scalePctLabel);
            scaleControl.Children.Add(scaleAutoCb);
            stack.Children.Add(UiKit.SettingsGroup("外观",
                fontRow,
                UiKit.SettingsRow("界面缩放", scaleControl, $"自动时为 {(int)Math.Round(autoScaleShown * 100)}%")));

            // ---- 窗口 ----
            var topmostCb = Switch("窗口置顶", AppPrefs.GetBool("Topmost", false));
            var lockCb = Switch("锁定位置", AppPrefs.GetBool("LockPosition", false));
            stack.Children.Add(UiKit.SettingsGroup("窗口",
                UiKit.SettingsRow("窗口置顶", topmostCb),
                UiKit.SettingsRow("锁定位置", lockCb, "锁定后切换分辨率也不会移动窗口")));

            // ---- 自动折叠 ----
            var autoCb = Switch("启用自动折叠", AppPrefs.GetBool("AutoCollapse", true));
            var delayBox = NumBox(AppPrefs.GetInt("AutoCollapseDelay", 8).ToString());
            System.Windows.Automation.AutomationProperties.SetName(delayBox, "折叠延时（秒）");
            var delaySentence = Sentence(delayBox, "秒");
            var expandAfterManualCb = Switch("手动折叠后仍允许悬停展开", AppPrefs.GetBool("AutoExpandAfterManual", false));
            stack.Children.Add(UiKit.SettingsGroup("自动折叠",
                UiKit.SettingsRow("启用自动折叠", autoCb),
                UiKit.SettingsRow("鼠标离开后折叠", delaySentence, "0 表示立即折叠"),
                UiKit.SettingsRow("手动折叠后仍允许悬停展开", expandAfterManualCb)));

            // ---- 启动 ----
            // 开机自启：统一状态标志可选择注册表、计划任务或 OneBoxSvc 服务方式。
            bool svcInstalled = AutoStartService.IsServiceInstalled();
            var autoStartCb = Switch("开机自启", AutoStartService.IsEnabled());
            stack.Children.Add(UiKit.SettingsGroup("启动",
                UiKit.SettingsRow("开机自启", autoStartCb, svcInstalled
                    ? "登录后在托盘中运行 · OneBoxSvc 已安装"
                    : "登录后在托盘中运行 · OneBoxSvc 未安装，将使用注册表启动")));

            var btns = MakeButtons();
            var ok = (Button)btns.Children[0];
            ok.Click += async (s, e) =>
            {
                var mw = owner as MainWindow;
                int d;
                if (!int.TryParse(delayBox.Text, out d) || d < 0)
                {
                    OneBoxMessage.Error(dlg, "OneBox 设置", "自动折叠延时必须是大于等于 0 的整数。");
                    return;
                }

                if (!TryPersist(dlg,
                    () => AppPrefs.SetString("App.FontFamily", (fontCombo.SelectedItem as string) ?? "Microsoft YaHei UI"),
                    () => AppPrefs.Set(PreferenceKeys.Window.Topmost, topmostCb.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Window.LockPosition, lockCb.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Window.AutoCollapse, autoCb.IsChecked == true),
                    () => AppPrefs.SetBool("AutoExpandAfterManual", expandAfterManualCb.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Window.AutoCollapseDelay, d))) return;

                if (mw != null)
                {
                    var runtimeResult = await mw.ExecuteCommandAsync(AppCommandId.RuntimeApplyGeneral, CommandSource.Settings,
                        new GeneralRuntimePayload(topmostCb.IsChecked == true, lockCb.IsChecked == true, true,
                            scaleAutoCb.IsChecked == true,
                            scaleAutoCb.IsChecked == true ? null : scaleSlider.Value / 100.0));
                    if (!runtimeResult.Success) return;
                }

                if (mw != null)
                {
                    var autoStartResult = await mw.ExecuteCommandAsync(AppCommandId.AutoStartApply,
                        CommandSource.Settings, new AutoStartApplyPayload(autoStartCb.IsChecked == true,
                            AppPrefs.Get(PreferenceKeys.AutoStart.LastMethod)));
                    if (!autoStartResult.Success) return;
                }
                dlg.DialogResult = true; dlg.Close();
            };
            ((Button)btns.Children[1]).Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            stack.Children.Add(btns);

            return Scroll(stack);
        }
    }
}
