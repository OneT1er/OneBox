using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PowerAudioManager
{
    // 非激活截图通知：预览、保存位置、明确的关闭入口，悬停时暂停自动关闭。
    internal static class ScreenshotToast
    {
        static Window _current;
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [StructLayout(LayoutKind.Sequential)] struct WindowRect { public int Left, Top, Right, Bottom; }

        public static void Show(string appName, string path, string source) =>
            Application.Current.Dispatcher.BeginInvoke(new Action(() => ShowInternal(appName, path, source, null)));
        public static void ShowError(string message) =>
            Application.Current.Dispatcher.BeginInvoke(new Action(() => ShowInternal(null, null, null, message)));

        static void ShowInternal(string appName, string path, string source, string error)
        {
            if (_current != null) Close(_current);
            var screen = System.Windows.Forms.Screen.FromHandle(GetForegroundWindow());
            var dlg = new Window
            {
                Width = 320, SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, AllowsTransparency = true, Background = Brushes.Transparent,
                Topmost = true, Focusable = false, ShowActivated = false,
                UseLayoutRounding = true, Opacity = 0
            };
            var fg = UiKit.FrozenBrush(ThemeTokens.SecondaryText);
            var accent = UiKit.FrozenBrush(error == null ? ThemeTokens.Accent : Color.FromRgb(240, 150, 150));
            var card = new Border
            {
                CornerRadius = new CornerRadius(12), Background = UiKit.FrozenBrush(ThemeTokens.TitleSurface),
                BorderBrush = UiKit.FrozenBrush(Color.FromRgb(76, 70, 106)), BorderThickness = new Thickness(1),
                Padding = new Thickness(14), Margin = new Thickness(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                    { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35, Color = Colors.Black }
            };
            var stack = new StackPanel();
            var header = new DockPanel();
            var close = new Button
            {
                Content = IconCatalog.CreateElement(IconKey.Close, 12, fg), Width = 24, Height = 24,
                Padding = new Thickness(0), Focusable = false, ToolTip = "关闭通知"
            };
            UiKit.ApplyIconButtonStyle(close);
            System.Windows.Automation.AutomationProperties.SetName(close, "关闭截图通知");
            close.Click += (_, _) => Close(dlg);
            DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
            var statusIcon = IconCatalog.CreateElement(error == null ? IconKey.Success : IconKey.Error, 18, accent);
            statusIcon.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(statusIcon, Dock.Left); header.Children.Add(statusIcon);
            header.Children.Add(new TextBlock { Text = error == null ? "截图已保存" : "截图失败", Foreground = Brushes.White,
                FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(header);
            if (error != null)
            {
                stack.Children.Add(new TextBlock { Text = error, Foreground = accent, FontSize = 11,
                    TextWrapping = TextWrapping.Wrap, MaxHeight = 130, TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = error, Margin = new Thickness(0, 8, 0, 2) });
            }
            else
            {
                string description = (string.IsNullOrWhiteSpace(appName) ? "截图" : appName) +
                    (string.IsNullOrWhiteSpace(source) ? "" : " · " + source);
                stack.Children.Add(new TextBlock { Text = description, ToolTip = description, Foreground = fg,
                    FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 10) });
                var thumb = ScreenshotService.LoadThumbnail(path, 544, 280);
                if (thumb != null)
                {
                    var preview = new Border { Height = 140, CornerRadius = new CornerRadius(6),
                        Background = UiKit.FrozenBrush(Color.FromRgb(20, 18, 28)), Padding = new Thickness(4),
                        Child = new Image { Source = thumb, Stretch = Stretch.Uniform } };
                    stack.Children.Add(preview);
                }
                stack.Children.Add(new TextBlock { Text = Path.GetFileName(path), ToolTip = path, Foreground = fg,
                    FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 8) });
                var open = new Button { Content = "打开文件夹", Height = 28, FontSize = 11, Focusable = false,
                    HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 0, 12, 0) };
                AppResources.StyleDialogButton(open, true);
                open.Click += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); Close(dlg); }
                    catch (Exception ex) { AppLog.Log("ScreenshotToast.Open", ex); }
                };
                stack.Children.Add(open);
            }
            card.Child = stack; dlg.Content = card;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(error == null ? 5 : 7) };
            bool fading = false;
            timer.Tick += (_, _) =>
            {
                if (dlg.IsMouseOver) return;
                timer.Stop(); fading = true;
                var fade = new DoubleAnimation(dlg.Opacity, 0, TimeSpan.FromMilliseconds(180));
                fade.Completed += (_, _) => { if (fading) Close(dlg); };
                dlg.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            dlg.MouseEnter += (_, _) => { timer.Stop(); fading = false; dlg.BeginAnimation(UIElement.OpacityProperty, null); dlg.Opacity = 1; };
            dlg.MouseLeave += (_, _) => { timer.Stop(); timer.Start(); };
            dlg.Closed += (_, _) => { timer.Stop(); fading = false; if (_current == dlg) _current = null; };
            dlg.SourceInitialized += (_, _) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
                int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | 0x08000000);
                SetWindowPos(hwnd, IntPtr.Zero, screen.WorkingArea.Left + 20, screen.WorkingArea.Top + 20, 0, 0, 0x15);
            };
            dlg.Loaded += (_, _) =>
            {
                // 使用物理坐标定位，兼容负坐标和不同 DPI 的副屏。
                var hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
                var area = screen.WorkingArea;
                if (GetWindowRect(hwnd, out var rect))
                    SetWindowPos(hwnd, IntPtr.Zero, Math.Max(area.Left, area.Right - (rect.Right - rect.Left) - 12),
                        Math.Max(area.Top, area.Bottom - (rect.Bottom - rect.Top) - 8), 0, 0, 0x15);
                dlg.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
                timer.Start();
            };
            _current = dlg; dlg.Show();
        }

        static void Close(Window dlg)
        {
            if (dlg == _current) _current = null;
            try { dlg.Close(); } catch { }
        }
    }
}
