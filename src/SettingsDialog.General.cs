using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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

            var lockCb = new CheckBox { Content = "锁定位置（禁止拖动悬浮窗）", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
            lockCb.IsChecked = AppPrefs.GetBool("LockPosition", false);
            stack.Children.Add(lockCb);

            stack.Children.Add(new TextBlock { Text = "固定位置：悬浮窗位置不受分辨率变化影响（拖到哪固定到哪，仅在完全离开屏幕时自动回到可视区）。", Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });

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

            var expandAfterManualCb = new CheckBox { Content = "手动折叠后，鼠标悬停也自动展开", Foreground = fg, FontSize = 11, Margin = new Thickness(20, 0, 0, 8) };
            expandAfterManualCb.IsChecked = AppPrefs.GetBool("AutoExpandAfterManual", false);
            stack.Children.Add(expandAfterManualCb);

            stack.Children.Add(new TextBlock { Text = "默认：手动折叠后保持折叠，鼠标悬停不展开；只有自动折叠的才悬停展开。", Foreground = fg, FontSize = 10, Margin = new Thickness(0, 0, 0, 16) });

            stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(80, 75, 120)), Margin = new Thickness(0, 4, 0, 12) });

            // 窗口缩放：1080p=100%（4K 同样），小屏自动缩小；手动 80%–200%。
            double curScale = 1.0;
            AppPrefs.GetDouble("WindowScale.Factor", out curScale);
            if (curScale < 0.8 || curScale > 2.0) curScale = 0; // 0 = auto
            bool isAuto = curScale == 0;
            var mwForScale = owner as MainWindow;
            double autoScaleShown = 1.0;
            try { if (mwForScale != null && mwForScale._scaling != null) autoScaleShown = mwForScale._scaling.AutoScale; } catch { }

            stack.Children.Add(new TextBlock { Text = "窗口缩放", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
            stack.Children.Add(new TextBlock { Text = $"auto {(int)Math.Round(autoScaleShown * 100)}%", Foreground = fg, FontSize = 10, Margin = new Thickness(0, 0, 0, 4) });
            var scaleRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
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
            stack.Children.Add(new TextBlock { Text = "小屏自动缩小；手动 80%–200%。", Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });

            stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(80, 75, 120)), Margin = new Thickness(0, 4, 0, 12) });

            // 开机自启：经 OneBoxSvc 服务实现，勾选/取消写用户注册表 flag，服务启动 GUI 前读取，无需 UAC。
            stack.Children.Add(new TextBlock { Text = "开机自启", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });

            var autoStartCb = new CheckBox { Content = "开机自启（开机时自动启动）", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
            bool svcInstalled = AutoStartService.IsServiceInstalled();
            autoStartCb.IsChecked = svcInstalled && AppPrefs.GetBool("AutoStart.Enabled", true);
            stack.Children.Add(autoStartCb);

            var autoStartStatus = new TextBlock { Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
            autoStartStatus.Text = svcInstalled
                ? "经 OneBoxSvc 服务实现，勾选/取消无需 UAC（下次开机生效）。取消勾选后服务仍驻留但不再自动启动主程序。"
                : "OneBoxSvc 服务未安装，需一次管理员授权安装服务后才能开机自启（勾选仅记录意图）。";
            stack.Children.Add(autoStartStatus);

            var btns = MakeButtons();
            var ok = (Button)btns.Children[0];
            ok.Click += (s, e) =>
            {
                AppPrefs.SetString("App.FontFamily", (fontCombo.SelectedItem as string) ?? "Microsoft YaHei UI");
                AppPrefs.SetBool("Topmost", topmostCb.IsChecked == true);
                AppPrefs.SetBool("LockPosition", lockCb.IsChecked == true);
                AppPrefs.SetBool("AutoCollapse", autoCb.IsChecked == true);
                AppPrefs.SetBool("AutoExpandAfterManual", expandAfterManualCb.IsChecked == true);
                AppPrefs.SetBool("AutoStart.Enabled", autoStartCb.IsChecked == true);
                int d; if (int.TryParse(delayBox.Text, out d) && d >= 0) AppPrefs.SetInt("AutoCollapseDelay", d);
                var mw = owner as MainWindow;
                if (scaleAutoCb.IsChecked == true)
                { try { mw?._scaling?.ResetManualScale(); } catch { } }
                else
                { try { mw?._scaling?.ApplyManualScale(scaleSlider.Value / 100.0); } catch { } }

                if (mw != null)
                {
                    mw.Topmost = topmostCb.IsChecked == true;
                    mw._topmost = topmostCb.IsChecked == true;
                    mw._lockPosition = lockCb.IsChecked == true;
                    if (mw._tray != null) mw._tray.SetLockChecked(mw._lockPosition);
                    if (mw._pinBtn != null)
                    {
                        mw._pinBtn.Content = UiKit.PinIcon(mw._lockPosition);
                        mw._pinBtn.Foreground = new SolidColorBrush(mw._lockPosition ? UiKit.AccentColor : UiKit.TextSecondary);
                    }
                    mw.RefreshAutoCollapse();
                    mw.RefreshHotkeys();
                    mw.ApplyFont();
                }
                dlg.DialogResult = true; dlg.Close();
            };
            ((Button)btns.Children[1]).Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            stack.Children.Add(btns);

            return Scroll(stack);
        }
    }
}
