using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;

namespace PowerAudioManager
{
    // Owns the system-tray NotifyIcon, its right-click context menu, the dynamic
    // memory-load-coloured bolt icon, and the tooltip. Extracted from MainWindow
    // so the window class no longer carries WinForms tray plumbing.
    //
    // Coupling to MainWindow is deliberately narrow: the constructor takes the
    // window (for show / topmost / pin-button state and the status-text getter)
    // plus an onExit callback (the window decides how shutdown proceeds, since it
    // also owns the device watcher and SystemEvents hooks).
    internal sealed class TrayController
    {
        readonly MainWindow _owner;
        readonly Action _onExit;
        NotifyIcon _tray;
        ContextMenuStrip _menu;
        ToolStripMenuItem _topmostItem;
        ToolStripMenuItem _lockItem;

        // ---- Dynamic tray icon --------------------------------------------------
        // Renders the "闪电" (bolt) logo as a 32x32 bitmap, recoloured by current
        // memory load: green < 60%, amber 60–80%, red > 80%. Updated on every
        // refresh tick so the tray reflects live system pressure.
        Icon _dynIcon;
        int _lastLoadBucket = -1; // -1 forces a redraw on first call

        public TrayController(MainWindow owner, Action onExit)
        {
            _owner = owner;
            _onExit = onExit;
        }

        public void Init()
        {
            try
            {
                _tray = new NotifyIcon
                {
                    Icon = Icon.FromHandle(CreateTrayIconHandle()),
                    Text = "OneBox",
                    Visible = true
                };
                _menu = new ContextMenuStrip();
                _menu.Font = AppResources.TrayFont(); // HarmonyOS Sans SC — match the window font
                // Match the floating window's dark-purple theme on the WinForms tray menu.
                _menu.RenderMode = ToolStripRenderMode.ManagerRenderMode;
                _menu.Renderer = new ToolStripProfessionalRenderer(new OneBoxMenuColorTable());
                _menu.BackColor = Color.FromArgb(28, 26, 40);   // BgColor
                _menu.ForeColor = Color.White;                    // TextPrimary
                // Keep the image margin ON — WinForms draws checkmarks (for 开机自启 /
                // 窗口置顶 / 锁定位置) in this margin. Hiding it makes the checks
                // invisible. The OneBoxMenuColorTable already paints the margin dark.
                _menu.ShowImageMargin = true;
                _menu.Padding = new Padding(2, 4, 2, 4);
                _menu.Items.Add("显示窗口", null, (s, e) => _owner.ShowWindow());
                var autoItem = new ToolStripMenuItem("开机自启") { CheckOnClick = true, Checked = IsAutoStartEnabled() };
                autoItem.Click += (s, e) => ToggleAutoStart(autoItem.Checked);
                _menu.Items.Add(autoItem);
                _menu.Opening += (s, e) => autoItem.Checked = IsAutoStartEnabled();
                _menu.Items.Add(new ToolStripSeparator());
                _topmostItem = new ToolStripMenuItem("窗口置顶") { CheckOnClick = true, Checked = _owner._topmost };
                _topmostItem.Click += (s, e) => { _owner._topmost = _topmostItem.Checked; _owner.Topmost = _owner._topmost; AppPrefs.SetBool("Topmost", _owner._topmost); };
                _menu.Items.Insert(_menu.Items.Count - 1, _topmostItem);
                _lockItem = new ToolStripMenuItem("锁定位置") { CheckOnClick = true, Checked = _owner._lockPosition };
                _lockItem.Click += (s, e) => {
                    _owner._lockPosition = _lockItem.Checked;
                    AppPrefs.SetBool("LockPosition", _owner._lockPosition);
                    if (_owner._pinBtn != null)
                    {
                        _owner._pinBtn.Content = MainWindow.PinIcon(_owner._lockPosition);
                        _owner._pinBtn.Foreground = new System.Windows.Media.SolidColorBrush(_owner._lockPosition ? MainWindow.AccentColor : MainWindow.TextSecondary);
                    }
                };
                _menu.Items.Insert(_menu.Items.Count - 1, _lockItem);
                var hiddenSub = new ToolStripMenuItem("显示已隐藏设备");
                // Force the submenu drop-down to inherit the dark theme (it does not
                // pick up BackColor/ForeColor from the parent ContextMenuStrip).
                hiddenSub.DropDown.BackColor = Color.FromArgb(28, 26, 40);
                hiddenSub.DropDown.ForeColor = Color.White;
                _menu.Items.Insert(_menu.Items.Count - 1, hiddenSub);
                _menu.Opening += (s, e) => {
                    hiddenSub.DropDownItems.Clear();
                    var devs = AudioDevices.GetOutputDevices();
                    bool any = false;
                    foreach (var d in devs) if (d.IsHidden) {
                        any = true;
                        var copy = d;
                        var mi = new ToolStripMenuItem(d.Name);
                        mi.ForeColor = Color.White;
                        mi.Click += (ss, ee) => { DevicePrefs.SetHidden(copy.Name, false); _owner.LoadData(); };
                        hiddenSub.DropDownItems.Add(mi);
                    }
                    hiddenSub.Visible = any;
                };
                _menu.Items.Insert(_menu.Items.Count - 1, new ToolStripSeparator());
                _menu.Items.Insert(_menu.Items.Count - 1,
                    new ToolStripMenuItem("清理内存", null, (s, e) => _owner.CleanMemory()));
                _menu.Items.Insert(_menu.Items.Count - 1,
                    new ToolStripMenuItem("设置...", null,
                        (ss, ee) => { _owner.ShowWindow(); SettingsDialog.Show(_owner, 0); }));
                _menu.Items.Insert(_menu.Items.Count - 1,
                    new ToolStripMenuItem("检查更新...", null,
                        (ss, ee) => { UpdateChecker.CheckAsync(_owner, true); }));
                _menu.Items.Insert(_menu.Items.Count - 1, new ToolStripSeparator());
                _menu.Items.Add("退出", null, (s, e) => {
                    if (_onExit != null) _onExit();
                });
                _tray.ContextMenuStrip = _menu;
                _tray.MouseUp += (s, e) => {
                    if (e.Button == MouseButtons.Left) _owner.ShowWindow();
                    else if (e.Button == MouseButtons.Middle) _owner.CleanMemory();
                };
            }
            catch
            {
            }
        }

