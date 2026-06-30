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
    // 全屏区域选择遮罩：屏幕变暗，用户拖拽矩形框，松开鼠标时通过 CopyFromScreen 截取选定区域并返回 PNG byte[]。
    // 百度图片翻译服务端生成覆盖译文，故普通的 GDI 区域截取即可。
    //
    // 覆盖整个虚拟屏幕（所有显示器）。Esc 取消。必须在 UI 线程调用——
    // 内部运行嵌套 DispatcherFrame，阻塞调用者的同时保持鼠标事件响应。取消/空选区返回 null。
    internal static class RegionCaptureService
    {
        public static byte[] CaptureRegion()
        {
            if (Application.Current == null) return null;
            byte[] result = null;

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
            // 半透明遮罩必须放在 AllowsTransparency=true 的窗口内——
            // 否则 0x55 alpha 会渲染为实心黑色，CopyFromScreen 会截到遮罩本身。
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

            // 嵌套 DispatcherFrame：阻塞调用者的同时保持 UI 响应（鼠标事件），直到窗口关闭。
            DispatcherFrame frame = null;

            dlg.Loaded += (s, e) =>
            {
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
                    // 先隐藏遮罩再截图，否则 CopyFromScreen 会截到遮罩本身（全黑）。
                    // Opacity=0 立即不可见；pump 一帧让合成器真正移除后同步截取。
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

        // 返回指定 DIP 坐标处的 DPI 缩放比例（物理像素/DIP）。失败回退 1.0。
        static double GetDpiScaleAt(double dipX, double dipY)
        {
            try
            {
                var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)dipX, (int)dipY));
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) { return g.DpiX / 96.0; }
            }
            catch { return 1.0; }
        }
    }
}
