using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PowerAudioManager
{
    public readonly struct CapturePixelRect
    {
        public CapturePixelRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public bool IsEmpty => Width <= 0 || Height <= 0;
    }

    public static class CaptureCoordinateMapper
    {
        // WPF 覆盖窗口的局部 DIP → 虚拟桌面物理像素。向外取整，避免漏掉选择边缘。
        public static CapturePixelRect MapDipSelection(
            double startDipX,
            double startDipY,
            double endDipX,
            double endDipY,
            int windowPixelLeft,
            int windowPixelTop,
            double scaleX,
            double scaleY,
            CapturePixelRect virtualScreen)
        {
            if (!double.IsFinite(scaleX) || scaleX <= 0) scaleX = 1;
            if (!double.IsFinite(scaleY) || scaleY <= 0) scaleY = 1;

            int left = (int)Math.Floor(windowPixelLeft + Math.Min(startDipX, endDipX) * scaleX);
            int top = (int)Math.Floor(windowPixelTop + Math.Min(startDipY, endDipY) * scaleY);
            int right = (int)Math.Ceiling(windowPixelLeft + Math.Max(startDipX, endDipX) * scaleX);
            int bottom = (int)Math.Ceiling(windowPixelTop + Math.Max(startDipY, endDipY) * scaleY);

            left = Math.Max(left, virtualScreen.X);
            top = Math.Max(top, virtualScreen.Y);
            right = Math.Min(right, virtualScreen.Right);
            bottom = Math.Min(bottom, virtualScreen.Bottom);
            return new CapturePixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        }
    }

    // 覆盖整个物理虚拟桌面；选区通过窗口实际 DPI 转换为 CopyFromScreen 所需的物理像素。
    internal static class RegionCaptureService
    {
        const int SM_XVIRTUALSCREEN = 76;
        const int SM_YVIRTUALSCREEN = 77;
        const int SM_CXVIRTUALSCREEN = 78;
        const int SM_CYVIRTUALSCREEN = 79;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_SHOWWINDOW = 0x0040;
        static readonly IntPtr HwndTopmost = new IntPtr(-1);

        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        public static byte[] CaptureRegion()
        {
            if (Application.Current == null) return null;
            ScreenshotToast.DismissForCapture();
            byte[] result = null;
            var virtualPixels = GetVirtualScreenPixels();
            if (virtualPixels.IsEmpty) return null;

            var dlg = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                ShowActivated = true,
                Cursor = Cursors.Cross,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Left = SystemParameters.VirtualScreenLeft,
                Top = SystemParameters.VirtualScreenTop,
                Width = Math.Max(1, SystemParameters.VirtualScreenWidth),
                Height = Math.Max(1, SystemParameters.VirtualScreenHeight)
            };

            var canvas = new Canvas();
            // Four dim panels surround the selection so the chosen area shows
            // the desktop undimmed; before dragging the top panel covers all.
            var dimBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x8C, 10, 8, 18));
            dimBrush.Freeze();
            System.Windows.Shapes.Rectangle Dim() => new System.Windows.Shapes.Rectangle { Fill = dimBrush, IsHitTestVisible = false };
            var dimTop = Dim(); var dimBottom = Dim(); var dimLeft = Dim(); var dimRight = Dim();
            canvas.Children.Add(dimTop); canvas.Children.Add(dimBottom); canvas.Children.Add(dimLeft); canvas.Children.Add(dimRight);

            var accent = UiKit.FrozenBrush(UiKit.AccentColor);
            var rect = new System.Windows.Shapes.Rectangle
            {
                Stroke = accent,
                StrokeThickness = 1,
                Fill = System.Windows.Media.Brushes.Transparent,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            canvas.Children.Add(rect);
            var handles = new System.Windows.Shapes.Rectangle[8];
            for (int i = 0; i < handles.Length; i++)
            {
                handles[i] = new System.Windows.Shapes.Rectangle
                {
                    Width = 6, Height = 6, Fill = UiKit.FrozenBrush(UiKit.TextPrimary), Stroke = accent, StrokeThickness = 1,
                    IsHitTestVisible = false, Visibility = Visibility.Collapsed
                };
                canvas.Children.Add(handles[i]);
            }
            var badgeText = new TextBlock { FontSize = 11, Foreground = UiKit.FrozenBrush(UiKit.TextPrimary), FontFamily = UiKit.MonoFont };
            var badge = new Border
            {
                Background = UiKit.FrozenBrush(UiKit.BgColor), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2), Child = badgeText, IsHitTestVisible = false, Visibility = Visibility.Collapsed
            };
            canvas.Children.Add(badge);
            var hint = new Border
            {
                Background = UiKit.FrozenBrush(UiKit.BgColor),
                BorderBrush = UiKit.FrozenBrush(UiKit.BorderStrongColor), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 5, 14, 5), IsHitTestVisible = false,
                Child = new TextBlock { Text = "拖动框选 · 松开完成截图 · 右键或 Esc 取消", FontSize = 12, Foreground = UiKit.FrozenBrush(UiKit.TextPrimary) }
            };
            canvas.Children.Add(hint);
            dlg.Content = canvas;

            double scaleXCached = 0, scaleYCached = 0;
            void EnsureScale()
            {
                if (scaleXCached > 0) return;
                var src = PresentationSource.FromVisual(dlg);
                scaleXCached = src?.CompositionTarget?.TransformToDevice.M11 ?? 1;
                scaleYCached = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
            }

            void Place(UIElement e, double x, double y, double w, double h)
            {
                Canvas.SetLeft(e, x); Canvas.SetTop(e, y);
                if (e is FrameworkElement fe) { fe.Width = Math.Max(0, w); fe.Height = Math.Max(0, h); }
            }

            void LayoutSelection(double x, double y, double w, double h, bool active)
            {
                double cw = Math.Max(0, canvas.ActualWidth), ch = Math.Max(0, canvas.ActualHeight);
                if (!active)
                {
                    Place(dimTop, 0, 0, cw, ch);
                    Place(dimBottom, 0, 0, 0, 0); Place(dimLeft, 0, 0, 0, 0); Place(dimRight, 0, 0, 0, 0);
                    return;
                }
                Place(dimTop, 0, 0, cw, y);
                Place(dimBottom, 0, y + h, cw, ch - y - h);
                Place(dimLeft, 0, y, x, h);
                Place(dimRight, x + w, y, cw - x - w, h);
                Place(rect, x, y, w, h);
                double[] hx = { x, x + w / 2, x + w, x + w, x + w, x + w / 2, x, x };
                double[] hy = { y, y, y, y + h / 2, y + h, y + h, y + h, y + h / 2 };
                for (int i = 0; i < handles.Length; i++)
                {
                    Canvas.SetLeft(handles[i], hx[i] - 3); Canvas.SetTop(handles[i], hy[i] - 3);
                    handles[i].Visibility = Visibility.Visible;
                }
                EnsureScale();
                badgeText.Text = Math.Round(w * scaleXCached) + " × " + Math.Round(h * scaleYCached);
                Canvas.SetLeft(badge, x);
                Canvas.SetTop(badge, y >= 26 ? y - 24 : y + 4);
                badge.Visibility = Visibility.Visible;
            }

            void PlaceHint()
            {
                // Center on the primary work area (the canvas spans every monitor).
                var wa = SystemParameters.WorkArea;
                double left = wa.Left - SystemParameters.VirtualScreenLeft + (wa.Width - hint.ActualWidth) / 2;
                double top = wa.Top - SystemParameters.VirtualScreenTop + 16;
                Canvas.SetLeft(hint, Math.Max(0, left));
                Canvas.SetTop(hint, Math.Max(0, top));
            }
            hint.SizeChanged += (s, e) => PlaceHint();

            DispatcherFrame frame = null;
            dlg.SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(dlg).Handle;
                SetWindowPos(hwnd, HwndTopmost, virtualPixels.X, virtualPixels.Y,
                    virtualPixels.Width, virtualPixels.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            };
            dlg.SizeChanged += (s, e) =>
            {
                if (rect.Visibility != Visibility.Visible) LayoutSelection(0, 0, 0, 0, false);
                PlaceHint();
            };

            System.Windows.Point start = default;
            bool dragging = false;
            dlg.MouseLeftButtonDown += (s, e) =>
            {
                dragging = true;
                start = e.GetPosition(canvas);
                rect.Visibility = Visibility.Visible;
                hint.Visibility = Visibility.Collapsed;
                LayoutSelection(start.X, start.Y, 0, 0, true);
                dlg.CaptureMouse();
            };
            dlg.MouseMove += (s, e) =>
            {
                if (!dragging) return;
                var point = e.GetPosition(canvas);
                LayoutSelection(Math.Min(start.X, point.X), Math.Min(start.Y, point.Y),
                    Math.Abs(point.X - start.X), Math.Abs(point.Y - start.Y), true);
            };
            dlg.MouseLeftButtonUp += (s, e) =>
            {
                if (!dragging) return;
                dragging = false;
                dlg.ReleaseMouseCapture();
                var end = e.GetPosition(canvas);

                try
                {
                    var source = PresentationSource.FromVisual(dlg);
                    double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
                    double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1;
                    var pixels = CaptureCoordinateMapper.MapDipSelection(
                        start.X, start.Y, end.X, end.Y,
                        virtualPixels.X, virtualPixels.Y, scaleX, scaleY, virtualPixels);
                    if (pixels.Width >= 4 && pixels.Height >= 4)
                    {
                        dlg.Opacity = 0;
                        dlg.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Render);
                        result = CapturePixels(pixels);
                    }
                }
                catch (Exception ex) { AppLog.Log("RegionCapture", ex); }
                dlg.Close();
            };
            dlg.KeyDown += (s, e) => { if (e.Key == Key.Escape) dlg.Close(); };
            dlg.MouseRightButtonUp += (s, e) => { if (!dragging) dlg.Close(); };
            dlg.Closed += (s, e) => { if (frame != null) frame.Continue = false; };

            dlg.Show();
            frame = new DispatcherFrame();
            Dispatcher.PushFrame(frame);
            return result;
        }

        static CapturePixelRect GetVirtualScreenPixels()
        {
            return new CapturePixelRect(
                GetSystemMetrics(SM_XVIRTUALSCREEN),
                GetSystemMetrics(SM_YVIRTUALSCREEN),
                GetSystemMetrics(SM_CXVIRTUALSCREEN),
                GetSystemMetrics(SM_CYVIRTUALSCREEN));
        }

        static byte[] CapturePixels(CapturePixelRect pixels)
        {
            if (pixels.IsEmpty) return null;
            using var bitmap = new Bitmap(pixels.Width, pixels.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(pixels.X, pixels.Y, 0, 0,
                    new System.Drawing.Size(pixels.Width, pixels.Height), CopyPixelOperation.SourceCopy);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }
}
