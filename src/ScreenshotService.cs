using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PowerAudioManager
{
    // Foreground-window screenshot service.
    //
    // Strategy (per the design):
    //  1. Capture the foreground window's client area with Graphics.CopyFromScreen
    //     (native resolution under Per-Monitor V2 — physical pixels).
    //  2. If the result is (near-)all-black, the window is likely a fullscreen
    //     exclusive game that GDI can't read — fall back to Game Bar
    //     (Win+Alt+PrtScn), watch the Captures folder, and move the file into the
    //     per-app subfolder.
    //  3. Otherwise save the captured PNG to <root>\<exe>\<timestamp>.png.
    //  4. Pop a Steam-style bottom-right toast with the thumbnail + path.
    //
    // Runs on a threadpool thread (invoked from the WM_HOTKEY handler) so the
    // hotkey loop is never blocked. The toast marshals back to the UI thread.
    internal static class ScreenshotService
    {
        const string RootPrefKey = "Screenshot.RootDir";
        const string DefaultRoot = "OneBoxScreenshots";
        const int GameBarWaitMs = 3000;

        // ---- Win32 ----
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr extraInfo);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        const byte VK_LWIN = 0x5B;
        const byte VK_RWIN = 0x5C;
        const byte VK_MENU = 0x12;   // Alt
        const byte VK_SNAPSHOT = 0x2C; // PrintScreen
        const uint KEYEVENTF_KEYDOWN = 0;
        const uint KEYEVENTF_KEYUP = 0x0002;

        public static void CaptureForeground()
        {
            string exeName = null;
            string savedPath = null;
            string error = null;
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) { error = "无可截取的前台窗口"; goto done; }

                exeName = GetExeName(hwnd);
                if (exeName == null) exeName = "Unknown";

                // Client area in screen coordinates.
                RECT cr;
                if (!GetClientRect(hwnd, out cr) || cr.Right <= cr.Left || cr.Bottom <= cr.Top)
                { error = "前台窗口无客户区"; goto done; }
                var tl = new POINT { X = cr.Left, Y = cr.Top };
                var br = new POINT { X = cr.Right, Y = cr.Bottom };
                ClientToScreen(hwnd, ref tl);
                ClientToScreen(hwnd, ref br);
                int x = tl.X, y = tl.Y;
                int w = br.X - tl.X;
                int h = br.Y - tl.Y;
                if (w <= 0 || h <= 0) { error = "前台窗口无客户区"; goto done; }

                // 1) CopyFromScreen.
                Bitmap bmp = null;
                bool black = false;
                try
                {
                    bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h), CopyPixelOperation.SourceCopy);
                    }
                    black = IsMostlyBlack(bmp);
                }
                catch (Exception ex) { AppLog.Log("Screenshot capture", ex); error = "截图失败: " + ex.Message; if (bmp != null) bmp.Dispose(); goto done; }

                if (black)
                {
                    // Likely fullscreen-exclusive game — try Game Bar.
                    bmp.Dispose();
                    savedPath = CaptureViaGameBar(exeName);
                    if (savedPath == null)
                        error = "截图为黑屏（可能是全屏游戏），且 Game Bar 未生成文件。请在系统设置→游戏中启用 Game Bar 捕获。";
                }
                else
                {
                    savedPath = SaveBitmap(bmp, exeName);
                    bmp.Dispose();
                }
            }
            catch (Exception ex) { AppLog.Log("ScreenshotService", ex); error = "截图失败: " + ex.Message; }

        done:
            // Show toast on the UI thread.
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (savedPath != null) ScreenshotToast.Show(exeName ?? "截图", savedPath);
                else if (error != null) ScreenshotToast.ShowError(error);
            }));
        }

        // Sample the bitmap; if ~all sampled pixels are near-black, treat as a
        // GDI-unreadable fullscreen surface. Sampling every 8px keeps it cheap on
        // 4K captures. Uses GetPixel (no unsafe block) so the project stays on the
        // /unsafe-free csc command line.
        static bool IsMostlyBlack(Bitmap bmp)
        {
            try
            {
                int step = 8;
                int samples = 0, black = 0;
                for (int y = 0; y < bmp.Height; y += step)
                {
                    for (int x = 0; x < bmp.Width; x += step)
                    {
                        var c = bmp.GetPixel(x, y);
                        samples++;
                        if ((int)c.R + c.G + c.B < 24) black++;
                    }
                }
                if (samples == 0) return false;
                return (black * 100 / samples) >= 99;
            }
            catch { return false; }
        }

        // Resolve the foreground window's owning process exe name (no extension),
        // with invalid path chars stripped so it's safe as a folder name.
        static string GetExeName(IntPtr hwnd)
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return null;
                var p = Process.GetProcessById((int)pid);
                string name = p.MainModule.FileName;
                p.Dispose();
                if (string.IsNullOrEmpty(name)) return null;
                name = Path.GetFileNameWithoutExtension(name);
                return Sanitize(name);
            }
            catch { return null; }
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Unknown";
            var invalid = Path.GetInvalidPathChars();
            var sb = new System.Text.StringBuilder();
            foreach (char c in s) if (Array.IndexOf(invalid, c) < 0) sb.Append(c);
            return sb.Length == 0 ? "Unknown" : sb.ToString();
        }

        static string RootDir()
        {
            var s = AppPrefs.GetString(RootPrefKey, "");
            if (string.IsNullOrWhiteSpace(s))
                s = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), DefaultRoot);
            return s;
        }

        static string SaveBitmap(Bitmap bmp, string exeName)
        {
            string dir = Path.Combine(RootDir(), exeName);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
            bmp.Save(path, ImageFormat.Png);
            return path;
        }

        // Trigger Game Bar screenshot (Win+Alt+PrtScn), then watch the Captures
        // folder for a freshly-created file and move it into the per-app folder.
        static string CaptureViaGameBar(string exeName)
        {
            string capturesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");
            if (!Directory.Exists(capturesDir)) return null;

            // Snapshot existing files before triggering.
            var before = new System.Collections.Generic.HashSet<string>();
            foreach (var f in Directory.GetFiles(capturesDir)) before.Add(f);

            // Win+Alt+PrtScn
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYDOWN, IntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYDOWN, IntPtr.Zero);
            keybd_event(VK_SNAPSHOT, 0, KEYEVENTF_KEYDOWN, IntPtr.Zero);
            keybd_event(VK_SNAPSHOT, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, IntPtr.Zero);

            // Poll for a new screenshot image (Game Bar saves .png for screenshots).
            string found = null;
            int waited = 0;
            while (waited < GameBarWaitMs)
            {
                Thread.Sleep(200);
                waited += 200;
                foreach (var f in Directory.GetFiles(capturesDir))
                {
                    if (before.Contains(f)) continue;
                    if (!f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                    found = f; break;
                }
                if (found != null) break;
            }
            if (found == null) return null;

            // Move + rename into the per-app folder.
            try
            {
                string dir = Path.Combine(RootDir(), exeName);
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                // Game Bar may still be writing the file; retry the move briefly.
                for (int i = 0; i < 10; i++)
                {
                    try { File.Move(found, dest); return dest; }
                    catch (IOException) { Thread.Sleep(150); }
                }
                return null;
            }
            catch (Exception ex) { AppLog.Log("Screenshot GameBar move", ex); return null; }
        }

        // Encode a saved PNG path to a frozen WPF thumbnail source for the toast.
        public static BitmapSource LoadThumbnail(string path, int maxW, int maxH)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = maxW;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }
}
