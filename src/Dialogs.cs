using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
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
        // 返回值编码：高 16 位 = 修饰键（bit0=Alt, bit1=Ctrl, bit2=Shift, bit3=Win），低 16 位 = VK 码；0=无
        // conflictCheck (optional) returns a warning for a captured combination
        // (e.g. 「该快捷键已被其他程序占用」); it is shown under the key caps.
        public static int? Show(Window owner, int currentEncoded, bool allowSingleFunctionKey = false,
            Func<int, string> conflictCheck = null)
        {
            int captured = currentEncoded;
            var stack = new StackPanel { Margin = new Thickness(16, 4, 16, 16) };
            var keys = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 12),
                MinHeight = 40
            };
            var hint = UiKit.Text(allowSingleFunctionKey
                    ? "请按组合键或 F1–F24，按 Esc 取消" : "请按下组合键，按 Esc 取消", 12, UiKit.TextSecondary);
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.TextAlignment = TextAlignment.Center;
            var conflict = UiKit.Text("", 11, UiKit.WarnColor);
            conflict.HorizontalAlignment = HorizontalAlignment.Center;
            conflict.TextWrapping = TextWrapping.Wrap;
            conflict.TextTrimming = TextTrimming.None;
            conflict.Margin = new Thickness(0, 8, 0, 0);
            conflict.Visibility = Visibility.Collapsed;

            void Render()
            {
                keys.Children.Clear();
                if (captured == 0)
                {
                    keys.Children.Add(UiKit.Text("(请按键)", 16, UiKit.TextTertiary));
                    conflict.Visibility = Visibility.Collapsed;
                    return;
                }
                var parts = Format(captured).Split('+');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0)
                    {
                        var plus = UiKit.Text("+", 14, UiKit.TextTertiary);
                        plus.Margin = new Thickness(6, 0, 6, 0);
                        keys.Children.Add(plus);
                    }
                    keys.Children.Add(KeyCap(parts[i]));
                }
                string warning = null;
                try { warning = conflictCheck?.Invoke(captured); } catch { }
                conflict.Text = warning ?? "";
                conflict.Visibility = string.IsNullOrEmpty(warning) ? Visibility.Collapsed : Visibility.Visible;
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = UiKit.MakeButton("取消", ButtonVariant.Secondary);
            cancel.Width = 72;
            var ok = UiKit.MakeButton("确定", ButtonVariant.Primary);
            ok.Width = 72;
            ok.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(cancel); buttons.Children.Add(ok);
            stack.Children.Add(keys); stack.Children.Add(hint); stack.Children.Add(conflict); stack.Children.Add(buttons);
            Render();

            var dlg = OneBoxWindow.Create(owner, "设置快捷键", 340, 230, stack, false);
            dlg.SizeToContent = SizeToContent.Height;
            if (owner != null) dlg.FontFamily = owner.FontFamily;

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
                if (mods == 0 && (!allowSingleFunctionKey || e.Key < Key.F1 || e.Key > Key.F24)) return;
                int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
                if (vk == 0) return;
                captured = (mods << 16) | (vk & 0xFFFF);
                Render();
                e.Handled = true;
            };
            ok.Click += (s, e) => { dlg.DialogResult = true; dlg.Close(); };
            cancel.Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };

            bool? result = dlg.ShowDialog();
            if (result == true && captured != 0) return captured;
            return null;
        }

        static Border KeyCap(string text)
        {
            return new Border
            {
                Background = UiKit.FrozenBrush(UiKit.RaisedColor),
                BorderBrush = UiKit.FrozenBrush(UiKit.BorderStrongColor),
                BorderThickness = new Thickness(1, 1, 1, 3),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 4, 10, 4),
                MinWidth = 36,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    FontFamily = UiKit.MonoFont,
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UiKit.FrozenBrush(UiKit.TextPrimary),
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };
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
        TextBlock _statusBlock, _countBlock;
        System.Windows.Shapes.Ellipse _statusDot;
        Button _btnGo, _btnCopy, _btnSwap, _btnSettings, _btnClear;
        readonly RequestGenerationGate _translationGeneration = new RequestGenerationGate();
        System.Threading.CancellationTokenSource _translationCancellation;
        bool _isClosed;

        public TranslateWindow()
        {
            Title = "OneBox · 翻译";
            Width = 720; Height = 520;
            MinWidth = 460; MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            BuildUI();
            // 恢复上次窗口位置
            double sl, st, sw, sh;
            if (AppPrefs.GetDouble("Translate.Left", out sl) && AppPrefs.GetDouble("Translate.Top", out st)) { Left = sl; Top = st; }
            if (AppPrefs.GetDouble("Translate.Width", out sw) && sw > 300) Width = sw;
            if (AppPrefs.GetDouble("Translate.Height", out sh) && sh > 200) Height = sh;
            LocationChanged += (s, e) => { if (IsLoaded) { AppPrefs.SetDouble("Translate.Left", Left); AppPrefs.SetDouble("Translate.Top", Top); } };
            SizeChanged += (s, e) => { if (IsLoaded) { AppPrefs.SetDouble("Translate.Width", Width); AppPrefs.SetDouble("Translate.Height", Height); } };
            // 从显示配置变更中恢复（4K→1080p、拔显示器、DPI 缩放变更）
            EventHandler displayHandler = (ss, ee) =>
            {
                try { Dispatcher.BeginInvoke(new Action(ClampToWorkArea)); } catch { }
            };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += displayHandler;
            this.Closed += (s, e) =>
            {
                _isClosed = true;
                _translationGeneration.Invalidate();
                var cancellation = _translationCancellation;
                _translationCancellation = null;
                if (cancellation != null)
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                }
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
                // 新工作区比恢复尺寸更小时压缩窗口
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

            // Title bar (40px, merged into the window ground).
            var titleBar = new DockPanel { Background = Brushes.Transparent, Height = 40, LastChildFill = true };
            var titleIcon = IconCatalog.CreateElement(IconKey.Translate, 16, UiKit.FrozenBrush(UiKit.AccentColor));
            titleIcon.Margin = new Thickness(14, 0, 8, 0);
            titleIcon.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(titleIcon, Dock.Left);
            var titleText = UiKit.Text("翻译", 13, UiKit.TextPrimary, FontWeights.SemiBold);
            var closeBtn = UiKit.MakeIconButton(IconKey.Close, "关闭", 32);
            closeBtn.Margin = new Thickness(0, 0, 4, 0);
            closeBtn.Click += (s, e) => Close();
            _btnSettings = UiKit.MakeIconButton(IconKey.Settings, "翻译 API 设置", 32);
            _btnSettings.Click += (s, e) =>
            {
                SettingsDialog.Show(this, 3);
                string credentialError = TranslateService.GetCredentialError();
                if (string.IsNullOrEmpty(credentialError)) SetStatus("已保存设置", null);
                else SetStatus("失败: " + credentialError, UiKit.DangerColor);
            };
            DockPanel.SetDock(closeBtn, Dock.Right);
            DockPanel.SetDock(_btnSettings, Dock.Right);
            titleBar.Children.Add(titleIcon);
            titleBar.Children.Add(closeBtn);
            titleBar.Children.Add(_btnSettings);
            titleBar.Children.Add(titleText);
            titleBar.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
            Grid.SetRow(titleBar, 0); rootGrid.Children.Add(titleBar);

            var grid = new Grid { Margin = new Thickness(12, 0, 12, 12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(grid, 1); rootGrid.Children.Add(grid);

            // Language bar: [源] [⇄] [目标], aligned over the two panes.
            var bar = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _fromBox = MakeLangBox(true);
            _toBox = MakeLangBox(false);
            _fromBox.HorizontalAlignment = HorizontalAlignment.Left;
            _toBox.HorizontalAlignment = HorizontalAlignment.Left;
            _btnSwap = new Button
            {
                Content = UiKit.Glyph(UiKit.Glyphs.Swap, 16, UiKit.FrozenBrush(UiKit.TextSecondary)),
                Width = 32, Height = 32,
                ToolTip = "交换源/目标语言",
                HorizontalAlignment = HorizontalAlignment.Center
            };
            AutomationProperties.SetName(_btnSwap, "交换源/目标语言");
            UiKit.ApplyIconButtonStyle(_btnSwap);
            _btnSwap.Background = UiKit.FrozenBrush(UiKit.CardColor);
            _btnSwap.Click += (s, e) => SwapLanguages();
            Grid.SetColumn(_fromBox, 0); bar.Children.Add(_fromBox);
            Grid.SetColumn(_btnSwap, 1); bar.Children.Add(_btnSwap);
            Grid.SetColumn(_toBox, 2); bar.Children.Add(_toBox);
            Grid.SetRow(bar, 0); grid.Children.Add(bar);

            var panes = new Grid();
            panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(panes, 1); grid.Children.Add(panes);

            // Input pane.
            _input = MakeBox(false, "在此输入或粘贴文本，按 Ctrl+Enter 翻译");
            _input.PreviewKeyDown += (s, e) => {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                { e.Handled = true; RunTranslation(_input.Text); }
            };
            _countBlock = UiKit.Text("0 字", 11, UiKit.TextSecondary);
            _input.TextChanged += (s, e) => { if (_countBlock != null) _countBlock.Text = (_input.Text?.Length ?? 0) + " 字"; };
            _btnClear = UiKit.MakeIconButton(IconKey.Close, "清空", 32, 14);
            _btnClear.Click += (s, e) => { _input.Clear(); _input.Focus(); };
            _btnGo = new Button { Height = 32, Padding = new Thickness(12, 0, 12, 0) };
            UiKit.ApplyButtonVariant(_btnGo, ButtonVariant.Primary);
            var goContent = new StackPanel { Orientation = Orientation.Horizontal };
            goContent.Children.Add(new TextBlock { Text = "翻译", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            goContent.Children.Add(new TextBlock { Text = "Ctrl+Enter", FontSize = 11, Opacity = 0.75, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            _btnGo.Content = goContent;
            AutomationProperties.SetName(_btnGo, "翻译");
            _btnGo.Click += (s, e) => RunTranslation(_input.Text);
            var inputFoot = new DockPanel { LastChildFill = true, Height = 44, Margin = new Thickness(14, 0, 8, 0) };
            DockPanel.SetDock(_btnGo, Dock.Right);
            DockPanel.SetDock(_btnClear, Dock.Right);
            _btnClear.Margin = new Thickness(0, 0, 6, 0);
            inputFoot.Children.Add(_btnGo);
            inputFoot.Children.Add(_btnClear);
            inputFoot.Children.Add(_countBlock);
            var inputPane = MakePane(_input, inputFoot, UiKit.CardColor);
            Grid.SetColumn(inputPane, 0); panes.Children.Add(inputPane);

            // Output pane.
            _output = MakeBox(true, "");
            _statusDot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            _statusBlock = UiKit.Text("", 11, UiKit.TextSecondary);
            _btnCopy = new Button { Height = 32, Padding = new Thickness(12, 0, 12, 0) };
            UiKit.ApplyButtonVariant(_btnCopy, ButtonVariant.Secondary);
            var copyContent = new StackPanel { Orientation = Orientation.Horizontal };
            copyContent.Children.Add(UiKit.Glyph(UiKit.Glyphs.Copy, 14, UiKit.FrozenBrush(UiKit.TextPrimary)));
            copyContent.Children.Add(new TextBlock { Text = "复制译文", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            _btnCopy.Content = copyContent;
            AutomationProperties.SetName(_btnCopy, "复制译文");
            _btnCopy.Click += (s, e) => { try { if (!string.IsNullOrEmpty(_output.Text)) Clipboard.SetText(_output.Text); SetStatus("已复制", UiKit.SuccessColor); } catch { } };
            var outputFoot = new DockPanel { LastChildFill = true, Height = 44, Margin = new Thickness(14, 0, 8, 0) };
            DockPanel.SetDock(_btnCopy, Dock.Right);
            outputFoot.Children.Add(_btnCopy);
            var statusRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            statusRow.Children.Add(_statusDot);
            statusRow.Children.Add(_statusBlock);
            outputFoot.Children.Add(statusRow);
            var outputPane = MakePane(_output, outputFoot, Color.FromRgb(28, 26, 38));
            Grid.SetColumn(outputPane, 2); panes.Children.Add(outputPane);

            var foot = UiKit.Text("由百度翻译提供 · Ctrl+Enter 触发翻译 · Ctrl+Shift+T 全局翻译剪贴板", 11, UiKit.TextTertiary);
            foot.HorizontalAlignment = HorizontalAlignment.Center;
            foot.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(foot, 2); grid.Children.Add(foot);

            var border = new Border {
                CornerRadius = new CornerRadius(12),
                Background = UiKit.FrozenBrush(UiKit.BgColor),
                BorderBrush = UiKit.FrozenBrush(UiKit.BorderColor),
                BorderThickness = new Thickness(1),
                Child = rootGrid
            };
            Content = border;
        }

        // A rounded pane holding a borderless text box over a footer row.
        static Border MakePane(TextBox box, UIElement footer, Color background)
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(box, 0); g.Children.Add(box);
            Grid.SetRow(footer, 1); g.Children.Add(footer);
            var pane = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = UiKit.FrozenBrush(background),
                BorderBrush = UiKit.FrozenBrush(UiKit.BorderColor),
                BorderThickness = new Thickness(1),
                Child = g
            };
            if (!box.IsReadOnly)
            {
                box.GotKeyboardFocus += (_, _) => pane.BorderBrush = UiKit.FrozenBrush(UiKit.AccentColor);
                box.LostKeyboardFocus += (_, _) => pane.BorderBrush = UiKit.FrozenBrush(UiKit.BorderColor);
            }
            return pane;
        }

        // Status line under the translation: text + optional colored dot.
        void SetStatus(string text, Color? dot)
        {
            if (_statusBlock == null) return;
            _statusBlock.Text = text ?? "";
            if (_statusDot == null) return;
            if (dot.HasValue && !string.IsNullOrEmpty(text))
            {
                _statusDot.Fill = UiKit.FrozenBrush(dot.Value);
                _statusDot.Visibility = Visibility.Visible;
            }
            else _statusDot.Visibility = Visibility.Collapsed;
        }

        TextBox MakeBox(bool readOnly, string placeholder)
        {
            return new TextBox {
                IsReadOnly = readOnly,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontSize = 14,
                Padding = new Thickness(10, 12, 10, 4),
                Background = Brushes.Transparent,
                Foreground = UiKit.FrozenBrush(UiKit.TextPrimary),
                CaretBrush = UiKit.FrozenBrush(UiKit.TextPrimary),
                SelectionBrush = UiKit.FrozenBrush(UiKit.AccentColor),
                BorderThickness = new Thickness(0),
                ToolTip = string.IsNullOrEmpty(placeholder) ? null : placeholder
            };
        }


        ComboBox MakeLangBox(bool isFrom)
        {
            var cb = new ComboBox {
                Width = 130, Height = 32,
                FontSize = 12,
                Padding = new Thickness(6, 0, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            AppResources.StyleDarkComboBox(cb);
            string[][] codes = isFrom
                ? new[] { new[] { "auto","自动检测" }, new[] { "zh","中文" }, new[] { "en","英语" }, new[] { "jp","日语" }, new[] { "kor","韩语" }, new[] { "fra","法语" }, new[] { "de","德语" }, new[] { "ru","俄语" }, new[] { "spa","西班牙语" }, new[] { "ara","阿拉伯语" } }
                : new[] { new[] { "zh","中文" }, new[] { "en","英语" }, new[] { "jp","日语" }, new[] { "kor","韩语" }, new[] { "fra","法语" }, new[] { "de","德语" }, new[] { "ru","俄语" }, new[] { "spa","西班牙语" }, new[] { "ara","阿拉伯语" } };
            foreach (var p in codes)
            {
                cb.Items.Add(new ComboBoxItem
                {
                    Content = p[1],
                    Tag = p[0],
                    Padding = new Thickness(8, 4, 8, 4)
                });
            }
            string saved;
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PowerAudioManager\App"))
                saved = k == null ? null : (k.GetValue(isFrom ? "Translate.From" : "Translate.To") as string);
            int idx = 0;
            for (int i = 0; i < codes.Length; i++) if (codes[i][0] == saved) { idx = i; break; }
            if (saved == null) idx = isFrom ? 0 : 0;
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
            if (fTag == "auto") return; // "自动检测"不能放在目标语言侧
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
            _ = RunTranslationAsync(text);
        }

        public async System.Threading.Tasks.Task RunTranslationAsync(string text)
        {
            if (text == null) text = "";
            _input.Text = text;
            var previousCancellation = _translationCancellation;
            var cancellation = new System.Threading.CancellationTokenSource();
            _translationCancellation = cancellation;
            int generation = _translationGeneration.Begin();
            if (previousCancellation != null)
            {
                previousCancellation.Cancel();
                previousCancellation.Dispose();
            }
            if (string.IsNullOrEmpty(text))
            {
                _output.Text = "";
                SetStatus("", null);
                _btnGo.IsEnabled = true;
                if (ReferenceEquals(_translationCancellation, cancellation)) _translationCancellation = null;
                cancellation.Dispose();
                return;
            }
            var fItem = _fromBox.SelectedItem as ComboBoxItem;
            var tItem = _toBox.SelectedItem as ComboBoxItem;
            string from = fItem == null ? "auto" : fItem.Tag.ToString();
            string to = tItem == null ? "zh" : tItem.Tag.ToString();
            SetStatus("翻译中…", UiKit.AccentColor);
            _btnGo.IsEnabled = false;
            _output.Text = "";
            TranslateService.Result result;
            try
            {
                result = await TranslateService.TranslateAsync(text, from, to, cancellation.Token);
            }
            catch (Exception ex)
            {
                AppLog.Log("TranslateWindow", ex);
                result = new TranslateService.Result { Error = "翻译失败：" + ex.Message };
            }

            if (_isClosed || !_translationGeneration.IsCurrent(generation)) return;
            if (ReferenceEquals(_translationCancellation, cancellation)) _translationCancellation = null;
            cancellation.Dispose();
            _btnGo.IsEnabled = true;
            if (!string.IsNullOrEmpty(result?.Error))
            {
                _output.Text = "";
                SetStatus("失败: " + result.Error, UiKit.DangerColor);
                return;
            }
            _output.Text = result?.Translation ?? "";
            SetStatus("完成" + (string.IsNullOrEmpty(result?.DetectedFrom) ? "" : " · 检测到 " + result.DetectedFrom), UiKit.SuccessColor);
        }
    }

    // 通用 OneBox 风格窗口外壳：无边框、圆角 12、#17151F 底色、40px 合并标题栏（可选图标 + 标题 + 关闭）。
    // 调用方传入内容和标题，返回可直接 ShowDialog() 的 Window。
    static class OneBoxWindow
    {
        public static Window Create(Window owner, string title, double width, double height, UIElement body, bool resizable,
            IconKey? icon = null)
        {
            var dlg = new Window {
                Title = title,
                Width = width, Height = height,
                Owner = owner,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                FontFamily = owner != null ? owner.FontFamily : SystemFonts.MessageFontFamily
            };

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 40px title bar merged into the window ground.
            var titleBar = new DockPanel { LastChildFill = true, Height = 40, Background = Brushes.Transparent };
            if (icon.HasValue)
            {
                var glyph = IconCatalog.CreateElement(icon.Value, 16, UiKit.FrozenBrush(UiKit.AccentColor));
                glyph.Margin = new Thickness(14, 0, 0, 0);
                glyph.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(glyph, Dock.Left);
                titleBar.Children.Add(glyph);
            }
            var titleText = new TextBlock {
                Text = title,
                Margin = new Thickness(icon.HasValue ? 8 : 14, 0, 0, 0),
                Foreground = UiKit.FrozenBrush(UiKit.TextPrimary), FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var closeBtn = UiKit.MakeIconButton(IconKey.Close, "关闭", 32);
            closeBtn.Margin = new Thickness(0, 0, 4, 0);
            closeBtn.VerticalAlignment = VerticalAlignment.Center;
            closeBtn.Click += (s, e) => dlg.Close();
            DockPanel.SetDock(closeBtn, Dock.Right);
            titleBar.Children.Add(closeBtn);
            titleBar.Children.Add(titleText);
            titleBar.MouseLeftButtonDown += (s, e) => { try { dlg.DragMove(); } catch { } };
            Grid.SetRow(titleBar, 0); rootGrid.Children.Add(titleBar);

            Grid.SetRow(body, 1); rootGrid.Children.Add(body);

            var border = new Border {
                CornerRadius = new CornerRadius(12),
                BorderBrush = UiKit.FrozenBrush(UiKit.BorderColor),
                BorderThickness = new Thickness(1),
                Background = UiKit.FrozenBrush(UiKit.BgColor),
                Child = rootGrid
            };
            dlg.Content = border;
            return dlg;
        }
    }
}
