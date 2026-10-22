using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PowerAudioManager
{
    // 电源/音频手动切换的右下角通知，仿 ScreenshotToast 样式：无边框圆角深色卡片，
    // WS_EX_NOACTIVATE 不抢焦点，约 3.5s 淡出，点击可关闭。
    internal static class AppProfileToast
    {
        static Window _current;


        // 手动音频循环切换提示（SoundSwitch 式）：只显示音频行，标题用"音频已切换"。
        public static void ShowAudioSwitch(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName)) return;
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => ShowInternal("音频已切换", null, deviceName)));
        }

        // 手动电源循环切换提示：只显示电源行，标题用"电源已切换"。
        public static void ShowPowerSwitch(string planName)
        {
            if (string.IsNullOrEmpty(planName)) return;
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => ShowInternal("电源已切换", planName, null)));
        }

        static void ShowInternal(string title, string powerName, string audioName)
        {
            if (_current != null) { try { _current.Close(); } catch { } _current = null; }

            var dlg = new Window
            {
                Width = 260,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                Focusable = false,
                ShowActivated = false
            };

            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = UiKit.FrozenBrush(UiKit.BgColor),
                BorderBrush = UiKit.FrozenBrush(UiKit.BorderColor),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.4, Color = Colors.Black }
            };

            UIElement stack;
            if (powerName == null && audioName == null)
            {
                var empty = new StackPanel();
                empty.Children.Add(UiKit.Text(title, 11, UiKit.TextSecondary));
                empty.Children.Add(UiKit.Text("(无变化)", 12, UiKit.TextSecondary));
                stack = empty;
            }
            else
            {
                bool isPower = powerName != null;
                var row = new DockPanel { LastChildFill = true };
                var tile = new Border
                {
                    Width = 32, Height = 32, CornerRadius = new CornerRadius(8),
                    Background = UiKit.FrozenBrush(UiKit.RaisedColor),
                    Margin = new Thickness(0, 0, 10, 0),
                    Child = isPower
                        ? IconCatalog.CreateElement(IconKey.Power, 16, UiKit.FrozenBrush(UiKit.AccentColor))
                        : (UIElement)UiKit.Glyph(UiKit.Glyphs.Headphones, 16, UiKit.FrozenBrush(UiKit.AccentColor))
                };
                if (tile.Child is FrameworkElement glyph)
                {
                    glyph.HorizontalAlignment = HorizontalAlignment.Center;
                    glyph.VerticalAlignment = VerticalAlignment.Center;
                }
                DockPanel.SetDock(tile, Dock.Left);
                row.Children.Add(tile);
                var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                texts.Children.Add(UiKit.Text(title, 11, UiKit.TextSecondary));
                var value = UiKit.Text(isPower ? powerName : audioName, 14, UiKit.TextPrimary, FontWeights.SemiBold);
                value.ToolTip = value.Text;
                texts.Children.Add(value);
                row.Children.Add(texts);
                stack = row;
            }

            card.Child = stack;
            dlg.Content = card;

            dlg.Loaded += (s, e) =>
            {
                var wa = SystemParameters.WorkArea;
                dlg.Left = wa.Right - dlg.ActualWidth - 16;
                dlg.Top = wa.Bottom - dlg.ActualHeight - 12;
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
                    int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                    Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | 0x08000000 /*WS_EX_NOACTIVATE*/);
                }
                catch { }

                dlg.Opacity = 0;
                dlg.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3500) };
                timer.Tick += (ts, te) =>
                {
                    timer.Stop();
                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
                    fadeOut.Completed += (cs, ce) => Close(dlg);
                    dlg.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                };
                timer.Start();
            };
            dlg.MouseLeftButtonDown += (s, e) => Close(dlg);

            _current = dlg;
            dlg.Show();
        }

        static void Close(Window dlg) { if (dlg == _current) _current = null; try { dlg.Close(); } catch { } }
    }
}
