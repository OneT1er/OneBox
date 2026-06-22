using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
                SettingsDialog.Show(this, 3); // 翻译 tab
                if (_statusBlock != null) _statusBlock.Text = "已保存设置";
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

        // A minimal dark ComboBox control template built with FrameworkElementFactory.
        // Replaces the default (light) template so the selected-item display area and
        // the toggle button both render on the dark card background, with light text.
        static System.Windows.Controls.ControlTemplate DarkComboBoxTemplate()
        {
            var template = new System.Windows.Controls.ControlTemplate(typeof(ComboBox));

            // Root: a grid holding the content presenter (selected item) + a toggle button.
            var root = new FrameworkElementFactory(typeof(Grid));

            // Content presenter for the selected item, left-aligned, vertically centred.
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.ContentSourceProperty, "SelectionBoxItem");
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(ContentPresenter.MarginProperty, new Thickness(6, 0, 22, 0));
            cp.SetValue(ContentPresenter.SnapsToDevicePixelsProperty, true);
            root.AppendChild(cp);

            // Toggle button: transparent overlay that fills the whole box and toggles
            // IsDropDownOpen on click. It sits above the content presenter so it
            // captures the mouse for the whole combo area.
            var toggle = new FrameworkElementFactory(typeof(ToggleButton));
            toggle.SetValue(ToggleButton.FocusableProperty, false);
            toggle.SetValue(ToggleButton.IsTabStopProperty, false);
            toggle.SetValue(ToggleButton.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            toggle.SetValue(ToggleButton.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            // Two-way bind IsChecked <-> IsDropDownOpen. Inside a control template the
            // binding source must be the templated parent (the ComboBox) — without
            // RelativeSource it binds to DataContext (null) and clicks never reach
            // IsDropDownOpen, so the popup never opens.
            var isOpenCheckBinding = new System.Windows.Data.Binding("IsDropDownOpen") {
                Mode = System.Windows.Data.BindingMode.TwoWay,
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            };
            toggle.SetBinding(ToggleButton.IsCheckedProperty, isOpenCheckBinding);
            // Transparent button template: a Border that stretches and has a real
            // (transparent) brush so it participates in hit-testing. A null background
            // would make it ignore clicks.
            var tbTemplate = new System.Windows.Controls.ControlTemplate(typeof(ToggleButton));
            var tbRoot = new FrameworkElementFactory(typeof(Border));
            tbRoot.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            tbRoot.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            tbRoot.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            tbTemplate.VisualTree = tbRoot;
            toggle.SetValue(ToggleButton.TemplateProperty, tbTemplate);
            root.AppendChild(toggle);

            // Dropdown arrow glyph on the right.
            var arrow = new FrameworkElementFactory(typeof(TextBlock));
            arrow.SetValue(TextBlock.TextProperty, "▾");
            arrow.SetValue(TextBlock.FontSizeProperty, 10.0);
            arrow.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(190, 188, 220)));
            arrow.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            arrow.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            arrow.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 6, 0));
            arrow.SetValue(TextBlock.IsHitTestVisibleProperty, false);
            root.AppendChild(arrow);

            // Popup hosting the items (dark background).
            var popup = new FrameworkElementFactory(typeof(Popup));
            popup.SetValue(Popup.NameProperty, "PART_Popup");
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
            popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.None);
            var isOpenBinding = new System.Windows.Data.Binding("IsDropDownOpen") {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            };
            popup.SetBinding(Popup.IsOpenProperty, isOpenBinding);
            popup.SetValue(Popup.FocusableProperty, false);

            var dropBorder = new FrameworkElementFactory(typeof(Border));
            dropBorder.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(34, 32, 50)));
            dropBorder.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(80, 75, 120)));
            dropBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            dropBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));

            var scroller = new FrameworkElementFactory(typeof(ScrollViewer));
            scroller.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            scroller.SetValue(ScrollViewer.MaxHeightProperty, 300.0);
            var itemsHost = new FrameworkElementFactory(typeof(ItemsPresenter));
            itemsHost.SetValue(KeyboardNavigation.DirectionalNavigationProperty, KeyboardNavigationMode.Cycle);
            scroller.AppendChild(itemsHost);
            dropBorder.AppendChild(scroller);
            popup.AppendChild(dropBorder);
            root.AppendChild(popup);

            template.VisualTree = root;
            return template;
        }

        ComboBox MakeLangBox(bool isFrom)
        {
            var cb = new ComboBox {
                Width = 110, Height = 28,
                FontSize = 12,
                Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 218, 245)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)),
                Padding = new Thickness(6, 0, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            // Dark theme: force every TextBlock inside (selected-display + popup) to
            // light text, and give the dropdown popup a dark background.
            var lightText = new SolidColorBrush(Color.FromRgb(220, 218, 245));
            var tbStyle = new Style(typeof(TextBlock));
            tbStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, lightText));
            cb.Resources.Add(typeof(TextBlock), tbStyle);
            cb.Resources.Add(System.Windows.SystemColors.WindowBrushKey, new SolidColorBrush(Color.FromRgb(34, 32, 50)));
            cb.Resources.Add(System.Windows.SystemColors.WindowTextBrushKey, lightText);
            cb.Resources.Add(System.Windows.SystemColors.HighlightBrushKey, new SolidColorBrush(Color.FromRgb(110, 105, 200)));
            cb.Resources.Add(System.Windows.SystemColors.HighlightTextBrushKey, Brushes.White);
            cb.Resources.Add(System.Windows.SystemColors.ControlBrushKey, new SolidColorBrush(Color.FromRgb(42, 39, 60)));
            cb.Resources.Add(System.Windows.SystemColors.ControlTextBrushKey, lightText);
            // Replace the default (light) ComboBox control template with a dark one,
            // otherwise the selected-item display area keeps the system light
            // background and the text becomes unreadable (白字灰底).
            cb.Template = DarkComboBoxTemplate();
            string[][] codes = isFrom
                ? new[] { new[] { "auto","自动检测" }, new[] { "zh","中文" }, new[] { "en","英语" }, new[] { "jp","日语" }, new[] { "kor","韩语" }, new[] { "fra","法语" }, new[] { "de","德语" }, new[] { "ru","俄语" }, new[] { "spa","西班牙语" }, new[] { "ara","阿拉伯语" } }
                : new[] { new[] { "zh","中文" }, new[] { "en","英语" }, new[] { "jp","日语" }, new[] { "kor","韩语" }, new[] { "fra","法语" }, new[] { "de","德语" }, new[] { "ru","俄语" }, new[] { "spa","西班牙语" }, new[] { "ara","阿拉伯语" } };
            foreach (var p in codes)
            {
                cb.Items.Add(new ComboBoxItem
                {
                    Content = p[1],
                    Tag = p[0],
                    Foreground = new SolidColorBrush(Color.FromRgb(220, 218, 245)),
                    Background = new SolidColorBrush(Color.FromRgb(34, 32, 50)),
                    Padding = new Thickness(8, 4, 8, 4)
                });
            }
            // Dark item container style with a purple hover, matching the app accent.
            var itemStyle = new Style(typeof(ComboBoxItem));
            itemStyle.Setters.Add(new Setter(ComboBoxItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(34, 32, 50))));
            itemStyle.Setters.Add(new Setter(ComboBoxItem.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 218, 245))));
            itemStyle.Setters.Add(new Setter(ComboBoxItem.PaddingProperty, new Thickness(8, 4, 8, 4)));
            itemStyle.Setters.Add(new Setter(ComboBoxItem.BorderBrushProperty, Brushes.Transparent));
            var hover = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
            hover.Setters.Add(new Setter(ComboBoxItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(58, 54, 84))));
            hover.Setters.Add(new Setter(ComboBoxItem.ForegroundProperty, Brushes.White));
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

    // Shared OneBox-style window shell: borderless, rounded, dark, with a custom
    // title bar (drag handle + ✕ close) matching the floating window. Callers pass
    // their content + a title; they get back a ready-to-ShowDialog() Window.
    static class OneBoxWindow
    {
        public static Window Create(Window owner, string title, double width, double height, UIElement body, bool resizable)
        {
            var dlg = new Window {
                Title = title,
                Width = width, Height = height,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                FontFamily = owner != null ? owner.FontFamily : null
            };

            var fg = new SolidColorBrush(Color.FromRgb(190, 188, 220));
            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Rounded title bar matching the floating card (top corners r=10).
            var titleBarBorder = new Border {
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(34, 32, 50)),
                Height = 36
            };
            var titleBar = new DockPanel { LastChildFill = true };
            var titleText = new TextBlock {
                Text = "  " + title,
                Foreground = Brushes.White, FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            var closeBtn = new Button {
                Content = "✕", Width = 36, Height = 36, FontSize = 12,
                Foreground = fg, Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "关闭"
            };
            closeBtn.Click += (s, e) => dlg.Close();
            DockPanel.SetDock(closeBtn, Dock.Right);
            titleBar.Children.Add(closeBtn);
            titleBar.Children.Add(titleText);
            titleBarBorder.Child = titleBar;
            titleBar.MouseLeftButtonDown += (s, e) => { try { dlg.DragMove(); } catch { } };
            Grid.SetRow(titleBarBorder, 0); rootGrid.Children.Add(titleBarBorder);

            Grid.SetRow(body, 1); rootGrid.Children.Add(body);

            // Outer rounded border + dark fill; bottom corners round to match.
            var border = new Border {
                CornerRadius = new CornerRadius(10),
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(28, 26, 40)),
                Child = rootGrid
            };
            dlg.Content = border;
            return dlg;
        }
    }
}
