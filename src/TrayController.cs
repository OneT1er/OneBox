using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;

namespace PowerAudioManager
{
    // 管理系统托盘 NotifyIcon、右键菜单、按内存负载着色的动态闪电图标及 tooltip。
    // 从 MainWindow 抽出，对主窗口耦合很窄：构造注入窗口引用（用于 show/topmost/pin-button）
    // 和 onExit 回调（退出流程由窗口决定，它拥有 device watcher 和 SystemEvents 钩子）。
    internal sealed class TrayController
    {
        readonly MainWindow _owner;
        readonly Action _onExit;
        NotifyIcon _tray;
        ContextMenuStrip _menu;
        ToolStripMenuItem _topmostItem;
        ToolStripMenuItem _lockItem;

        // 动态闪电图标：32x32 位图，按内存负载着色 — 绿 <60%, 橙 60–80%, 红 >80%
        Icon _dynIcon;
        int _lastLoadBucket = -1; // -1 强制首次调用重绘

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
                _menu.Font = AppResources.TrayFont(); // HarmonyOS Sans SC，匹配窗口字体
                // WinForms 托盘菜单匹配悬浮窗紫影深色主题
                _menu.RenderMode = ToolStripRenderMode.ManagerRenderMode;
                _menu.Renderer = new ToolStripProfessionalRenderer(new OneBoxMenuColorTable());
                _menu.BackColor = Color.FromArgb(28, 26, 40);   // BgColor #1C1A28
                _menu.ForeColor = Color.White;                  // TextPrimary
                // 必须保留 ImageMargin：WinForms 在这个 margin 内绘制勾选标记（开机自启/置顶/锁定）。
                // 隐藏 margin 会导致勾选不可见。OneBoxMenuColorTable 已将 margin 绘为深色。
                _menu.ShowImageMargin = true;
                _menu.Padding = new Padding(2, 4, 2, 4);
                _menu.Items.Add("显示窗口", null, (s, e) => _owner.ShowWindow());
                var autoItem = new ToolStripMenuItem("开机自启") { CheckOnClick = true, Checked = AutoStartService.GetCurrent() != AutoStartMethod.None };
                autoItem.Click += (s, e) =>
                {
                    if (autoItem.Checked)
                    {
                        // 开启自启：使用上次选用的方式（默认注册表）
                        var last = AppPrefs.GetInt("AutoStart.LastMethod", 1);
                        if (last < 1 || last > 3) last = 1;
                        string err = AutoStartService.Enable((AutoStartMethod)last);
                        if (err != null)
                        {
                            System.Windows.MessageBox.Show(err, "开机自启", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                            autoItem.Checked = false;
                        }
                    }
                    else
                    {
                        string err = AutoStartService.Disable();
                        if (err != null)
                        {
                            System.Windows.MessageBox.Show(err, "开机自启", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                            autoItem.Checked = true; // 回滚勾选：禁用失败，自启实际仍在生效
                        }
                    }
                };
                _menu.Items.Add(autoItem);
                _menu.Opening += (s, e) => autoItem.Checked = AutoStartService.GetCurrent() != AutoStartMethod.None;
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
                // 子菜单下拉不会自动继承父级 ContextMenuStrip 的 BackColor/ForeColor，需强制设置深色主题
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

        // pin 按钮切换时，同步托盘菜单"锁定位置"勾选状态
        public void SetLockChecked(bool locked)
        {
            if (_lockItem != null) _lockItem.Checked = locked;
        }

        static Color LoadColor(uint load)
        {
            if (load >= 80) return Color.FromArgb(232, 93, 93);
            if (load >= 60) return Color.FromArgb(240, 180, 80);
            return Color.FromArgb(120, 200, 130);
        }

        Icon BuildDynamicIcon(uint load)
        {
            var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                var color = LoadColor(load);
                // 闪电多边形，居中于 32x32 画布
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
                if (bucket == _lastLoadBucket && _dynIcon != null) return;
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
            // 初始图标（绿色），后续更新走 UpdateIcon
            try
            {
                if (_dynIcon == null) _dynIcon = BuildDynamicIcon(0);
                return _dynIcon.Handle;
            }
            catch { }
            return new Bitmap(32, 32).GetHicon();
        }

        public void UpdateTooltip()
        {
            if (_tray == null) return;
            string txt = _owner.TrayStatusText;
            // WinShell 硬限制 127 字符（含 null），但 .NET 的 NotifyIcon.Text setter 有更严格的 63 字符检查。
            // 使用反射绕过此限制直接写底层字段，再调用 UpdateIcon() 刷新 tooltip。
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
                // 最后手段：遵守 63 字符公开限制，至少能看到一部分
                try { _tray.Text = txt.Length > 63 ? txt.Substring(0, 60) + "…" : txt; } catch { }
            }
        }

        public void UpdateAutoStart()
        {
            try
            {
                if (_menu?.Items == null) return;
                foreach (var item in _menu.Items)
                {
                    if (item is ToolStripMenuItem mi && mi.Text == "开机自启")
                        { mi.Checked = AutoStartService.GetCurrent() != AutoStartMethod.None; break; }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        }

        // WinForms ProfessionalColorTable，映射 WPF 悬浮窗紫影深色调色板，使托盘菜单与窗口风格一致
        sealed class OneBoxMenuColorTable : ProfessionalColorTable
        {
            static readonly Color Bg     = Color.FromArgb(28, 26, 40);   // #1C1A28
            static readonly Color Title  = Color.FromArgb(34, 32, 50);   // #222132
            static readonly Color Card   = Color.FromArgb(42, 39, 60);   // #2A273C
            static readonly Color Hover  = Color.FromArgb(58, 54, 84);   // #3A3654
            static readonly Color Active = Color.FromArgb(110, 105, 200);// #6E69C8
            static readonly Color Accent = Color.FromArgb(142, 140, 216);// #8E8CD8
            static readonly Color Border = Color.FromArgb(80, 75, 120);  // #504F78

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
