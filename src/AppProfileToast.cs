using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PowerAudioManager
{
    // 自学习自动切换的右下角通知，仿 ScreenshotToast 样式：无边框圆角深色卡片，
    // WS_EX_NOACTIVATE 不抢焦点，约 3.5s 淡出，点击可关闭。Learn.Notify 控制是否弹。
    internal static class AppProfileToast
    {
        static Window _current;

        // powerName/audioName 为 null 表示该项未切换（不显示该行）。
        public static void Show(string exeName, string powerName, string audioName)
        {
            if (string.IsNullOrEmpty(exeName)) return;
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => ShowInternal(exeName, powerName, audioName)));
        }

        static void ShowInternal(string exeName, string powerName, string audioName)
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
                Background = new SolidColorBrush(Color.FromRgb(34, 32, 50)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(142, 140, 216)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
                Margin = new Thickness(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { BlurRadius = 24, ShadowDepth = 2, Opacity = 0.4, Color = Colors.Black }
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = "🎓 已为 " + exeName + " 自动切换",
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });

            if (powerName != null)
                stack.Children.Add(Row("⚡ 电源", powerName));
            if (audioName != null)
                stack.Children.Add(Row("🔊 音频", audioName));
            if (powerName == null && audioName == null)
                stack.Children.Add(new TextBlock { Text = "(无变化)", Foreground = new SolidColorBrush(Color.FromRgb(154, 150, 184)), FontSize = 11 });

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

        static UIElement Row(string label, string value)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            sp.Children.Add(new TextBlock { Text = label + "  ", Foreground = new SolidColorBrush(Color.FromRgb(154, 150, 184)), FontSize = 11 });
            sp.Children.Add(new TextBlock { Text = value, Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.Medium });
            return sp;
        }

        static void Close(Window dlg) { if (dlg == _current) _current = null; try { dlg.Close(); } catch { } }
    }
}
