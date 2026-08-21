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
        static ScrollViewer BuildMemoryTab(Window owner, Window dlg, SolidColorBrush fg)
        {
            var stack = new StackPanel { Margin = new Thickness(20) };

            stack.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(40, 60, 50)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new TextBlock { Text = "内存清理由 OneBoxSvc 服务执行（SYSTEM 权限），所有清理项可用，无需管理员重启", Foreground = Brushes.White, FontSize = 11, TextWrapping = TextWrapping.Wrap }
            });

            stack.Children.Add(new TextBlock { Text = "自动清理", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
            var enableCb = new CheckBox { Content = "启用自动清理", Foreground = Brushes.White, FontSize = 13, Margin = new Thickness(0, 0, 0, 14) };
            enableCb.IsChecked = AppPrefs.GetBool("AutoCleanEnabled", false);
            stack.Children.Add(enableCb);

            var byTimeCb = new CheckBox { Content = "按时间周期清理", Foreground = fg, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
            byTimeCb.IsChecked = AppPrefs.GetBool("AutoCleanByTime", true);
            stack.Children.Add(byTimeCb);
            var timeRow = new DockPanel { Margin = new Thickness(20, 0, 0, 14) };
            timeRow.Children.Add(new TextBlock { Text = "每", VerticalAlignment = VerticalAlignment.Center, Foreground = fg });
            var timeBox = new TextBox { Width = 60, MinHeight = 24, Margin = new Thickness(8, 0, 8, 0), Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)) };
            double tmin; AppPrefs.GetDouble("AutoCleanMinutes", out tmin); if (tmin <= 0) tmin = 30;
            timeBox.Text = ((int)tmin).ToString();
            timeRow.Children.Add(timeBox);
            timeRow.Children.Add(new TextBlock { Text = "分钟清理一次", VerticalAlignment = VerticalAlignment.Center, Foreground = fg });
            stack.Children.Add(timeRow);

            var byThCb = new CheckBox { Content = "按内存占用率清理", Foreground = fg, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
            byThCb.IsChecked = AppPrefs.GetBool("AutoCleanByThreshold", true);
            stack.Children.Add(byThCb);
            var thRow = new DockPanel { Margin = new Thickness(20, 0, 0, 18) };
            thRow.Children.Add(new TextBlock { Text = "占用率达到", VerticalAlignment = VerticalAlignment.Center, Foreground = fg });
            var thBox = new TextBox { Width = 60, MinHeight = 24, Margin = new Thickness(8, 0, 8, 0), Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)) };
            double th; AppPrefs.GetDouble("AutoCleanThreshold", out th); if (th <= 0) th = 80;
            thBox.Text = ((int)th).ToString();
            thRow.Children.Add(thBox);
            thRow.Children.Add(new TextBlock { Text = "% 时清理", VerticalAlignment = VerticalAlignment.Center, Foreground = fg });
            stack.Children.Add(thRow);

            stack.Children.Add(new TextBlock { Text = "要清理的内存区域", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 8, 0, 4) });
            stack.Children.Add(new TextBlock { Text = "可以全部取消；未选择任何项目时，手动与自动清理都不会执行。", Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            var cbWS = MakeAreaCb("Working set", "释放各进程的工作集（已加载到物理内存的代码与数据），把未使用的页面交还系统。", "Clean.WorkingSet", true, fg, true);
            var cbSFC = MakeAreaCb("System file cache", "归还系统文件缓存：Windows 用来加速文件读取的内存被释放回可用池。", "Clean.SystemFileCache", true, fg, true);
            var cbMPL = MakeAreaCb("Modified page list", "把已修改但尚未写回磁盘的脏页刷盘后转入可用列表。", "Clean.ModifiedPageList", false, fg, true);
            var cbSL = MakeAreaCb("Standby list", "清空整个 standby（备用）列表，包括所有优先级缓存的页面。", "Clean.StandbyList", false, fg, true);
            var cbSLNP = MakeAreaCb("Standby list (without priority)", "只清理低优先级的 standby 页（影响小、释放慢但稳定）。", "Clean.StandbyListNoPrio", true, fg, true);
            var cbMFC = MakeAreaCb("Modified file cache", "刷新已修改的文件缓存页（与 Modified page list 的非分页部分对应）。", "Clean.ModifiedFileCache", true, fg, true);
            var cbReg = MakeAreaCb("Registry cache (win8.1+)", "Windows 8.1 及以上：归还注册表配置单元的缓存内存。", "Clean.RegistryCache", true, fg, AdminUtils.RealOsVersion() >= new Version(6, 3));
            var cbCML = MakeAreaCb("Combine memory lists (win10+)", "Windows 10 及以上：合并相同内容的物理内存页（内存压缩 / 共享）。", "Clean.CombineMemoryLists", true, fg, AdminUtils.RealOsVersion().Major >= 10);
            stack.Children.Add(cbWS);
            stack.Children.Add(cbSFC);
            stack.Children.Add(cbMPL);
            stack.Children.Add(cbSL);
            stack.Children.Add(cbSLNP);
            stack.Children.Add(cbMFC);
            stack.Children.Add(cbReg);
            stack.Children.Add(cbCML);

            // 带 * 的两项可能导致系统短暂卡顿，启用时弹窗确认（与 MemReduct 行为一致）。
            ConfirmIfDangerous(cbSL, dlg, "清空整个 standby 列表可能导致系统短暂卡顿，确定启用？");
            ConfirmIfDangerous(cbMPL, dlg, "刷盘 Modified page list 可能导致系统短暂卡顿，确定启用？");

            stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(80, 75, 120)), Margin = new Thickness(0, 14, 0, 12) });
            var allowFreezeCb = new CheckBox { Content = "允许自动清理危险项（Standby list / Modified page list）", Foreground = fg, FontSize = 11, Margin = new Thickness(0, 0, 0, 4), IsChecked = AppPrefs.GetBool("AutoCleanAllowFreezes", false) };
            stack.Children.Add(allowFreezeCb);
            stack.Children.Add(new TextBlock { Text = "默认自动清理会跳过这两项以避免后台卡顿；勾选后自动清理也执行它们。", Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 0) });

            var btns = MakeButtons();
            ((Button)btns.Children[0]).Click += async (s, e) =>
            {
                if (!int.TryParse(timeBox.Text, out int n) || n <= 0 ||
                    !int.TryParse(thBox.Text, out int t) || t <= 0 || t > 100)
                {
                    MessageBox.Show(dlg, "自动清理周期必须为正整数，内存阈值必须在 1 到 100 之间。", "OneBox 设置",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!TryPersist(dlg,
                    () => AppPrefs.Set(PreferenceKeys.Memory.AutoEnabled, enableCb.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Memory.AutoByTime, byTimeCb.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Memory.AutoByThreshold, byThCb.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Memory.AutoMinutes, n),
                    () => AppPrefs.Set(PreferenceKeys.Memory.AutoThreshold, t),
                    () => AppPrefs.Set(PreferenceKeys.Memory.AllowFreezes, allowFreezeCb.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Memory.WorkingSet, cbWS.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Memory.SystemFileCache, cbSFC.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Memory.ModifiedPageList, cbMPL.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Memory.StandbyList, cbSL.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Memory.StandbyListNoPriority, cbSLNP.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Memory.ModifiedFileCache, cbMFC.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Memory.RegistryCache, cbReg.IsChecked == true),
                    () => AppPrefs.Set(PreferenceKeys.Memory.CombineMemoryLists, cbCML.IsChecked == true))) return;
                if (owner is MainWindow mw)
                {
                    var result = await mw.ExecuteCommandAsync(AppCommandId.RuntimeRestartAutoClean, CommandSource.Settings);
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

