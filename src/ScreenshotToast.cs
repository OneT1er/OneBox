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
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
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
                Width = options.Compact ? 280 : 340, SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, AllowsTransparency = true, Background = Brushes.Transparent,
                Topmost = true, Focusable = false, ShowActivated = false,
                UseLayoutRounding = true, Opacity = 0, Tag = options
            };
            var fg = UiKit.FrozenBrush(UiKit.TextSecondary);
            bool failed = error != null;
            var card = new Border
            {
                CornerRadius = new CornerRadius(options.Compact || failed ? 10 : 12),
                Background = UiKit.FrozenBrush(UiKit.BgColor),
                BorderBrush = UiKit.FrozenBrush(failed ? Color.FromRgb(90, 58, 68) : UiKit.BorderColor),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(options.Compact || failed ? 8 : 8),
                Margin = new Thickness(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                    { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.4, Color = Colors.Black }
            };
            var stack = new StackPanel();

            var close = new Button
            {
                Content = IconCatalog.CreateElement(IconKey.Close, 12, fg), Width = 28, Height = 28,
                Padding = new Thickness(0), Focusable = false, ToolTip = "关闭通知",
                VerticalAlignment = VerticalAlignment.Center
            };
            UiKit.ApplyIconButtonStyle(close);
            close.MinWidth = 24; close.MinHeight = 24;
            System.Windows.Automation.AutomationProperties.SetName(close, "关闭截图通知");
            close.Click += (_, _) => Close(dlg);
            DockPanel.SetDock(close, Dock.Right);

            void OpenFolder()
            {
                if (isPreview || failed || string.IsNullOrEmpty(path)) return;
                try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); Close(dlg); }
                catch (Exception ex) { AppLog.Log("ScreenshotToast.Open", ex); }
            }

            // Round status badge: check on success, "!" on failure.
            Border Badge()
            {
                return new Border
                {
                    Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                    Background = UiKit.FrozenBrush(failed ? UiKit.DangerSurface : UiKit.SuccessSurface),
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = failed ? VerticalAlignment.Top : VerticalAlignment.Center,
                    Child = failed
                        ? (UIElement)new TextBlock { Text = "!", FontSize = 12, FontWeight = FontWeights.Bold,
                            Foreground = UiKit.FrozenBrush(UiKit.DangerColor),
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                        : UiKit.Glyph(UiKit.Glyphs.Check, 12, UiKit.FrozenBrush(UiKit.SuccessColor), 3)
                };
            }

            // Rounded thumbnail via ImageBrush so the image follows the radius.
            Border Thumb(ImageSource source, double width, double height, double radius)
            {
                var b = new Border
                {
                    Height = height, CornerRadius = new CornerRadius(radius),
                    Background = source != null
                        ? new ImageBrush(source) { Stretch = Stretch.UniformToFill }
                        : (Brush)UiKit.FrozenBrush(UiKit.CardColor)
                };
                if (!double.IsNaN(width)) b.Width = width;
                return b;
            }

            string title = isPreview ? "截图通知预览" : failed ? "截图失败" : "截图已保存";
            if (failed)
            {
                var row = new DockPanel { LastChildFill = true, Margin = new Thickness(4) };
                row.Children.Add(close);
                var badge = Badge(); DockPanel.SetDock(badge, Dock.Left); row.Children.Add(badge);
                var texts = new StackPanel();
                texts.Children.Add(UiKit.Text(title, 12, UiKit.TextPrimary, FontWeights.SemiBold));
                var detail = new TextBlock { Text = error, Foreground = fg, FontSize = 11, LineHeight = 15,
                    TextWrapping = TextWrapping.Wrap, MaxHeight = 130, TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = error, Margin = new Thickness(0, 2, 0, 0) };
                texts.Children.Add(detail);
                row.Children.Add(texts);
                stack.Children.Add(row);
            }
            else
            {
                string description = (string.IsNullOrWhiteSpace(appName) ? "截图" : appName) +
                    (string.IsNullOrWhiteSpace(source) ? "" : " · " + source);
                string fileName = isPreview ? "截图预览.png" : Path.GetFileName(path);
                ImageSource thumb = isPreview ? CreatePreviewImage() : ScreenshotService.LoadThumbnail(path, options.Compact ? 128 : 544, 280);
                if (options.Compact)
                {
                    var row = new DockPanel { LastChildFill = true };
                    row.Children.Add(close);
                    var t = Thumb(thumb, 64, 36, 6);
                    t.Margin = new Thickness(0, 0, 10, 0);
                    DockPanel.SetDock(t, Dock.Left); row.Children.Add(t);
                    var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    texts.Children.Add(UiKit.Text(title, 12, UiKit.TextPrimary, FontWeights.SemiBold));
                    texts.Children.Add(UiKit.Text(isPreview ? "预览不会保存文件" : "点击打开文件夹", 11, UiKit.TextSecondary));
                    row.Children.Add(texts);
                    row.ToolTip = isPreview ? "预览不会保存文件" : path;
                    if (!isPreview)
                    {
                        row.Cursor = Cursors.Hand;
                        row.Background = Brushes.Transparent;
                        row.MouseLeftButtonUp += (_, e) => { if (!close.IsMouseOver) OpenFolder(); };
                    }
                    stack.Children.Add(row);
                }
                else
                {
                    if (thumb != null) stack.Children.Add(Thumb(thumb, double.NaN, 168, 8));
                    var row = new DockPanel { LastChildFill = true, Margin = new Thickness(6, 10, 0, 4) };
                    row.Children.Add(close);
                    var open = new Button { Content = "打开文件夹", Height = 30, Focusable = false,
                        Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(8, 0, 2, 0),
                        IsEnabled = !isPreview, ToolTip = isPreview ? "预览不会保存文件" : null,
                        VerticalAlignment = VerticalAlignment.Center };
                    UiKit.ApplyButtonVariant(open, ButtonVariant.Secondary);
                    open.Click += (_, _) => OpenFolder();
                    DockPanel.SetDock(open, Dock.Right); row.Children.Add(open);
                    var badge = Badge(); DockPanel.SetDock(badge, Dock.Left); row.Children.Add(badge);
                    var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    texts.Children.Add(UiKit.Text(title, 13, UiKit.TextPrimary, FontWeights.SemiBold));
                    var meta = UiKit.Text(description + " · " + fileName, 11, UiKit.TextSecondary);
                    meta.ToolTip = isPreview ? description : path;
                    texts.Children.Add(meta);
                    row.Children.Add(texts);
                    stack.Children.Add(row);
                }
            }

            // Remaining-time strip; restarts with the timer, pauses on hover.
            var progressScale = new ScaleTransform(1, 1);
            var progress = new Border
            {
                Height = 2, CornerRadius = new CornerRadius(1),
                Background = UiKit.FrozenBrush(failed ? UiKit.DangerColor : UiKit.AccentColor),
                Margin = new Thickness(6, 6, 6, 0),
                RenderTransform = progressScale,
                RenderTransformOrigin = new Point(0, 0.5)
            };
            var progressTrack = new Border { Background = UiKit.FrozenBrush(UiKit.CardColor), CornerRadius = new CornerRadius(1), Height = 2, Margin = new Thickness(6, 6, 6, 0) };
            var progressHost = new Grid();
            progressHost.Children.Add(progressTrack);
            progressHost.Children.Add(progress);
            stack.Children.Add(progressHost);
            void RestartProgress()
            {
                progressScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(1, 0, TimeSpan.FromSeconds(options.DurationSeconds)));
            }
            void PauseProgress()
            {
                progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                progressScale.ScaleX = 1;
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
            dlg.MouseEnter += (_, _) => { timer.Stop(); fading = false; dlg.BeginAnimation(UIElement.OpacityProperty, null); dlg.Opacity = 1; PauseProgress(); };
            dlg.MouseLeave += (_, _) => { timer.Stop(); timer.Start(); RestartProgress(); };
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
            bool placementQueued = false;
            bool revealed = false;
            void QueuePlacement()
            {
                if (!dlg.IsLoaded || placementQueued) return;
                placementQueued = true;
                // Loaded 仍处于首次 Show/SizeToContent 布局中；此时原生定位会被 WPF
                // 恢复为 SourceInitialized 的位置。等布局和渲染完成后再按最终像素尺寸定位。
                dlg.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    placementQueued = false;
                    if (!dlg.IsVisible) return;
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
                    if (!GetWindowRect(hwnd, out var rect)) return;
                    var area = screen.WorkingArea;
                    var origin = options.GetOrigin(area, rect.Right - rect.Left, rect.Bottom - rect.Top);
                    if (!SetWindowPos(hwnd, IntPtr.Zero, origin.X, origin.Y, 0, 0, 0x15))
                        AppLog.Log("ScreenshotToast", "Position failed: " + Marshal.GetLastWin32Error());
                    if (!revealed)
                    {
                        revealed = true;
                        dlg.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
                        timer.Start();
                        RestartProgress();
                    }
                }));
            }
            dlg.Loaded += (_, _) => QueuePlacement();
            dlg.ContentRendered += (_, _) => QueuePlacement();
            dlg.SizeChanged += (_, _) => QueuePlacement();
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
                dc.DrawRectangle(UiKit.FrozenBrush(Color.FromRgb(44, 54, 70)), null, new Rect(0, 0, 272, 140));
                dc.DrawEllipse(UiKit.FrozenBrush(ThemeTokens.Accent), null, new Point(206, 36), 16, 16);
                dc.DrawGeometry(UiKit.FrozenBrush(Color.FromRgb(90, 83, 135)), null,
                    Geometry.Parse("M0,140 L80,48 L144,112 L190,72 L272,140 Z"));
            }
            drawing.Freeze();
            return new DrawingImage(drawing);
        }
    }
}
