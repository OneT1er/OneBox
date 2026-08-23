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
        static ScrollViewer BuildGeneralTab(Window owner, Window dlg, SolidColorBrush fg, SolidColorBrush lightText)
        {
            var stack = new StackPanel { Margin = new Thickness(20) };

            stack.Children.Add(new TextBlock { Text = "字体", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });

            var fontRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
            fontRow.Children.Add(new TextBlock { Text = "界面字体：", Foreground = fg, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var fontCombo = new ComboBox
            {
                Width = 220, Height = 28, FontSize = 12,
                Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)),
                Foreground = lightText,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            AppResources.StyleDarkComboBox(fontCombo);
            string currentFont = AppPrefs.GetString("App.FontFamily", "Microsoft YaHei UI");
            foreach (var fam in System.Windows.Media.Fonts.SystemFontFamilies.OrderBy(f => f.Source))
                fontCombo.Items.Add(fam.Source);
            fontCombo.SelectedItem = currentFont;
            // 实时预览标签：将选中的字体应用到示例文本。
            var preview = new TextBlock
            {
                Text = "OneBox 预览 1234",
                FontFamily = new FontFamily(currentFont),
                Foreground = lightText, FontSize = 13,
                Margin = new Thickness(0, 8, 0, 16)
            };
            fontCombo.SelectionChanged += (s, e) =>
            {
                var sel = fontCombo.SelectedItem as string;
                if (!string.IsNullOrEmpty(sel)) preview.FontFamily = new FontFamily(sel);
            };
            DockPanel.SetDock(fontCombo, Dock.Right);
            fontRow.Children.Add(fontCombo);
            stack.Children.Add(fontRow);
            stack.Children.Add(preview);

            stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(80, 75, 120)), Margin = new Thickness(0, 4, 0, 12) });

            stack.Children.Add(new TextBlock { Text = "窗口", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });

            var topmostCb = new CheckBox { Content = "窗口置顶", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
            topmostCb.IsChecked = AppPrefs.GetBool("Topmost", false);
            stack.Children.Add(topmostCb);

            var lockCb = new CheckBox { Content = "锁定位置", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 16) };
            lockCb.IsChecked = AppPrefs.GetBool("LockPosition", false);
            stack.Children.Add(lockCb);

            stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(80, 75, 120)), Margin = new Thickness(0, 4, 0, 12) });

            stack.Children.Add(new TextBlock { Text = "自动折叠", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });

            var autoCb = new CheckBox { Content = "启用自动折叠", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
            autoCb.IsChecked = AppPrefs.GetBool("AutoCollapse", true);
            stack.Children.Add(autoCb);

            var delayRow = new DockPanel { Margin = new Thickness(20, 0, 0, 8) };
            delayRow.Children.Add(new TextBlock { Text = "鼠标离开后", Foreground = fg, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var delayBox = new TextBox { Width = 50, MinHeight = 24, Margin = new Thickness(8, 0, 8, 0), Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)) };
            delayBox.Text = AppPrefs.GetInt("AutoCollapseDelay", 8).ToString();
            delayRow.Children.Add(delayBox);
            delayRow.Children.Add(new TextBlock { Text = "秒后折叠（0=立即）", Foreground = fg, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(delayRow);

            var expandAfterManualCb = new CheckBox { Content = "手动折叠后允许悬停展开", Foreground = fg, FontSize = 11, Margin = new Thickness(20, 0, 0, 16) };
            expandAfterManualCb.IsChecked = AppPrefs.GetBool("AutoExpandAfterManual", false);
            stack.Children.Add(expandAfterManualCb);

            stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(80, 75, 120)), Margin = new Thickness(0, 4, 0, 12) });

            // 窗口缩放：1080p=100%（4K 同样），小屏自动缩小；手动 80%–200%。
            double curScale = 1.0;
            AppPrefs.GetDouble("WindowScale.Factor", out curScale);
            if (curScale < 0.8 || curScale > 2.0) curScale = 0; // 0 = auto
            bool isAuto = curScale == 0;
            var mwForScale = owner as MainWindow;
            double autoScaleShown = 1.0;
            try { if (mwForScale != null && mwForScale._scaling != null) autoScaleShown = mwForScale._scaling.AutoScale; } catch { }

            stack.Children.Add(new TextBlock { Text = $"窗口缩放（自动 {(int)Math.Round(autoScaleShown * 100)}%）", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
            var scaleRow = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            var scaleSlider = new Slider { Minimum = 80, Maximum = 200, Value = isAuto ? 100 : (int)(curScale * 100), TickFrequency = 5, IsSnapToTickEnabled = true, Width = 160, VerticalAlignment = VerticalAlignment.Center };
            var scalePctLabel = new TextBlock { Text = isAuto ? "自动" : $"{(int)(curScale * 100)}%", Foreground = fg, FontSize = 11, Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            var scaleAutoCb = new CheckBox { Content = "自动", Foreground = fg, FontSize = 11, IsChecked = isAuto, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            scaleAutoCb.Checked += (_, _) => { scaleSlider.IsEnabled = false; scalePctLabel.Text = "自动"; };
            scaleAutoCb.Unchecked += (_, _) => { scaleSlider.IsEnabled = true; scalePctLabel.Text = $"{(int)scaleSlider.Value}%"; };
            scaleSlider.ValueChanged += (_, _) => { if (scaleSlider.IsEnabled) scalePctLabel.Text = $"{(int)scaleSlider.Value}%"; };
            scaleSlider.IsEnabled = !isAuto;
            DockPanel.SetDock(scalePctLabel, Dock.Right);
            DockPanel.SetDock(scaleAutoCb, Dock.Right);
            scaleRow.Children.Add(scalePctLabel);
            scaleRow.Children.Add(scaleAutoCb);
            scaleRow.Children.Add(scaleSlider);
            stack.Children.Add(scaleRow);

            stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(80, 75, 120)), Margin = new Thickness(0, 4, 0, 12) });

            // 开机自启：统一状态标志可选择注册表、计划任务或 OneBoxSvc 服务方式。
            stack.Children.Add(new TextBlock { Text = "开机自启", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });

            var autoStartCb = new CheckBox { Content = "开机自启", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
            bool svcInstalled = AutoStartService.IsServiceInstalled();
            autoStartCb.IsChecked = AutoStartService.IsEnabled();
            stack.Children.Add(autoStartCb);

            var autoStartStatus = new TextBlock { Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
            autoStartStatus.Text = svcInstalled
                ? "OneBoxSvc 已安装"
                : "OneBoxSvc 未安装，将使用注册表启动";
            stack.Children.Add(autoStartStatus);

            var btns = MakeButtons();
            var ok = (Button)btns.Children[0];
            ok.Click += async (s, e) =>
            {
                var mw = owner as MainWindow;
                int d;
                if (!int.TryParse(delayBox.Text, out d) || d < 0)
                {
                    MessageBox.Show(dlg, "自动折叠延时必须是大于等于 0 的整数。", "OneBox 设置",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
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
