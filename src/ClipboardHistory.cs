using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerAudioManager.Commands;
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
        static uint? _lastClipboardSequence;
        [DllImport("user32.dll")]
        static extern uint GetClipboardSequenceNumber();

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
            if (_poll != null) return;
            Load();
            _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _poll.Tick += (s, e) => CaptureIfNew();
            _poll.Start();
        }

        public static void Stop()
        {
            var poll = _poll;
            _poll = null;
            if (poll != null)
            {
                try { poll.Stop(); } catch { }
                try { Save(); } catch { }
            }
            lock (_lock) _items = new List<ClipItem>();
            _lastHash = "";
            _lastClipboardSequence = null;
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
                uint sequence = GetClipboardSequenceNumber();
                if (!ShouldReadClipboardSequence(_lastClipboardSequence, sequence)) return;

                // 优先记录图片（截图复制会同时设置图片和文本回退）。
                if (Clipboard.ContainsImage())
                {
                    var bmp = Clipboard.GetImage();
                    if (bmp == null) return;
                    if (!ClipboardSequenceUnchanged(sequence)) return;
                    // 按 PNG 内容哈希去重，而非仅比较尺寸（否则同尺寸不同截图会被视为重复）。
                    byte[] png = EncodeToPng(bmp);
                    if (png == null || !ClipboardSequenceUnchanged(sequence)) return;
                    string hash = "IMG:" + Sha256Hex(png);
                    if (!ClipboardSequenceUnchanged(sequence)) return;
                    if (hash == _lastHash) { MarkClipboardSequenceProcessed(sequence); return; }
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
                    _lastHash = hash;
                    MarkClipboardSequenceProcessed(sequence);
                    Save();
                    return;
                }
                if (!Clipboard.ContainsText())
                {
                    if (ClipboardSequenceUnchanged(sequence)) MarkClipboardSequenceProcessed(sequence);
                    return;
                }
                string text = Clipboard.GetText();
                if (!ClipboardSequenceUnchanged(sequence)) return;
                if (string.IsNullOrEmpty(text)) { MarkClipboardSequenceProcessed(sequence); return; }
                string hash2 = text.Length + ":" + text.GetHashCode().ToString();
                if (hash2 == _lastHash) { MarkClipboardSequenceProcessed(sequence); return; }
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
                _lastHash = hash2;
                MarkClipboardSequenceProcessed(sequence);
                Save();
            }
            catch { }
        }

        internal static bool ShouldReadClipboardSequence(uint? lastProcessed, uint current) =>
            current == 0 || !lastProcessed.HasValue || lastProcessed.Value != current;

        static bool ClipboardSequenceUnchanged(uint sequence) =>
            sequence == 0 || GetClipboardSequenceNumber() == sequence;

        static void MarkClipboardSequenceProcessed(uint sequence)
        {
            if (sequence != 0) _lastClipboardSequence = sequence;
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

            var outer = new DockPanel { Margin = new Thickness(10, 0, 10, 0) };

            // Search + type filter.
            var tools = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(tools, Dock.Top);
            int filter = 0; // 0 全部, 1 文本, 2 图片
            string query = "";
            Action render = null;
            var segmented = UiKit.MakeSegmented(new[] { "全部", "文本", "图片" }, 0, i => { filter = i; render?.Invoke(); }, "类型");
            segmented.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(segmented, Dock.Right);
            tools.Children.Add(segmented);
            var search = UiKit.MakeTextBox("");
            search.Background = UiKit.FrozenBrush(UiKit.CardColor);
            search.BorderBrush = UiKit.FrozenBrush(UiKit.CardColor);
            search.Padding = new Thickness(26, 5, 8, 5);
            System.Windows.Automation.AutomationProperties.SetName(search, "搜索剪贴板历史");
            var searchIcon = UiKit.Glyph(UiKit.Glyphs.Search, 13, UiKit.FrozenBrush(UiKit.TextSecondary));
            searchIcon.HorizontalAlignment = HorizontalAlignment.Left;
            searchIcon.VerticalAlignment = VerticalAlignment.Center;
            searchIcon.Margin = new Thickness(9, 0, 0, 0);
            searchIcon.IsHitTestVisible = false;
            var searchHint = UiKit.Text("搜索", 12, UiKit.TextTertiary);
            searchHint.Margin = new Thickness(27, 0, 0, 0);
            searchHint.IsHitTestVisible = false;
            var searchHost = new Grid();
            searchHost.Children.Add(search);
            searchHost.Children.Add(searchIcon);
            searchHost.Children.Add(searchHint);
            search.TextChanged += (s, e) =>
            {
                query = search.Text ?? "";
                searchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                render?.Invoke();
            };
            tools.Children.Add(searchHost);
            outer.Children.Add(tools);

            // Footer: hint + 清空.
            var footer = new DockPanel { LastChildFill = true, Height = 44 };
            DockPanel.SetDock(footer, Dock.Bottom);
            var clearBtn = UiKit.MakeButton("清空", ButtonVariant.Flat);
            clearBtn.Foreground = UiKit.FrozenBrush(UiKit.DangerColor);
            clearBtn.Padding = new Thickness(8, 0, 8, 0);
            clearBtn.Height = 28;
            DockPanel.SetDock(clearBtn, Dock.Right);
            footer.Children.Add(clearBtn);
            footer.Children.Add(UiKit.Text("单击复制 · 右键删除 · ↑↓ Enter 键盘选取", 11, UiKit.TextSecondary));
            var footerRule = new Border { Height = 1, Background = UiKit.FrozenBrush(UiKit.RaisedColor), Margin = new Thickness(-10, 4, -10, 0) };
            DockPanel.SetDock(footerRule, Dock.Bottom);
            outer.Children.Add(footer);
            outer.Children.Add(footerRule);

            var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            scroller.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = ThemeTokens.CreateDarkScrollBarStyle();
            var list = new StackPanel();
            scroller.Content = list;

            var dlg = OneBoxWindow.Create(owner, "剪贴板历史", 360, 560, outer, true, IconKey.Clipboard);
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

            // Keyboard selection over the currently visible cards.
            var visible = new List<(Button Card, Action Copy)>();
            int selected = 0;
            void Highlight()
            {
                for (int i = 0; i < visible.Count; i++)
                {
                    bool on = i == selected;
                    visible[i].Card.Background = UiKit.FrozenBrush(on ? UiKit.RaisedColor : UiKit.CardColor);
                    visible[i].Card.BorderBrush = on ? UiKit.FrozenBrush(UiKit.AccentColor) : Brushes.Transparent;
                }
                if (selected >= 0 && selected < visible.Count) visible[selected].Card.BringIntoView();
            }

            render = () =>
            {
                list.Children.Clear();
                visible.Clear();
                var items = ClipboardHistory.GetItems();
                for (int idx = 0; idx < items.Count; idx++)
                {
                    var captured = items[idx];
                    int capturedIndex = idx;
                    if (filter == 1 && captured.IsImage) continue;
                    if (filter == 2 && !captured.IsImage) continue;
                    if (query.Length > 0 && (captured.IsImage || captured.Text == null ||
                        captured.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)) continue;

                    var content = new DockPanel { LastChildFill = true };
                    var del = new Button
                    {
                        Content = IconCatalog.CreateElement(IconKey.Close, 12, UiKit.FrozenBrush(UiKit.TextSecondary)),
                        Width = 24, Height = 24, ToolTip = "删除这条", VerticalAlignment = VerticalAlignment.Top
                    };
                    UiKit.ApplyIconButtonStyle(del);
                    del.MinWidth = 24; del.MinHeight = 24;
                    System.Windows.Automation.AutomationProperties.SetName(del, "删除这条");
                    del.Click += (s, e) => { ClipboardHistory.RemoveAt(capturedIndex); render(); e.Handled = true; };
                    DockPanel.SetDock(del, Dock.Right);
                    content.Children.Add(del);

                    var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    string tooltip;
                    if (captured.IsImage)
                    {
                        var (thumb, pw, ph) = LoadThumbnailWithSize(captured.ImagePath);
                        var tile = new Border
                        {
                            Width = 72, Height = 44, CornerRadius = new CornerRadius(6),
                            Margin = new Thickness(0, 0, 10, 0),
                            Background = thumb != null
                                ? new ImageBrush(thumb) { Stretch = Stretch.UniformToFill }
                                : (Brush)UiKit.FrozenBrush(UiKit.RaisedColor)
                        };
                        if (pw > 0)
                        {
                            tile.Child = new Border
                            {
                                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                                Margin = new Thickness(3), Padding = new Thickness(3, 0, 3, 0), CornerRadius = new CornerRadius(3),
                                Background = UiKit.FrozenBrush(Color.FromArgb(115, 0, 0, 0)),
                                Child = new TextBlock { Text = pw + "×" + ph, FontSize = 9, Foreground = UiKit.FrozenBrush(UiKit.TextPrimary) }
                            };
                        }
                        DockPanel.SetDock(tile, Dock.Left);
                        content.Children.Add(tile);
                        texts.Children.Add(UiKit.Text("图片", 12, UiKit.TextPrimary));
                        texts.Children.Add(Meta(ImageTimeLabel(captured.ImagePath) + "PNG"));
                        tooltip = "单击复制 · 右键删除";
                    }
                    else
                    {
                        string preview = captured.Text.Replace("\r", " ").Replace("\n", " ");
                        if (preview.Length > 160) preview = preview.Substring(0, 160) + "…";
                        texts.Children.Add(new TextBlock
                        {
                            Text = preview, FontSize = 12, LineHeight = 17, MaxHeight = 34,
                            Foreground = UiKit.FrozenBrush(UiKit.TextPrimary),
                            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis
                        });
                        texts.Children.Add(Meta(captured.Text.Length + " 字"));
                        tooltip = captured.Text.Length > 200 ? captured.Text.Substring(0, 200) + "…" : captured.Text;
                    }
                    content.Children.Add(texts);

                    var card = new Button
                    {
                        Content = content,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(10, 8, 6, 8),
                        Margin = new Thickness(0, 0, 0, 4),
                        Cursor = Cursors.Hand,
                        ToolTip = tooltip
                    };
                    UiKit.ApplyFlatStyle(card);
                    card.Background = UiKit.FrozenBrush(UiKit.CardColor);
                    card.BorderBrush = Brushes.Transparent;
                    card.Foreground = UiKit.FrozenBrush(UiKit.TextPrimary);
                    Action copy = () =>
                    {
                        try
                        {
                            if (captured.IsImage) CopyImageBack(captured.ImagePath);
                            else Clipboard.SetText(captured.Text);
                        }
                        catch { }
                        dlg.Close();
                    };
                    card.Click += (s, e) => copy();
                    // 右键：删除此条（不关闭窗口）
                    card.MouseRightButtonUp += (s, e) => {
                        ClipboardHistory.RemoveAt(capturedIndex);
                        render();
                        e.Handled = true;
                    };
                    visible.Add((card, copy));
                    list.Children.Add(card);
                }
                if (visible.Count == 0)
                {
                    var empty = UiKit.Text(items.Count == 0 ? "（暂无记录）" : "没有匹配的记录", 12, UiKit.TextSecondary);
                    empty.HorizontalAlignment = HorizontalAlignment.Center;
                    empty.Margin = new Thickness(0, 24, 0, 0);
                    list.Children.Add(empty);
                }
                selected = Math.Max(0, Math.Min(selected, visible.Count - 1));
                Highlight();
            };
            render();

            dlg.PreviewKeyDown += (s, e) =>
            {
                if (visible.Count == 0) return;
                if (e.Key == Key.Down) { selected = Math.Min(selected + 1, visible.Count - 1); Highlight(); e.Handled = true; }
                else if (e.Key == Key.Up) { selected = Math.Max(selected - 1, 0); Highlight(); e.Handled = true; }
                else if (e.Key == Key.Enter && selected >= 0 && selected < visible.Count) { e.Handled = true; visible[selected].Copy(); }
            };

            clearBtn.Click += async (s, e) =>
            {
                if (owner is MainWindow mainWindow)
                    await mainWindow.ExecuteCommandAsync(AppCommandId.ClipboardClear, CommandSource.MainWindow);
                else ClipboardHistory.Clear();
                render();
            };

            outer.Children.Add(scroller);

            dlg.Loaded += (s, e) => { render(); search.Focus(); };
            dlg.ShowDialog();
        }

        static TextBlock Meta(string text)
        {
            var t = UiKit.Text(text, 11, UiKit.TextTertiary);
            t.Margin = new Thickness(0, 3, 0, 0);
            return t;
        }

        // Image items are stored as clip_yyyyMMdd_HHmmss_fff.bin; derive a
        // short relative time from the name without touching storage.
        static string ImageTimeLabel(string path)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(path) ?? "";
                if (!name.StartsWith("clip_", StringComparison.Ordinal) || name.Length < 20) return "";
                if (!DateTime.TryParseExact(name.Substring(5, 15), "yyyyMMdd_HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var when)) return "";
                var age = DateTime.Now - when;
                if (age.TotalMinutes < 1) return "刚刚 · ";
                if (age.TotalHours < 1) return (int)age.TotalMinutes + " 分钟前 · ";
                if (age.TotalDays < 1) return (int)age.TotalHours + " 小时前 · ";
                if (age.TotalDays < 2) return "昨天 · ";
                return when.ToString("M月d日") + " · ";
            }
            catch { return ""; }
        }

        // Thumbnail decoded at list size plus the original pixel size, read
        // from the image header so the full bitmap is never kept.
        static (BitmapSource Thumb, int Width, int Height) LoadThumbnailWithSize(string path)
        {
            try
            {
                var bytes = ClipboardHistory.LoadImageBytes(path);
                if (bytes == null) return (null, 0, 0);
                int w = 0, h = 0;
                try
                {
                    using (var header = new MemoryStream(bytes))
                    {
                        var decoder = BitmapDecoder.Create(header, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                        if (decoder.Frames.Count > 0) { w = decoder.Frames[0].PixelWidth; h = decoder.Frames[0].PixelHeight; }
                    }
                }
                catch { }
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.DecodePixelWidth = 144;
                img.StreamSource = new MemoryStream(bytes);
                img.EndInit();
                img.Freeze();
                return (img, w, h);
            }
            catch { return (null, 0, 0); }
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
