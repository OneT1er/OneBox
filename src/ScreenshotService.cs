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
    // 前台窗口截图服务。
    // 策略：
    //  1. Graphics.CopyFromScreen 截取前台窗口客户区（Per-Monitor V2 下为物理像素）。
    //  2. 若结果为（接近）全黑，则窗口很可能是 GDI 无法读取的全屏独占游戏——
    //     回退到 Game Bar（Win+Alt+PrtScn），监听 Captures 文件夹，将文件移入对应应用子目录。
    //  3. 否则将截图保存为 <root>\<exe>\<timestamp>.png。
    //  4. 弹出 Steam 风格的右下角 Toast，显示缩略图 + 路径。
    //
    // 在 ThreadPool 上运行（从 WM_HOTKEY 触发），保证热键循环不阻塞。Toast 通过 UI 线程回调。
    internal static class ScreenshotService
    {
        const string RootPrefKey = "Screenshot.RootDir";
        const string GameBarDirPrefKey = "Screenshot.GameBarDir";
        const string GameBarHotkeyPrefKey = "Screenshot.GameBarHotkey";
        const string GameBarEnabledPrefKey = "Screenshot.GameBarEnabled";
        const string DefaultRoot = "OneBoxScreenshots";
        // Game Bar 写文件较慢，HDR 下尤甚——需捕获、色调映射/编码，同时写入 .png 和 .jxr。
        // 实测 HDR 截图在高速机器上约需 16s，故最长等待 25s
        //（有文件出现即返回）。在 ThreadPool 上等待，不阻塞 UI/热键。
        const int GameBarWaitMs = 25000;

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr extraInfo);
        // OpenProcess + QueryFullProcessImageName：比 Process.MainModule 更可靠
        //（后者对提权/系统进程抛 Win32Exception）。此方式可读取大多数进程，避免受保护前台应用落入 Unknown 目录。
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder buf, ref uint size);
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        // ---- HDR 显示器检测（通过 Vortice.DXGI）----
        // 检测前台窗口所在显示器是否处于 HDR（HDR10 色彩空间）。
        // 之前手写 DXGI vtable 互操作因索引错误导致不可捕获的 AccessViolationException（每次截图崩溃）；
        // Vortice 从头文件生成 COM 绑定，vtable 正确。尽力而为：失败返回 false，后续图像质量启发式检测是兜底。
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
                                            // HDR10：DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020，值为 12。
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

        // 热键编码（与 HotkeyCaptureDialog 共享）：高 16 位 = 修饰键
        //（bit0 Alt, bit1 Ctrl, bit2 Shift, bit3 Win），低 16 位 = VK 码。
        // 通过 keybd_event 注入组合键：按下修饰键 → 按下主键 → 反向释放。
        // 不含 Win 的修饰键是游戏前台可用的关键——注入的 Win 键被系统吞掉，但 Alt/Ctrl/Shift 不受影响。
        static void SendGameBarHotkey(int encoded)
        {
            int mods = (encoded >> 16) & 0xFFFF;
            int vk = encoded & 0xFFFF;
            var downs = new System.Collections.Generic.List<byte>();
            if ((mods & 2) != 0) downs.Add(VK_CONTROL);
            if ((mods & 1) != 0) downs.Add(VK_MENU);
            if ((mods & 4) != 0) downs.Add(VK_SHIFT);
            if ((mods & 8) != 0) downs.Add(VK_LWIN);
            foreach (var m in downs) keybd_event(m, 0, KEYEVENTF_KEYDOWN, IntPtr.Zero);
            keybd_event((byte)vk, 0, KEYEVENTF_KEYDOWN, IntPtr.Zero);
            keybd_event((byte)vk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
            // 反向释放修饰键（顺序与按下相反）
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

        // 截图保存后在 UI 线程触发，供图库面板刷新。
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

                // DPI 修正：PerMonitorV2 下 GetClientRect 返回物理像素。
                // 150% 缩放下 1920 宽的窗口报告 2880px。按物理分辨率截取后缩小到逻辑像素（DIP），使输出匹配用户感知的 1080p。
                uint windowDpi = 96;
                try { windowDpi = GetDpiForWindow(hwnd); } catch { }
                if (windowDpi < 96) windowDpi = 96;
                double dpiScale = windowDpi / 96.0;
                int dipW = (int)Math.Round(w / dpiScale);
                int dipH = (int)Math.Round(h / dpiScale);
                if (dipW < 1) dipW = 1;
                if (dipH < 1) dipH = 1;

                AppLog.Log("Screenshot", $"start app={exeName} size={w}x{h} dpi={windowDpi}{(dpiScale != 1.0 ? " dip=" + dipW + "x" + dipH : "")}");

                // Game Bar 截图是高级可选功能（默认关闭）。
                // 关闭时仅使用 CopyFromScreen——简单、对普通窗口有效（HDR 内容可能返回黑色/泛白，因为 GDI 无法读取 HDR 表面，这是保持简洁的代价）。
                // 开启时检测 HDR 显示器并回退到 Game Bar（其 HDR 感知，可截取系统吞 Win 键的游戏窗口）。
                bool gameBarOn = AppPrefs.GetBool(GameBarEnabledPrefKey, false);

                // 检测前台窗口是否位于 HDR（HDR10）显示器。尽力而为的 DXGI 探测；失败时依赖图像质量启发式检测兜底。
                bool hdr = IsHdrDisplay(hwnd);
                AppLog.Log("Screenshot", $"hdr={hdr} gameBar={gameBarOn} app={exeName}");

                if (gameBarOn && hdr)
                {
                    // HDR 表面 GDI CopyFromScreen 无法读取（返回黑色/泛白），直接跳过进入 Game Bar——
                    // 其 HDR 感知，可色调映射为正确的 SDR .png，若用户启用"HDR 捕获"还会生成 .jxr 予以保留。这是务实的 HDR 路径。
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

                // CopyFromScreen（物理像素），然后缩小到 DIP，输出匹配用户感知的分辨率。
                Bitmap bmp = null;
                bool bad = false;
                try
                {
                    using (var raw = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(raw))
                            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h), CopyPixelOperation.SourceCopy);
                        if (dpiScale != 1.0)
                        {
                            bmp = new Bitmap(dipW, dipH, PixelFormat.Format32bppArgb);
                            using (var g = Graphics.FromImage(bmp))
                            {
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                g.DrawImage(raw, 0, 0, dipW, dipH);
                            }
                        }
                        else
                        {
                            bmp = new Bitmap(raw);
                        }
                    }
                    int blackPct; double stdDev, mean;
                    SampleQuality(bmp, out blackPct, out stdDev, out mean);
                    // 全黑 → GDI 无法读取的表面（全屏独占游戏 / HDR 读取失败）
                    bool black = blackPct >= 99;
                    // 平场（亮度方差接近零）→ 读取失败返回灰色/泛白而非黑色（窗口化 HDR 内容 CopyFromScreen 的典型表现）。
                    // 仅在 HDR 显示器上生效（非 HDR 的 CopyFromScreen 不会这样失败，全局平场检测会误杀合法纯色图像如空白页面）。
                    // 排除近白平场：失败读取偏灰/暗，合法纯色图像通常较亮，避免空白 PDF 等落入回退路径。
                    bool flat = hdr && stdDev < 8.0 && mean < 240.0;
                    if (hdr) { black = blackPct >= 92; } // HDR 下阈值更激进
                    bad = black || flat;
                    AppLog.Log("Screenshot", $"quality blackPct={blackPct} stdDev={stdDev:0.0} mean={mean:0} bad={bad} app={exeName}");
                }
                catch (Exception ex) { AppLog.Log("Screenshot capture", ex); error = $"截图失败: {ex.Message}"; if (bmp != null) bmp.Dispose(); goto done; }

                if (gameBarOn && bad)
                {
                    // GDI 无法读取的表面（全屏游戏 / HDR 内容）。回退到 HDR 感知的 Game Bar；
                    // 若用户开启"HDR 捕获"，还会生成 .jxr 与 SDR .png 预览一同保留。
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

        // 采样位图提取两个质量指标：
        //  - blackPct：近黑像素占比（R+G+B < 24），高值表示 GDI 将全屏独占/HDR 表面读为黑色。
        //  - stdDev： 采样亮度总体标准差，接近零表示平场——读取失败返回均匀灰色/白色。
        // 每隔 8px 采样，4K 下开销可控。使用 GetPixel（无需 unsafe 块）。
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

        // 解析前台窗口所属进程的 exe 名称（无扩展名），去掉非法路径字符以保证文件夹名安全。
        // 使用 QueryFullProcessImageName（PROCESS_QUERY_LIMITED_INFORMATION），
        // 可处理提权/UWP 应用（Process.MainModule 对这些进程抛访问拒绝）。
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

        // 触发 Game Bar 截图（Win+Alt+PrtScn），然后监听 Captures 文件夹中的新文件，复制到对应应用子目录。
        // Game Bar HDR 感知：始终生成 SDR .png，若用户启用"HDR 捕获"还会生成 .jxr。
        // 保留 .png 作为 toast/图库预览，同时保留 .jxr。返回预览路径（.png，若仅生成 .jxr 则返回 .jxr）。
        static string CaptureViaGameBar(string exeName)
        {
            string capturesDir = GameBarDir();
            if (!Directory.Exists(capturesDir)) { AppLog.Log("Screenshot", $"GameBar fallback: Captures dir not found: {capturesDir}"); return null; }
            AppLog.Log("Screenshot", $"GameBar fallback: dir={capturesDir}");

            // Snapshot existing files before triggering.
            var before = new HashSet<string>();
            foreach (var f in Directory.GetFiles(capturesDir)) before.Add(f);
            AppLog.Log("Screenshot", $"GameBar fallback: before={before.Count} files");

            // 触发 Game Bar 截图快捷键。默认为 Win+Alt+PrtScn，
            // 但游戏前台时 Win 键被吞（游戏模式/低级钩子过滤注入的 Win 键事件——已验证：注入的 VK_LWIN 在 GetAsyncKeyState 中读不到）。
            // 故允许用户将 Game Bar 截图快捷键改为不含 Win 的组合（如 Alt+F12），注入该组合。未设置时回退 Win+Alt+PrtScn。
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

            // 将 .png（预览）和 .jxr（HDR）双双复制到对应应用子目录，保留 Game Bar 原文件不动。
            // 之前移动 .png 离开 Captures 目录——但 Game Bar 监听该文件夹，中途移走文件导致后续触发不再生成文件。
            // 复制保持 Game Bar 状态完整。WaitForFileReady（在 CopyInto 中）防止复制到半写入文件。
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

        // Game Bar 渐进写入——文件在目录中出现时仍在写入中。此时复制/移动会拿到截断文件（实测 14MB 的 .jxr 只复制到 1186 字节）。
        // 等待文件大小在两次间隔约 150ms 的读取中稳定，且可被打开读取（无独占写锁），设置超时防止死等。
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

        public static BitmapSource LoadThumbnail(string path, int maxW, int maxH)
        {
            try
            {
                // 必须用 FileStream（StreamSource）而非 UriSource：UriSource 对非 ASCII 路径（如根目录含中文"图片"）
                // 处理错误，BitmapImage 静默加载失败 → 图库空白。
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

        // 获取所有应用子目录中最近 N 张截图（最新在前），供悬浮窗内嵌图库预览。
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

        // Game Bar（Win+Alt+PrtScn）截图保存位置。默认为 <Videos>\Captures，
        // 但用户可能重定向了视频库或更换了 Game Bar 截图文件夹，默认路径常无效。允许在设置中覆盖。
        public static string GameBarDir()
        {
            var s = AppPrefs.GetString(GameBarDirPrefKey, "");
            if (string.IsNullOrWhiteSpace(s))
                s = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyVideos), "Captures");
            return s;
        }
    }
}
