using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PowerAudioManager
{
    // Clipboard history: records the most recent text copies (max 20), persists
    // them to disk so they survive restarts, and shows them in a popup list.
    // Capture is poll-based (cheap and robust across processes) rather than
    // WM_CLIPBOARDUPDATE (which needs a hidden message-only window + AddClipboardFormatListener).
    public static class ClipboardHistory
    {
        const int MaxItems = 20;
        static readonly string _storePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OneBox", "clipboard.txt");
        static readonly object _lock = new object();
        static List<string> _items = new List<string>();
        static DispatcherTimer _poll;
        static string _lastHash = "";

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
                if (File.Exists(_storePath))
                {
                    _items = new List<string>();
                    foreach (var line in File.ReadAllLines(_storePath, Encoding.UTF8))
                        if (line.Length > 0) _items.Add(Unescape(line));
                }
            }
            catch { }
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storePath));
                var sb = new StringBuilder();
                foreach (var it in _items) sb.AppendLine(Escape(it));
                File.WriteAllText(_storePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        // Newline-safe line storage: escape \n and \r so each item is one file line.
        static string Escape(string s) { return s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r"); }
        static string Unescape(string s) { return s.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\\\", "\\"); }

        static void CaptureIfNew()
        {
            try
            {
                if (!Clipboard.ContainsText()) return;
                string text = Clipboard.GetText();
                if (string.IsNullOrEmpty(text)) return;
                string hash = text.Length + ":" + text.GetHashCode().ToString();
                if (hash == _lastHash) return;
                _lastHash = hash;
                lock (_lock)
                {
                    // De-dupe: move existing identical item to front.
                    _items.Remove(text);
                    _items.Insert(0, text);
                    while (_items.Count > MaxItems) _items.RemoveAt(_items.Count - 1);
                }
                Save();
            }
            catch { }
        }

        public static List<string> GetItems()
        {
            lock (_lock) { return new List<string>(_items); }
        }

        public static void Clear()
        {
            lock (_lock) _items.Clear();
            Save();
        }
    }

    // Popup panel that lists clipboard history; click an item to copy it back.
    public static class ClipboardHistoryPanel
    {
        public static void Show(Window owner)
        {
            var dlg = new Window {
                Title = "剪贴板历史",
                Width = 360, Height = 420,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(28, 26, 40)),
                FontFamily = owner.FontFamily
            };
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
                    string captured = item;
                    string preview = item.Replace("\r", " ").Replace("\n", " ");
                    if (preview.Length > 80) preview = preview.Substring(0, 80) + "…";
                    var btn = new Button {
                        Content = preview,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 235)),
                        Background = new SolidColorBrush(Color.FromRgb(42, 39, 60)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(80, 75, 120)),
                        Padding = new Thickness(8, 6, 8, 6),
                        Margin = new Thickness(0, 0, 0, 4),
                        FontSize = 11,
                        Cursor = Cursors.Hand,
                        ToolTip = captured.Length > 200 ? captured.Substring(0, 200) + "…" : captured
                    };
                    btn.Click += (s, e) => {
                        try { Clipboard.SetText(captured); } catch { }
                        dlg.Close();
                    };
                    list.Children.Add(btn);
                }
            };
            render();

            clearBtn.Click += (s, e) => { ClipboardHistory.Clear(); render(); };

            outer.Children.Add(scroller);
            dlg.Content = outer;

            // Refresh once when shown so freshly-copied items appear.
            dlg.Loaded += (s, e) => render();
            dlg.ShowDialog();
        }
    }
}
