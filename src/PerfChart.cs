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
    // 一条折线的数据
    public class ChartSeries
    {
        public string Name;
        public Color Color;
        public List<float> Points;   // 最新值在末尾
        public string Unit = "°C";
        public bool IsTemp = true;   // true=温度(左Y轴 0-100°C)，false=风扇(右Y轴 0-FanYMax rpm)
    }

    /// <summary>
    /// 手写 WPF 折线图（Control + OnRender）。双 Y 轴（左温度 0-100°C，右风扇自适应 rpm）。
    /// 图例高度自适应（按行数抬高 padTop），永不与折线重叠。颜色由 PerfHistory 调色板传入。
    /// OnlyTemp=true 时只画温度（悬浮窗小图用），大图关掉以同时显示风扇。点数超宽时降采样。
    /// EnableTooltip=true 时鼠标移动画十字线 + 各线交点值卡片（大图用）。
    /// </summary>
    public class PerfChart : Control
    {
        public const int Capacity = PerfHistory.Capacity;

        public List<ChartSeries> Series { get; set; }
        public int MaxPoints { get; set; } = 300;
        public float FanYMax { get; set; } = 2000;
        public bool EnableTooltip { get; set; }
        public bool OnlyTemp { get; set; }     // 悬浮窗小图：只画温度，不画风扇
        public double IntervalSec { get; set; } = 1;  // 采样间隔，用于 x 轴时间刻度
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
            double padLeft = 26, padRight = OnlyTemp ? 8 : 34, padBottom = 18;  // 底部留 x 轴时间刻度

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

            int cap = MaxPoints > 0 ? MaxPoints : Capacity;

            // 前台应用时间段标注：半透明色块（交替）+ 切换点虚线 + exe 标签，按 x 轴时间对齐
            if (Segments != null && Segments.Count > 0 && plotH > 2)
            {
                DateTime nowT = DateTime.Now;
                int nForTime = 0;
                foreach (var s in eff) if (s.Points.Count > nForTime) nForTime = s.Points.Count;
                if (nForTime > 0 && IntervalSec > 0)
                {
                    double TimeToX(DateTime t)
                    {
                        double j = nForTime - 1 - (nowT - t).TotalSeconds / IntervalSec;
                        return padLeft + plotW * (cap - nForTime + j) / Math.Max(1, cap - 1);
                    }
                    var segBrushAlt = new SolidColorBrush(Color.FromArgb(0x20, 0x8E, 0x8C, 0xD8));
                    segBrushAlt.Freeze();
                    var segPen = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x9A, 0x96, 0xB8)), 1) { DashStyle = DashStyles.Dot };
                    segPen.Freeze();
                    int si = 0;
                    foreach (var seg in Segments)
                    {
                        double x1 = TimeToX(seg.Start);
                        double x2 = TimeToX(seg.End);
                        if (x2 <= padLeft || x1 >= w - padRight) { si++; continue; }
                        if (x1 < padLeft) x1 = padLeft;
                        if (x2 > w - padRight) x2 = w - padRight;
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
            }

            // 折线（降采样）
            foreach (var s in eff)
            {
                if (s.Points.Count < 2) continue;
                int n = s.Points.Count;
                double yMax = s.IsTemp ? 100.0 : fanMax;
                if (yMax <= 0) yMax = 1;

                int target = (int)plotW; if (target < 2) target = 2;
                int step = n > target ? (int)Math.Ceiling(n / (double)target) : 1;

                var pen = new Pen(new SolidColorBrush(s.Color), 1.5);
                pen.Freeze();
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    bool first = true;
                    for (int i = 0; i < n; i += step)
                    {
                        double x = padLeft + plotW * (cap - n + i) / Math.Max(1, cap - 1);
                        float val = Clamp(s.Points[i], s.IsTemp, yMax);
                        double y = padTop + plotH - (val / yMax) * plotH;
                        if (first) { ctx.BeginFigure(new Point(x, y), false, false); first = false; }
                        else ctx.LineTo(new Point(x, y), true, false);
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);

                int li = n - 1;
                double lx = padLeft + plotW * (cap - n + li) / Math.Max(1, cap - 1);
                float lv = Clamp(s.Points[li], s.IsTemp, yMax);
                double ly = padTop + plotH - (lv / yMax) * plotH;
                dc.DrawEllipse(new SolidColorBrush(s.Color), null, new Point(lx, ly), 2, 2);
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

            // x 轴时间刻度（底部）：最新点=现在，往左按 IntervalSec 回推
            if (plotH > 2)
            {
                int nForTime = 0;
                foreach (var s in eff) if (s.Points.Count > nForTime) nForTime = s.Points.Count;
                if (nForTime > 0)
                {
                    int ticks = 4;
                    for (int k = 0; k <= ticks; k++)
                    {
                        double frac = (double)k / ticks;
                        double xt = padLeft + plotW * frac;
                        int j = (int)Math.Round(frac * (cap - 1) - (cap - nForTime));
                        if (j < 0) j = 0; if (j >= nForTime) j = nForTime - 1;
                        DateTime tm = DateTime.Now.AddSeconds(-(nForTime - 1 - j) * IntervalSec);
                        var ft = MakeText(tm.ToString("HH:mm"), 9, TempLabelBrush, ppd);
                        dc.DrawText(ft, new Point(xt - ft.Width / 2, h - padBottom + 3));
                    }
                }
            }

            // tooltip：十字线 + 各线交点值卡片（顶部显示该时间点的前台应用）
            if (EnableTooltip && _mouseX >= padLeft && _mouseX <= w - padRight && plotH > 2)
            {
                var vpen = new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0x96, 0xB8)), 1) { DashStyle = DashStyles.Dot };
                vpen.Freeze();
                dc.DrawLine(vpen, new Point(_mouseX, padTop), new Point(_mouseX, h - padBottom));

                double t = (_mouseX - padLeft) / plotW;
                var labels = new List<(Color, string)>();
                int nForTip = 0;
                foreach (var s in eff)
                {
                    if (s.Points.Count > nForTip) nForTip = s.Points.Count;
                    if (s.Points.Count < 1) continue;
                    int n = s.Points.Count;
                    int j = (int)Math.Round(t * (cap - 1) - (cap - n));
                    if (j < 0) j = 0; if (j >= n) j = n - 1;
                    float val = s.Points[j];
                    double yMax = s.IsTemp ? 100.0 : fanMax; if (yMax <= 0) yMax = 1;
                    float vv = Clamp(val, s.IsTemp, yMax);
                    double y = padTop + plotH - (vv / yMax) * plotH;
                    dc.DrawEllipse(new SolidColorBrush(s.Color), null, new Point(_mouseX, y), 3, 3);
                    labels.Add((s.Color, $"{s.Name} {val:0}{s.Unit}"));
                }

                // 查鼠标时间点对应的前台应用
                string fgExe = null;
                if (Segments != null && Segments.Count > 0 && nForTip > 0 && IntervalSec > 0)
                {
                    int jTip = (int)Math.Round(t * (cap - 1) - (cap - nForTip));
                    if (jTip < 0) jTip = 0; if (jTip >= nForTip) jTip = nForTip - 1;
                    DateTime tAt = DateTime.Now.AddSeconds(-(nForTip - 1 - jTip) * IntervalSec);
                    foreach (var seg in Segments)
                        if (tAt >= seg.Start && tAt < seg.End) { fgExe = seg.Exe; break; }
                }

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
