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
            var dim = new System.Windows.Shapes.Rectangle
            {
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, 0, 0, 0)),
                IsHitTestVisible = false
            };
            canvas.Children.Add(dim);
            var rect = new System.Windows.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8E, 0x8C, 0xD8)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 2, 2 },
                Fill = System.Windows.Media.Brushes.Transparent,
                Visibility = Visibility.Collapsed
            };
            canvas.Children.Add(rect);
            dlg.Content = canvas;

            DispatcherFrame frame = null;
            dlg.SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(dlg).Handle;
                SetWindowPos(hwnd, HwndTopmost, virtualPixels.X, virtualPixels.Y,
                    virtualPixels.Width, virtualPixels.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            };
            dlg.SizeChanged += (s, e) =>
            {
                dim.Width = Math.Max(0, canvas.ActualWidth);
                dim.Height = Math.Max(0, canvas.ActualHeight);
            };

            System.Windows.Point start = default;
            bool dragging = false;
            dlg.MouseLeftButtonDown += (s, e) =>
            {
                dragging = true;
                start = e.GetPosition(canvas);
                Canvas.SetLeft(rect, start.X);
                Canvas.SetTop(rect, start.Y);
                rect.Width = 0;
                rect.Height = 0;
                rect.Visibility = Visibility.Visible;
                dlg.CaptureMouse();
            };
            dlg.MouseMove += (s, e) =>
            {
                if (!dragging) return;
                var point = e.GetPosition(canvas);
                Canvas.SetLeft(rect, Math.Min(start.X, point.X));
                Canvas.SetTop(rect, Math.Min(start.Y, point.Y));
                rect.Width = Math.Abs(point.X - start.X);
                rect.Height = Math.Abs(point.Y - start.Y);
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
