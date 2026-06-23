using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerAudioManager
{
    // Unified settings window with tabs: 常规 / 板块 / 内存 / 翻译.
    // Replaces the former ModulesSettingsDialog, CleanerSettingsDialog and
    // TranslateSettingsDialog (now deleted). Opened from the tray "设置..." item
    // and (翻译 tab) from the translate window's ⚙ button.
    internal static class SettingsDialog
    {
        public static void Show(Window owner)
        {
            Show(owner, 0);
        }

        // openTab: 0=常规 1=板块 2=内存 3=翻译
        public static void Show(Window owner, int openTab)
        {
            // Dark font for tab headers / content.
            var fg = new SolidColorBrush(Color.FromRgb(190, 188, 220));
            var lightText = new SolidColorBrush(Color.FromRgb(220, 218, 245));

            // The TabControl is the body OneBoxWindow.Create wraps (title bar +
            // rounded border). Build it first, then create the window around it.
            var tabs = new TabControl
            {
                Margin = new Thickness(0),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Padding = new Thickness(0)
            };
            AppResources.StyleDarkTabControl(tabs);

            var dlg = OneBoxWindow.Create(owner, "设置", 460, 560, tabs, true);

            tabs.Items.Add(BuildGeneralTab(owner, dlg, fg, lightText));
            tabs.Items.Add(BuildModulesTab(owner, dlg, fg, lightText));
            tabs.Items.Add(BuildMemoryTab(owner, dlg, fg, lightText));
            tabs.Items.Add(BuildTranslateTab(owner, dlg, fg, lightText));
            tabs.Items.Add(BuildScreenshotTab(owner, dlg, fg, lightText));

            if (openTab >= 0 && openTab < tabs.Items.Count) tabs.SelectedIndex = openTab;

            dlg.ShowDialog();
        }

        // ---- 常规 tab ----------------------------------------------------------

        static TabItem BuildGeneralTab(Window owner, Window dlg, SolidColorBrush fg, SolidColorBrush lightText)
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
            // Live preview label: shows the selected font applied to sample text.
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

            var autoStartCb = new CheckBox { Content = "开机自启", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 16) };
            autoStartCb.IsChecked = IsAutoStartEnabled();
            stack.Children.Add(autoStartCb);

            var btns = MakeButtons();
            var ok = (Button)btns.Children[0];
            ok.Click += (s, e) =>
            {
                AppPrefs.SetString("App.FontFamily", (fontCombo.SelectedItem as string) ?? "Microsoft YaHei UI");
                AppPrefs.SetBool("Topmost", topmostCb.IsChecked == true);
                AppPrefs.SetBool("LockPosition", lockCb.IsChecked == true);
                AppPrefs.SetBool("AutoCollapse", autoCb.IsChecked == true);
                AppPrefs.SetBool("AutoExpandAfterManual", expandAfterManualCb.IsChecked == true);
                int d; if (int.TryParse(delayBox.Text, out d) && d >= 0) AppPrefs.SetInt("AutoCollapseDelay", d);
                if (autoStartCb.IsChecked != IsAutoStartEnabled()) ToggleAutoStart(autoStartCb.IsChecked == true);

                var mw = owner as MainWindow;
                if (mw != null)
                {
                    // Apply new font + lock/topmost + auto-collapse settings live.
                    mw.Topmost = topmostCb.IsChecked == true;
                    mw._topmost = topmostCb.IsChecked == true;
                    mw._lockPosition = lockCb.IsChecked == true;
                    if (mw._tray != null) mw._tray.SetLockChecked(mw._lockPosition);
                    if (mw._pinBtn != null)
                    {
                        mw._pinBtn.Content = mw._lockPosition ? "🔒" : "🔓";
                        mw._pinBtn.Foreground = new SolidColorBrush(mw._lockPosition ? MainWindow.AccentColor : MainWindow.TextSecondary);
                    }
                    mw.RefreshAutoCollapse();
                    mw.ApplyFont();
                }
                dlg.DialogResult = true; dlg.Close();
            };
            ((Button)btns.Children[1]).Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            stack.Children.Add(btns);

            return new TabItem { Header = " 常规 ", Content = Scroll(stack) };
        }

        // ---- 板块 tab ----------------------------------------------------------

        static TabItem BuildModulesTab(Window owner, Window dlg, SolidColorBrush fg, SolidColorBrush lightText)
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
            stack.Children.Add(cbPower);
            stack.Children.Add(cbAudio);
            stack.Children.Add(cbMem);
            stack.Children.Add(cbTr);
            stack.Children.Add(cbLaunch);
            stack.Children.Add(cbClip);
            stack.Children.Add(cbGallery);
            stack.Children.Add(new TextBlock { Text = "隐藏后悬浮窗立即刷新；托盘菜单与全局快捷键不受影响。", Foreground = fg, FontSize = 10, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap });

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
                if (owner is MainWindow) ((MainWindow)owner).RebuildUI();
                dlg.DialogResult = true; dlg.Close();
            };
            ((Button)btns.Children[1]).Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            stack.Children.Add(btns);

            return new TabItem { Header = " 板块 ", Content = Scroll(stack) };
        }

        // ---- 内存 tab ----------------------------------------------------------

        static TabItem BuildMemoryTab(Window owner, Window dlg, SolidColorBrush fg, SolidColorBrush lightText)
        {
            var stack = new StackPanel { Margin = new Thickness(20) };
            bool isAdmin = AdminUtils.IsAdmin();

            var adminBanner = new Border
            {
                Background = new SolidColorBrush(isAdmin ? Color.FromRgb(40, 60, 50) : Color.FromRgb(70, 50, 50)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var adminRow = new DockPanel { LastChildFill = true };
            adminRow.Children.Add(new TextBlock
            {
                Text = isAdmin ? "已以管理员身份运行：所有清理项可用" : "当前未以管理员身份运行：部分项需要管理员权限",
                Foreground = Brushes.White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center
            });
            if (!isAdmin)
            {
                var elevateBtn = new Button { Content = "以管理员重启", Padding = new Thickness(10, 4, 10, 4), FontSize = 11 };
                AppResources.StyleDialogButton(elevateBtn, true);
                DockPanel.SetDock(elevateBtn, Dock.Right);
                elevateBtn.Click += (s, e) => AdminUtils.RestartAsAdmin();
                adminRow.Children.Add(elevateBtn);
            }
            adminBanner.Child = adminRow;
            stack.Children.Add(adminBanner);

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
            var cbWS = MakeAreaCb("Working set", "释放各进程的工作集（已加载到物理内存的代码与数据），把未使用的页面交还系统。", "Clean.WorkingSet", true, fg, true);
            var cbSFC = MakeAreaCb("System file cache", "归还系统文件缓存：Windows 用来加速文件读取的内存被释放回可用池。", "Clean.SystemFileCache", true, fg, true);
            var cbMPL = MakeAreaCb("Modified page list*", "把已修改但尚未写回磁盘的脏页刷盘后转入可用列表。* 需要管理员权限。", "Clean.ModifiedPageList", false, fg, isAdmin);
            var cbSL = MakeAreaCb("Standby list*", "清空整个 standby（备用）列表，包括所有优先级缓存的页面。* 需要管理员权限。", "Clean.StandbyList", false, fg, isAdmin);
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

            // The two *-marked items can cause brief system freezes; warn when the
            // user enables them (matching memreduct's confirmation prompt).
            ConfirmIfDangerous(cbSL, dlg, "清空整个 standby 列表可能导致系统短暂卡顿，确定启用？");
            ConfirmIfDangerous(cbMPL, dlg, "刷盘 Modified page list 可能导致系统短暂卡顿，确定启用？");

            stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(80, 75, 120)), Margin = new Thickness(0, 14, 0, 12) });
            var allowFreezeCb = new CheckBox { Content = "允许自动清理危险项（Standby list / Modified page list）", Foreground = fg, FontSize = 11, Margin = new Thickness(0, 0, 0, 4), IsChecked = AppPrefs.GetBool("AutoCleanAllowFreezes", false) };
            stack.Children.Add(allowFreezeCb);
            stack.Children.Add(new TextBlock { Text = "默认自动清理会跳过这两项以避免后台卡顿；勾选后自动清理也执行它们。", Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 0) });

            var btns = MakeButtons();
            ((Button)btns.Children[0]).Click += (s, e) =>
            {
                AppPrefs.SetBool("AutoCleanEnabled", enableCb.IsChecked == true);
                AppPrefs.SetBool("AutoCleanByTime", byTimeCb.IsChecked == true);
                AppPrefs.SetBool("AutoCleanByThreshold", byThCb.IsChecked == true);
                int n; if (int.TryParse(timeBox.Text, out n) && n > 0) AppPrefs.SetDouble("AutoCleanMinutes", n);
                int t; if (int.TryParse(thBox.Text, out t) && t > 0 && t <= 100) AppPrefs.SetDouble("AutoCleanThreshold", t);
                AppPrefs.SetBool("AutoCleanAllowFreezes", allowFreezeCb.IsChecked == true);
                AppPrefs.SetBool("Clean.WorkingSet", cbWS.IsChecked == true);
                AppPrefs.SetBool("Clean.SystemFileCache", cbSFC.IsChecked == true);
                AppPrefs.SetBool("Clean.ModifiedPageList", cbMPL.IsChecked == true);
                AppPrefs.SetBool("Clean.StandbyList", cbSL.IsChecked == true);
                AppPrefs.SetBool("Clean.StandbyListNoPrio", cbSLNP.IsChecked == true);
                AppPrefs.SetBool("Clean.ModifiedFileCache", cbMFC.IsChecked == true);
                AppPrefs.SetBool("Clean.RegistryCache", cbReg.IsChecked == true);
                AppPrefs.SetBool("Clean.CombineMemoryLists", cbCML.IsChecked == true);
                if (owner is MainWindow) ((MainWindow)owner).RestartAutoCleanTimer();
                dlg.DialogResult = true; dlg.Close();
            };
            ((Button)btns.Children[1]).Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            stack.Children.Add(btns);

            return new TabItem { Header = " 内存 ", Content = Scroll(stack) };
        }

        // When a "dangerous" cleaning option is checked, prompt for confirmation;
        // if the user declines, uncheck it again.
        static void ConfirmIfDangerous(CheckBox cb, Window dlg, string message)
        {
            cb.Checked += (s, e) =>
            {
                var rc = MessageBox.Show(dlg, message, "提示", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (rc != MessageBoxResult.OK) cb.IsChecked = false;
            };
        }

        // ---- 翻译 tab ----------------------------------------------------------

        static TabItem BuildTranslateTab(Window owner, Window dlg, SolidColorBrush fg, SolidColorBrush lightText)
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

            var btns = MakeButtons();
            ((Button)btns.Children[0]).Click += (s, e) =>
            {
                TranslateService.SetCreds(appIdBox.Text.Trim(), keyBox.Text.Trim(), instBox.Text);
                dlg.DialogResult = true; dlg.Close();
            };
            ((Button)btns.Children[1]).Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            stack.Children.Add(btns);

            return new TabItem { Header = " 翻译 ", Content = Scroll(stack) };
        }

        // ---- 截图 tab -----------------------------------------------------------

        static TabItem BuildScreenshotTab(Window owner, Window dlg, SolidColorBrush fg, SolidColorBrush lightText)
        {
            var stack = new StackPanel { Margin = new Thickness(20) };

            stack.Children.Add(new TextBlock { Text = "截图保存位置", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
            var rootRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
            var rootBox = new TextBox
            {
                MinHeight = 26, FontSize = 12,
                Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120))
            };
            string savedRoot = AppPrefs.GetString("Screenshot.RootDir", "");
            if (string.IsNullOrWhiteSpace(savedRoot))
                savedRoot = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures), "OneBoxScreenshots");
            rootBox.Text = savedRoot;
            var browseBtn = new Button { Content = "浏览…", Height = 26, FontSize = 12, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(browseBtn, false);
            browseBtn.Click += (s, e) =>
            {
                var fbd = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = rootBox.Text };
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    rootBox.Text = fbd.SelectedPath;
            };
            DockPanel.SetDock(browseBtn, Dock.Right);
            rootRow.Children.Add(browseBtn);
            rootRow.Children.Add(rootBox);
            stack.Children.Add(rootRow);
            stack.Children.Add(new TextBlock { Text = "截图按前台应用名自动建子文件夹存放。", Foreground = fg, FontSize = 10, Margin = new Thickness(0, 0, 0, 16) });

            stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(80, 75, 120)), Margin = new Thickness(0, 0, 0, 12) });

            stack.Children.Add(new TextBlock { Text = "截图快捷键", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
            int curHotkey = AppPrefs.GetInt("Screenshot.Hotkey", 0);
            var hkLabel = new TextBlock
            {
                Text = curHotkey != 0 ? HotkeyCaptureDialog.Format(curHotkey) : "（未设置）",
                Foreground = curHotkey != 0 ? Brushes.White : fg,
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            var setHkBtn = new Button { Content = "设置快捷键", Height = 28, FontSize = 12, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(setHkBtn, false);
            var clearHkBtn = new Button { Content = "清除", Height = 28, FontSize = 12, Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(clearHkBtn, false);
            setHkBtn.Click += (s, e) =>
            {
                var captured = HotkeyCaptureDialog.Show(dlg, curHotkey);
                if (captured.HasValue)
                {
                    curHotkey = captured.Value;
                    hkLabel.Text = HotkeyCaptureDialog.Format(curHotkey);
                    hkLabel.Foreground = Brushes.White;
                }
            };
            clearHkBtn.Click += (s, e) =>
            {
                curHotkey = 0;
                hkLabel.Text = "（未设置）";
                hkLabel.Foreground = fg;
            };
            var hkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            hkRow.Children.Add(hkLabel);
            hkRow.Children.Add(setHkBtn);
            hkRow.Children.Add(clearHkBtn);
            stack.Children.Add(hkRow);
            stack.Children.Add(new TextBlock { Text = "普通窗口直接截取客户区；全屏游戏截图为黑屏时自动回退到 Game Bar（需在系统设置→游戏中启用捕获）。", Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 0) });

            var btns = MakeButtons();
            ((Button)btns.Children[0]).Click += (s, e) =>
            {
                AppPrefs.SetString("Screenshot.RootDir", rootBox.Text.Trim());
                AppPrefs.SetInt("Screenshot.Hotkey", curHotkey);
                if (owner is MainWindow) ((MainWindow)owner).RefreshHotkeys();
                dlg.DialogResult = true; dlg.Close();
            };
            ((Button)btns.Children[1]).Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            stack.Children.Add(btns);

            return new TabItem { Header = " 截图 ", Content = Scroll(stack) };
        }

        // ---- shared helpers ----------------------------------------------------

        static StackPanel MakeButtons()
        {
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var ok = new Button { Content = "确定", Width = 72, Height = 28, FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "取消", Width = 72, Height = 28, FontSize = 12 };
            AppResources.StyleDialogButton(ok, true);
            AppResources.StyleDialogButton(cancel, false);
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            return btns;
        }

        static CheckBox MakeCb(string label, string key)
        {
            return new CheckBox
            {
                Content = label, Foreground = Brushes.White, FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0),
                IsChecked = MainWindow.ModuleVisible(key)
            };
        }

        static CheckBox MakeAreaCb(string label, string tip, string prefKey, bool defChecked, SolidColorBrush fg, bool enabled)
        {
            var cb = new CheckBox
            {
                Content = label, Foreground = fg, FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                IsChecked = AppPrefs.GetBool(prefKey, defChecked),
                IsEnabled = enabled, ToolTip = tip
            };
            ToolTipService.SetInitialShowDelay(cb, 250);
            ToolTipService.SetShowDuration(cb, 8000);
            ToolTipService.SetShowOnDisabled(cb, true);
            cb.IsHitTestVisible = true;
            return cb;
        }

        static TextBox MakeBox()
        {
            return new TextBox { FontSize = 12, Padding = new Thickness(8, 6, 8, 6), Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)), BorderThickness = new Thickness(1) };
        }

        static ScrollViewer Scroll(StackPanel stack)
        {
            return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0) };
        }

        static bool IsAutoStartEnabled()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                    return key != null && key.GetValue("OneBox") != null;
            }
            catch { return false; }
        }

        static void ToggleAutoStart(bool enable)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (enable) key.SetValue("OneBox", System.Reflection.Assembly.GetExecutingAssembly().Location);
                    else key.DeleteValue("OneBox", false);
                }
            }
            catch { }
        }
    }
}