        // Keep the lock-position menu checkbox in sync when the pin button toggles.
        public void SetLockChecked(bool locked)
        {
            if (_lockItem != null) _lockItem.Checked = locked;
        }

        // ---- Dynamic icon ------------------------------------------------------

        static Color LoadColor(uint load)
        {
            if (load >= 80) return Color.FromArgb(232, 93, 93);   // red
            if (load >= 60) return Color.FromArgb(240, 180, 80);  // amber
            return Color.FromArgb(120, 200, 130);                 // green
        }

        Icon BuildDynamicIcon(uint load)
        {
            var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                var color = LoadColor(load);
                // Bolt polygon, centred in 32x32.
                var pts = new PointF[] {
                    new PointF(19f, 3f),
                    new PointF(8f, 18f),
                    new PointF(15f, 18f),
                    new PointF(13f, 29f),
                    new PointF(24f, 13f),
                    new PointF(17f, 13f)
                };
                using (var brush = new SolidBrush(color))
                    g.FillPolygon(brush, pts);
                using (var pen = new Pen(Color.FromArgb(255, 255, 255), 1.2f))
                    g.DrawPolygon(pen, pts);
            }
            var hicon = bmp.GetHicon();
            var icon = Icon.FromHandle(hicon);
            return icon;
        }

        public void UpdateIcon()
        {
            try
            {
                if (_tray == null) return;
                uint load = 0;
                var s = MemoryCleaner.GetStatus();
                if (s != null) load = s.MemoryLoadPercent;
                int bucket = load >= 80 ? 2 : (load >= 60 ? 1 : 0);
                if (bucket == _lastLoadBucket && _dynIcon != null) return; // unchanged
                _lastLoadBucket = bucket;
                var old = _dynIcon;
                _dynIcon = BuildDynamicIcon(load);
                _tray.Icon = _dynIcon;
                if (old != null) { try { old.Dispose(); } catch { } }
                UpdateTooltip();
            }
            catch (Exception ex) { AppLog.Log("UpdateTrayIcon", ex); }
        }

        IntPtr CreateTrayIconHandle()
        {
            // Initial icon (green bucket). Subsequent updates go through UpdateIcon.
            try
            {
                if (_dynIcon == null) _dynIcon = BuildDynamicIcon(0);
                return _dynIcon.Handle;
            }
            catch { }
            return new Bitmap(32, 32).GetHicon();
        }

