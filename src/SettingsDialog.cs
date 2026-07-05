using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace PowerAudioManager
{
    internal static class SettingsDialog
    {
        public static void Show(Window owner)
        {
            Show(owner, 0);
        }

        // openTab 参数：0=常规 1=板块 2=内存 3=翻译 4=截图 5=剪贴板 6=温度
        public static void Show(Window owner, int openTab)
        {
            var fg = new SolidColorBrush(Color.FromRgb(190, 188, 220));
            var lightText = new SolidColorBrush(Color.FromRgb(220, 218, 245));

            // 左侧垂直 tab 栏 + 右侧内容的布局，适配 7 个 tab
            var sideBar = new ListBox
            {
                Width = 100,
                Background = new SolidColorBrush(Color.FromRgb(32, 30, 48)),
                BorderBrush = Brushes.Transparent,
                Margin = new Thickness(0),
                Padding = new Thickness(0)
            };
            sideBar.ItemContainerStyle = SidebarItemStyle();
            sideBar.SelectionChanged += (s, e) =>
            {
                // 更新所有 item 的文字颜色（选中白，未选中灰）
                foreach (ListBoxItem item in sideBar.Items)
                {
                    var tb = item.Content as TextBlock;
                    if (tb != null)
                        tb.Foreground = item.IsSelected
                            ? Brushes.White
                            : new SolidColorBrush(Color.FromRgb(190, 188, 220));
                }
                if (sideBar.SelectedIndex >= 0)
                    _contentHost.Content = _tabContents[sideBar.SelectedIndex];
            };

            _contentHost = new ContentControl();

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(sideBar, 0);
            Grid.SetColumn(_contentHost, 1);
            layout.Children.Add(sideBar);
            layout.Children.Add(_contentHost);

            var dlg = OneBoxWindow.Create(owner, "设置", 500, 560, layout, true);

            // 构建所有 tab 内容
            _tabContents = new System.Collections.Generic.List<UIElement>
            {
                BuildGeneralTab(owner, dlg, fg, lightText),
                BuildModulesTab(owner, dlg, fg, lightText),
                BuildMemoryTab(owner, dlg, fg, lightText),
                BuildTranslateTab(owner, dlg, fg, lightText),
                BuildScreenshotTab(owner, dlg, fg, lightText),
                BuildClipboardTab(owner, dlg, fg, lightText),
                BuildTempTab(owner, dlg, fg, lightText),
            };

            // 填充侧栏
            sideBar.Items.Add(SidebarItem("  常规"));
            sideBar.Items.Add(SidebarItem("  板块"));
            sideBar.Items.Add(SidebarItem("  内存"));
            sideBar.Items.Add(SidebarItem("  翻译"));
            sideBar.Items.Add(SidebarItem("  截图"));
            sideBar.Items.Add(SidebarItem("  剪贴板"));
            sideBar.Items.Add(SidebarItem("  性能"));

            if (openTab >= 0 && openTab < sideBar.Items.Count)
            {
                sideBar.SelectedIndex = openTab;
                _contentHost.Content = _tabContents[openTab];
            }
            else
            {
                sideBar.SelectedIndex = 0;
                _contentHost.Content = _tabContents[0];
            }

            dlg.ShowDialog();
        }

        private static System.Collections.Generic.List<UIElement> _tabContents;
        private static ContentControl _contentHost;

        static ListBoxItem SidebarItem(string text)
        {
            return new ListBoxItem
            {
                Content = new TextBlock { Text = text, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(190, 188, 220)) },
                Height = 40,
                Padding = new Thickness(0)
            };
        }

        static Style SidebarItemStyle()
        {
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(ListBoxItem.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(ListBoxItem.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(12, 0, 12, 0)));
            style.Setters.Add(new Setter(ListBoxItem.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(ListBoxItem.CursorProperty, System.Windows.Input.Cursors.Hand));
            // 选中态：左侧紫条 + 深色底
            var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(42, 39, 60))));
            // 左侧指示条用 BorderThickness + BorderBrush 实现
            selectedTrigger.Setters.Add(new Setter(ListBoxItem.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(142, 140, 216))));
            selectedTrigger.Setters.Add(new Setter(ListBoxItem.BorderThicknessProperty, new Thickness(3, 0, 0, 0)));
            // 选中文字变白
            // 需要遍历设置 TextBlock.Foreground，这里用 DataTrigger 不行，直接用简单方法：在代码里更新
            style.Triggers.Add(selectedTrigger);

            var hoverTrigger = new Trigger { Property = ListBoxItem.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(50, 47, 70))));
            style.Triggers.Add(hoverTrigger);

            return style;
        }

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

            var scaleLbl = new TextBlock { Text = "窗口缩放", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
            stack.Children.Add(scaleLbl);
            var scaleRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            double curScale = 1.0;
            AppPrefs.GetDouble("WindowScale.Factor", out curScale);
            if (curScale < 0.8 || curScale > 2.0) curScale = 0; // 0 = auto
            bool isAuto = curScale == 0;
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
            stack.Children.Add(new TextBlock { Text = "拖动滑块调整窗口大小，或直接拖拽悬浮窗右下角。", Foreground = fg, FontSize = 10, Margin = new Thickness(0, 0, 0, 16) });

            stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(80, 75, 120)), Margin = new Thickness(0, 4, 0, 12) });

            var autoStartCb = new ComboBox
            {
                Foreground = Brushes.White,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 16),
                MinWidth = 180
            };
            autoStartCb.Items.Add("关闭");
            autoStartCb.Items.Add("注册表 (普通权限)");
            autoStartCb.Items.Add("计划任务 (管理员权限)");
            autoStartCb.Items.Add("服务 (SYSTEM 权限)");
            int curMethod = (int)AutoStartService.GetCurrent();
            autoStartCb.SelectedIndex = Math.Clamp(curMethod, 0, 3);
            var autoLbl = new TextBlock { Text = "开机自启方式", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
            stack.Children.Add(autoLbl);
            stack.Children.Add(autoStartCb);

            stack.Children.Add(new TextBlock { Text = "注册表：最简单，无额外权限。计划任务：以最高权限运行，无 UAC 弹窗。服务：SYSTEM 账户运行，开机即启。", Foreground = fg, FontSize = 10, Margin = new Thickness(0, 0, 0, 16), TextWrapping = TextWrapping.Wrap });

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
                var mw = owner as MainWindow;
                if (scaleAutoCb.IsChecked == true)
                { try { mw?._scaling?.ResetManualScale(); } catch { } }
                else
                { try { mw?._scaling?.ApplyManualScale(scaleSlider.Value / 100.0); } catch { } }

                var newMethod = (AutoStartMethod)(Math.Clamp(autoStartCb.SelectedIndex, 0, 3));
                if (newMethod != AutoStartService.GetCurrent())
                {
                    string err = AutoStartService.Enable(newMethod);
                    if (err != null)
                    {
                        System.Windows.MessageBox.Show(err, "开机自启", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                        // 恢复到实际当前状态
                        autoStartCb.SelectedIndex = (int)AutoStartService.GetCurrent();
                    }
                    else if (mw != null && mw._tray != null)
                    {
                        mw._tray.UpdateAutoStart();
                        // 服务/计划任务方式需要管理员权限;若非管理员则立即提权重启
                        if ((newMethod == AutoStartMethod.Service || newMethod == AutoStartMethod.ScheduledTask)
                            && !AdminUtils.IsAdmin())
                        {
                            AdminUtils.RestartAsAdmin();
                            return;
                        }
                    }
                }
                if (mw != null)
                {
                    mw.Topmost = topmostCb.IsChecked == true;
                    mw._topmost = topmostCb.IsChecked == true;
                    mw._lockPosition = lockCb.IsChecked == true;
                    if (mw._tray != null) mw._tray.SetLockChecked(mw._lockPosition);
                    if (mw._pinBtn != null)
                    {
                        mw._pinBtn.Content = MainWindow.PinIcon(mw._lockPosition);
                        mw._pinBtn.Foreground = new SolidColorBrush(mw._lockPosition ? MainWindow.AccentColor : MainWindow.TextSecondary);
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

        static ScrollViewer BuildModulesTab(Window owner, Window dlg, SolidColorBrush fg, SolidColorBrush lightText)
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
                if (owner is MainWindow) ((MainWindow)owner).RebuildUI();
                dlg.DialogResult = true; dlg.Close();
            };
            ((Button)btns.Children[1]).Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            stack.Children.Add(btns);

            return Scroll(stack);
        }

        static ScrollViewer BuildMemoryTab(Window owner, Window dlg, SolidColorBrush fg, SolidColorBrush lightText)
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

            // 带 * 的两项可能导致系统短暂卡顿，启用时弹窗确认（与 MemReduct 行为一致）。
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

            return Scroll(stack);
        }

        // 勾选"危险"清理项时弹确认框，用户取消则取消勾选。
        static void ConfirmIfDangerous(CheckBox cb, Window dlg, string message)
        {
            cb.Checked += (s, e) =>
            {
                var rc = MessageBox.Show(dlg, message, "提示", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (rc != MessageBoxResult.OK) cb.IsChecked = false;
            };
        }

        static ScrollViewer BuildTranslateTab(Window owner, Window dlg, SolidColorBrush fg, SolidColorBrush lightText)
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

            return Scroll(stack);
        }

        static ScrollViewer BuildScreenshotTab(Window owner, Window dlg, SolidColorBrush fg, SolidColorBrush lightText)
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

            // 高级：Game Bar 截图默认关闭。开启后启用 HDR 检测 + Game Bar 回退，HDR/全屏游戏截图不走黑。
            stack.Children.Add(new TextBlock { Text = "高级：Game Bar 截图（HDR / 全屏游戏）", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
            bool gbEnabled = AppPrefs.GetBool("Screenshot.GameBarEnabled", false);
            var gbToggle = new CheckBox { Content = "启用 Game Bar 截图回退（默认关闭，仅普通截图）", IsChecked = gbEnabled, Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
            stack.Children.Add(gbToggle);

            // Game Bar 配置仅在启用时有效，关闭开关时整体变灰。
            var gbPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
            gbPanel.IsEnabled = gbEnabled;

            gbPanel.Children.Add(new TextBlock { Text = "Game Bar 截图读取位置", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
            var gbRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
            var gbBox = new TextBox
            {
                MinHeight = 26, FontSize = 12,
                Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120))
            };
            string savedGb = AppPrefs.GetString("Screenshot.GameBarDir", "");
            if (string.IsNullOrWhiteSpace(savedGb))
                savedGb = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyVideos), "Captures");
            gbBox.Text = savedGb;
            var gbBrowseBtn = new Button { Content = "浏览…", Height = 26, FontSize = 12, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(gbBrowseBtn, false);
            gbBrowseBtn.Click += (s, e) =>
            {
                var fbd = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = gbBox.Text };
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    gbBox.Text = fbd.SelectedPath;
            };
            DockPanel.SetDock(gbBrowseBtn, Dock.Right);
            gbRow.Children.Add(gbBrowseBtn);
            gbRow.Children.Add(gbBox);
            gbPanel.Children.Add(gbRow);
            gbPanel.Children.Add(new TextBlock { Text = "Game Bar 生成截图后，从这里读取文件。若你的 Game Bar 图库位置被改过，请设为实际路径。留空则用默认的“视频\\Captures”。", Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });

            gbPanel.Children.Add(new TextBlock { Text = "Game Bar 截图快捷键", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
            int curGbHotkey = AppPrefs.GetInt("Screenshot.GameBarHotkey", 0);
            var gbHkLabel = new TextBlock
            {
                Text = curGbHotkey != 0 ? HotkeyCaptureDialog.Format(curGbHotkey) : "（未设置，用默认 Win+Alt+PrtScn）",
                Foreground = curGbHotkey != 0 ? Brushes.White : fg,
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            var gbHkSetBtn = new Button { Content = "设置快捷键", Height = 28, FontSize = 12, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(gbHkSetBtn, false);
            var gbHkClearBtn = new Button { Content = "清除", Height = 28, FontSize = 12, Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(gbHkClearBtn, false);
            gbHkSetBtn.Click += (s, e) =>
            {
                var captured = HotkeyCaptureDialog.Show(dlg, curGbHotkey);
                if (captured.HasValue)
                {
                    curGbHotkey = captured.Value;
                    gbHkLabel.Text = HotkeyCaptureDialog.Format(curGbHotkey);
                    gbHkLabel.Foreground = Brushes.White;
                }
            };
            gbHkClearBtn.Click += (s, e) =>
            {
                curGbHotkey = 0;
                gbHkLabel.Text = "（未设置，用默认 Win+Alt+PrtScn）";
                gbHkLabel.Foreground = fg;
            };
            var gbHkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            gbHkRow.Children.Add(gbHkLabel);
            gbHkRow.Children.Add(gbHkSetBtn);
            gbHkRow.Children.Add(gbHkClearBtn);
            gbPanel.Children.Add(gbHkRow);
            gbPanel.Children.Add(new TextBlock { Text = "游戏前台时系统会吞掉注入的 Win 键，导致默认 Win+Alt+PrtScn 触发不了 Game Bar。配置步骤：1) 先在这里点“设置快捷键”设一个不含 Win 的组合（如 Alt+F12）；2) 再去 Game Bar 设置里把截图快捷键改成同一个组合。注意：被 Game Bar 注册的组合在 OneBox 里按 Alt+键可能捕获不到，可改用 Ctrl+ 组合并在 Game Bar 里设同款。", Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });

            stack.Children.Add(gbPanel);
            gbToggle.Checked += (s, e) => gbPanel.IsEnabled = true;
            gbToggle.Unchecked += (s, e) => gbPanel.IsEnabled = false;

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
                    int enc = captured.Value;
                    bool ok = (owner is MainWindow) ? ((MainWindow)owner).TestHotkey(enc) : true;
                    if (ok)
                    {
                        curHotkey = enc;
                        hkLabel.Text = HotkeyCaptureDialog.Format(curHotkey);
                        hkLabel.Foreground = Brushes.White;
                    }
                    else
                    {
                        curHotkey = enc;
                        hkLabel.Text = HotkeyCaptureDialog.Format(curHotkey) + "（被占用）";
                        hkLabel.Foreground = new SolidColorBrush(Color.FromRgb(240, 170, 170));
                        MessageBox.Show(dlg, "该快捷键已被其他程序占用，OneBox 无法注册。\n你可以换一个组合，或先释放占用它的程序。", "快捷键被占用", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
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
            stack.Children.Add(new TextBlock { Text = "普通窗口直接截取客户区；全屏游戏截图为黑屏时自动回退到 Game Bar（需在系统设置→游戏中启用捕获）。", Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });

            stack.Children.Add(new TextBlock { Text = "图片翻译快捷键（框选截图→翻译）", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
            int curItHotkey = AppPrefs.GetInt("Screenshot.ImageTranslateHotkey", 0);
            var itHkLabel = new TextBlock
            {
                Text = curItHotkey != 0 ? HotkeyCaptureDialog.Format(curItHotkey) : "（未设置）",
                Foreground = curItHotkey != 0 ? Brushes.White : fg,
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            var itSetBtn = new Button { Content = "设置快捷键", Height = 28, FontSize = 12, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(itSetBtn, false);
            var itClearBtn = new Button { Content = "清除", Height = 28, FontSize = 12, Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(itClearBtn, false);
            itSetBtn.Click += (s, e) =>
            {
                var captured = HotkeyCaptureDialog.Show(dlg, curItHotkey);
                if (captured.HasValue)
                {
                    int enc = captured.Value;
                    bool ok = (owner is MainWindow) ? ((MainWindow)owner).TestHotkey(enc) : true;
                    if (ok)
                    {
                        curItHotkey = enc;
                        itHkLabel.Text = HotkeyCaptureDialog.Format(curItHotkey);
                        itHkLabel.Foreground = Brushes.White;
                    }
                    else
                    {
                        curItHotkey = enc;
                        itHkLabel.Text = HotkeyCaptureDialog.Format(curItHotkey) + "（被占用）";
                        itHkLabel.Foreground = new SolidColorBrush(Color.FromRgb(240, 170, 170));
                        MessageBox.Show(dlg, "该快捷键已被其他程序占用，OneBox 无法注册。\n你可以换一个组合，或先释放占用它的程序。", "快捷键被占用", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            };
            itClearBtn.Click += (s, e) =>
            {
                curItHotkey = 0;
                itHkLabel.Text = "（未设置）";
                itHkLabel.Foreground = fg;
            };
            var itHkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            itHkRow.Children.Add(itHkLabel);
            itHkRow.Children.Add(itSetBtn);
            itHkRow.Children.Add(itClearBtn);
            stack.Children.Add(itHkRow);
            stack.Children.Add(new TextBlock { Text = "按下后屏幕变暗，拖框选区，松开自动调用百度图片翻译（需先在翻译设置里配好 AppId/Key）。返回擦除原文、贴合译文的整图，可复制译文。", Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 0) });

            var btns = MakeButtons();
            ((Button)btns.Children[0]).Click += (s, e) =>
            {
                AppPrefs.SetString("Screenshot.RootDir", rootBox.Text.Trim());
                AppPrefs.SetBool("Screenshot.GameBarEnabled", gbToggle.IsChecked == true);
                AppPrefs.SetString("Screenshot.GameBarDir", gbBox.Text.Trim());
                AppPrefs.SetInt("Screenshot.GameBarHotkey", curGbHotkey);
                AppPrefs.SetInt("Screenshot.Hotkey", curHotkey);
                AppPrefs.SetInt("Screenshot.ImageTranslateHotkey", curItHotkey);
                if (owner is MainWindow) { ((MainWindow)owner).RefreshHotkeys(); ((MainWindow)owner).RebuildUI(); }
                dlg.DialogResult = true; dlg.Close();
            };
            ((Button)btns.Children[1]).Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            stack.Children.Add(btns);

            return Scroll(stack);
        }

        static ScrollViewer BuildClipboardTab(Window owner, Window dlg, SolidColorBrush fg, SolidColorBrush lightText)
        {
            var stack = new StackPanel { Margin = new Thickness(20) };

            stack.Children.Add(new TextBlock { Text = "剪贴板历史快捷键", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
            int curClipHk = AppPrefs.GetInt("Clipboard.Hotkey", 0);
            var clipHkLabel = new TextBlock
            {
                Text = curClipHk != 0 ? HotkeyCaptureDialog.Format(curClipHk) : "（未设置）",
                Foreground = curClipHk != 0 ? Brushes.White : fg,
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            var setClipHkBtn = new Button { Content = "设置快捷键", Height = 28, FontSize = 12, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(setClipHkBtn, false);
            var clearClipHkBtn = new Button { Content = "清除", Height = 28, FontSize = 12, Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(clearClipHkBtn, false);
            setClipHkBtn.Click += (s, e) =>
            {
                var captured = HotkeyCaptureDialog.Show(dlg, curClipHk);
                if (captured.HasValue)
                {
                    int enc = captured.Value;
                    // 立即测试注册，检测快捷键是否被其他程序占用，避免绑定后静默失败。
                    bool ok = (owner is MainWindow) ? ((MainWindow)owner).TestHotkey(enc) : true;
                    if (ok)
                    {
                        curClipHk = enc;
                        clipHkLabel.Text = HotkeyCaptureDialog.Format(curClipHk);
                        clipHkLabel.Foreground = Brushes.White;
                    }
                    else
                    {
                        // 仍显示按键但标注被占用。
                        curClipHk = enc;
                        clipHkLabel.Text = HotkeyCaptureDialog.Format(curClipHk) + "（被占用）";
                        clipHkLabel.Foreground = new SolidColorBrush(Color.FromRgb(240, 170, 170));
                        MessageBox.Show(dlg, "该快捷键已被其他程序占用，OneBox 无法注册。\n你可以换一个组合，或先释放占用它的程序。", "快捷键被占用", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            };
            clearClipHkBtn.Click += (s, e) =>
            {
                curClipHk = 0;
                clipHkLabel.Text = "（未设置）";
                clipHkLabel.Foreground = fg;
            };
            var clipHkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            clipHkRow.Children.Add(clipHkLabel);
            clipHkRow.Children.Add(setClipHkBtn);
            clipHkRow.Children.Add(clearClipHkBtn);
            stack.Children.Add(clipHkRow);
            stack.Children.Add(new TextBlock { Text = "按下快捷键从鼠标位置弹出剪贴板历史。左键复制，右键删除单条。", Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 0) });

            var btns = MakeButtons();
            ((Button)btns.Children[0]).Click += (s, e) =>
            {
                AppPrefs.SetInt("Clipboard.Hotkey", curClipHk);
                if (owner is MainWindow) ((MainWindow)owner).RefreshHotkeys();
                dlg.DialogResult = true; dlg.Close();
            };
            ((Button)btns.Children[1]).Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            stack.Children.Add(btns);

            return Scroll(stack);
        }

        static ScrollViewer BuildTempTab(Window owner, Window dlg, SolidColorBrush fg, SolidColorBrush lightText)
        {
            var stack = new StackPanel { Margin = new Thickness(20) };
            var hw = HardwareMonitorService.Instance;

            stack.Children.Add(new TextBlock { Text = "性能监控", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 10) });

            // ---- 当前指标列表 ----
            var metricList = new StackPanel(); // 动态填充
            RefreshMetricList(metricList, hw, fg);

            stack.Children.Add(new TextBlock { Text = "已添加的指标：", Foreground = fg, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) });
            stack.Children.Add(metricList);

            // ---- 添加指标 ----
            var addPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var addBtn = new Button { Content = "+ 添加指标", Height = 28, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(addBtn, false);
            addBtn.Click += (s, e) =>
            {
                addPanel.Children.Clear();
                addPanel.Children.Add(BuildAddForm(metricList, addPanel, hw, fg));
            };
            stack.Children.Add(addBtn);
            stack.Children.Add(addPanel);

            stack.Children.Add(new TextBlock { Text = $"已发现 {hw.AllTempSensors.Count} 个温度传感器 · {hw.AllFanSensors.Count} 个风扇传感器", Foreground = fg, FontSize = 10, Margin = new Thickness(0, 10, 0, 12) });

            // ---- 更新 + 阈值 ----
            stack.Children.Add(new TextBlock { Text = "更新间隔", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
            var intervalRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var intervalBox = new TextBox { Width = 60, MinHeight = 24, Text = AppPrefs.GetInt("Temp.IntervalMs", 1000).ToString(), Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)), VerticalContentAlignment = VerticalAlignment.Center };
            intervalRow.Children.Add(intervalBox);
            intervalRow.Children.Add(new TextBlock { Text = " ms (500-60000)", Foreground = fg, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(intervalRow);

            var warnRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var warnBox = new TextBox { Width = 60, MinHeight = 24, Text = AppPrefs.GetInt("Temp.WarnC", 80).ToString(), Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)), VerticalContentAlignment = VerticalAlignment.Center };
            warnRow.Children.Add(warnBox);
            warnRow.Children.Add(new TextBlock { Text = " °C 橙色", Foreground = fg, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(warnRow);

            var critRow = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            var critBox = new TextBox { Width = 60, MinHeight = 24, Text = AppPrefs.GetInt("Temp.CriticalC", 95).ToString(), Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)), VerticalContentAlignment = VerticalAlignment.Center };
            critRow.Children.Add(critBox);
            critRow.Children.Add(new TextBlock { Text = " °C 红色", Foreground = fg, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(critRow);

            var btns = MakeButtons();
            ((Button)btns.Children[0]).Click += (s, e) =>
            {
                int iv; if (int.TryParse(intervalBox.Text, out iv) && iv >= 500 && iv <= 60000) AppPrefs.SetInt("Temp.IntervalMs", iv);
                int w; if (int.TryParse(warnBox.Text, out w) && w > 0) AppPrefs.SetInt("Temp.WarnC", w);
                int c; if (int.TryParse(critBox.Text, out c) && c > 0) AppPrefs.SetInt("Temp.CriticalC", c);
                if (owner is MainWindow mw) mw.RestartTempTimer();
                dlg.DialogResult = true; dlg.Close();
            };
            ((Button)btns.Children[1]).Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            stack.Children.Add(btns);

            return Scroll(stack);
        }

        static void RefreshMetricList(StackPanel list, HardwareMonitorService hw, SolidColorBrush fg)
        {
            list.Children.Clear();
            foreach (var key in hw.EnabledMetrics)
            {
                var cfg = HardwareMonitorService.DecodeConfig(key);
                if (cfg == null) continue;
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), LastChildFill = true };
                string icon = HardwareMonitorService.AutoIcon(cfg);
                string label = cfg.SensorType == SensorType.Fan ? "风扇" : "温度";
                row.Children.Add(new TextBlock { Text = $"{icon}  {label}  —  {cfg.HardwareName}  »  {cfg.SensorName}", Foreground = fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });

                var delBtn = new Button { Content = "✕", Width = 22, Height = 22, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Foreground = fg, FontSize = 11, Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(0) };
                MainWindow.ApplyFlatStyle(delBtn);
                delBtn.MinWidth = 0; delBtn.MinHeight = 0;
                string capturedKey = key;
                var capturedList = list;
                delBtn.Click += (s2, e2) =>
                {
                    var updated = new List<string>(hw.EnabledMetrics);
                    updated.Remove(capturedKey);
                    hw.SaveEnabledMetrics(updated);
                    RefreshMetricList(capturedList, hw, fg);
                };
                DockPanel.SetDock(delBtn, Dock.Right);
                row.Children.Add(delBtn);

                list.Children.Add(row);
            }
            if (hw.EnabledMetrics.Count == 0)
                list.Children.Add(new TextBlock { Text = "(无指标)", Foreground = fg, FontSize = 11, FontStyle = FontStyles.Italic });
        }

        static UIElement BuildAddForm(StackPanel metricList, StackPanel addPanel, HardwareMonitorService hw, SolidColorBrush fg)
        {
            var form = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            // 类型选择
            form.Children.Add(new TextBlock { Text = "类型", Foreground = fg, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
            var typeCombo = new ComboBox { Height = 26, FontSize = 11, Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = new SolidColorBrush(Color.FromRgb(220, 218, 245)), BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)) };
            AppResources.StyleDarkComboBox(typeCombo);
            typeCombo.Items.Add(new ComboBoxItem { Content = "温度", Tag = "Temp" });
            typeCombo.Items.Add(new ComboBoxItem { Content = "风扇转速", Tag = "Fan" });
            typeCombo.SelectedIndex = 0;
            form.Children.Add(typeCombo);

            // 传感器选择
            form.Children.Add(new TextBlock { Text = "传感器", Foreground = fg, FontSize = 11, Margin = new Thickness(0, 6, 0, 2) });
            var sensorCombo = new ComboBox { Height = 26, FontSize = 11, Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = new SolidColorBrush(Color.FromRgb(220, 218, 245)), BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)) };
            AppResources.StyleDarkComboBox(sensorCombo);
            form.Children.Add(sensorCombo);

            void PopulateSensors()
            {
                sensorCombo.Items.Clear();
                bool isFan = ((typeCombo.SelectedItem as ComboBoxItem)?.Tag as string) == "Fan";
                var pool = isFan ? hw.AllFanSensors : hw.AllTempSensors;
                if (pool.Count == 0)
                {
                    sensorCombo.Items.Add(new ComboBoxItem { Content = "(无可用传感器)", Tag = null });
                }
                else
                {
                    foreach (var s in pool)
                    {
                        string icon = HardwareMonitorService.AutoIcon(s);
                        sensorCombo.Items.Add(new ComboBoxItem { Content = $"{icon} {s}", Tag = HardwareMonitorService.EncodeConfig(s) });
                    }
                }
                sensorCombo.SelectedIndex = pool.Count > 0 ? 0 : -1;
            }
            PopulateSensors();
            typeCombo.SelectionChanged += (_, _) => PopulateSensors();

            // 按钮行
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var confirmBtn = new Button { Content = "确认添加", Height = 26, FontSize = 11, Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(confirmBtn, true);
            var cancelBtn = new Button { Content = "取消", Height = 26, FontSize = 11, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(8, 0, 0, 0) };
            AppResources.StyleDialogButton(cancelBtn, false);
            btnRow.Children.Add(confirmBtn);
            btnRow.Children.Add(cancelBtn);
            form.Children.Add(btnRow);

            confirmBtn.Click += (_, _) =>
            {
                var key = (sensorCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                if (!string.IsNullOrEmpty(key))
                {
                    var updated = new List<string>(hw.EnabledMetrics);
                    if (!updated.Contains(key))
                    {
                        updated.Add(key);
                        hw.SaveEnabledMetrics(updated);
                    }
                }
                RefreshMetricList(metricList, hw, fg);
                addPanel.Children.Clear();
            };
            cancelBtn.Click += (_, _) => addPanel.Children.Clear();

            return form;
        }

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

    }
}
