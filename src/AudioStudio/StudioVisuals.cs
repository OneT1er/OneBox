using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NAudio.Dsp;

namespace PowerAudioManager.AudioStudio;

internal sealed class StudioMeter : FrameworkElement
{
    double _value;
    public double Value { get => _value; set { _value = Math.Clamp(value, 0, 60); InvalidateVisual(); } }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(45, 142, 140, 216)), null, new Rect(RenderSize), 3, 3);
        dc.DrawRoundedRectangle(Value > 56 ? Brushes.Orange : ThemeTokens.Brush(ThemeTokens.Accent), null, new Rect(0, 0, ActualWidth * Value / 60, ActualHeight), 3, 3);
    }
}

internal sealed class StudioSpectrum : FrameworkElement
{
    readonly Complex[] _fft = new Complex[1024];
    readonly float[] _bars = new float[48];
    public StudioSpectrum() { Height = 36; }
    public void Update(float[] samples)
    {
        for (int i = 0; i < _fft.Length; i++) { _fft[i].X = i < samples.Length ? samples[i] * (float)FastFourierTransform.HannWindow(i, samples.Length) : 0; _fft[i].Y = 0; }
        FastFourierTransform.FFT(true, 10, _fft);
        for (int b = 0; b < _bars.Length; b++)
        {
            int start = Math.Clamp((int)(30 * Math.Pow(600, b / 48d) / 48000 * 1024), 1, 510);
            int end = Math.Clamp((int)(30 * Math.Pow(600, (b + 1) / 48d) / 48000 * 1024), start + 1, 512);
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
            dc.DrawRoundedRectangle(ThemeTokens.Brush(ThemeTokens.Accent), null,
                new Rect(i * width + 1, ActualHeight - h, Math.Max(1, width - 3), h), 2, 2);
        }
    }
}

internal sealed class StudioEqCurve : FrameworkElement
{
    public float[] Gains { get; set; } = new float[10];
    public event Action Changed;
    int _selected = -1;
    public StudioEqCurve()
    {
        Height = 220; Focusable = true; Cursor = Cursors.Hand;
        ToolTip = "拖动圆点调整增益；方向键微调 / Drag points; arrow keys fine-tune";
    }
    double X(double frequency) => 24 + Math.Log(frequency / 20) / Math.Log(1000) * Math.Max(1, ActualWidth - 48);
    double Y(double gain) => 20 + (12 - gain) / 24 * (ActualHeight - 40);
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(70, 142, 140, 216)), 1);
        for (int db = -12; db <= 12; db += 6) dc.DrawLine(grid, new Point(20, Y(db)), new Point(ActualWidth - 20, Y(db)));
        foreach (float hz in StudioDsp.Frequencies) dc.DrawLine(grid, new Point(X(hz), 20), new Point(X(hz), ActualHeight - 20));
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            for (int p = 0; p <= 300; p++)
            {
                double hz = 20 * Math.Pow(1000, p / 300d);
                double db = 0;
                for (int b = 0; b < 10; b++) db += Response(hz, StudioDsp.Frequencies[b], Gains[b]);
                var point = new Point(X(hz), Y(Math.Clamp(db, -12, 12)));
                if (p == 0) c.BeginFigure(point, false, false); else c.LineTo(point, true, false);
            }
        }
        dc.DrawGeometry(null, new Pen(ThemeTokens.Brush(ThemeTokens.Accent), 2), geometry);
        for (int b = 0; b < 10; b++) dc.DrawEllipse(b == _selected ? Brushes.White : ThemeTokens.Brush(ThemeTokens.Accent), grid, new Point(X(StudioDsp.Frequencies[b]), Y(Gains[b])), 6, 6);
        for (int b = 0; b < 10; b++)
        {
            float hz = StudioDsp.Frequencies[b];
            string label = hz >= 1000 ? (hz / 1000).ToString("0") + "k" : hz.ToString("0");
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, ThemeTokens.Brush(ThemeTokens.Accent), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(X(hz) - text.Width / 2, ActualHeight - 15));
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
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus(); var point = e.GetPosition(this); double best = double.MaxValue;
        for (int i = 0; i < 10; i++) { double d = Math.Abs(point.X - X(StudioDsp.Frequencies[i])); if (d < best) { best = d; _selected = i; } }
        CaptureMouse(); Drag(point); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Drag(e.GetPosition(this)); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); }
    void Drag(Point p)
    {
        if (_selected < 0) return;
        Gains[_selected] = (float)Math.Clamp(12 - (p.Y - 20) / (ActualHeight - 40) * 24, -12, 12);
        ToolTip = $"{StudioDsp.Frequencies[_selected]} Hz · {Gains[_selected]:+0.0;-0.0;0} dB";
        Changed?.Invoke(); InvalidateVisual();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        _selected = Math.Max(0, _selected);
        if (e.Key == Key.Left) _selected = Math.Max(0, _selected - 1);
        else if (e.Key == Key.Right) _selected = Math.Min(9, _selected + 1);
        else if (e.Key == Key.Up || e.Key == Key.Down) { Gains[_selected] = Math.Clamp(Gains[_selected] + (e.Key == Key.Up ? .5f : -.5f), -12, 12); Changed?.Invoke(); }
        else return;
        InvalidateVisual(); e.Handled = true;
    }
}
