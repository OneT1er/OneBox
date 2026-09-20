using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("dwmapi.dll")] static extern int DwmFlush();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [StructLayout(LayoutKind.Sequential)] struct WindowRect { public int Left, Top, Right, Bottom; }

        public static void Show(string appName, string path, string source) =>
            Application.Current.Dispatcher.BeginInvoke(new Action(() => ShowInternal(appName, path, source, null)));
        public static void ShowError(string message) =>
            Application.Current.Dispatcher.BeginInvoke(new Action(() => ShowInternal(null, null, null, message)));

        internal static void ShowPreview(ScreenshotToastOptions options) =>
            ShowInternal("OneBox", null, "通知预览", null, options);

        internal static async Task DismissForCaptureAsync()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            await dispatcher.InvokeAsync(DismissForCapture);
        }

        internal static void DismissForCapture()
        {
            if (_current?.Tag is ScreenshotToastOptions { ExcludeFromCapture: true })
            {
                Close(_current);
                // 等待桌面合成提交，避免紧接着的 CopyFromScreen 仍读到上一帧。
                DwmFlush();
            }
        }

        static void ShowInternal(string appName, string path, string source, string error, ScreenshotToastOptions previewOptions = null)
        {
            if (_current != null) Close(_current);
            var options = (previewOptions ?? ScreenshotToastOptions.Load()).Normalize();
            bool isPreview = previewOptions != null;
            var screen = System.Windows.Forms.Screen.FromHandle(GetForegroundWindow());
            var dlg = new Window
            {
                Width = options.Compact ? 280 : 320, SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, AllowsTransparency = true, Background = Brushes.Transparent,
                Topmost = true, Focusable = false, ShowActivated = false,
                UseLayoutRounding = true, Opacity = 0, Tag = options
            };
            var fg = UiKit.FrozenBrush(ThemeTokens.SecondaryText);
            var accent = UiKit.FrozenBrush(error == null ? ThemeTokens.Accent : Color.FromRgb(240, 150, 150));
            var card = new Border
            {
                CornerRadius = new CornerRadius(12), Background = UiKit.FrozenBrush(ThemeTokens.TitleSurface),
                BorderBrush = UiKit.FrozenBrush(Color.FromRgb(76, 70, 106)), BorderThickness = new Thickness(1),
                Padding = new Thickness(options.Compact ? 10 : 14), Margin = new Thickness(10),
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
            header.Children.Add(new TextBlock { Text = isPreview ? "截图通知预览" : error == null ? "截图已保存" : "截图失败", Foreground = Brushes.White,
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
                ImageSource thumb = isPreview ? CreatePreviewImage() : ScreenshotService.LoadThumbnail(path, options.Compact ? 128 : 544, 280);
                var imageRow = new DockPanel { LastChildFill = true };
                if (thumb != null)
                {
                    var preview = new Border { Height = options.Compact ? 44 : 140, CornerRadius = new CornerRadius(6),
                        Background = UiKit.FrozenBrush(Color.FromRgb(20, 18, 28)), Padding = new Thickness(4),
                        Child = new Image { Source = thumb, Stretch = Stretch.Uniform } };
                    if (options.Compact)
                    {
                        preview.Width = 64;
                        preview.Margin = new Thickness(0, 0, 8, 0);
                        DockPanel.SetDock(preview, Dock.Left);
                        imageRow.Children.Add(preview);
                    }
                    else stack.Children.Add(preview);
                }
                var filename = new TextBlock { Text = isPreview ? "截图预览.png" : Path.GetFileName(path), ToolTip = path, Foreground = fg,
                    FontSize = 10, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, options.Compact ? 0 : 8, 0, 8) };
                if (options.Compact) { imageRow.Children.Add(filename); stack.Children.Add(imageRow); }
                else stack.Children.Add(filename);
                var open = new Button { Content = "打开文件夹", Height = 28, FontSize = 11, Focusable = false,
                    HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 0, 12, 0),
                    IsEnabled = !isPreview, ToolTip = isPreview ? "预览不会保存文件" : null };
                AppResources.StyleDialogButton(open, true);
                open.Click += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); Close(dlg); }
                    catch (Exception ex) { AppLog.Log("ScreenshotToast.Open", ex); }
                };
                stack.Children.Add(open);
            }
            card.Child = stack; dlg.Content = card;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(options.DurationSeconds) };
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
                if (options.ExcludeFromCapture && !SetWindowDisplayAffinity(hwnd, 0x00000011 /*WDA_EXCLUDEFROMCAPTURE*/))
                    AppLog.Log("ScreenshotToast", "Capture exclusion unavailable: " + Marshal.GetLastWin32Error());
                SetWindowPos(hwnd, IntPtr.Zero, screen.WorkingArea.Left + 20, screen.WorkingArea.Top + 20, 0, 0, 0x15);
            };
            dlg.Loaded += (_, _) =>
            {
                // 使用物理坐标定位，兼容负坐标和不同 DPI 的副屏。
                var hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
                var area = screen.WorkingArea;
                if (GetWindowRect(hwnd, out var rect))
                {
                    var origin = options.GetOrigin(area, rect.Right - rect.Left, rect.Bottom - rect.Top);
                    SetWindowPos(hwnd, IntPtr.Zero, origin.X, origin.Y, 0, 0, 0x15);
                }
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

        static ImageSource CreatePreviewImage()
        {
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
            {
                dc.DrawRectangle(UiKit.FrozenBrush(Color.FromRgb(38, 34, 59)), null, new Rect(0, 0, 272, 140));
                dc.DrawEllipse(UiKit.FrozenBrush(ThemeTokens.Accent), null, new Point(206, 36), 16, 16);
                dc.DrawGeometry(UiKit.FrozenBrush(Color.FromRgb(90, 83, 135)), null,
                    Geometry.Parse("M0,140 L80,48 L144,112 L190,72 L272,140 Z"));
            }
            drawing.Freeze();
            return new DrawingImage(drawing);
        }
    }
}
