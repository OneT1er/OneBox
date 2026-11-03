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
        var times = Enumerable.Range(0, PerfHistory.Capacity).Select(i => start.AddSeconds(i)).ToList();
        for (int i = 0; i < 8; i++)
            chart.Series.Add(new ChartSeries
            {
                Name = "Sensor " + i, Color = Colors.MediumPurple, IsTemp = i < 4,
                Unit = i < 4 ? "°C" : "rpm", Times = times,
                Points = Enumerable.Repeat(i < 4 ? 50f : 4000f, PerfHistory.Capacity).ToList()
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
