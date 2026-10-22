using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NAudio.Dsp;

namespace PowerAudioManager.AudioStudio;

// Shared frozen paint for the OneMic visuals.  These controls repaint up to
// ~12 times per second; allocating brushes/pens per frame produced steady
// Gen0 churn, so every resource used in OnRender is created once.
internal static class StudioPaint
{
    internal static readonly Brush Track = Frozen(ThemeTokens.Background);
    internal static readonly Brush Accent = Frozen(ThemeTokens.Accent);
    internal static readonly Brush Warn = Frozen(ThemeTokens.Warn);
    internal static readonly Brush QuietBar = Frozen(Color.FromRgb(74, 70, 112));
    internal static readonly Brush Label = Frozen(ThemeTokens.TertiaryText);
    internal static readonly Brush HandleFill = Frozen(ThemeTokens.Background);
    internal static readonly Brush CurveFill = Frozen(Color.FromArgb(40, ThemeTokens.Accent.R, ThemeTokens.Accent.G, ThemeTokens.Accent.B));
    internal static readonly Pen Grid = FrozenPen(ThemeTokens.Raised, 1);
    internal static readonly Pen ZeroLine = FrozenPen(ThemeTokens.BorderStrong, 1);
    internal static readonly Pen Curve = FrozenPen(ThemeTokens.Accent, 2);
    internal static readonly Pen Handle = FrozenPen(ThemeTokens.Accent, 2);
    internal static readonly Pen SelectedHandle = FrozenPen(ThemeTokens.PrimaryText, 2);
    internal static readonly Typeface LabelFace = new Typeface("Segoe UI");

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static Pen FrozenPen(Color c, double thickness) { var p = new Pen(Frozen(c), thickness); p.Freeze(); return p; }
}

internal sealed class StudioMeter : FrameworkElement
{
    double _value;
    public double Value { get => _value; set { double v = Math.Clamp(value, 0, 60); if (Math.Abs(v - _value) < .05) return; _value = v; InvalidateVisual(); } }
    // Fill for the level part; the last 4 dB before clipping always use Warn.
    public Brush Fill { get; set; } = StudioPaint.Accent;
    protected override void OnRender(DrawingContext dc)
    {
        double radius = ActualHeight / 2;
        dc.DrawRoundedRectangle(StudioPaint.Track, null, new Rect(RenderSize), radius, radius);
        double width = ActualWidth * Value / 60;
        if (width > 0.5)
            dc.DrawRoundedRectangle(Value > 56 ? StudioPaint.Warn : Fill, null, new Rect(0, 0, width, ActualHeight), radius, radius);
    }
}

internal sealed class StudioSpectrum : FrameworkElement
{
    readonly Complex[] _fft = new Complex[1024];
    readonly float[] _bars = new float[40];
    public StudioSpectrum() { Height = 36; }
    public void Update(float[] samples)
    {
        for (int i = 0; i < _fft.Length; i++) { _fft[i].X = i < samples.Length ? samples[i] * (float)FastFourierTransform.HannWindow(i, samples.Length) : 0; _fft[i].Y = 0; }
        FastFourierTransform.FFT(true, 10, _fft);
        int count = _bars.Length;
        for (int b = 0; b < count; b++)
        {
            int start = Math.Clamp((int)(30 * Math.Pow(600, b / (double)count) / 48000 * 1024), 1, 510);
            int end = Math.Clamp((int)(30 * Math.Pow(600, (b + 1) / (double)count) / 48000 * 1024), start + 1, 512);
            float magnitude = 0;
            for (int i = start; i < end; i++) magnitude = Math.Max(magnitude, MathF.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y));
            float level = Math.Clamp((20 * MathF.Log10(Math.Max(magnitude, .000001f)) + 70) / 65, 0, 1);
            _bars[b] = Math.Max(level, _bars[b] * .8f);
        }
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth / _bars.Length;
        for (int i = 0; i < _bars.Length; i++)
        {
            double h = Math.Max(2, _bars[i] * (ActualHeight - 2));
            dc.DrawRoundedRectangle(_bars[i] > .6f ? StudioPaint.Accent : StudioPaint.QuietBar, null,
                new Rect(i * width + 1, ActualHeight - h, Math.Max(1, width - 2), h), 1, 1);
        }
    }
}

