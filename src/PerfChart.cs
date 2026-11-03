using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PowerAudioManager
{
    // 一条折线的数据。Points 与 Times 一一对应（按时间升序），Times 用于按真实时间定位 x、缺口断线。
    public class ChartSeries
    {
        public string Name;
        public Color Color;
        public List<float> Points;      // 最新值在末尾
        public List<DateTime> Times;    // 与 Points 等长，每点的采集时刻
        public string Unit = "°C";
        public bool IsTemp = true;      // true=温度(左Y轴 0-100°C)，false=风扇(右Y轴 0-FanYMax rpm)
        internal object HistorySource;
        internal long HistoryStart, HistoryEnd;
        internal bool TimesOrdered = true;
    }

    /// <summary>
    /// 手写 WPF 折线图（FrameworkElement + DrawingVisual）。双 Y 轴（左温度 0-100°C，右风扇自适应 rpm）。
    /// 时间轴：x 按 Points 的真实时间戳映射（WindowSec 时间窗），相邻点时间差超阈值则断线--
    /// 传感器失配/跨重启的"无数据"区间显示为断口，而非用旧值填满。图例高度自适应，永不与折线重叠。
    /// OnlyTemp=true 时只画温度（悬浮窗小图用），大图关掉以同时显示风扇。EnableTooltip=true 时鼠标画十字线+各线交点值卡片。
    /// </summary>
    public class PerfChart : FrameworkElement
    {
        public const int Capacity = PerfHistory.Capacity;

        public List<ChartSeries> Series { get; set; }
        public double WindowSec { get; set; } = 900;   // 时间窗秒数；0=全天 24 小时。默认 15 分钟
        public double DisplayWindowSec => WindowSec > 0 ? WindowSec : PerfHistory.DaySeconds;
        public DateTime? EndTime { get; set; }         // 查询与绘制共用的窗口末端；null 时跟随当前时间。
        public float FanYMax { get; set; } = 2000;
        public bool EnableTooltip { get; set; }
        public bool OnlyTemp { get; set; }     // 悬浮窗小图：只画温度，不画风扇
        public double IntervalSec { get; set; } = 1;  // 采样间隔，仅用于缺口和悬停取值阈值。
        public List<ForegroundSegment> Segments { get; set; }  // 前台应用时间段（大图按时间标注）
        public HashSet<string> HiddenSeries { get; set; }      // 图例切换隐藏的系列名
        public bool ShowLegend { get; set; } = true;           // 大图窗口用外部图例芯片时关闭
        public double WarnTemp { get; set; }                   // >0 时画高温警告虚线

        static readonly Brush BgBrush = new SolidColorBrush(Color.FromRgb(0x1C, 0x1A, 0x26));
        static readonly Brush AxisBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x28, 0x39));
        static readonly Brush TempLabelBrush = new SolidColorBrush(Color.FromRgb(0x8E, 0x8B, 0xAE));
        static readonly Brush FanLabelBrush = new SolidColorBrush(Color.FromRgb(0xA9, 0xA6, 0xC4));
        static readonly Brush LegendBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xF1, 0xFA));
        static readonly Brush TooltipBg = new SolidColorBrush(Color.FromArgb(0xF5, 0x17, 0x15, 0x1F));
        static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xB2, 0x5A));
        static readonly Typeface LabelType =
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        // 每秒重绘都会用到的画笔/笔：提为静态冻结，避免每帧 new 一批 Freezable 制造 GC 压力。
        // 按系列颜色缓存（颜色数 = 系列数，有界），命中即复用冻结实例。
        static readonly Dictionary<Color, SolidColorBrush> _brushCache = new Dictionary<Color, SolidColorBrush>();
        static readonly Dictionary<Color, Pen> _penCache = new Dictionary<Color, Pen>();
        static readonly Pen _gridPen;
        static readonly Pen _segPen;
        static readonly SolidColorBrush _segBrushAlt;
        static readonly Pen _vpen;
        static readonly Pen _tooltipPen;
        static readonly Pen _warnPen;

        double _mouseX = -1;
        DrawingGroup _chartDrawing;
        readonly DrawingVisual _chartVisual = new DrawingVisual
        {
            CacheMode = new BitmapCache { SnapsToDevicePixels = true }
        };
        readonly DrawingVisual _tooltipVisual = new DrawingVisual();
        Size _drawingSize;
        double _drawingPixelsPerDip;
        List<ChartSeries> _drawnSeries;
        Rect _plotBounds;
        DateTime _plotFromTime;
        double _plotWindowSec, _plotFanMax;

        static PerfChart()
        {
            BgBrush.Freeze(); AxisBrush.Freeze(); TempLabelBrush.Freeze(); FanLabelBrush.Freeze(); LegendBrush.Freeze(); TooltipBg.Freeze(); WarnBrush.Freeze();
            _gridPen = new Pen(AxisBrush, 1); _gridPen.Freeze();
            _segBrushAlt = new SolidColorBrush(Color.FromArgb(0x18, 0x8E, 0x8C, 0xD8)); _segBrushAlt.Freeze();
            _segPen = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x5A, 0x56, 0x80)), 1) { DashStyle = DashStyles.Dot }; _segPen.Freeze();
            _vpen = new Pen(new SolidColorBrush(Color.FromRgb(0x5A, 0x56, 0x80)), 1); _vpen.Freeze();
            _tooltipPen = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x36, 0x50)), 1); _tooltipPen.Freeze();
            _warnPen = new Pen(WarnBrush, 1) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) }; _warnPen.Freeze();
        }

        static SolidColorBrush BrushFor(Color c)
        {
            if (!_brushCache.TryGetValue(c, out var b))
            {
                b = new SolidColorBrush(c); b.Freeze();
                _brushCache[c] = b;
            }
            return b;
        }
        static Pen PenFor(Color c)
        {
            if (!_penCache.TryGetValue(c, out var p))
            {
                p = new Pen(new SolidColorBrush(c), 1.5); p.Freeze();
                _penCache[c] = p;
            }
            return p;
        }

        public PerfChart()
        {
            SnapsToDevicePixels = true; Width = double.NaN; Focusable = false;
            AddVisualChild(_chartVisual);
            AddVisualChild(_tooltipVisual);
        }

        protected override int VisualChildrenCount => base.VisualChildrenCount + 2;

        protected override Visual GetVisualChild(int index)
        {
            int count = base.VisualChildrenCount;
            if (index < count) return base.GetVisualChild(index);
            return (index - count) switch
            {
                0 => _chartVisual,
                1 => _tooltipVisual,
                _ => throw new ArgumentOutOfRangeException(nameof(index))
            };
        }

        // 数据、时长或图例改变时重建底图；鼠标移动只重画十字线和提示卡片。
        public void Refresh()
        {
            _chartDrawing = null;
            InvalidateVisual();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            Refresh();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!EnableTooltip) return;
            _mouseX = e.GetPosition(this).X;
            InvalidateVisual();
        }
        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (_mouseX >= 0) { _mouseX = -1; InvalidateVisual(); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (ActualWidth <= 1 || ActualHeight <= 1)
            {
                using (_chartVisual.RenderOpen()) { }
                using (_tooltipVisual.RenderOpen()) { }
                _chartDrawing = null;
                return;
            }
            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (_chartDrawing == null || _drawingSize != RenderSize || _drawingPixelsPerDip != ppd)
            {
                var drawing = new DrawingGroup();
                using (var context = drawing.Open()) DrawChart(context, ppd);
                drawing.Freeze();
                _chartDrawing = drawing;
                _drawingSize = RenderSize;
                _drawingPixelsPerDip = ppd;
                using (var context = _chartVisual.RenderOpen()) context.DrawDrawing(drawing);
            }
            // 独立的位图缓存层：鼠标更新不能重新提交底图，否则 WPF 仍会逐帧光栅化整天的折线。
            using (var context = _tooltipVisual.RenderOpen()) DrawTooltip(context, ppd);
        }

        void DrawChart(DrawingContext dc, double ppd)
        {
            double w = ActualWidth, h = ActualHeight;
            double padLeft = 26, padRight = OnlyTemp ? 8 : 34, padBottom = 18;

            // 有效 series（OnlyTemp 时只取温度）
            var eff = new List<ChartSeries>();
            if (Series != null)
                foreach (var s in Series)
                {
                    if (OnlyTemp && !s.IsTemp) continue;
                    if (s.Points == null || s.Points.Count == 0) continue;
                    if (HiddenSeries != null && s.Name != null && HiddenSeries.Contains(s.Name)) continue;
                    eff.Add(s);
                }

            // 自适应风扇 Y 上限
            float fanMax = FanYMax;
            if (!OnlyTemp)
            {
                float observed = 0;
                foreach (var s in eff)
                {
                    if (s.IsTemp) continue;
                    foreach (var v in s.Points) if (v > observed) observed = v;
                }
                if (observed > fanMax) fanMax = observed;
                fanMax = (float)Math.Ceiling(fanMax / 100) * 100;
                if (fanMax < 500) fanMax = 500;
            }

            // 算图例行数 -> 自适应 padTop，避免图例与折线重叠
            int rows = ShowLegend ? 1 : 0;
            double lgx = padLeft + 2;
            if (ShowLegend)
                foreach (var s in eff)
                {
                    float cur = s.Points[s.Points.Count - 1];
                    var ft = MakeText($"{s.Name} {cur:0}{s.Unit}", 10, LegendBrush, ppd);
                    double itemW = 12 + ft.Width + 12;
                    if (lgx + itemW > w - padRight && lgx > padLeft + 2) { lgx = padLeft + 2; rows++; }
                    lgx += itemW;
                }
            double legendH = ShowLegend ? rows * 14 + 4 : 10;
            double padTop = legendH;
            double plotW = w - padLeft - padRight;
            double plotH = h - padTop - padBottom;

            double windowSec = DisplayWindowSec;
            DateTime nowT = EndTime ?? DateTime.Now;
            DateTime fromTime = nowT.AddSeconds(-windowSec);
            // 缺口阈值：相邻真实读数时间差超过此值则断线（默认 3 个采样间隔，至少 5s）
            double gapSec = Math.Max(3 * IntervalSec, 5.0);
            double TimeToX(DateTime t) => padLeft + plotW * (t - fromTime).TotalSeconds / windowSec;

            // 背景
            dc.DrawRoundedRectangle(BgBrush, null, new Rect(0, 0, w, h), 10, 10);

            // 网格 + 双 Y 轴标签
            for (int pct = 0; pct <= 100; pct += 25)
            {
                double y = padTop + plotH - (pct / 100.0) * plotH;
                dc.DrawLine(_gridPen, new Point(padLeft, y), new Point(w - padRight, y));
                var ftT = MakeText(pct + "°", 9, TempLabelBrush, ppd);
                dc.DrawText(ftT, new Point(1, y - ftT.Height / 2));
                if (!OnlyTemp)
                {
                    int rpm = (int)(pct / 100.0 * fanMax);
                    var ftF = MakeText(rpm + "", 9, FanLabelBrush, ppd);
                    dc.DrawText(ftF, new Point(w - padRight + 2, y - ftF.Height / 2));
                }
            }

            // 高温警告虚线
            if (WarnTemp > 0 && WarnTemp < 100 && plotH > 2)
            {
                double wy = padTop + plotH - (WarnTemp / 100.0) * plotH;
                dc.DrawLine(_warnPen, new Point(padLeft, wy), new Point(w - padRight, wy));
                var wl = MakeText("高温警告 " + WarnTemp.ToString("0") + "°", 9, WarnBrush, ppd);
                dc.DrawText(wl, new Point(w - padRight - wl.Width - 2, wy - wl.Height - 1));
            }

            if (eff.Count == 0 || plotH < 2)
            {
                var hint = MakeText("暂无温度数据", 10, TempLabelBrush, ppd);
                dc.DrawText(hint, new Point(w / 2 - hint.Width / 2, padTop + Math.Max(0, plotH / 2 - hint.Height / 2)));
            }

            // 前台应用时间段标注：半透明色块（交替）+ 切换点虚线 + exe 标签，按 x 轴时间对齐
            if (Segments != null && Segments.Count > 0 && plotH > 2)
            {
                int band = 0;
                double lastBoundary = padLeft;
                const double minBandWidth = 8;
                for (int si = 0; si < Segments.Count; si++)
                {
                    var seg = Segments[si];
                    if (seg.End < fromTime || seg.Start > nowT) continue;
                    DateTime s0 = seg.Start < fromTime ? fromTime : seg.Start;
                    DateTime s1 = seg.End > nowT ? nowT : seg.End;
                    string exe = seg.Exe;
                    // 合并连续、无法辨认的窄区间；原始 Segments 仍供悬停精确查值。
                    while (TimeToX(s1) - TimeToX(s0) < minBandWidth && si + 1 < Segments.Count)
                    {
                        var next = Segments[si + 1];
                        if (next.Start > s1 || next.End <= s1 || next.Start >= nowT) break;
                        s1 = next.End > nowT ? nowT : next.End;
                        exe = null;
                        si++;
                    }
                    double x1 = TimeToX(s0), x2 = TimeToX(s1);
                    if (x1 < padLeft) x1 = padLeft;
                    if (x2 > w - padRight) x2 = w - padRight;
                    if (x2 <= x1) continue;
                    if ((band++ % 2) == 0 && x2 - x1 >= 1 / ppd)
                        dc.DrawRectangle(_segBrushAlt, null, new Rect(x1, padTop, x2 - x1, plotH));
                    if (x1 - lastBoundary >= minBandWidth)
                    {
                        dc.DrawLine(_segPen, new Point(x1, padTop), new Point(x1, h - padBottom));
                        lastBoundary = x1;
                    }
                    double segW = x2 - x1;
                    if (segW > 28 && !string.IsNullOrEmpty(exe))
                    {
                        var ft = MakeText(exe, 9, FanLabelBrush, ppd);
                        if (ft.Width < segW - 4)
                            dc.DrawText(ft, new Point(x1 + (segW - ft.Width) / 2, padTop + 1));
                    }
                }
            }

            // 每个像素列保留峰谷，每段另保留首尾；按原采样顺序连接，不跳过同像素内的尖峰。
            foreach (var s in eff)
            {
                int n = s.Points.Count;
                if (n < 1) continue;
                double yMax = s.IsTemp ? 100.0 : fanMax;
                if (yMax <= 0) yMax = 1;

                var pen = PenFor(s.Color);
                var geo = new StreamGeometry();
                int lastInWin = -1;
                using (var ctx = geo.Open())
                {
                    bool newFigure = true;
                    int bucket = -1, first = -1, last = -1, min = -1, max = -1, emitted = -1;
                    long prevTicks = 0;
                    double pixelsPerTick = plotW * ppd / (windowSec * TimeSpan.TicksPerSecond);
                    long gapTicks = (long)(gapSec * TimeSpan.TicksPerSecond);
                    void Emit(int i)
                    {
                        if (i == emitted) return;
                        double y = padTop + plotH - (Clamp(s.Points[i], s.IsTemp, yMax) / yMax) * plotH;
                        var point = new Point(TimeToX(s.Times[i]), y);
                        if (newFigure) { ctx.BeginFigure(point, false, false); newFigure = false; }
                        else ctx.LineTo(point, true, false);
                        emitted = i;
                    }
                    void FlushBucket()
                    {
                        if (first < 0) return;
                        if (newFigure) Emit(first);
                        if (min < max) { Emit(min); Emit(max); }
                        else { Emit(max); Emit(min); }
                    }
                    for (int i = 0; i < n; i++)
                    {
                        var t = s.Times[i];
                        if (t < fromTime || t > nowT) continue;
                        lastInWin = i;
                        int pixel = (int)((t.Ticks - fromTime.Ticks) * pixelsPerTick);
                        bool gap = first >= 0 && (t.Ticks - prevTicks > gapTicks || t.Ticks < prevTicks);
                        if (first < 0 || pixel != bucket || gap)
                        {
                            FlushBucket();
                            if (gap) { Emit(last); newFigure = true; }
                            bucket = pixel;
                            first = last = min = max = i;
                        }
                        else
                        {
                            if (s.Points[i] < s.Points[min]) min = i;
                            if (s.Points[i] > s.Points[max]) max = i;
                            last = i;
                        }
                        prevTicks = t.Ticks;
                    }
                    FlushBucket();
                    if (last >= 0) Emit(last);
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);

                // 最新点圆点
                if (lastInWin >= 0)
                {
                    double lx = TimeToX(s.Times[lastInWin]);
                    float lv = Clamp(s.Points[lastInWin], s.IsTemp, yMax);
                    double ly = padTop + plotH - (lv / yMax) * plotH;
                    dc.DrawEllipse(BrushFor(s.Color), null, new Point(lx, ly), 2, 2);
                }
            }

            // 图例（顶部，0..legendH，与折线区 padTop 分开）
            lgx = padLeft + 2; double lgy = 2;
            if (ShowLegend) foreach (var s in eff)
            {
                float cur = s.Points[s.Points.Count - 1];
                var ft = MakeText($"{s.Name} {cur:0}{s.Unit}", 10, LegendBrush, ppd);
                double itemW = 12 + ft.Width + 12;
                if (lgx + itemW > w - padRight && lgx > padLeft + 2) { lgx = padLeft + 2; lgy += 14; }
                dc.DrawEllipse(BrushFor(s.Color), null, new Point(lgx + 4, lgy + 7), 4, 4);
                dc.DrawText(ft, new Point(lgx + 12, lgy));
                lgx += itemW;
            }

            // x 轴时间刻度（底部）：从 fromTime 到 nowT 均分
            if (plotH > 2)
            {
                int ticks = 4;
                bool showDate = windowSec > 86400;
                for (int k = 0; k <= ticks; k++)
                {
                    double frac = (double)k / ticks;
                    DateTime tm = fromTime.AddSeconds(frac * windowSec);
                    string lbl = showDate ? tm.ToString("MM-dd HH:mm") : tm.ToString("HH:mm");
                    double xt = padLeft + plotW * frac;
                    var ft = MakeText(lbl, 9, TempLabelBrush, ppd);
                    dc.DrawText(ft, new Point(xt - ft.Width / 2, h - padBottom + 3));
                }
            }

            // 提示层使用底图同一份时间轴/刻度，避免两次每秒刷新之间鼠标取值与曲线错位。
            _drawnSeries = eff;
            _plotBounds = new Rect(padLeft, padTop, Math.Max(0, plotW), Math.Max(0, plotH));
            _plotFromTime = fromTime;
            _plotWindowSec = windowSec;
            _plotFanMax = fanMax;
        }

        void DrawTooltip(DrawingContext dc, double ppd)
        {
            double w = ActualWidth;
            double padLeft = _plotBounds.Left, padTop = _plotBounds.Top;
            double plotW = _plotBounds.Width, plotH = _plotBounds.Height;
            // tooltip：十字线 + 各线在该时间点最近的值卡片（顶部显示该时间点的前台应用）
            if (EnableTooltip && _mouseX >= padLeft && _mouseX <= _plotBounds.Right && plotW > 0 && plotH > 2)
            {
                var vpen = _vpen;
                dc.DrawLine(vpen, new Point(_mouseX, padTop), new Point(_mouseX, _plotBounds.Bottom));

                double tt = (_mouseX - padLeft) / plotW;
                DateTime tAt = _plotFromTime.AddSeconds(tt * _plotWindowSec);
                double nearSec = Math.Max(2 * IntervalSec, 3.0);

                var labels = new List<(Color, string)>();
                foreach (var s in _drawnSeries)
                {
                    if (s.Points == null || s.Points.Count < 1) continue;
                    int n = s.Points.Count;
                    // 二分找最接近 tAt 的点（Times 升序），避免全天 86400 点下鼠标移动线性扫描卡顿
                    int best;
                    if (!s.TimesOrdered)
                    {
                        best = 0;
                        for (int i = 1; i < n; i++)
                            if (Math.Abs((s.Times[i] - tAt).Ticks) < Math.Abs((s.Times[best] - tAt).Ticks)) best = i;
                    }
                    else if (tAt <= s.Times[0]) best = 0;
                    else if (tAt >= s.Times[n - 1]) best = n - 1;
                    else
                    {
                        int lo = 0, hi = n - 1;
                        while (lo < hi) { int mid = (lo + hi) >> 1; if (s.Times[mid] < tAt) lo = mid + 1; else hi = mid; }
                        best = lo;
                        if (lo > 0 && Math.Abs((s.Times[lo - 1] - tAt).TotalSeconds) < Math.Abs((s.Times[lo] - tAt).TotalSeconds)) best = lo - 1;
                    }
                    double bestDt = Math.Abs((s.Times[best] - tAt).TotalSeconds);
                    if (bestDt > nearSec) continue;   // 该时间点无数据（缺口），不显示该线
                    float val = s.Points[best];
                    double yMax = s.IsTemp ? 100.0 : _plotFanMax; if (yMax <= 0) yMax = 1;
                    float vv = Clamp(val, s.IsTemp, yMax);
                    double y = padTop + plotH - (vv / yMax) * plotH;
                    dc.DrawEllipse(BrushFor(s.Color), null, new Point(_mouseX, y), 3, 3);
                    labels.Add((s.Color, $"{s.Name} {val:0}{s.Unit}"));
                }

                // 查鼠标时间点对应的前台应用
                string fgExe = null;
                if (Segments != null && Segments.Count > 0)
                    foreach (var seg in Segments)
                        if (tAt >= seg.Start && tAt < seg.End) { fgExe = seg.Exe; break; }

                if (labels.Count > 0 || Segments != null)
                {
                    int tipRows = labels.Count + 1;
                    tipRows++; // time header
                    double cardW = 160, cardH = 16 * tipRows + 12;
                    double cx = _mouseX + 10; if (cx + cardW > w - 2) cx = _mouseX - cardW - 10;
                    double cy = padTop + 4;
                    dc.DrawRoundedRectangle(TooltipBg, _tooltipPen, new Rect(cx, cy, cardW, cardH), 8, 8);
                    int row = 0;
                    var fth = MakeText(tAt.ToString("HH:mm:ss"), 11, LegendBrush, ppd);
                    fth.SetFontWeight(FontWeights.SemiBold);
                    dc.DrawText(fth, new Point(cx + 10, cy + 6 + row * 16));
                    row++;
                    for (int i = 0; i < labels.Count; i++)
                    {
                        dc.DrawEllipse(BrushFor(labels[i].Item1), null, new Point(cx + 13, cy + 14 + row * 16), 3, 3);
                        var ft = MakeText(labels[i].Item2, 10, LegendBrush, ppd);
                        dc.DrawText(ft, new Point(cx + 22, cy + 6 + row * 16));
                        row++;
                    }
                    var ftf = MakeText("前台：" + (string.IsNullOrEmpty(fgExe) ? "未记录" : fgExe), 10, FanLabelBrush, ppd);
                    ftf.MaxTextWidth = cardW - 20;
                    ftf.MaxLineCount = 1;
                    ftf.Trimming = TextTrimming.CharacterEllipsis;
                    dc.DrawText(ftf, new Point(cx + 10, cy + 6 + row * 16));
                }
            }
        }

        static float Clamp(float v, bool isTemp, double yMax)
        {
            if (v < 0) v = 0;
            if (isTemp && v > 100) v = 100;
            else if (!isTemp && v > yMax) v = (float)yMax;
            return v;
        }

        static FormattedText MakeText(string text, double size, Brush brush, double ppd)
        {
            return new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, LabelType, size, brush, ppd);
        }
    }
}
