using System;
using System.Collections.Generic;
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
        const string GameBarDirPrefKey = "Screenshot.GameBarDir";
        const string GameBarHotkeyPrefKey = "Screenshot.GameBarHotkey";
        const string GameBarEnabledPrefKey = "Screenshot.GameBarEnabled";
        const string DefaultRoot = "OneBoxScreenshots";
        // Game Bar can take a while to write its capture — especially under HDR,
        // where it captures, tonemaps/encodes, and writes both .png and .jxr.
        // Observed ~16s for an HDR screenshot on a fast machine, so allow up to 25s
        // (conditional: returns as soon as a file appears). Runs on a threadpool
        // thread so this never blocks the UI/hotkey loop.
        const int GameBarWaitMs = 25000;

        // ---- Win32 ----
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr extraInfo);
        // OpenProcess + QueryFullProcessImageName: more reliable than Process.MainModule
        // (which throws Win32Exception for elevated / system processes the user can't
        // OpenProcess with QUERY_LIMITED_INFORMATION). This reads most processes and
        // avoids the "Unknown" folder for protected foreground apps.
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder buf, ref uint size);
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        // ---- HDR display detection (via Vortice.DXGI) ----
        // True if the monitor hosting the foreground window is currently in HDR
        // (HDR10 color space). The previous hand-written DXGI vtable interop had a
        // wrong index and raised an uncatchable AccessViolationException (crash on
        // every screenshot); Vortice generates the COM bindings from the headers so
        // the vtable is correct. Best-effort: any failure returns false, and the
        // image-quality heuristics below are the safety net.
        static bool IsHdrDisplay(IntPtr hwnd)
        {
            try
            {
                if (!GetClientRect(hwnd, out var cr)) return false;
                var c = new POINT { X = (cr.Left + cr.Right) / 2, Y = (cr.Top + cr.Bottom) / 2 };
                ClientToScreen(hwnd, ref c);

                var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
                try
                {
                    uint ai = 0;
                    while (true)
                    {
                        if (factory.EnumAdapters1(ai, out var adapter).Failure || adapter == null) break;
                        try
                        {
                            uint oi = 0;
                            while (true)
                            {
                                if (adapter.EnumOutputs(oi, out var output).Failure || output == null) break;
                                try
                                {
                                    var o6 = output.QueryInterface<Vortice.DXGI.IDXGIOutput6>();
                                    if (o6 != null)
                                    {
                                        try
                                        {
                                            var d = o6.Description1;
                                            // HDR10: DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 == 12.
                                            if (d.AttachedToDesktop &&
                                                d.ColorSpace == Vortice.DXGI.ColorSpaceType.RgbFullG2084NoneP2020 &&
                                                c.X >= d.DesktopCoordinates.Left && c.X < d.DesktopCoordinates.Right &&
                                                c.Y >= d.DesktopCoordinates.Top && c.Y < d.DesktopCoordinates.Bottom)
                                                return true;
                                        }
                                        finally { o6.Dispose(); }
                                    }
                                }
                                finally { output.Dispose(); }
                                oi++;
                            }
                        }
                        finally { adapter.Dispose(); }
                        ai++;
                    }
                }
                finally { factory.Dispose(); }
                return false;
            }
            catch (Exception ex) { AppLog.Log("Screenshot HDR detect", ex); return false; }
        }

        const byte VK_LWIN = 0x5B;
        const byte VK_RWIN = 0x5C;
        const byte VK_MENU = 0x12;   // Alt
        const byte VK_CONTROL = 0x11;
        const byte VK_SHIFT = 0x10;
        const byte VK_SNAPSHOT = 0x2C; // PrintScreen
        const uint KEYEVENTF_KEYDOWN = 0;
        const uint KEYEVENTF_KEYUP = 0x0002;

        // Hotkey encoding (shared with HotkeyCaptureDialog): high 16 bits = mods
        // (bit0 Alt, bit1 Ctrl, bit2 Shift, bit3 Win), low 16 bits = VK code.
        // Injects the combo via keybd_event: modifiers down, key down, then reverse.
        // Modifiers WITHOUT Win are what makes this work in a game's foreground —
        // injected Win is swallowed, but Alt/Ctrl/Shift are not.
        static void SendGameBarHotkey(int encoded)
        {
            int mods = (encoded >> 16) & 0xFFFF;
            int vk = encoded & 0xFFFF;
            var downs = new System.Collections.Generic.List<byte>();
            if ((mods & 2) != 0) downs.Add(VK_CONTROL);
            if ((mods & 1) != 0) downs.Add(VK_MENU);
            if ((mods & 4) != 0) downs.Add(VK_SHIFT);
            if ((mods & 8) != 0) downs.Add(VK_LWIN);
            // press modifiers
            foreach (var m in downs) keybd_event(m, 0, KEYEVENTF_KEYDOWN, IntPtr.Zero);
            // key down + up
            keybd_event((byte)vk, 0, KEYEVENTF_KEYDOWN, IntPtr.Zero);
            keybd_event((byte)vk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
            // release modifiers in reverse
            for (int i = downs.Count - 1; i >= 0; i--) keybd_event(downs[i], 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        }

        static string FormatHotkey(int encoded)
        {
            int mods = (encoded >> 16) & 0xFFFF;
            int vk = encoded & 0xFFFF;
            var parts = new System.Collections.Generic.List<string>();
            if ((mods & 2) != 0) parts.Add("Ctrl");
            if ((mods & 1) != 0) parts.Add("Alt");
            if ((mods & 4) != 0) parts.Add("Shift");
            if ((mods & 8) != 0) parts.Add("Win");
            parts.Add("VK0x" + vk.ToString("X2"));
            return string.Join("+", parts.ToArray());
        }

        // Fired (on the UI thread) after a screenshot is saved, so the gallery
        // strip can refresh to include the new capture.
        public static event Action Captured;

        public static void CaptureForeground()
        {
            string exeName = null;
            string savedPath = null;
            string error = null;
            string source = null;
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) { error = "无可截取的前台窗口"; AppLog.Log("Screenshot", "fail: no foreground window"); goto done; }

                exeName = GetExeName(hwnd);
                if (exeName == null) exeName = "Unknown";

                // Client area in screen coordinates.
                if (!GetClientRect(hwnd, out var cr) || cr.Right <= cr.Left || cr.Bottom <= cr.Top)
                { error = "前台窗口无客户区"; AppLog.Log("Screenshot", $"fail: no client area, app={exeName}"); goto done; }
                var tl = new POINT { X = cr.Left, Y = cr.Top };
                var br = new POINT { X = cr.Right, Y = cr.Bottom };
                ClientToScreen(hwnd, ref tl);
                ClientToScreen(hwnd, ref br);
                int x = tl.X, y = tl.Y;
                int w = br.X - tl.X;
                int h = br.Y - tl.Y;
                if (w <= 0 || h <= 0) { error = "前台窗口无客户区"; AppLog.Log("Screenshot", $"fail: empty client area, app={exeName}"); goto done; }

                AppLog.Log("Screenshot", $"start app={exeName} size={w}x{h}");

                // Game Bar screenshot is an ADVANCED, opt-in feature (default off).
                // When off, we just CopyFromScreen — simple and works for normal
                // windows (HDR content may come back black/washed, since GDI can't
                // read HDR surfaces, but that's the trade-off for staying simple).
                // When on, we detect HDR displays and fall back to Game Bar (which is
                // HDR-aware and can capture game windows whose Win-key the OS swallows).
                bool gameBarOn = AppPrefs.GetBool(GameBarEnabledPrefKey, false);

                // Is the foreground window on an HDR (HDR10) monitor right now?
                // Best-effort DXGI probe; on failure we lean on the image heuristics.
                bool hdr = IsHdrDisplay(hwnd);
                AppLog.Log("Screenshot", $"hdr={hdr} gameBar={gameBarOn} app={exeName}");

                if (gameBarOn && hdr)
                {
                    // HDR surfaces are unreadable by GDI CopyFromScreen (comes back black/
                    // washed), so skip it and go straight to Game Bar, which is HDR-aware:
                    // it tonemaps to a correct SDR .png and, if the user enabled "capture in
                    // HDR", also emits a .jxr we preserve. This is the pragmatic HDR path.
                    savedPath = CaptureViaGameBar(exeName);
                    if (savedPath != null)
                    {
                        source = "Game Bar (HDR)";
                        AppLog.Log("Screenshot", $"ok source=GameBar-HDR app={exeName} saved={savedPath}");
                    }
                    else
                    {
                        error = "HDR 截图失败：Game Bar 未生成文件。请在系统设置→游戏中启用 Game Bar 捕获，并在 OneBox 设置里配好 Game Bar 截图快捷键。";
                        AppLog.Log("Screenshot", $"fail: HDR + Game Bar produced no file, app={exeName}");
                    }
                    goto done;
                }

                // 1) CopyFromScreen.
                Bitmap bmp = null;
                bool bad = false;
                try
                {
                    bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h), CopyPixelOperation.SourceCopy);
                    }
                    int blackPct; double stdDev, mean;
                    SampleQuality(bmp, out blackPct, out stdDev, out mean);
                    // All-black -> GDI-unreadable surface (fullscreen-exclusive game / HDR read fail).
                    bool black = blackPct >= 99;
                    // Flat field (near-zero luminance variance) -> failed read that came back
                    // grey/washed instead of black (typical of windowed HDR content under CopyFromScreen).
                    // Only applied on HDR displays (non-HDR CopyFromScreen doesn't fail this way, and a
                    // global flat check would misfire on legit uniform images like blank white pages).
                    // Exclude near-white fields: failed reads are grey/dark, legit uniform images are
                    // often bright, so this keeps e.g. a blank PDF page out of the fallback path.
                    bool flat = hdr && stdDev < 8.0 && mean < 240.0;
                    if (hdr) { black = blackPct >= 92; } // more aggressive on HDR
                    bad = black || flat;
                    AppLog.Log("Screenshot", $"quality blackPct={blackPct} stdDev={stdDev:0.0} mean={mean:0} bad={bad} app={exeName}");
                }
                catch (Exception ex) { AppLog.Log("Screenshot capture", ex); error = $"截图失败: {ex.Message}"; if (bmp != null) bmp.Dispose(); goto done; }

                if (gameBarOn && bad)
                {
                    // GDI-unreadable surface (fullscreen game / HDR content). Fall back to Game Bar,
                    // which is HDR-aware; if the user enabled "capture in HDR" it also emits a .jxr
                    // we preserve alongside the SDR .png preview.
                    AppLog.Log("Screenshot", $"CopyFromScreen unreadable (black/flat{(hdr ? "/HDR" : "")}), falling back to Game Bar, app={exeName}");
                    bmp.Dispose();
                    savedPath = CaptureViaGameBar(exeName);
                    if (savedPath == null)
                    {
                        error = "截图不可读（可能是全屏游戏或 HDR 内容），且 Game Bar 未生成文件。请在系统设置→游戏中启用 Game Bar 捕获，并在 OneBox 设置里配好 Game Bar 截图快捷键。";
                        AppLog.Log("Screenshot", $"fail: unreadable + Game Bar produced no file, app={exeName}");
                    }
                    else
                    {
                        source = "Game Bar";
                        AppLog.Log("Screenshot", $"ok source=GameBar app={exeName} saved={savedPath}");
                    }
                }
                else
                {
                    savedPath = SaveBitmap(bmp, exeName);
                    source = "CopyFromScreen";
                    AppLog.Log("Screenshot", $"ok source=CopyFromScreen app={exeName} saved={savedPath}");
                    bmp.Dispose();
                }
            }
            catch (Exception ex) { AppLog.Log("ScreenshotService", ex); error = $"截图失败: {ex.Message}"; }

        done:
            // Show toast on the UI thread.
            string toastSource = source;
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (savedPath != null)
                {
                    ScreenshotToast.Show(exeName ?? "截图", savedPath, toastSource);
                    if (Captured != null) Captured();
                }
                else if (error != null) ScreenshotToast.ShowError(error);
            }));
        }

        // Sample the bitmap for two quality signals:
        //  - blackPct: % of sampled pixels that are near-black (R+G+B < 24). A high
        //    value means GDI read a fullscreen-exclusive / HDR surface as black.
        //  - stdDev:   population stddev of per-sample luminance. A near-zero value
        //    means a flat field — a failed read that came back uniform grey/white
        //    instead of black (windowed HDR content under CopyFromScreen).
        // Sampling every 8px keeps it cheap on 4K captures. Uses GetPixel (no unsafe
        // block) so the project stays on the /unsafe-free csc command line.
        static void SampleQuality(Bitmap bmp, out int blackPct, out double stdDev, out double mean)
        {
            blackPct = 0; stdDev = 0; mean = 0;
            try
            {
                int step = 8;
                long sum = 0; double sumSq = 0; int samples = 0, black = 0;
                for (int y = 0; y < bmp.Height; y += step)
                {
                    for (int x = 0; x < bmp.Width; x += step)
                    {
                        var c = bmp.GetPixel(x, y);
                        int lum = (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
                        samples++;
                        sum += lum;
                        sumSq += (double)lum * lum;
                        if ((int)c.R + c.G + c.B < 24) black++;
                    }
                }
                if (samples == 0) return;
                blackPct = black * 100 / samples;
                mean = (double)sum / samples;
                stdDev = Math.Sqrt(sumSq / samples - mean * mean);
            }
            catch { }
        }

        // Resolve the foreground window's owning process exe name (no extension),
        // with invalid path chars stripped so it's safe as a folder name. Uses
        // QueryFullProcessImageName (PROCESS_QUERY_LIMITED_INFORMATION) which works
        // for elevated/UWP-store apps where Process.MainModule throws access denied.
        static string GetExeName(IntPtr hwnd)
        {
            try
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0) return null;
                var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return null;
                try
                {
                    var sb = new System.Text.StringBuilder(1024);
                    uint size = 1024;
                    if (!QueryFullProcessImageName(h, 0, sb, ref size)) return null;
                    string name = sb.ToString();
                    if (string.IsNullOrEmpty(name)) return null;
                    name = Path.GetFileNameWithoutExtension(name);
                    return Sanitize(name);
                }
                finally { CloseHandle(h); }
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

        static string SaveBitmap(Bitmap bmp, string exeName)
        {
            string dir = Path.Combine(RootDir(), exeName);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
            bmp.Save(path, ImageFormat.Png);
            return path;
        }

        // Trigger Game Bar screenshot (Win+Alt+PrtScn), then watch the Captures
        // folder for freshly-created files and move/copy them into the per-app
        // folder. Game Bar is HDR-aware: it emits an SDR .png (always) and, if the
        // user enabled "capture in HDR", a .jxr (HDR) too. We keep the .png as the
        // preview for the toast/gallery and preserve the .jxr alongside it.
        // Returns the preview path (.png, or .jxr if that's all Game Bar produced).
        static string CaptureViaGameBar(string exeName)
        {
            string capturesDir = GameBarDir();
            if (!Directory.Exists(capturesDir)) { AppLog.Log("Screenshot", $"GameBar fallback: Captures dir not found: {capturesDir}"); return null; }
            AppLog.Log("Screenshot", $"GameBar fallback: dir={capturesDir}");

            // Snapshot existing files before triggering.
            var before = new HashSet<string>();
            foreach (var f in Directory.GetFiles(capturesDir)) before.Add(f);
            AppLog.Log("Screenshot", $"GameBar fallback: before={before.Count} files");

            // Trigger Game Bar's screenshot shortcut. The default is Win+Alt+PrtScn,
            // but the Win key is swallowed when a game is in the foreground (game mode
            // / low-level hooks filter injected Win-key events — verified: injected
            // VK_LWIN doesn't even register in GetAsyncKeyState). So we let the user
            // remap Game Bar's screenshot shortcut to a Win-less combo (e.g. Alt+F12)
            // and inject THAT instead. Falls back to Win+Alt+PrtScn if unset.
            int hk = AppPrefs.GetInt(GameBarHotkeyPrefKey, 0);
            if (hk != 0) SendGameBarHotkey(hk);
            else
            {
                keybd_event(VK_LWIN, 0, KEYEVENTF_KEYDOWN, IntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYDOWN, IntPtr.Zero);
                keybd_event(VK_SNAPSHOT, 0, KEYEVENTF_KEYDOWN, IntPtr.Zero);
                keybd_event(VK_SNAPSHOT, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
                keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
            }
            AppLog.Log("Screenshot", $"GameBar fallback: sent {(hk != 0 ? FormatHotkey(hk) : "Win+Alt+PrtScn")}");

            // Poll for new image files. Phase 1: wait for at least one (.png or .jxr).
            string png = null, jxr = null;
            int waited = 0;
            while (waited < GameBarWaitMs)
            {
                Thread.Sleep(200); waited += 200;
                ScanNewCaptures(capturesDir, before, ref png, ref jxr);
                if (png != null || jxr != null) break;
            }
            // Phase 2: if we already have a .png, give Game Bar a moment more to emit
            // the HDR .jxr (it sometimes lags the .png by a second or two).
            if (png != null && jxr == null)
            {
                int extra = 0;
                while (extra < 1500)
                {
                    Thread.Sleep(200); extra += 200; waited += 200;
                    ScanNewCaptures(capturesDir, before, ref png, ref jxr);
                    if (jxr != null) break;
                }
            }
            if (png == null && jxr == null) { AppLog.Log("Screenshot", $"GameBar fallback: no new file within {waited}ms, app={exeName} (dir now has {Directory.GetFiles(capturesDir).Length} files)"); return null; }
            AppLog.Log("Screenshot", $"GameBar fallback: png={png != null} jxr={jxr != null} after {waited}ms app={exeName}");

            // Copy BOTH the .png (preview) and .jxr (HDR) into the per-app folder,
            // leaving Game Bar's originals in place. Previously we *moved* the .png
            // out of the Captures dir — but Game Bar watches that folder and removing
            // its files mid-session appeared to make it stop responding to further
            // Win+Alt+PrtScn triggers (every capture after the first produced no
            // file). Copying keeps Game Bar's state intact. WaitForFileReady (in
            // CopyInto) guards against grabbing a half-written file.
            try
            {
                string dir = Path.Combine(RootDir(), exeName);
                Directory.CreateDirectory(dir);
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string preview = null;
                if (png != null) preview = CopyInto(png, Path.Combine(dir, ts + ".png"));
                string jxrDest = null;
                if (jxr != null) jxrDest = CopyInto(jxr, Path.Combine(dir, ts + "_hdr.jxr"));
                AppLog.Log("Screenshot", $"GameBar harvest preview={preview != null} jxr={jxrDest != null} app={exeName}");
                // If Game Bar only produced a .jxr (HDR capture on, no SDR png), use it
                // as the preview; WIC may decode JPEG-XR for the toast thumbnail.
                if (preview == null && jxrDest != null) preview = jxrDest;
                return preview;
            }
            catch (Exception ex) { AppLog.Log("Screenshot GameBar move", ex); return null; }
        }

        static void ScanNewCaptures(string dir, HashSet<string> before, ref string png, ref string jxr)
        {
            foreach (var f in Directory.GetFiles(dir))
            {
                if (before.Contains(f)) continue;
                if (f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { if (png == null) png = f; }
                else if (f.EndsWith(".jxr", StringComparison.OrdinalIgnoreCase)) { if (jxr == null) jxr = f; }
            }
        }

        // Game Bar writes its capture files progressively — a .png/.jxr appears in
        // the directory while still being written. Copying/moving at that moment
        // grabs a truncated file (observed: a 14MB .jxr copied as 1186 bytes).
        // Wait until the file size is stable across two reads ~150ms apart AND the
        // file can be opened for reading (no exclusive write lock), with a cap so
        // we never hang. Returns true when the file looks complete.
        static bool WaitForFileReady(string path, int timeoutMs)
        {
            long prev = -1;
            int waited = 0;
            while (waited < timeoutMs)
            {
                try
                {
                    var len = new FileInfo(path).Length;
                    if (len > 0 && len == prev)
                    {
                        // size stable — also confirm we can open it for reading
                        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) { }
                        return true;
                    }
                    prev = len;
                }
                catch { prev = -1; }
                Thread.Sleep(150); waited += 150;
            }
            return false;
        }

        static string CopyInto(string src, string dest)
        {
            WaitForFileReady(src, 8000);
            for (int i = 0; i < 10; i++)
            {
                try { File.Copy(src, dest, true); return dest; }
                catch (IOException) { Thread.Sleep(150); }
                catch { return null; }
            }
            return null;
        }

        // Encode a saved PNG path to a frozen WPF thumbnail source for the toast.
        public static BitmapSource LoadThumbnail(string path, int maxW, int maxH)
        {
            try
            {
                // Use a FileStream (StreamSource) instead of UriSource: UriSource
                // mangles non-ASCII paths (e.g. 中文 "图片" in the root dir) and the
                // BitmapImage silently fails to load -> empty gallery.
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
                bmp.DecodePixelWidth = maxW;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        // The N most recent screenshots across all per-app subfolders (newest first),
        // for the embedded gallery preview in the floating window.
        public static System.Collections.Generic.List<string> GetRecent(int count)
        {
            var list = new System.Collections.Generic.List<string>();
            try
            {
                if (count <= 0) return list;
                var root = RootDir();
                if (!System.IO.Directory.Exists(root)) return list;
                var files = new System.Collections.Generic.List<string>(
                    System.IO.Directory.GetFiles(root, "*.png", System.IO.SearchOption.AllDirectories));
                files.Sort((a, b) => System.IO.File.GetLastWriteTime(b).CompareTo(System.IO.File.GetLastWriteTime(a)));
                for (int i = 0; i < files.Count && i < count; i++) list.Add(files[i]);
            }
            catch { }
            return list;
        }

        public static string RootDir()
        {
            var s = AppPrefs.GetString(RootPrefKey, "");
            if (string.IsNullOrWhiteSpace(s))
                s = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures), DefaultRoot);
            return s;
        }

        // Where Game Bar (Win+Alt+PrtScn) writes its captures. Default is
        // <Videos>\Captures, but the user can redirect the Videos library or change
        // the Game Bar capture folder, so the default often points nowhere. Let the
        // user override it in Settings; empty/missing falls back to the default.
        public static string GameBarDir()
        {
            var s = AppPrefs.GetString(GameBarDirPrefKey, "");
            if (string.IsNullOrWhiteSpace(s))
                s = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyVideos), "Captures");
            return s;
        }
    }
}