internal sealed class StudioEqCurve : FrameworkElement
{
    public float[] Gains { get; set; } = new float[10];
    public event Action Changed;
    public event Action SelectionChanged;
    int _selected = -1;
    public int Selected => _selected;
    public StudioEqCurve()
    {
        Height = 240; Focusable = true; Cursor = Cursors.Hand;
        FocusVisualStyle = null;
        ToolTip = "拖动圆点调整增益；方向键微调 / Drag points; arrow keys fine-tune";
    }
    public void Select(int band)
    {
        int next = Math.Clamp(band, 0, 9);
        if (next == _selected) return;
        _selected = next; UpdateTip(); SelectionChanged?.Invoke(); InvalidateVisual();
    }
    double X(double frequency) => 28 + Math.Log(frequency / 20) / Math.Log(1000) * Math.Max(1, ActualWidth - 56);
    double Y(double gain) => 20 + (12 - gain) / 24 * (ActualHeight - 44);
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int db = -12; db <= 12; db += 6)
        {
            dc.DrawLine(db == 0 ? StudioPaint.ZeroLine : StudioPaint.Grid, new Point(24, Y(db)), new Point(ActualWidth - 20, Y(db)));
            var label = new FormattedText((db > 0 ? "+" : "") + db, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                StudioPaint.LabelFace, 9, StudioPaint.Label, pixelsPerDip);
            dc.DrawText(label, new Point(20 - label.Width, Y(db) - label.Height / 2));
        }
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var c = line.Open())
        using (var f = area.Open())
        {
            for (int p = 0; p <= 300; p++)
            {
                double hz = 20 * Math.Pow(1000, p / 300d);
                double db = 0;
                for (int b = 0; b < 10; b++) db += Response(hz, StudioDsp.Frequencies[b], Gains[b]);
                var point = new Point(X(hz), Y(Math.Clamp(db, -12, 12)));
                if (p == 0) { c.BeginFigure(point, false, false); f.BeginFigure(new Point(point.X, Y(0)), true, true); f.LineTo(point, false, false); }
                else { c.LineTo(point, true, false); f.LineTo(point, false, false); }
                if (p == 300) f.LineTo(new Point(point.X, Y(0)), false, false);
            }
        }
        line.Freeze(); area.Freeze();
        dc.DrawGeometry(StudioPaint.CurveFill, null, area);
        dc.DrawGeometry(null, StudioPaint.Curve, line);
        for (int b = 0; b < 10; b++)
        {
            bool selected = b == _selected;
            dc.DrawEllipse(selected ? StudioPaint.Accent : StudioPaint.HandleFill, selected ? StudioPaint.SelectedHandle : StudioPaint.Handle,
                new Point(X(StudioDsp.Frequencies[b]), Y(Gains[b])), selected ? 7 : 6, selected ? 7 : 6);
        }
        for (int b = 0; b < 10; b++)
        {
            float hz = StudioDsp.Frequencies[b];
            string label = hz >= 1000 ? (hz / 1000).ToString("0") + "k" : hz.ToString("0");
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                StudioPaint.LabelFace, 10, StudioPaint.Label, pixelsPerDip);
            dc.DrawText(text, new Point(X(hz) - text.Width / 2, ActualHeight - 16));
        }
    }
    static double Response(double f, double center, double gain)
    {
        double a = Math.Pow(10, gain / 40), w0 = 2 * Math.PI * center / 48000, alpha = Math.Sin(w0) / 2 / 1.1;
        double b0 = 1 + alpha * a, b1 = -2 * Math.Cos(w0), b2 = 1 - alpha * a;
        double a0 = 1 + alpha / a, a1 = b1, a2 = 1 - alpha / a, w = 2 * Math.PI * f / 48000;
        double Power(double x, double y, double z) => Math.Pow(x + y * Math.Cos(w) + z * Math.Cos(2 * w), 2) + Math.Pow(y * Math.Sin(w) + z * Math.Sin(2 * w), 2);
        return 10 * Math.Log10(Power(b0, b1, b2) / Power(a0, a1, a2));
    }
    void UpdateTip()
    {
        if (_selected >= 0) ToolTip = $"{StudioDsp.Frequencies[_selected]} Hz · {Gains[_selected]:+0.0;-0.0;0} dB";
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus(); var point = e.GetPosition(this); double best = double.MaxValue; int nearest = 0;
        for (int i = 0; i < 10; i++) { double d = Math.Abs(point.X - X(StudioDsp.Frequencies[i])); if (d < best) { best = d; nearest = i; } }
        if (nearest != _selected) { _selected = nearest; SelectionChanged?.Invoke(); }
        CaptureMouse(); Drag(point); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Drag(e.GetPosition(this)); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); }
    void Drag(Point p)
    {
        if (_selected < 0) return;
        Gains[_selected] = (float)Math.Clamp(12 - (p.Y - 20) / (ActualHeight - 44) * 24, -12, 12);
        UpdateTip();
        Changed?.Invoke(); InvalidateVisual();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int before = _selected;
        _selected = Math.Max(0, _selected);
        if (e.Key == Key.Left) _selected = Math.Max(0, _selected - 1);
        else if (e.Key == Key.Right) _selected = Math.Min(9, _selected + 1);
        else if (e.Key == Key.Up || e.Key == Key.Down) { Gains[_selected] = Math.Clamp(Gains[_selected] + (e.Key == Key.Up ? .5f : -.5f), -12, 12); Changed?.Invoke(); }
        else { _selected = before; return; }
        if (before != _selected) SelectionChanged?.Invoke();
        UpdateTip(); InvalidateVisual(); e.Handled = true;
    }
}
