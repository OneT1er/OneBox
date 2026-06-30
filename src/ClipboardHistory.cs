using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PowerAudioManager
{
    // 剪贴板历史：记录最近文本和图片复制（最多 20 条），持久化到磁盘以在重启后保留，弹窗展示。
    // 图片以 DPAPI 加密的 PNG 字节存储在 %LocalAppData%\OneBox\clip_images\（新截取用 .bin，旧版明文 .png 仍可读）。
    // clipboard.txt 整体存储为单个 DPAPI blob（CurrentUser 范围）。捕获采用轮询（轻量、跨进程可靠）。
    //
    // 隐私说明：DPAPI CurrentUser 范围仅阻止其他 Windows 用户账户读取历史。无法防御同一用户下运行的恶意软件——这是剪贴板管理器可接受的取舍。
    public static class ClipboardHistory
    {
        const int MaxItems = 20;
        static readonly string _storeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OneBox");
        static readonly string _storePath = Path.Combine(_storeDir, "clipboard.txt");
        static readonly string _imageDir = Path.Combine(_storeDir, "clip_images");
        static readonly object _lock = new object();
        static List<ClipItem> _items = new List<ClipItem>();
        static DispatcherTimer _poll;
        static string _lastHash = "";

        // DPAPI 固定应用专属熵。使用常量即可：目的是将 blob 绑定到 OneBox，防止其他应用在同一作用域下轻易解密，而非保密熵值本身。
        static readonly byte[] _dpapiEntropy = Encoding.UTF8.GetBytes("OneBox.Clipboard.v1");

        public class ClipItem
        {
            public bool IsImage;
            public string Text;          // for text items
            public string ImagePath;     // for image items (encrypted .bin / legacy .png)
        }

        public static void Start()
        {
            Load();
            _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _poll.Tick += (s, e) => CaptureIfNew();
            _poll.Start();
        }

        static void Load()
        {
            try
            {
                if (!File.Exists(_storePath)) return;
                // 文件为 DPAPI blob。尝试解密；失败则为旧版明文，直接按 UTF-8 读取（下次变更时会重新加密保存）。
                string content;
                var raw = File.ReadAllBytes(_storePath);
                try { content = Encoding.UTF8.GetString(ProtectedData.Unprotect(raw, _dpapiEntropy, DataProtectionScope.CurrentUser)); }
                catch { content = Encoding.UTF8.GetString(raw); }

                _items = new List<ClipItem>();
                foreach (var line in content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    if (line.Length == 0) continue;
                    if (line.StartsWith("IMG:"))
                    {
                        string path = Unescape(line.Substring(4));
                        if (File.Exists(path)) _items.Add(new ClipItem { IsImage = true, ImagePath = path });
                    }
                    else
                    {
                        _items.Add(new ClipItem { IsImage = false, Text = Unescape(line) });
                    }
                }
            }
            catch { }
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(_storeDir);
                var sb = new StringBuilder();
                foreach (var it in _items)
                {
                    if (it.IsImage) sb.AppendLine("IMG:" + Escape(it.ImagePath ?? ""));
                    else sb.AppendLine(Escape(it.Text ?? ""));
                }
                // 用 DPAPI（CurrentUser）加密整个历史 blob，避免文本（可能含密码/验证码）明文落盘。
                var plain = Encoding.UTF8.GetBytes(sb.ToString());
                var blob = ProtectedData.Protect(plain, _dpapiEntropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_storePath, blob);
            }
            catch { }
        }

        // 换行安全存储：转义 \n 和 \r，使每条记录占一行。
        static string Escape(string s) { return s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r"); }
        static string Unescape(string s) { return s.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\\\", "\\"); }

        static void CaptureIfNew()
        {
            try
            {
                // 优先记录图片（截图复制会同时设置图片和文本回退）。
                if (Clipboard.ContainsImage())
                {
                    var bmp = Clipboard.GetImage();
                    if (bmp == null) return;
                    // 按 PNG 内容哈希去重，而非仅比较尺寸（否则同尺寸不同截图会被视为重复）。
                    byte[] png = EncodeToPng(bmp);
                    if (png == null) return;
                    string hash = "IMG:" + Sha256Hex(png);
                    if (hash == _lastHash) return;
                    _lastHash = hash;
                    string path = SaveImage(png);
                    if (path == null) return;
                    lock (_lock)
                    {
                        _items.Insert(0, new ClipItem { IsImage = true, ImagePath = path });
                        while (_items.Count > MaxItems)
                        {
                            var dropped = _items[_items.Count - 1];
                            _items.RemoveAt(_items.Count - 1);
                            if (dropped.IsImage) { try { File.Delete(dropped.ImagePath); } catch { } }
                        }
                    }
                    Save();
                    return;
                }
                if (!Clipboard.ContainsText()) return;
                string text = Clipboard.GetText();
                if (string.IsNullOrEmpty(text)) return;
                string hash2 = text.Length + ":" + text.GetHashCode().ToString();
                if (hash2 == _lastHash) return;
                _lastHash = hash2;
                lock (_lock)
                {
                    // 去重：移除已有相同文本项，然后插入到最前。
                    for (int i = 0; i < _items.Count; i++)
                        if (!_items[i].IsImage && _items[i].Text == text) { _items.RemoveAt(i); break; }
                    _items.Insert(0, new ClipItem { IsImage = false, Text = text });
                    while (_items.Count > MaxItems)
                    {
                        var dropped = _items[_items.Count - 1];
                        _items.RemoveAt(_items.Count - 1);
                        if (dropped.IsImage) { try { File.Delete(dropped.ImagePath); } catch { } }
                    }
                }
                Save();
            }
            catch { }
        }

        static byte[] EncodeToPng(BitmapSource src)
        {
            try
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(src));
                using (var ms = new MemoryStream())
                {
                    enc.Save(ms);
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }

        static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(data);
                var sb = new StringBuilder(h.Length * 2);
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // DPAPI 加密写入 PNG 字节到磁盘。新文件用 .bin 扩展名，避免被误认为明文 PNG。
        static string SaveImage(byte[] pngBytes)
        {
            try
            {
                Directory.CreateDirectory(_imageDir);
                string path = Path.Combine(_imageDir, "clip_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".bin");
                var blob = ProtectedData.Protect(pngBytes, _dpapiEntropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(path, blob);
                return path;
            }
            catch { return null; }
        }

        // 透明解密 DPAPI 加密的 .bin 文件，旧版明文 .png 直接读取。
        internal static byte[] LoadImageBytes(string path)
        {
            try
            {
                var raw = File.ReadAllBytes(path);
                try { return ProtectedData.Unprotect(raw, _dpapiEntropy, DataProtectionScope.CurrentUser); }
                catch { return raw; } // legacy plaintext PNG
            }
            catch { return null; }
        }

        public static List<ClipItem> GetItems()
        {
            lock (_lock) { return new List<ClipItem>(_items); }
        }

        // 按索引删除单条历史（0=最新）。图片项同时删除磁盘文件。
        public static void RemoveAt(int index)
        {
            lock (_lock)
            {
                if (index < 0 || index >= _items.Count) return;
                var it = _items[index];
                if (it.IsImage) { try { File.Delete(it.ImagePath); } catch { } }
                _items.RemoveAt(index);
            }
            Save();
        }

        public static void Clear()
        {
            lock (_lock)
            {
                foreach (var it in _items) if (it.IsImage) { try { File.Delete(it.ImagePath); } catch { } }
                _items.Clear();
            }
            Save();
        }
    }

    // 剪贴板历史弹窗面板，点击条目复制回剪贴板。
    public static class ClipboardHistoryPanel
    {
        public static void Show(Window owner) { ShowAt(owner, 0, 0); }

        // 显示面板。若提供 x/y（屏幕设备像素，如 GetCursorPos），将窗口定位到鼠标附近
        // 而非居中于 owner——使剪贴板热键在鼠标位置弹出列表。
        public static void ShowAt(Window owner, int cursorX, int cursorY)
        {
            bool atCursor = cursorX != 0 || cursorY != 0;

            var outer = new DockPanel { Margin = new Thickness(12) };

            var header = new TextBlock {
                Text = "最近复制的内容（左键复制 · 右键删除）",
                Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(header, Dock.Top);
            outer.Children.Add(header);

            var clearBtn = new Button {
                Content = "清空", Width = 56, Height = 24, FontSize = 11, Margin = new Thickness(0, 0, 0, 8) };
            AppResources.StyleDialogButton(clearBtn, false);
            DockPanel.SetDock(clearBtn, Dock.Bottom);
            outer.Children.Add(clearBtn);

            var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var list = new StackPanel();
            scroller.Content = list;

            var dlg = OneBoxWindow.Create(owner, "剪贴板历史", 360, 420, outer, true);
            // 剪贴板热键触发时将窗口定位到鼠标右下，限制在工作区内，覆盖 OneBoxWindow 的居中定位。
            if (atCursor)
            {
                dlg.WindowStartupLocation = WindowStartupLocation.Manual;
                dlg.Loaded += (s, e) =>
                {
                    double dpi = 96.0;
                    try
                    {
                        var src = System.Windows.PresentationSource.FromVisual(dlg);
                        if (src != null && src.CompositionTarget != null)
                            dpi = 96.0 * src.CompositionTarget.TransformToDevice.M11;
                    }
                    catch { }
                    double scale = 96.0 / dpi;
                    double w = dlg.ActualWidth > 0 ? dlg.ActualWidth : dlg.Width;
                    double h = dlg.ActualHeight > 0 ? dlg.ActualHeight : dlg.Height;
                    double left = cursorX * scale + 8;
                    double top = cursorY * scale + 8;
                    var wa = SystemParameters.WorkArea;
                    if (left + w > wa.Right) left = wa.Right - w;
                    if (top + h > wa.Bottom) top = wa.Bottom - h;
                    if (left < wa.Left) left = wa.Left;
                    if (top < wa.Top) top = wa.Top;
                    dlg.Left = left;
                    dlg.Top = top;
                };
            }

            Action render = null;
            render = () =>
            {
                list.Children.Clear();
                var items = ClipboardHistory.GetItems();
                if (items.Count == 0)
                {
                    list.Children.Add(new TextBlock {
                        Text = "（暂无记录）", Foreground = new SolidColorBrush(Color.FromRgb(190, 188, 220)),
                        FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
                    return;
                }
                for (int idx = 0; idx < items.Count; idx++)
                {
                    var captured = items[idx];
                    int capturedIndex = idx;
                    Button btn;
                    if (captured.IsImage)
                    {
                        var row = new StackPanel { Orientation = Orientation.Horizontal };
                        try
                        {
                            var thumb = LoadThumbnail(captured.ImagePath);
                            if (thumb != null)
                            {
                                var img = new System.Windows.Controls.Image {
                                    Source = thumb,
                                    Width = 48, Height = 32, Stretch = Stretch.UniformToFill,
                                    Margin = new Thickness(0, 0, 8, 0) };
                                row.Children.Add(img);
                            }
                        }
                        catch { }
                        row.Children.Add(new TextBlock {
                            Text = "[图片]",
                            Foreground = new SolidColorBrush(Color.FromRgb(190, 188, 220)),
                            FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
                        btn = new Button {
                            Content = row,
                            HorizontalContentAlignment = HorizontalAlignment.Left,
                            Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 235)),
                            Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)),
                            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)),
                            Padding = new Thickness(8, 4, 8, 4),
                            Margin = new Thickness(0, 0, 0, 4),
                            FontSize = 11,
                            Cursor = Cursors.Hand,
                            ToolTip = "左键复制 · 右键删除"
                        };
                    }
                    else
                    {
                        string preview = captured.Text.Replace("\r", " ").Replace("\n", " ");
                        if (preview.Length > 80) preview = preview.Substring(0, 80) + "…";
                        btn = new Button {
                            Content = preview,
                            HorizontalContentAlignment = HorizontalAlignment.Left,
                            Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 235)),
                            Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)),
                            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)),
                            Padding = new Thickness(8, 6, 8, 6),
                            Margin = new Thickness(0, 0, 0, 4),
                            FontSize = 11,
                            Cursor = Cursors.Hand,
                            ToolTip = captured.Text.Length > 200 ? captured.Text.Substring(0, 200) + "…" : captured.Text
                        };
                    }
                    btn.Click += (s, e) => {
                        try
                        {
                            if (captured.IsImage) CopyImageBack(captured.ImagePath);
                            else Clipboard.SetText(captured.Text);
                        }
                        catch { }
                        dlg.Close();
                    };
                    // 右键：删除此条（不关闭窗口）
                    btn.MouseRightButtonUp += (s, e) => {
                        ClipboardHistory.RemoveAt(capturedIndex);
                        render();
                        e.Handled = true;
                    };
                    list.Children.Add(btn);
                }
            };
            render();

            clearBtn.Click += (s, e) => { ClipboardHistory.Clear(); render(); };

            outer.Children.Add(scroller);

            dlg.Loaded += (s, e) => render();
            dlg.ShowDialog();
        }

        static BitmapSource LoadThumbnail(string path)
        {
            try
            {
                var bytes = ClipboardHistory.LoadImageBytes(path);
                if (bytes == null) return null;
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = new MemoryStream(bytes);
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch { return null; }
        }

        static void CopyImageBack(string path)
        {
            try
            {
                var bytes = ClipboardHistory.LoadImageBytes(path);
                if (bytes == null) return;
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = new MemoryStream(bytes);
                img.EndInit();
                img.Freeze();
                Clipboard.SetImage(img);
            }
            catch { }
        }
    }
}
