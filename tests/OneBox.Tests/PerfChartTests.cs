using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using PowerAudioManager;
using Xunit;

namespace OneBox.Tests;

public sealed class PerfChartTests
{
    [Fact]
    public void FullDayHover_ReusesCurvesWhileUpdatingTooltipAndClearingItOnLeave() => OnSta(() =>
    {
        var chart = new TestChart { WindowSec = 86400, EnableTooltip = true, ShowLegend = false, Series = new() };
        DateTime start = DateTime.Now.AddDays(-1);
        var times = Enumerable.Range(0, PerfHistory.DaySeconds).Select(i => start.AddSeconds(i)).ToList();
        for (int i = 0; i < 8; i++)
            chart.Series.Add(new ChartSeries
            {
                Name = "Sensor " + i, Color = Colors.MediumPurple, IsTemp = i < 4,
                Unit = i < 4 ? "°C" : "rpm", Times = times,
                Points = Enumerable.Repeat(i < 4 ? 50f : 4000f, PerfHistory.DaySeconds).ToList()
            });
        chart.Layout(830, 400);
        DrawingGroup first = chart.Draw(300);
        var background = Assert.IsType<DrawingVisual>(VisualTreeHelper.GetChild(chart, 0));
        Assert.IsType<BitmapCache>(background.CacheMode);
        DependencyObject hit = VisualTreeHelper.HitTest(chart, new Point(400, 200))?.VisualHit;
        Assert.NotNull(hit);
        while (hit is not UIElement) hit = VisualTreeHelper.GetParent(hit);
        Assert.Same(chart, hit); // DrawingVisual children must still route mouse input to the chart.
        var curves = Curves(first);
        Assert.Equal(8, curves.Length);
        Assert.All(curves, curve => Assert.True(curve.IsFrozen));

        // Every hover must retain the same curve geometries, regardless of history size.
        DrawingGroup moved = chart.Draw(500);
        var movedCurves = Curves(moved);
        for (int i = 0; i < curves.Length; i++)
            Assert.True(ReferenceEquals(curves[i], movedCurves[i]), "Hover rebuilt a full-day curve.");
        var markers = TooltipMarkers(moved).Where(marker => marker.Center.X == 500).ToArray();
        Assert.Equal(8, markers.Length);
        Assert.All(markers, marker => Assert.Equal(500, marker.Center.X));
        Assert.Equal(196, markers[0].Center.Y); // 50°C, plot y=10..382.
        Assert.Equal(10, markers[4].Center.Y);  // Fan axis expands to the observed 4000 rpm.

        chart.Leave();
        Assert.Empty(TooltipMarkers(chart.Draw()));
        Assert.Same(curves[0], Curves(chart.Draw())[0]);
    });

