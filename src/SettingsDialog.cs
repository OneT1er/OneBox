using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PowerAudioManager
{
    // 设置对话框：侧栏 + 7 个 tab。各 tab 构建逻辑按文件拆分（partial）：
    //   SettingsDialog.General / Modules / Memory / Translate / Screenshot / Clipboard / Temp / Metrics
    internal static partial class SettingsDialog
    {
        static List<UIElement> _tabContents;
        static ContentControl _contentHost;
        public static void Show(Window owner)
        {
            Show(owner, 0);
        }

        public static void Show(Window owner, int openTab)
        {
            var fg = new SolidColorBrush(Color.FromRgb(190, 188, 220));
            var lightText = new SolidColorBrush(Color.FromRgb(220, 218, 245));

            // ---- 侧栏 ----
            var sideBar = new ListBox
            {
                Width = 130,
                Background = new SolidColorBrush(Color.FromRgb(24, 22, 36)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(40, 36, 56)),
                BorderThickness = new Thickness(0, 0, 1, 0),
                Margin = new Thickness(0),
                Padding = new Thickness(0, 10, 0, 0)
            };
            sideBar.ItemContainerStyle = SidebarItemStyle();
            sideBar.SelectionChanged += (s, e) =>
            {
                foreach (ListBoxItem item in sideBar.Items)
                {
                    var tb = (item.Content as StackPanel)?.Children[1] as TextBlock;
                    if (tb != null)
                        tb.Foreground = item.IsSelected ? Brushes.White : new SolidColorBrush(Color.FromRgb(180, 177, 210));
                }
                if (sideBar.SelectedIndex >= 0)
                    _contentHost.Content = _tabContents[sideBar.SelectedIndex];
            };

            _contentHost = new ContentControl { Background = new SolidColorBrush(Color.FromRgb(28, 26, 40)) };

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(sideBar, 0);
            Grid.SetColumn(_contentHost, 1);
            layout.Children.Add(sideBar);
            layout.Children.Add(_contentHost);

            var dlg = OneBoxWindow.Create(owner, "设置", 520, 570, layout, true);

            _tabContents = new System.Collections.Generic.List<UIElement>
            {
                BuildGeneralTab(owner, dlg, fg, lightText),
                BuildModulesTab(owner, dlg, fg),
                BuildMemoryTab(owner, dlg, fg),
                BuildTranslateTab(owner, dlg, fg),
                BuildScreenshotTab(owner, dlg, fg),
                BuildClipboardTab(owner, dlg, fg),
                BuildTempTab(owner, dlg, fg),
            };

            sideBar.Items.Add(SidebarItem("⚙", "常规"));
            sideBar.Items.Add(SidebarItem("▣", "板块"));
            sideBar.Items.Add(SidebarItem("◈", "内存"));
            sideBar.Items.Add(SidebarItem("↗", "翻译"));
            sideBar.Items.Add(SidebarItem("◻", "截图"));
            sideBar.Items.Add(SidebarItem("▤", "剪贴板"));
            sideBar.Items.Add(SidebarItem("◉", "性能 "));

            if (openTab >= 0 && openTab < sideBar.Items.Count)
            {
                sideBar.SelectedIndex = openTab;
                _contentHost.Content = _tabContents[openTab];
            }
            else { sideBar.SelectedIndex = 0; _contentHost.Content = _tabContents[0]; }

            dlg.ShowDialog();
        }

        static ListBoxItem SidebarItem(string icon, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = icon, FontFamily = AppResources.AppFont, FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(180, 177, 210)), Width = 20, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(190, 188, 220)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            return new ListBoxItem { Content = row, Height = 42, Padding = new Thickness(10, 0, 10, 0) };
        }

        static Style SidebarItemStyle()
        {
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(ListBoxItem.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(ListBoxItem.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(ListBoxItem.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(ListBoxItem.CursorProperty, System.Windows.Input.Cursors.Hand));
            style.Setters.Add(new Setter(ListBoxItem.MarginProperty, new Thickness(6, 2, 6, 2)));

            // 选中态：紫色圆角填充 + 左侧指示点
            var sel = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            sel.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(60, 52, 100))));
            sel.Setters.Add(new Setter(ListBoxItem.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(142, 140, 216))));
            sel.Setters.Add(new Setter(ListBoxItem.BorderThicknessProperty, new Thickness(3, 0, 0, 0)));
            style.Triggers.Add(sel);

            var hover = new Trigger { Property = ListBoxItem.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 42, 62))));
            style.Triggers.Add(hover);

            return style;
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

        static void ConfirmIfDangerous(CheckBox cb, Window dlg, string message)
        {
            cb.Checked += (s, e) =>
            {
                var rc = MessageBox.Show(dlg, message, "提示", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (rc != MessageBoxResult.OK) cb.IsChecked = false;
            };
        }

        static void ShowTextDialog(Window owner, string title, string text)
        {
            var dlg = new Window
            {
                Title = title, Width = 540, Height = 440,
                Background = new SolidColorBrush(Color.FromRgb(28, 26, 40)),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner, ShowInTaskbar = false
            };
            var dock = new DockPanel { Margin = new Thickness(12) };
            var ok = new Button { Content = "关闭", Height = 28, Width = 88, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            AppResources.StyleDialogButton(ok, false);
            ok.Click += (_, _) => dlg.Close();
            DockPanel.SetDock(ok, Dock.Bottom);
            dock.Children.Add(ok);
            var tb = new TextBox
            {
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(20, 18, 28)),
                Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(60, 55, 80)),
                FontSize = 12, Text = text
            };
            dock.Children.Add(tb);
            dlg.Content = dock;
            dlg.ShowDialog();
        }
        // 热键"标签 + 设置/清除"行。捕获后立即测试注册占用；占用时保留按键值并标注"（被占用）"+ 弹提示。
        // testOccupancy=false 时只捕获不测试（如 Game Bar 热键，注册的是系统组合）。
        static HotkeyRow MakeHotkeyRow(Window owner, Window dlg, int current, SolidColorBrush fg,
            string emptyText = "（未设置）", int bottomMargin = 4,
            bool testOccupancy = true,
            string occupiedMessage = "该快捷键已被其他程序占用，OneBox 无法注册。\n你可以换一个组合，或先释放占用它的程序。")
        {
            var row = new HotkeyRow { Value = current };
            row.Label = new TextBlock
            {
                Text = current != 0 ? HotkeyCaptureDialog.Format(current) : emptyText,
                Foreground = current != 0 ? Brushes.White : fg,
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            var setBtn = new Button { Content = "设置快捷键", Height = 28, FontSize = 12, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(setBtn, false);
            var clearBtn = new Button { Content = "清除", Height = 28, FontSize = 12, Padding = new Thickness(10, 0, 10, 0) };
            AppResources.StyleDialogButton(clearBtn, false);
            setBtn.Click += (s, e) =>
            {
                var captured = HotkeyCaptureDialog.Show(dlg, row.Value);
                if (captured.HasValue)
                {
                    row.Value = captured.Value;
                    if (testOccupancy && owner is MainWindow mw && !mw.TestHotkey(captured.Value))
                    {
                        row.Label.Text = HotkeyCaptureDialog.Format(row.Value) + "（被占用）";
                        row.Label.Foreground = new SolidColorBrush(Color.FromRgb(240, 170, 170));
                        MessageBox.Show(dlg, occupiedMessage, "快捷键被占用", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        row.Label.Text = HotkeyCaptureDialog.Format(row.Value);
                        row.Label.Foreground = Brushes.White;
                    }
                }
            };
            clearBtn.Click += (s, e) => { row.Value = 0; row.Label.Text = emptyText; row.Label.Foreground = fg; };
            row.Row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, bottomMargin) };
            row.Row.Children.Add(row.Label);
            row.Row.Children.Add(setBtn);
            row.Row.Children.Add(clearBtn);
            return row;
        }
    }

    // 热键行的状态容器：Value 供确定按钮读取最新编码（含被占用的），Label/Row 用于布局。
    sealed class HotkeyRow
    {
        public int Value;
        public TextBlock Label;
        public StackPanel Row;
    }
}
