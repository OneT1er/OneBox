using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PowerAudioManager
{
    // Full-screen region selection overlay: the screens dim, the user drags a rectangle,
    // and on mouse-up the selected screen region is captured via CopyFromScreen and
    // returned as a PNG byte[]. The translated/overlaid image is produced server-side
    // by Baidu, so a plain GDI capture of the region is enough (same as the existing
    // screenshot path, just user-selected instead of the foreground window).
    //
    // Spans the full virtual screen (all monitors). Esc cancels. Must be called on the
    // UI thread — it runs a nested dispatcher frame so mouse events pump while blocking
    // the caller until selection completes. Returns null on cancel/empty.
    internal static class RegionCaptureService
    {
        public static byte[] CaptureRegion()
        {
            if (Application.Current == null) return null;
            byte[] result = null;

            // Span the full virtual screen (DIP units). The overlay window covers everything.
            double vx = SystemParameters.VirtualScreenLeft;
            double vy = SystemParameters.VirtualScreenTop;
            double vw = SystemParameters.VirtualScreenWidth;
            double vh = SystemParameters.VirtualScreenHeight;

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
                Left = vx, Top = vy, Width = vw, Height = vh
            };

            var canvas = new System.Windows.Controls.Canvas();
            // The dim overlay is a full-size semi-transparent rectangle INSIDE the
            // transparent window (a non-AllowsTransparency window can't actually be
            // translucent — the 0x55 alpha would render solid black, which is what got
            // captured by CopyFromScreen). The selection rectangle cuts a clear hole.
            var dim = new System.Windows.Shapes.Rectangle
            {
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, 0, 0, 0)),
                Width = vw, Height = vh
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

            // Nested dispatcher frame: keeps the UI responsive (mouse events) while blocking
            // this call until the window closes.
            DispatcherFrame frame = null;

            dlg.Loaded += (s, e) =>
            {
                // Force the window to cover the virtual screen even under per-monitor DPI.
                dlg.Left = vx; dlg.Top = vy; dlg.Width = vw; dlg.Height = vh;
            };

            System.Windows.Point start;
            bool dragging = false;

            dlg.MouseLeftButtonDown += (s, e) =>
            {
                dragging = true;
                start = e.GetPosition(canvas);
                Canvas.SetLeft(rect, start.X);
                Canvas.SetTop(rect, start.Y);
                rect.Width = 0; rect.Height = 0;
                rect.Visibility = Visibility.Visible;
                dlg.CaptureMouse();
            };
            dlg.MouseMove += (s, e) =>
            {
                if (!dragging) return;
                var p = e.GetPosition(canvas);
                Canvas.SetLeft(rect, Math.Min(start.X, p.X));
                Canvas.SetTop(rect, Math.Min(start.Y, p.Y));
                rect.Width = Math.Abs(p.X - start.X);
                rect.Height = Math.Abs(p.Y - start.Y);
            };
            dlg.MouseLeftButtonUp += (s, e) =>
            {
                if (!dragging) return;
                dragging = false;
                dlg.ReleaseMouseCapture();
                var p = e.GetPosition(canvas);
                double x1 = Math.Min(start.X, p.X);
                double y1 = Math.Min(start.Y, p.Y);
                double wDip = Math.Abs(p.X - start.X);
                double hDip = Math.Abs(p.Y - start.Y);

                if (wDip >= 4 && hDip >= 4)
                {
                    // Hide the overlay BEFORE capturing, else CopyFromScreen grabs the dim
                    // overlay itself (the selection comes back black/dark). Opacity=0 makes
                    // the window invisible immediately; we pump one render frame to let the
                    // compositor actually drop it, then capture synchronously.
                    try
                    {
                        dlg.Opacity = 0;
                        dlg.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Render);
                        result = CapturePixels(vx + x1, vy + y1, wDip, hDip);
                    }
                    catch (Exception ex) { AppLog.Log("RegionCapture", ex); }
                }
                dlg.Close();
            };
            dlg.KeyDown += (s, e) => { if (e.Key == Key.Escape) dlg.Close(); };
            dlg.Closed += (s, e) => { if (frame != null) frame.Continue = false; };

            dlg.Show();
            frame = new DispatcherFrame();
            Dispatcher.PushFrame(frame);
            return result;
        }

        // DIP rect on the virtual screen -> physical pixels -> CopyFromScreen -> PNG.
        static byte[] CapturePixels(double dipX, double dipY, double dipW, double dipH)
        {
            double scale = GetDpiScaleAt(dipX, dipY);
            int px = (int)Math.Round(dipX * scale);
            int py = (int)Math.Round(dipY * scale);
            int pw = (int)Math.Round(dipW * scale);
            int ph = (int)Math.Round(dipH * scale);
            if (pw <= 0 || ph <= 0) return null;
            using (var bmp = new Bitmap(pw, ph, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(px, py, 0, 0, new System.Drawing.Size(pw, ph), CopyPixelOperation.SourceCopy);
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        // DPI scale (physical px / DIP) at a DIP point. Uses the DPI of the monitor
        // containing that point; falls back to 1.0 (correct for 100% scaling).
        static double GetDpiScaleAt(double dipX, double dipY)
        {
            try
            {
                // Convert DIP virtual coords to a physical point for Screen.FromPoint.
                // SystemParameters are DIP; multiply by ~1 and let FromPoint pick the monitor,
                // then read that monitor's DPI via the legacy GDI DC (good enough for capture).
                var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)dipX, (int)dipY));
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) { return g.DpiX / 96.0; }
            }
            catch { return 1.0; }
        }
    }
}
