using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using Microsoft.Win32;

namespace PowerAudioManager
{
    public static class HotkeyCaptureDialog
    {
        // Returns: high 16 bits = modifiers (bit0 Alt, bit1 Ctrl, bit2 Shift, bit3 Win),
        //          low 16 bits = VK code. 0 = none.
        public static int? Show(Window owner, int currentEncoded)
        {
            int captured = currentEncoded;
            var dlg = new Window {
                Title = "设置快捷键",
                Width = 320, Height = 160,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                FontFamily = owner.FontFamily,
                Background = new SolidColorBrush(Color.FromRgb(32,32,32))
            };
            var stack = new StackPanel { Margin = new Thickness(16) };
            var hint = new TextBlock { Text = "请按下组合键，按 Esc 取消", Foreground = new SolidColorBrush(Color.FromRgb(180,180,180)), FontSize = 12, Margin = new Thickness(0,0,0,12) };
            var display = new TextBlock {
                Text = currentEncoded != 0 ? Format(currentEncoded) : "(请按键)",
                FontSize = 18, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,0,0,12)
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "确定", Width = 64, Height = 28, Margin = new Thickness(0,0,8,0) };
            var cancel = new Button { Content = "取消", Width = 64, Height = 28 };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            stack.Children.Add(hint); stack.Children.Add(display); stack.Children.Add(buttons);
            dlg.Content = stack;