        // ---- Tooltip -----------------------------------------------------------

        public void UpdateTooltip()
        {
            if (_tray == null) return;
            string txt = _owner.TrayStatusText;
            // Truncate at the WinShell hard limit (127 wchars including null) — but .NET 4 has a stricter
            // 63-char check on the public NotifyIcon.Text setter. Use reflection to bypass it and reach
            // the underlying field, then call UpdateIcon() to refresh the tooltip.
            if (txt.Length > 127) txt = txt.Substring(0, 126) + "…";
            try
            {
                var t = typeof(NotifyIcon);
                var fld = t.GetField("text", BindingFlags.NonPublic | BindingFlags.Instance);
                if (fld != null) fld.SetValue(_tray, txt);
                var added = t.GetField("added", BindingFlags.NonPublic | BindingFlags.Instance);
                bool isAdded = added != null && (bool)added.GetValue(_tray);
                if (isAdded)
                {
                    var update = t.GetMethod("UpdateIcon", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (update != null) update.Invoke(_tray, new object[] { true });
                }
            }
            catch
            {
                // Last-resort: stay within the public 63-char limit so we at least see something
                try { _tray.Text = txt.Length > 63 ? txt.Substring(0, 60) + "…" : txt; } catch { }
            }
        }

        // ---- Autostart ---------------------------------------------------------

        static bool IsAutoStartEnabled()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    return key != null && key.GetValue("OneBox") != null;
                }
            }
            catch { return false; }
        }

        static void ToggleAutoStart(bool enable)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (enable)
                        key.SetValue("OneBox",
                            Environment.ProcessPath);
                    else
                        key.DeleteValue("OneBox", false);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        }

        // WinForms ProfessionalColorTable that mirrors the WPF floating window's
        // dark-purple palette (BgColor/CardColor/Hover/Accent/Border) so the tray
        // right-click menu looks like the window, not the default light OS menu.
        sealed class OneBoxMenuColorTable : ProfessionalColorTable
        {
            static readonly Color Bg     = Color.FromArgb(28, 26, 40);   // BgColor #1C1A28
            static readonly Color Title  = Color.FromArgb(34, 32, 50);   // title bar #222132
            static readonly Color Card   = Color.FromArgb(42, 39, 60);   // CardColor #2A273C
            static readonly Color Hover  = Color.FromArgb(58, 54, 84);   // HoverColor #3A3654
            static readonly Color Active = Color.FromArgb(110, 105, 200);// ActiveBg #6E69C8
            static readonly Color Accent = Color.FromArgb(142, 140, 216);// Accent #8E8CD8
            static readonly Color Border = Color.FromArgb(80, 75, 120);  // BorderColor #504F78

            public override Color MenuBorder { get { return Border; } }
            public override Color MenuItemBorder { get { return Accent; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Color MenuItemPressedGradientBegin { get { return Active; } }
            public override Color MenuItemPressedGradientMiddle { get { return Active; } }
            public override Color MenuItemPressedGradientEnd { get { return Active; } }
            public override Color MenuStripGradientBegin { get { return Bg; } }
            public override Color MenuStripGradientEnd { get { return Bg; } }
            public override Color ToolStripGradientBegin { get { return Title; } }
            public override Color ToolStripGradientMiddle { get { return Title; } }
            public override Color ToolStripGradientEnd { get { return Bg; } }
            public override Color ImageMarginGradientBegin { get { return Card; } }
            public override Color ImageMarginGradientMiddle { get { return Card; } }
            public override Color ImageMarginGradientEnd { get { return Card; } }
            public override Color SeparatorDark { get { return Border; } }
            public override Color SeparatorLight { get { return Card; } }
            public override Color CheckBackground { get { return Active; } }
            public override Color CheckPressedBackground { get { return Active; } }
            public override Color CheckSelectedBackground { get { return Hover; } }
            public override Color ButtonSelectedHighlight { get { return Hover; } }
            public override Color ButtonSelectedGradientBegin { get { return Hover; } }
            public override Color ButtonSelectedGradientEnd { get { return Hover; } }
            public override Color ButtonPressedGradientBegin { get { return Active; } }
            public override Color ButtonPressedGradientMiddle { get { return Active; } }
            public override Color ButtonPressedGradientEnd { get { return Active; } }
            public override Color StatusStripGradientBegin { get { return Bg; } }
            public override Color StatusStripGradientEnd { get { return Bg; } }
        }
    }
}
