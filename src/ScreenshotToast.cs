using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PowerAudioManager
{
    // Steam 风格右下角截图通知：无边框圆角深色卡片，显示应用名+缩略图+路径+"打开文件夹"按钮。
    // 约 3.5s 后自动淡出，点击可关闭。使用 WS_EX_NOACTIVATE 不抢焦点。
    internal static class ScreenshotToast
    {
        static Window _current;

        public static void Show(string appName, string path, string source)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() => ShowInternal(appName, path, source, null)));
        }

        public static void ShowError(string message)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() => ShowInternal("截图失败", null, null, message)));
        }

        static void ShowInternal(string appName, string path, string source, string error)
        {
            if (_current != null) { try { _current.Close(); } catch { } _current = null; }

            var dlg = new Window
            {
                Width = 240,
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
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
                Margin = new Thickness(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { BlurRadius = 24, ShadowDepth = 2, Opacity = 0.4, Color = Colors.Black }
            };

            var stack = new StackPanel();

            string srcTag = string.IsNullOrEmpty(source) ? "" : " · " + source;
            var title = new TextBlock
            {
                Text = "已保存截图 · " + appName + srcTag,
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            stack.Children.Add(title);

            if (error != null)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = error,
                    Foreground = new SolidColorBrush(Color.FromRgb(240, 170, 170)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            else
            {
                BitmapSource thumb = ScreenshotService.LoadThumbnail(path, 160, 160);
                if (thumb != null)
                {
                    var img = new System.Windows.Controls.Image
                    {
                        Source = thumb,
                        MaxWidth = 160, MaxHeight = 120,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 10)
                    };
                    stack.Children.Add(img);
                }

                var openBtn = new Button
                {
                    Content = "打开文件夹",
                    Height = 28,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Padding = new Thickness(16, 0, 16, 0)
                };
                AppResources.StyleDialogButton(openBtn, false);
                openBtn.Click += (s, e) =>
                {
                    try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
                    Close(dlg);
                };
                stack.Children.Add(openBtn);
            }

            card.Child = stack;
            dlg.Content = card;

            dlg.Loaded += (s, e) =>
            {
                var wa = SystemParameters.WorkArea;
                dlg.Left = wa.Right - dlg.ActualWidth - 16;
                dlg.Top = wa.Bottom - dlg.ActualHeight - 12;
                // WS_EX_NOACTIVATE 防止 toast 抢焦点。
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
                    int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                    Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | 0x08000000 /*WS_EX_NOACTIVATE*/);
                }
                catch { }

                // 淡入 → 定时淡出。
                dlg.Opacity = 0;
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
                dlg.BeginAnimation(UIElement.OpacityProperty, fadeIn);

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

        static void Close(Window dlg)
        {
            if (dlg == _current) _current = null;
            try { dlg.Close(); } catch { }
        }
    }
}