    [Fact]
    public void PixelEnvelope_KeepsPeaksTroughsGapsAndTheLastSample() => OnSta(() =>
    {
        DateTime end = DateTime.Now.AddDays(-2);
        DateTime start = end.AddDays(-1);
        var series = new ChartSeries { Name = "CPU", Color = Colors.MediumPurple, Points = new(), Times = new() };
        for (int i = 1; i <= PerfHistory.DaySeconds; i++)
        {
            if (i >= 20000 && i < 20120) continue; // A real gap must survive pixel bucketing.
            series.Times.Add(start.AddSeconds(i));
            series.Points.Add(i == 43201 ? 95 : i == 43202 ? 5 : i == PerfHistory.DaySeconds ? 70 : 50);
        }
        var chart = new TestChart
        {
            Series = new() { series }, WindowSec = 0, EndTime = end,
            ShowLegend = false, EnableTooltip = true
        };
        chart.Layout(830, 400);
        double cursor = 26 + 770 * 43201.0 / PerfHistory.DaySeconds;
        var drawing = chart.Draw(cursor);
        var curve = PathGeometry.CreateFromGeometry(Assert.Single(Curves(drawing)));
        Assert.Equal(2, curve.Figures.Count);
        Assert.Equal(26 + 770 * 19999.0 / PerfHistory.DaySeconds,
            CurvePoints(new PathGeometry(new[] { curve.Figures[0] })).Last().X, 5);
        Point[] points = CurvePoints(curve).ToArray();
        Assert.Equal(28.6, points.Min(p => p.Y), 5); // 95°C survived in the same pixel as 5°C.
        Assert.Equal(363.4, points.Max(p => p.Y), 5);
        Assert.Equal(121.6, points[^1].Y, 5);       // The line reaches the latest 70°C reading.
        Assert.Equal(796, points[^1].X, 5);
        Assert.True(points.Length <= 4 * (Math.Ceiling(770 * VisualTreeHelper.GetDpi(chart).PixelsPerDip) + 2));
        Assert.Equal(28.6, Assert.Single(TooltipMarkers(drawing), p => p.Center.X == cursor).Center.Y, 5); // Original raw reading.
        foreach (var figure in curve.Figures)
        {
            var xs = CurvePoints(new PathGeometry(new[] { figure })).Select(p => p.X).ToArray();
            Assert.True(xs.Zip(xs.Skip(1), (a, b) => a <= b).All(value => value));
        }
    });

    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(60)]
    public void FullDay_Is24HoursAtEverySamplingIntervalAndUsesTheHistoricalEnd(double interval) => OnSta(() =>
    {
        DateTime end = DateTime.Now.AddDays(-2);
        var series = new ChartSeries
        {
            Name = "Old CPU", Color = Colors.MediumPurple, Points = new() { 40, 50 },
            Times = new() { end.AddSeconds(-1), end }
        };
        var chart = new TestChart
        {
            Series = new() { series }, WindowSec = 0, IntervalSec = interval,
            EndTime = end, ShowLegend = false, EnableTooltip = true
        };
        chart.Layout(830, 400);
        Assert.Equal(86400, chart.DisplayWindowSec);
        DrawingGroup drawing = chart.Draw(796);
        var curve = PathGeometry.CreateFromGeometry(Assert.Single(Curves(drawing)));
        Assert.Single(curve.Figures);
        Assert.Equal(796, CurvePoints(curve).Last().X, 5);
        Assert.Equal(196, Assert.Single(TooltipMarkers(drawing), p => p.Center.X == 796).Center.Y, 5);
    });

    [Fact]
    public void DenseForeground_MergesVisibleMarkersWithoutFillingGapsOrChangingHover() => OnSta(() =>
    {
        DateTime end = DateTime.Now;
        DateTime start = end.AddDays(-1);
        var chart = new TestChart
        {
            WindowSec = 0, EndTime = end, ShowLegend = false, EnableTooltip = true,
            Series = new(), Segments = new()
        };
        for (int i = 0; i < 720; i++)
        {
            if (i == 360) continue;
            chart.Segments.Add(new ForegroundSegment
            {
                Start = start.AddSeconds(i * 120), End = start.AddSeconds((i + 1) * 120), Exe = "app" + i + ".exe"
            });
        }
        chart.Layout(830, 400);
        DrawingGroup drawing = chart.Draw(26 + 770 * 120.5 / 720);
        var background = ((DrawingVisual)VisualTreeHelper.GetChild(chart, 0)).Drawing;
        int boundaries = GeometryDrawings(background).Count(g => g.Geometry is LineGeometry && g.Pen?.DashStyle == DashStyles.Dot);
        Assert.InRange(boundaries, 1, 97);
        var gap = new Point(26 + 770 * 360.5 / 720, 100);
        foreach (var band in GeometryDrawings(background).Where(g => g.Brush is SolidColorBrush b && b.Color.A == 0x18))
            Assert.False(band.Geometry.FillContains(gap));
        var tooltip = ((DrawingVisual)VisualTreeHelper.GetChild(chart, 1)).Drawing;
        Assert.Contains("app120.exe", DrawingText(tooltip));
    });

    [Fact]
    public void RefreshResizeAndDpi_RebuildCurvesAndPreserveGapsAndVisibility() => OnSta(() =>
    {
        DateTime start = DateTime.Now.AddSeconds(-250);
        var series = new ChartSeries
        {
            Name = "CPU", Color = Colors.MediumPurple,
            Points = new() { 40, 50, 60, 70 },
            Times = new() { start, start.AddSeconds(1), start.AddSeconds(120), start.AddSeconds(121) }
        };
        var chart = new TestChart
        {
            WindowSec = 300, ShowLegend = false, EnableTooltip = true,
            Series = new() { series }, HiddenSeries = new()
        };
        chart.Layout(830, 400);
        var curve = Assert.Single(Curves(chart.Draw(350)));
        Assert.Equal(2, PathGeometry.CreateFromGeometry(curve).Figures.Count);
        Assert.Empty(TooltipMarkers(chart.Draw(350))); // Cursor is inside the sampling gap.

        series.Points[0] = 80;
        chart.Refresh();
        var refreshed = Assert.Single(Curves(chart.Draw()));
        Assert.NotSame(curve, refreshed);
        Assert.Equal(84.4, PathGeometry.CreateFromGeometry(refreshed).Figures[0].StartPoint.Y, 5);

        chart.WindowSec = 120;
        chart.Refresh();
        Assert.Empty(PathGeometry.CreateFromGeometry(Assert.Single(Curves(chart.Draw()))).Figures);
        chart.WindowSec = 300;
        chart.Refresh();
        refreshed = Assert.Single(Curves(chart.Draw()));
        chart.Layout(1000, 500);
        var resized = Assert.Single(Curves(chart.Draw()));
        Assert.NotSame(refreshed, resized);
        Assert.NotEqual(refreshed.Bounds, resized.Bounds);
        chart.ChangeDpi();
        var dpiChanged = Assert.Single(Curves(chart.Draw()));
        Assert.NotSame(resized, dpiChanged);
        chart.Layout(0, 0);
        Assert.Empty(Curves(chart.Draw()));
        chart.Layout(1000, 500);
        Assert.Single(Curves(chart.Draw()));

        chart.HiddenSeries.Add("CPU");
        chart.Refresh();
        Assert.Empty(Curves(chart.Draw()));
        chart.HiddenSeries.Clear();
        chart.Refresh();
        Assert.Single(Curves(chart.Draw()));
        chart.Series = new();
        chart.Refresh();
        Assert.Empty(Curves(chart.Draw()));
        chart.Layout(20, 20);
        Assert.Empty(TooltipMarkers(chart.Draw(26)));
    });

    static IEnumerable<GeometryDrawing> GeometryDrawings(Drawing drawing)
    {
        if (drawing is GeometryDrawing geometry) yield return geometry;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var item in GeometryDrawings(child)) yield return item;
    }

    static StreamGeometry[] Curves(Drawing drawing) => GeometryDrawings(drawing)
        .Select(item => item.Geometry).OfType<StreamGeometry>().ToArray();

    static IEnumerable<Point> CurvePoints(PathGeometry geometry)
    {
        foreach (var figure in geometry.Figures)
        {
            yield return figure.StartPoint;
            foreach (var segment in figure.Segments)
            {
                if (segment is LineSegment line) yield return line.Point;
                if (segment is PolyLineSegment lines) foreach (var point in lines.Points) yield return point;
            }
        }
    }

    static string DrawingText(Drawing drawing) => drawing switch
    {
        GlyphRunDrawing glyph => new string(glyph.GlyphRun.Characters.ToArray()),
        DrawingGroup group => string.Concat(group.Children.Select(DrawingText)),
        _ => ""
    };

    static EllipseGeometry[] TooltipMarkers(Drawing drawing) => GeometryDrawings(drawing)
        .Select(item => item.Geometry).OfType<EllipseGeometry>()
        .Where(ellipse => ellipse.RadiusX == 3 && ellipse.RadiusY == 3).ToArray();

    static void OnSta(Action test)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Chart rendering test did not finish.");
        Assert.Null(failure);
    }

    sealed class TestChart : PerfChart
    {
        static readonly FieldInfo MouseX = typeof(PerfChart).GetField("_mouseX", BindingFlags.NonPublic | BindingFlags.Instance);
        public void Layout(double width, double height)
        {
            Measure(new Size(width, height));
            Arrange(new Rect(0, 0, width, height));
        }
        public DrawingGroup Draw(double? mouseX = null)
        {
            if (mouseX.HasValue) MouseX.SetValue(this, mouseX.Value);
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open()) OnRender(dc);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(this); i++)
                if (VisualTreeHelper.GetChild(this, i) is DrawingVisual visual && visual.Drawing is Drawing child)
                    drawing.Children.Add(child);
            return drawing;
        }
        public void Leave() => OnMouseLeave(null);
        public void ChangeDpi() => OnDpiChanged(new DpiScale(1, 1), new DpiScale(1.5, 1.5));
    }
}