            dlg.PreviewKeyDown += (s, e) => {
                if (e.Key == Key.Escape) { captured = 0; dlg.DialogResult = false; dlg.Close(); return; }
                if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
                    e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
                    e.Key == Key.LeftShift || e.Key == Key.RightShift ||
                    e.Key == Key.System || e.Key == Key.LWin || e.Key == Key.RWin) return;
                int mods = 0;
                if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= 1;
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= 2;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= 4;
                if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods |= 8;
                if (mods == 0) return; // require at least one modifier
                int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
                if (vk == 0) return;
                captured = (mods << 16) | (vk & 0xFFFF);
                display.Text = Format(captured);
                e.Handled = true;
            };
            ok.Click += (s, e) => { dlg.DialogResult = true; dlg.Close(); };
            cancel.Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };

            bool? result = dlg.ShowDialog();
            if (result == true && captured != 0) return captured;
            return null;
        }

        public static string Format(int encoded)
        {
            if (encoded == 0) return "(无)";
            int mods = (encoded >> 16) & 0xFFFF;
            int vk = encoded & 0xFFFF;
            var parts = new List<string>();
            if ((mods & 2) != 0) parts.Add("Ctrl");
            if ((mods & 1) != 0) parts.Add("Alt");
            if ((mods & 4) != 0) parts.Add("Shift");
            if ((mods & 8) != 0) parts.Add("Win");
            try { parts.Add(KeyInterop.KeyFromVirtualKey(vk).ToString()); } catch { parts.Add("?"); }
            return string.Join("+", parts.ToArray());
        }
    }

    public static class CleanerSettingsDialog
    {
        // Helpers for the per-area checkbox + tooltip
        static CheckBox MakeAreaCb(string label, string tip, string prefKey, bool defChecked, SolidColorBrush fg, bool enabled)
        {
            var cb = new CheckBox {
                Content = label,
                Foreground = fg,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                IsChecked = AppPrefs.GetBool(prefKey, defChecked),
                IsEnabled = enabled,
                ToolTip = tip
            };
            ToolTipService.SetInitialShowDelay(cb, 250);
            ToolTipService.SetShowDuration(cb, 8000);
            // Allow tooltip to show even when the checkbox is disabled (default WPF behavior is to suppress).
            ToolTipService.SetShowOnDisabled(cb, true);
            cb.IsHitTestVisible = true;
            return cb;
        }

        public static void Show(Window owner)
        {
            var dlg = new Window {
                Title = "内存清理设置",
                Width = 420, Height = 600,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                FontFamily = owner.FontFamily,
                Background = new SolidColorBrush(Color.FromRgb(28, 26, 40))
            };
            var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stack = new StackPanel { Margin = new Thickness(20) };
            scroller.Content = stack;
            var fg = new SolidColorBrush(Color.FromRgb(190, 188, 220));
            bool isAdmin = AdminUtils.IsAdmin();

            // Admin status banner
            var adminBanner = new Border {
                Background = new SolidColorBrush(isAdmin ? Color.FromRgb(40, 60, 50) : Color.FromRgb(70, 50, 50)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var adminRow = new DockPanel { LastChildFill = true };
            var adminText = new TextBlock {
                Text = isAdmin ? "已以管理员身份运行：所有清理项可用" : "当前未以管理员身份运行：部分项需要管理员权限",
                Foreground = Brushes.White,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            adminRow.Children.Add(adminText);
            if (!isAdmin)
            {
                var elevateBtn = new Button {
                    Content = "以管理员重启",
                    Padding = new Thickness(8, 2, 8, 2),
                    FontSize = 11
                };
                DockPanel.SetDock(elevateBtn, Dock.Right);
                elevateBtn.Click += (s, e) => { AdminUtils.RestartAsAdmin(); };
                adminRow.Children.Add(elevateBtn);
            }
            adminBanner.Child = adminRow;
            stack.Children.Add(adminBanner);

            // Section: 自动清理触发
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

            // Section: 清理区域
            stack.Children.Add(new TextBlock {
                Text = "要清理的内存区域",
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(0, 8, 0, 4)
            });

            var cbWS    = MakeAreaCb("Working set", "释放各进程的工作集（已加载到物理内存的代码与数据），把未使用的页面交还系统。", "Clean.WorkingSet", true, fg, true);
            var cbSFC   = MakeAreaCb("System file cache", "归还系统文件缓存：Windows 用来加速文件读取的内存被释放回可用池。", "Clean.SystemFileCache", true, fg, true);
            var cbMPL   = MakeAreaCb("Modified page list*", "把已修改但尚未写回磁盘的脏页刷盘后转入可用列表。* 需要管理员权限。", "Clean.ModifiedPageList", false, fg, isAdmin);
            var cbSL    = MakeAreaCb("Standby list*", "清空整个 standby（备用）列表，包括所有优先级缓存的页面。* 需要管理员权限。", "Clean.StandbyList", false, fg, isAdmin);
            var cbSLNP  = MakeAreaCb("Standby list (without priority)", "只清理低优先级的 standby 页（影响小、释放慢但稳定）。", "Clean.StandbyListNoPrio", true, fg, true);
            var cbMFC   = MakeAreaCb("Modified file cache", "刷新已修改的文件缓存页（与 Modified page list 的非分页部分对应）。", "Clean.ModifiedFileCache", true, fg, true);
            var cbReg   = MakeAreaCb("Registry cache (win8.1+)", "Windows 8.1 及以上：归还注册表配置单元的缓存内存。", "Clean.RegistryCache", true, fg, AdminUtils.RealOsVersion() >= new Version(6, 3));
            var cbCML   = MakeAreaCb("Combine memory lists (win10+)", "Windows 10 及以上：合并相同内容的物理内存页（内存压缩 / 共享）。", "Clean.CombineMemoryLists", false, fg, AdminUtils.RealOsVersion().Major >= 10);

            stack.Children.Add(cbWS);
            stack.Children.Add(cbSFC);
            stack.Children.Add(cbMPL);
            stack.Children.Add(cbSL);
            stack.Children.Add(cbSLNP);
            stack.Children.Add(cbMFC);
            stack.Children.Add(cbReg);
            stack.Children.Add(cbCML);
            stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(80, 75, 120)), Margin = new Thickness(0, 14, 0, 12) });

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "确定", Width = 64, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "取消", Width = 64, Height = 28 };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            stack.Children.Add(btns);
            dlg.Content = scroller;

            ok.Click += (s, e) => {
                AppPrefs.SetBool("AutoCleanEnabled", enableCb.IsChecked == true);
                AppPrefs.SetBool("AutoCleanByTime", byTimeCb.IsChecked == true);
                AppPrefs.SetBool("AutoCleanByThreshold", byThCb.IsChecked == true);
                int n; if (int.TryParse(timeBox.Text, out n) && n > 0) AppPrefs.SetDouble("AutoCleanMinutes", n);
                int t; if (int.TryParse(thBox.Text, out t) && t > 0 && t <= 100) AppPrefs.SetDouble("AutoCleanThreshold", t);
                AppPrefs.SetBool("Clean.WorkingSet",        cbWS.IsChecked == true);
                AppPrefs.SetBool("Clean.SystemFileCache",   cbSFC.IsChecked == true);
                AppPrefs.SetBool("Clean.ModifiedPageList",  cbMPL.IsChecked == true);
                AppPrefs.SetBool("Clean.StandbyList",       cbSL.IsChecked == true);
                AppPrefs.SetBool("Clean.StandbyListNoPrio", cbSLNP.IsChecked == true);
                AppPrefs.SetBool("Clean.ModifiedFileCache", cbMFC.IsChecked == true);
                AppPrefs.SetBool("Clean.RegistryCache",     cbReg.IsChecked == true);
                AppPrefs.SetBool("Clean.CombineMemoryLists",cbCML.IsChecked == true);
                if (owner is MainWindow) ((MainWindow)owner).RestartAutoCleanTimer();
                dlg.DialogResult = true; dlg.Close();
            };
            cancel.Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            dlg.ShowDialog();
        }

        public static MemoryCleaner.CleanFlags GetSavedFlags()
        {
            var f = MemoryCleaner.CleanFlags.None;
            if (AppPrefs.GetBool("Clean.WorkingSet",        true))  f |= MemoryCleaner.CleanFlags.WorkingSet;
            if (AppPrefs.GetBool("Clean.SystemFileCache",   true))  f |= MemoryCleaner.CleanFlags.SystemFileCache;
            if (AppPrefs.GetBool("Clean.ModifiedPageList",  false)) f |= MemoryCleaner.CleanFlags.ModifiedPageList;
            if (AppPrefs.GetBool("Clean.StandbyList",       false)) f |= MemoryCleaner.CleanFlags.StandbyList;
            if (AppPrefs.GetBool("Clean.StandbyListNoPrio", true))  f |= MemoryCleaner.CleanFlags.StandbyListNoPrio;
            if (AppPrefs.GetBool("Clean.ModifiedFileCache", true))  f |= MemoryCleaner.CleanFlags.ModifiedFileCache;
            if (AppPrefs.GetBool("Clean.RegistryCache",     true))  f |= MemoryCleaner.CleanFlags.RegistryCache;
            if (AppPrefs.GetBool("Clean.CombineMemoryLists",false)) f |= MemoryCleaner.CleanFlags.CombineMemoryLists;
            if (f == MemoryCleaner.CleanFlags.None) f = MemoryCleaner.CleanFlags.Default;
            return f;
        }
    }

    public class TranslateWindow : Window
    {
        TextBox _input, _output;
        ComboBox _fromBox, _toBox;
        TextBlock _statusBlock;
        Button _btnGo, _btnCopy, _btnSwap, _btnSettings;

        public TranslateWindow()
        {
            Title = "OneBox · 翻译";
            Width = 720; Height = 520;
            MinWidth = 460; MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromRgb(28, 26, 40));
            BuildUI();
            // Restore last position
            double sl, st, sw, sh;
            if (AppPrefs.GetDouble("Translate.Left", out sl) && AppPrefs.GetDouble("Translate.Top", out st)) { Left = sl; Top = st; }
            if (AppPrefs.GetDouble("Translate.Width", out sw) && sw > 300) Width = sw;
            if (AppPrefs.GetDouble("Translate.Height", out sh) && sh > 200) Height = sh;
            LocationChanged += (s, e) => { if (IsLoaded) { AppPrefs.SetDouble("Translate.Left", Left); AppPrefs.SetDouble("Translate.Top", Top); } };
            SizeChanged += (s, e) => { if (IsLoaded) { AppPrefs.SetDouble("Translate.Width", Width); AppPrefs.SetDouble("Translate.Height", Height); } };
            // Recover from display config changes (4K -> 1080p, monitor unplug, DPI scale change).
            EventHandler displayHandler = (ss, ee) =>
            {
                try { Dispatcher.BeginInvoke(new Action(ClampToWorkArea)); } catch { }
            };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += displayHandler;
            this.Closed += (s, e) =>
            {
                try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= displayHandler; } catch { }
            };
            this.Loaded += (s, e) => ClampToWorkArea();
        }

        void ClampToWorkArea()
        {
            try
            {
                var wa = SystemParameters.WorkArea;
                double w = ActualWidth > 0 ? ActualWidth : Width;
                double h = ActualHeight > 0 ? ActualHeight : Height;
                if (double.IsNaN(w) || w <= 0) w = 720;
                if (double.IsNaN(h) || h <= 0) h = 520;
                double left = Left;
                double top = Top;
                bool offscreen = double.IsNaN(left) || double.IsNaN(top)
                    || left + w <= wa.Left + 8 || left >= wa.Right - 8
                    || top + h <= wa.Top + 8  || top >= wa.Bottom - 8;
                if (offscreen)
                {
                    Left = wa.Left + Math.Max(0, (wa.Width - w) / 2);
                    Top  = wa.Top  + Math.Max(0, (wa.Height - h) / 2);
                    return;
                }
                // Shrink the window if the new work area is smaller than its restored size.
                if (w > wa.Width)  { Width  = wa.Width;  w = wa.Width; }
                if (h > wa.Height) { Height = wa.Height; h = wa.Height; }
                if (left + w > wa.Right)  left = wa.Right - w;
                if (top  + h > wa.Bottom) top  = wa.Bottom - h;
                if (left < wa.Left) left = wa.Left;
                if (top  < wa.Top)  top  = wa.Top;
                if (left != Left) Left = left;
                if (top  != Top)  Top  = top;
            }
            catch { }
        }

        void BuildUI()
        {
            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Custom title bar (OneBox style)
            var titleBar = new DockPanel {
                Background = new SolidColorBrush(Color.FromRgb(34, 32, 50)),
                Height = 36,
                LastChildFill = true
            };
            var titleText = new TextBlock {
                Text = "  \uD83D\uDCDD  OneBox · 翻译",
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            var closeBtn = new Button {
                Content = "\u2715",
                Width = 36, Height = 36,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(190, 188, 220)),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = "关闭"
            };
            closeBtn.Click += (s, e) => Close();
            DockPanel.SetDock(closeBtn, Dock.Right);
            titleBar.Children.Add(closeBtn);
            titleBar.Children.Add(titleText);
            titleBar.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
            Grid.SetRow(titleBar, 0); rootGrid.Children.Add(titleBar);

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(grid, 1); rootGrid.Children.Add(grid);

            // Top toolbar
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = false };
            _fromBox = MakeLangBox(true);
            _toBox = MakeLangBox(false);
            _btnSwap = new Button { Content = "\u21C4", Width = 32, Height = 28, FontSize = 14, Margin = new Thickness(4, 0, 4, 0), ToolTip = "交换源/目标语言" };
            _btnSwap.Click += (s, e) => SwapLanguages();
            DockPanel.SetDock(_fromBox, Dock.Left);
            DockPanel.SetDock(_btnSwap, Dock.Left);
            DockPanel.SetDock(_toBox, Dock.Left);
            bar.Children.Add(_fromBox);
            bar.Children.Add(_btnSwap);
            bar.Children.Add(_toBox);
            _btnGo = new Button { Content = "翻译", Width = 80, Height = 28, FontSize = 12 };
            _btnGo.Click += (s, e) => RunTranslation(_input.Text);
            DockPanel.SetDock(_btnGo, Dock.Right);
            bar.Children.Add(_btnGo);
            _btnSettings = new Button { Content = "\u2699", Width = 32, Height = 28, FontSize = 14, Margin = new Thickness(0, 0, 4, 0), ToolTip = "翻译 API 设置" };
            _btnSettings.Click += (s, e) =>
            {
                if (TranslateSettingsDialog.Show(this))
                {
                    if (_statusBlock != null) _statusBlock.Text = "已保存设置";
                }
            };
            DockPanel.SetDock(_btnSettings, Dock.Right);
            bar.Children.Add(_btnSettings);
            Grid.SetRow(bar, 0); grid.Children.Add(bar);

            _input = MakeBox(false, "在此输入或粘贴文本，按 Ctrl+Enter 翻译");
            _input.PreviewKeyDown += (s, e) => {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                { e.Handled = true; RunTranslation(_input.Text); }
            };
            Grid.SetRow(_input, 1); grid.Children.Add(_input);

            // Status / actions row
            var midBar = new DockPanel { Margin = new Thickness(0, 8, 0, 8), LastChildFill = true };
            _statusBlock = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(190, 188, 220)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            _btnCopy = new Button { Content = "复制译文", Width = 80, Height = 28, FontSize = 12 };
            _btnCopy.Click += (s, e) => { try { if (!string.IsNullOrEmpty(_output.Text)) Clipboard.SetText(_output.Text); _statusBlock.Text = "已复制"; } catch { } };
            DockPanel.SetDock(_btnCopy, Dock.Right);
            midBar.Children.Add(_btnCopy);
            midBar.Children.Add(_statusBlock);
            Grid.SetRow(midBar, 2); grid.Children.Add(midBar);

            _output = MakeBox(true, "");
            Grid.SetRow(_output, 3); grid.Children.Add(_output);

            var foot = new TextBlock { Text = "由百度翻译提供 · Ctrl+Enter 触发翻译 · Ctrl+Shift+T 全局翻译剪贴板", Foreground = new SolidColorBrush(Color.FromRgb(140, 138, 180)), FontSize = 10, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetRow(foot, 4); grid.Children.Add(foot);

            // Outer border for visual finish
            var border = new Border {
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)),
                BorderThickness = new Thickness(1),
                Child = rootGrid
            };
            Content = border;
        }

        TextBox MakeBox(bool readOnly, string placeholder)
        {
            return new TextBox {
                IsReadOnly = readOnly,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontSize = 13,
                Padding = new Thickness(8),
                Background = new SolidColorBrush(readOnly ? Color.FromRgb(24, 22, 36) : Color.FromRgb(42, 39, 60)),
                Foreground = readOnly ? new SolidColorBrush(Color.FromRgb(220, 218, 245)) : Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)),
                BorderThickness = new Thickness(1),
                ToolTip = placeholder
            };
        }

        ComboBox MakeLangBox(bool isFrom)
        {
            var cb = new ComboBox {
                Width = 110, Height = 28,
                FontSize = 12,
                Background = Brushes.White,
                Foreground = Brushes.Black,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120))
            };
            // Force any TextBlock inside the ComboBox (selected-display + popup items) to black on white.
            var tbStyle = new Style(typeof(TextBlock));
            tbStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Black));
            cb.Resources.Add(typeof(TextBlock), tbStyle);
            string[][] codes = isFrom
                ? new[] { new[] { "auto","自动检测" }, new[] { "zh","中文" }, new[] { "en","英语" }, new[] { "jp","日语" }, new[] { "kor","韩语" }, new[] { "fra","法语" }, new[] { "de","德语" }, new[] { "ru","俄语" }, new[] { "spa","西班牙语" }, new[] { "ara","阿拉伯语" } }
                : new[] { new[] { "zh","中文" }, new[] { "en","英语" }, new[] { "jp","日语" }, new[] { "kor","韩语" }, new[] { "fra","法语" }, new[] { "de","德语" }, new[] { "ru","俄语" }, new[] { "spa","西班牙语" }, new[] { "ara","阿拉伯语" } };
            foreach (var p in codes)
            {
                cb.Items.Add(new ComboBoxItem
                {
                    Content = p[1],
                    Tag = p[0],
                    Foreground = Brushes.Black,
                    Background = Brushes.White,
                    Padding = new Thickness(8, 4, 8, 4)
                });
            }
            // Make sure the dropdown popup itself has a dark background and visible items.
            // WPF defaults paint items on system white, ignoring item.Background. We override the
            // hosted ItemsPresenter via a Style with template trigger.
            var itemStyle = new Style(typeof(ComboBoxItem));
            itemStyle.Setters.Add(new Setter(ComboBoxItem.BackgroundProperty, Brushes.White));
            itemStyle.Setters.Add(new Setter(ComboBoxItem.ForegroundProperty, Brushes.Black));
            itemStyle.Setters.Add(new Setter(ComboBoxItem.PaddingProperty, new Thickness(8, 4, 8, 4)));
            var hover = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
            hover.Setters.Add(new Setter(ComboBoxItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(220, 215, 245))));
            hover.Setters.Add(new Setter(ComboBoxItem.ForegroundProperty, Brushes.Black));
            itemStyle.Triggers.Add(hover);
            cb.ItemContainerStyle = itemStyle;
            string saved;
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PowerAudioManager\App"))
                saved = k == null ? null : (k.GetValue(isFrom ? "Translate.From" : "Translate.To") as string);
            int idx = 0;
            for (int i = 0; i < codes.Length; i++) if (codes[i][0] == saved) { idx = i; break; }
            if (saved == null) idx = isFrom ? 0 : 0; // auto / zh
            cb.SelectedIndex = idx;
            cb.SelectionChanged += (s, e) => {
                var item = cb.SelectedItem as ComboBoxItem;
                if (item != null)
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\PowerAudioManager\App"))
                        k.SetValue(isFrom ? "Translate.From" : "Translate.To", item.Tag.ToString());
                }
            };
            return cb;
        }

        void SwapLanguages()
        {
            var fItem = _fromBox.SelectedItem as ComboBoxItem;
            var tItem = _toBox.SelectedItem as ComboBoxItem;
            if (fItem == null || tItem == null) return;
            string fTag = fItem.Tag.ToString();
            if (fTag == "auto") return; // cannot put auto on the right side
            // Find matching items
            foreach (ComboBoxItem ci in _fromBox.Items) if ((ci.Tag as string) == tItem.Tag.ToString()) { _fromBox.SelectedItem = ci; break; }
            foreach (ComboBoxItem ci in _toBox.Items) if ((ci.Tag as string) == fTag) { _toBox.SelectedItem = ci; break; }
            if (!string.IsNullOrEmpty(_output.Text))
            {
                var temp = _input.Text;
                _input.Text = _output.Text;
                _output.Text = temp;
            }
        }

        public void RunTranslation(string text)
        {
            if (text == null) text = "";
            _input.Text = text;
            if (string.IsNullOrEmpty(text)) { _output.Text = ""; return; }
            var fItem = _fromBox.SelectedItem as ComboBoxItem;
            var tItem = _toBox.SelectedItem as ComboBoxItem;
            string from = fItem == null ? "auto" : fItem.Tag.ToString();
            string to = tItem == null ? "zh" : tItem.Tag.ToString();
            _statusBlock.Text = "翻译中...";
            _btnGo.IsEnabled = false;
            _output.Text = "";
            System.Threading.ThreadPool.QueueUserWorkItem(state =>
            {
                TranslateService.Result r = null;
                Exception err = null;
                try { r = TranslateService.Translate(text, from, to); }
                catch (Exception ex) { err = ex; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _btnGo.IsEnabled = true;
                    if (err != null) { _output.Text = ""; _statusBlock.Text = "失败: " + err.Message; return; }
                    if (r != null && !string.IsNullOrEmpty(r.Error)) { _output.Text = ""; _statusBlock.Text = "失败: " + r.Error; }
                    else { _output.Text = r == null ? "" : (r.Translation ?? ""); _statusBlock.Text = "完成" + (r == null || string.IsNullOrEmpty(r.DetectedFrom) ? "" : " · 检测到 " + r.DetectedFrom); }
                }));
            });
        }
    }


    public static class ModulesSettingsDialog
    {
        static CheckBox MakeCb(string label, string key)
        {
            return new CheckBox {
                Content = label, Foreground = Brushes.White, FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0),
                IsChecked = MainWindow.ModuleVisible(key)
            };
        }

        // Settings dialog for showing/hiding floating-window modules. On OK, writes
        // UI.Show{Power,Audio,Mem,Translate} prefs and rebuilds the calling window.
        public static void Show(Window owner)
        {
            var dlg = new Window {
                Title = "板块设置",
                Width = 340, Height = 320,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                FontFamily = owner.FontFamily,
                Background = new SolidColorBrush(Color.FromRgb(28, 26, 40))
            };
            var stack = new StackPanel { Margin = new Thickness(20) };
            var fg = new SolidColorBrush(Color.FromRgb(190, 188, 220));
            stack.Children.Add(new TextBlock {
                Text = "勾选要在悬浮窗中显示的板块：",
                Foreground = Brushes.White, FontSize = 13, Margin = new Thickness(0, 0, 0, 12) });

            var cbPower = MakeCb("电源计划", "Power");
            var cbAudio = MakeCb("音频控制", "Audio");
            var cbMem   = MakeCb("内存清理", "Mem");
            var cbTr    = MakeCb("翻译", "Translate");
            stack.Children.Add(cbPower);
            stack.Children.Add(cbAudio);
            stack.Children.Add(cbMem);
            stack.Children.Add(cbTr);
            stack.Children.Add(new TextBlock {
                Text = "隐藏后悬浮窗立即刷新；托盘菜单与全局快捷键不受影响。",
                Foreground = fg, FontSize = 10, Margin = new Thickness(0, 14, 0, 0),
                TextWrapping = TextWrapping.Wrap });

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var ok = new Button { Content = "确定", Width = 64, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "取消", Width = 64, Height = 28 };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            stack.Children.Add(btns);
            dlg.Content = stack;

            ok.Click += (s, e) => {
                AppPrefs.SetBool("UI.ShowPower",     cbPower.IsChecked == true);
                AppPrefs.SetBool("UI.ShowAudio",     cbAudio.IsChecked == true);
                AppPrefs.SetBool("UI.ShowMem",       cbMem.IsChecked == true);
                AppPrefs.SetBool("UI.ShowTranslate", cbTr.IsChecked == true);
                if (owner is MainWindow) ((MainWindow)owner).RebuildUI();
                dlg.DialogResult = true; dlg.Close();
            };
            cancel.Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            dlg.ShowDialog();
        }
    }


        public static class TranslateSettingsDialog
    {
        public static bool Show(Window owner)
        {
            var dlg = new Window
            {
                Title = "翻译 API 设置",
                Width = 460, Height = 360,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                FontFamily = owner.FontFamily,
                Background = new SolidColorBrush(Color.FromRgb(28, 26, 40)),
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true
            };

            var fg = new SolidColorBrush(Color.FromRgb(190, 188, 220));
            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var titleBar = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(34, 32, 50)), Height = 36, LastChildFill = true };
            var titleText = new TextBlock { Text = "  \u2699  翻译 API 设置", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var closeBtn = new Button { Content = "\u2715", Width = 36, Height = 36, FontSize = 12, Foreground = fg, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Cursor = Cursors.Hand };
            closeBtn.Click += (s, e) => dlg.Close();
            DockPanel.SetDock(closeBtn, Dock.Right);
            titleBar.Children.Add(closeBtn);
            titleBar.Children.Add(titleText);
            titleBar.MouseLeftButtonDown += (s, e) => { try { dlg.DragMove(); } catch { } };
            Grid.SetRow(titleBar, 0); rootGrid.Children.Add(titleBar);

            var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
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

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var ok = new Button { Content = "保存", Width = 80, Height = 28, FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "取消", Width = 80, Height = 28, FontSize = 12 };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            stack.Children.Add(btns);
            Grid.SetRow(stack, 1); rootGrid.Children.Add(stack);

            var border = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)), BorderThickness = new Thickness(1), Child = rootGrid };
            dlg.Content = border;

            bool saved = false;
            ok.Click += (s, e) => { TranslateService.SetCreds(appIdBox.Text.Trim(), keyBox.Text.Trim(), instBox.Text); saved = true; dlg.DialogResult = true; dlg.Close(); };
            cancel.Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };
            dlg.ShowDialog();
            return saved;
        }

        static TextBox MakeBox()
        {
            return new TextBox { FontSize = 12, Padding = new Thickness(8, 6, 8, 6), Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)), BorderThickness = new Thickness(1) };
        }
    }
}
