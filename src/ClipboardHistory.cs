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
    // Clipboard history: records the most recent text AND image copies (max 20),
    // persists them to disk so they survive restarts, and shows them in a popup.
    // Images are stored as DPAPI-encrypted PNG bytes under
    // %LocalAppData%\OneBox\clip_images\ (.bin for new captures; legacy .png
    // plaintext files are still readable). clipboard.txt is stored as a single
    // DPAPI blob (CurrentUser scope). Capture is poll-based (cheap and robust
    // across processes).
    //
    // Privacy note: DPAPI CurrentUser scope only prevents OTHER Windows user
    // accounts (and casual file snooping) from reading the history. It does NOT
    // defend against malware running under the same user — that is an accepted
    // trade-off for a clipboard manager.
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

        // Fixed app-specific entropy for DPAPI. Using a constant is fine: the
        // goal is to bind the blob to OneBox so a different app can't trivially
        // Unprotect it with the same scope, not to keep the entropy secret.
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
                // The file is a DPAPI blob. Try to unprotect; if that fails the
                // file is a legacy plaintext copy from an older OneBox, so read
                // it as UTF-8 text directly (it will be re-saved encrypted on
                // the next change).
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
                // Encrypt the whole history blob with DPAPI (CurrentUser) so the
                // text — which may include pasted passwords / codes — is not
                // sitting on disk in plaintext.
                var plain = Encoding.UTF8.GetBytes(sb.ToString());
                var blob = ProtectedData.Protect(plain, _dpapiEntropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_storePath, blob);
            }
            catch { }
        }

        // Newline-safe line storage: escape \n and \r so each item is one line.
        static string Escape(string s) { return s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r"); }
        static string Unescape(string s) { return s.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\\\", "\\"); }

        static void CaptureIfNew()
        {
            try
            {
                // Prefer image when present (a screenshot copy sets both image and sometimes
                // a text fallback); record the image.
                if (Clipboard.ContainsImage())
                {
                    var bmp = Clipboard.GetImage();
                    if (bmp == null) return;
                    // De-dupe by the PNG-encoded content hash, not just dimensions:
                    // two different screenshots of the same size would otherwise be
                    // treated as duplicates under the old PixelWidth x PixelHeight key.
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
                    // De-dupe: remove existing identical text item, then insert at front.
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

        // Encode a BitmapSource to PNG bytes (in memory).
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

        // SHA256 of the given bytes as a lowercase hex string (stable de-dupe key).
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

        // Write the PNG bytes to disk, DPAPI-encrypted (CurrentUser). New files
        // use the .bin extension so they aren't mistaken for plain PNGs. Returns
        // the path or null on failure.
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

        // Read an image file and return its PNG bytes, transparently
        // decrypting DPAPI-encrypted .bin files and falling back to raw PNG
        // bytes for legacy plaintext .png captures.
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

    // Popup panel that lists clipboard history; click an item to copy it back.
    public static class ClipboardHistoryPanel
    {
        public static void Show(Window owner)
        {
            var outer = new DockPanel { Margin = new Thickness(12) };

            var header = new TextBlock {
                Text = "最近复制的内容（点击复制）",
                Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(header, Dock.Top);
            outer.Children.Add(header);

            var clearBtn = new Button {
                Content = "清空", Width = 56, Height = 24, FontSize = 11, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(clearBtn, Dock.Bottom);
            outer.Children.Add(clearBtn);

            var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var list = new StackPanel();
            scroller.Content = list;

            var dlg = OneBoxWindow.Create(owner, "剪贴板历史", 360, 420, outer, true);

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
                foreach (var item in items)
                {
                    var captured = item;
                    Button btn;
                    if (captured.IsImage)
                    {
                        // Image item: thumbnail + "[图片]" label.
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
                            ToolTip = "点击复制此图片"
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
                    list.Children.Add(btn);
                }
            };
            render();

            clearBtn.Click += (s, e) => { ClipboardHistory.Clear(); render(); };

            outer.Children.Add(scroller);

            // Refresh once when shown so freshly-copied items appear.
            dlg.Loaded += (s, e) => render();
            dlg.ShowDialog();
        }

        // Decode (and decrypt if needed) an image file into a frozen thumbnail
        // BitmapSource. Returns null if the file can't be read/decoded.
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
