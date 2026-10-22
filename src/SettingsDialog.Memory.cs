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
        static FrameworkElement BuildMemoryTab(Window owner, Window dlg, SolidColorBrush fg)
        {
            var stack = NewPage("内存", UiKit.StatusLine("OneBoxSvc 已接管特权清理", UiKit.SuccessColor));

            // ---- 自动清理 ----
            var enableCb = Switch("启用自动清理", AppPrefs.GetBool("AutoCleanEnabled", false));

            double th; AppPrefs.GetDouble("AutoCleanThreshold", out th); if (th <= 0) th = 80;
            var byThCb = DarkCheck(null, AppPrefs.GetBool("AutoCleanByThreshold", true));
            System.Windows.Automation.AutomationProperties.SetName(byThCb, "按内存占用率清理");
            var thBox = NumBox(((int)th).ToString());
            System.Windows.Automation.AutomationProperties.SetName(thBox, "内存占用率阈值");

            double tmin; AppPrefs.GetDouble("AutoCleanMinutes", out tmin); if (tmin <= 0) tmin = 30;
            var byTimeCb = DarkCheck(null, AppPrefs.GetBool("AutoCleanByTime", true));
            System.Windows.Automation.AutomationProperties.SetName(byTimeCb, "按时间周期清理");
            var timeBox = NumBox(((int)tmin).ToString());
            System.Windows.Automation.AutomationProperties.SetName(timeBox, "自动清理周期（分钟）");

            var allowFreezeCb = Switch("自动清理时允许危险项", AppPrefs.GetBool("AutoCleanAllowFreezes", false));
            allowFreezeCb.ToolTip = "Standby list / Modified page list 可能造成短暂卡顿";
            var allowRow = UiKit.SettingsRow("自动清理时允许危险项", allowFreezeCb, "关闭时，自动清理会跳过下方标了「可能卡顿」的区域");
            if (allowRow.Children.Count > 0 && allowRow.Children[allowRow.Children.Count - 1] is StackPanel allowTexts &&
                allowTexts.Children.Count > 0 && allowTexts.Children[0] is TextBlock allowTitle)
            {
                allowTexts.Children.RemoveAt(0);
                var titleWithTag = new StackPanel { Orientation = Orientation.Horizontal };
                titleWithTag.Children.Add(allowTitle);
                titleWithTag.Children.Add(UiKit.Tag("可能卡顿", UiKit.WarnColor, UiKit.WarnSurface));
                allowTexts.Children.Insert(0, titleWithTag);
            }

            stack.Children.Add(UiKit.SettingsGroup("自动清理",
                UiKit.SettingsRow("启用自动清理", enableCb),
                SentenceRow(Sentence(byThCb, "占用率达到", thBox, "% 时清理")),
                SentenceRow(Sentence(byTimeCb, "每", timeBox, "分钟清理一次")),
                allowRow));

            // ---- 要清理的内存区域 ----
            var cbWS = MakeAreaCb("进程工作集", "释放各进程的工作集（已加载到物理内存的代码与数据），把未使用的页面交还系统。", "Clean.WorkingSet", true, fg, true);
            var cbSFC = MakeAreaCb("系统文件缓存", "归还系统文件缓存：Windows 用来加速文件读取的内存被释放回可用池。", "Clean.SystemFileCache", true, fg, true);
            var cbSLNP = MakeAreaCb("低优先级备用列表", "只清理低优先级的 standby 页（影响小、释放慢但稳定）。", "Clean.StandbyListNoPrio", true, fg, true);
            var cbSL = MakeAreaCb("全部备用列表", "清空整个 standby（备用）列表，包括所有优先级缓存的页面。", "Clean.StandbyList", false, fg, true, risky: true);
            var cbMPL = MakeAreaCb("已修改页列表", "把已修改但尚未写回磁盘的脏页刷盘后转入可用列表。", "Clean.ModifiedPageList", false, fg, true, risky: true);
            var cbMFC = MakeAreaCb("已修改文件缓存", "刷新已修改的文件缓存页（与 Modified page list 的非分页部分对应）。", "Clean.ModifiedFileCache", true, fg, true);
            var cbReg = MakeAreaCb("注册表缓存", "Windows 8.1 及以上：归还注册表配置单元的缓存内存。", "Clean.RegistryCache", true, fg, AdminUtils.RealOsVersion() >= new Version(6, 3));
            var cbCML = MakeAreaCb("合并相同内存页", "Windows 10 及以上：合并相同内容的物理内存页（内存压缩 / 共享）。", "Clean.CombineMemoryLists", true, fg, AdminUtils.RealOsVersion().Major >= 10);
            stack.Children.Add(UiKit.SettingsGroup("要清理的内存区域", cbWS, cbSFC, cbSLNP, cbSL, cbMPL, cbMFC, cbReg, cbCML));

            // 标了「可能卡顿」的两项启用时弹窗确认（与 MemReduct 行为一致）。
            ConfirmIfDangerous(cbSL, dlg, "清空整个 standby 列表可能导致系统短暂卡顿，确定启用？");
            ConfirmIfDangerous(cbMPL, dlg, "刷盘 Modified page list 可能导致系统短暂卡顿，确定启用？");

            var btns = MakeButtons();
            ((Button)btns.Children[0]).Click += async (s, e) =>
            {
                if (!int.TryParse(timeBox.Text, out int n) || n <= 0 ||
                    !int.TryParse(thBox.Text, out int t) || t <= 0 || t > 100)
                {
                    OneBoxMessage.Error(dlg, "OneBox 设置", "自动清理周期必须为正整数，内存阈值必须在 1 到 100 之间。");
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

