using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
    }

    /// <summary>
    /// 手写 WPF 折线图（Control + OnRender）。双 Y 轴（左温度 0-100°C，右风扇自适应 rpm）。
    /// 时间轴：x 按 Points 的真实时间戳映射（WindowSec 时间窗），相邻点时间差超阈值则断线--
    /// 传感器失配/跨重启的"无数据"区间显示为断口，而非用旧值填满。图例高度自适应，永不与折线重叠。
    /// OnlyTemp=true 时只画温度（悬浮窗小图用），大图关掉以同时显示风扇。EnableTooltip=true 时鼠标画十字线+各线交点值卡片。
    /// </summary>
    public class PerfChart : Control
    {
        public const int Capacity = PerfHistory.Capacity;

        public List<ChartSeries> Series { get; set; }
        public double WindowSec { get; set; } = 900;   // 时间窗秒数；0=全部(Capacity*IntervalSec)。默认 15 分钟
        public float FanYMax { get; set; } = 2000;
        public bool EnableTooltip { get; set; }
        public bool OnlyTemp { get; set; }     // 悬浮窗小图：只画温度，不画风扇
        public double IntervalSec { get; set; } = 1;  // 采样间隔，用于缺口阈值与全部窗计算
        public List<ForegroundSegment> Segments { get; set; }  // 前台应用时间段（大图按时间标注）

        static readonly Brush BgBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1B, 0x2E));
        static readonly Brush AxisBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x44, 0x6A));
        static readonly Brush TempLabelBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x96, 0xB8));
        static readonly Brush FanLabelBrush = new SolidColorBrush(Color.FromRgb(0xB8, 0x9C, 0xE8));
        static readonly Brush LegendBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xD6, 0xF0));
        static readonly Brush TooltipBg = new SolidColorBrush(Color.FromArgb(0xE0, 0x22, 0x20, 0x32));
        static readonly Typeface LabelType =
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        double _mouseX = -1;

        static PerfChart()
        {
            BgBrush.Freeze(); AxisBrush.Freeze(); TempLabelBrush.Freeze(); FanLabelBrush.Freeze(); LegendBrush.Freeze(); TooltipBg.Freeze();
        }

        public PerfChart() { SnapsToDevicePixels = true; Width = double.NaN; Focusable = false; Background = Brushes.Transparent; }

        public void Refresh() => InvalidateVisual();

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
            double w = ActualWidth, h = ActualHeight;
            if (w <= 1 || h <= 1) return;

            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double padLeft = 26, padRight = OnlyTemp ? 8 : 34, padBottom = 18;

            // 有效 series（OnlyTemp 时只取温度）
            var eff = new List<ChartSeries>();
            if (Series != null)
                foreach (var s in Series)
                {
                    if (OnlyTemp && !s.IsTemp) continue;
                    if (s.Points == null || s.Points.Count == 0) continue;
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
            int rows = 1;
            double lgx = padLeft + 2;
            foreach (var s in eff)
            {
                float cur = s.Points[s.Points.Count - 1];
                var ft = MakeText($"{s.Name} {cur:0}{s.Unit}", 10, LegendBrush, ppd);
                double itemW = 12 + ft.Width + 12;
                if (lgx + itemW > w - padRight && lgx > padLeft + 2) { lgx = padLeft + 2; rows++; }
                lgx += itemW;
            }
            double legendH = rows * 14 + 4;
            double padTop = legendH;
            double plotW = w - padLeft - padRight;
            double plotH = h - padTop - padBottom;

            // 时间窗（WindowSec=0 -> 全部 = Capacity*IntervalSec）
            double windowSec = WindowSec > 0 ? WindowSec : Capacity * IntervalSec;
            if (windowSec <= 0) windowSec = 1;
            DateTime nowT = DateTime.Now;
            DateTime fromTime = nowT.AddSeconds(-windowSec);
            // 缺口阈值：相邻真实读数时间差超过此值则断线（默认 3 个采样间隔，至少 5s）
            double gapSec = Math.Max(3 * IntervalSec, 5.0);
            double TimeToX(DateTime t) => padLeft + plotW * (t - fromTime).TotalSeconds / windowSec;

            // 背景
            dc.DrawRoundedRectangle(BgBrush, null, new Rect(0, 0, w, h), 6, 6);

            // 网格 + 双 Y 轴标签
            var gridPen = new Pen(AxisBrush, 1) { DashStyle = DashStyles.Dash };
            gridPen.Freeze();
            for (int pct = 0; pct <= 100; pct += 25)
            {
                double y = padTop + plotH - (pct / 100.0) * plotH;
                dc.DrawLine(gridPen, new Point(padLeft, y), new Point(w - padRight, y));
                var ftT = MakeText(pct + "°", 9, TempLabelBrush, ppd);
                dc.DrawText(ftT, new Point(1, y - ftT.Height / 2));
                if (!OnlyTemp)
                {
                    int rpm = (int)(pct / 100.0 * fanMax);
                    var ftF = MakeText(rpm + "", 9, FanLabelBrush, ppd);
                    dc.DrawText(ftF, new Point(w - padRight + 2, y - ftF.Height / 2));
                }
            }

            if (eff.Count == 0 || plotH < 2)
            {
                var hint = MakeText("暂无温度数据", 10, TempLabelBrush, ppd);
                dc.DrawText(hint, new Point(w / 2 - hint.Width / 2, padTop + Math.Max(0, plotH / 2 - hint.Height / 2)));
            }

            // 前台应用时间段标注：半透明色块（交替）+ 切换点虚线 + exe 标签，按 x 轴时间对齐
            if (Segments != null && Segments.Count > 0 && plotH > 2)
            {
                var segBrushAlt = new SolidColorBrush(Color.FromArgb(0x20, 0x8E, 0x8C, 0xD8));
                segBrushAlt.Freeze();
                var segPen = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x9A, 0x96, 0xB8)), 1) { DashStyle = DashStyles.Dot };
                segPen.Freeze();
                int si = 0;
                foreach (var seg in Segments)
                {
                    if (seg.End < fromTime || seg.Start > nowT) { si++; continue; }
                    DateTime s0 = seg.Start < fromTime ? fromTime : seg.Start;
                    DateTime s1 = seg.End > nowT ? nowT : seg.End;
                    double x1 = TimeToX(s0), x2 = TimeToX(s1);
                    if (x1 < padLeft) x1 = padLeft;
                    if (x2 > w - padRight) x2 = w - padRight;
                    if (x2 <= x1) { si++; continue; }
                    if ((si % 2) == 0)
                        dc.DrawRectangle(segBrushAlt, null, new Rect(x1, padTop, x2 - x1, plotH));
                    if (x1 > padLeft) dc.DrawLine(segPen, new Point(x1, padTop), new Point(x1, h - padBottom));
                    double segW = x2 - x1;
                    if (segW > 28 && !string.IsNullOrEmpty(seg.Exe))
                    {
                        var ft = MakeText(seg.Exe, 9, FanLabelBrush, ppd);
                        if (ft.Width < segW - 4)
                            dc.DrawText(ft, new Point(x1 + (segW - ft.Width) / 2, padTop + 1));
                    }
                    si++;
                }
            }

            // 折线：按时间戳定位 x，缺口断线，像素级去重避免超密
            foreach (var s in eff)
            {
                int n = s.Points.Count;
                if (n < 1) continue;
                double yMax = s.IsTemp ? 100.0 : fanMax;
                if (yMax <= 0) yMax = 1;

                var pen = new Pen(new SolidColorBrush(s.Color), 1.5);
                pen.Freeze();
                var geo = new StreamGeometry();
                int lastInWin = -1;
                using (var ctx = geo.Open())
                {
                    bool first = true;
                    double lastX = double.NaN;
                    DateTime prevT = default;
                    for (int i = 0; i < n; i++)
                    {
                        var t = s.Times[i];
                        if (t < fromTime) continue;        // 窗外左溢出
                        lastInWin = i;
                        double x = TimeToX(t);
                        float val = Clamp(s.Points[i], s.IsTemp, yMax);
                        double y = padTop + plotH - (val / yMax) * plotH;
                        bool gap = !first && (t - prevT).TotalSeconds > gapSec;
                        if (first || gap) { ctx.BeginFigure(new Point(x, y), false, false); lastX = x; }
                        else if (x - lastX >= 1.0) { ctx.LineTo(new Point(x, y), true, false); lastX = x; }
                        first = false;
                        prevT = t;
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);

                // 最新点圆点
                if (lastInWin >= 0)
                {
                    double lx = TimeToX(s.Times[lastInWin]);
                    float lv = Clamp(s.Points[lastInWin], s.IsTemp, yMax);
                    double ly = padTop + plotH - (lv / yMax) * plotH;
                    dc.DrawEllipse(new SolidColorBrush(s.Color), null, new Point(lx, ly), 2, 2);
                }
            }

            // 图例（顶部，0..legendH，与折线区 padTop 分开）
            lgx = padLeft + 2; double lgy = 2;
            foreach (var s in eff)
            {
                float cur = s.Points[s.Points.Count - 1];
                var ft = MakeText($"{s.Name} {cur:0}{s.Unit}", 10, LegendBrush, ppd);
                double itemW = 12 + ft.Width + 12;
                if (lgx + itemW > w - padRight && lgx > padLeft + 2) { lgx = padLeft + 2; lgy += 14; }
                dc.DrawRectangle(new SolidColorBrush(s.Color), null, new Rect(lgx, lgy + 3, 8, 8));
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

            // tooltip：十字线 + 各线在该时间点最近的值卡片（顶部显示该时间点的前台应用）
            if (EnableTooltip && _mouseX >= padLeft && _mouseX <= w - padRight && plotH > 2)
            {
                var vpen = new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0x96, 0xB8)), 1) { DashStyle = DashStyles.Dot };
                vpen.Freeze();
                dc.DrawLine(vpen, new Point(_mouseX, padTop), new Point(_mouseX, h - padBottom));

                double tt = (_mouseX - padLeft) / plotW;
                DateTime tAt = fromTime.AddSeconds(tt * windowSec);
                double nearSec = Math.Max(2 * IntervalSec, 3.0);

                var labels = new List<(Color, string)>();
                foreach (var s in eff)
                {
                    if (s.Points == null || s.Points.Count < 1) continue;
                    int n = s.Points.Count;
                    // 二分找最接近 tAt 的点（Times 升序），避免全天 86400 点下鼠标移动线性扫描卡顿
                    int best;
                    if (tAt <= s.Times[0]) best = 0;
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
                    double yMax = s.IsTemp ? 100.0 : fanMax; if (yMax <= 0) yMax = 1;
                    float vv = Clamp(val, s.IsTemp, yMax);
                    double y = padTop + plotH - (vv / yMax) * plotH;
                    dc.DrawEllipse(new SolidColorBrush(s.Color), null, new Point(_mouseX, y), 3, 3);
                    labels.Add((s.Color, $"{s.Name} {val:0}{s.Unit}"));
                }

                // 查鼠标时间点对应的前台应用
                string fgExe = null;
                if (Segments != null && Segments.Count > 0)
                    foreach (var seg in Segments)
                        if (tAt >= seg.Start && tAt < seg.End) { fgExe = seg.Exe; break; }

                if (labels.Count > 0 || !string.IsNullOrEmpty(fgExe))
                {
                    int tipRows = labels.Count + (string.IsNullOrEmpty(fgExe) ? 0 : 1);
                    double cardW = 150, cardH = 14 * tipRows + 8;
                    double cx = _mouseX + 8; if (cx + cardW > w - 2) cx = _mouseX - cardW - 8;
                    double cy = padTop + 4;
                    dc.DrawRoundedRectangle(TooltipBg, null, new Rect(cx, cy, cardW, cardH), 4, 4);
                    int row = 0;
                    if (!string.IsNullOrEmpty(fgExe))
                    {
                        var ftf = MakeText("前台: " + fgExe, 10, FanLabelBrush, ppd);
                        dc.DrawText(ftf, new Point(cx + 6, cy + 4 + row * 14));
                        row++;
                    }
                    for (int i = 0; i < labels.Count; i++)
                    {
                        dc.DrawRectangle(new SolidColorBrush(labels[i].Item1), null, new Rect(cx + 6, cy + 7 + row * 14, 8, 8));
                        var ft = MakeText(labels[i].Item2, 10, LegendBrush, ppd);
                        dc.DrawText(ft, new Point(cx + 20, cy + 4 + row * 14));
                        row++;
                    }
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
