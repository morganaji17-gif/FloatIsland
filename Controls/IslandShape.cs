using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Pen = System.Windows.Media.Pen;
using Color = System.Windows.Media.Color;

namespace FloatIsland.Controls;

/// <summary>
/// The island silhouette: a flat-topped capsule that flares wider
/// at the very top and curves inward to the body over <see cref="FlareRadius"/>,
/// with rounded bottom corners of <see cref="BottomRadius"/>.
/// (attached, non-floating mode):
/// the top edge spans the full width, the body starts <c>flare</c> px in on each
/// side at depth <c>flare</c>.
/// </summary>
public sealed class IslandShape : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(IslandShape),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(IslandShape),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(IslandShape),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BottomRadiusProperty = DependencyProperty.Register(
        nameof(BottomRadius), typeof(double), typeof(IslandShape),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FlareRadiusProperty = DependencyProperty.Register(
        nameof(FlareRadius), typeof(double), typeof(IslandShape),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>When &gt; 0, draw a soft stroke shadow instead of a fill: a set of
    /// concentric strokes (no blur), which is far cheaper to re-rasterise than a
    /// <c>DropShadowEffect</c> on a software-composited layered window.</summary>
    public static readonly DependencyProperty ShadowLayersProperty = DependencyProperty.Register(
        nameof(ShadowLayers), typeof(int), typeof(IslandShape),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public double BottomRadius { get => (double)GetValue(BottomRadiusProperty); set => SetValue(BottomRadiusProperty, value); }
    public double FlareRadius { get => (double)GetValue(FlareRadiusProperty); set => SetValue(FlareRadiusProperty, value); }
    public int ShadowLayers { get => (int)GetValue(ShadowLayersProperty); set => SetValue(ShadowLayersProperty, value); }

    private static readonly double[] ShadowWidths = { 7, 13, 20, 28 };
    private static readonly double[] ShadowAlphas = { 0.05, 0.034, 0.022, 0.012 };

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        var geo = Build(w, h, FlareRadius, BottomRadius);

        if (ShadowLayers > 0)
        {
            for (int i = 0; i < ShadowWidths.Length; i++)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(ShadowAlphas[i] * 255), 0, 0, 0)), ShadowWidths[i])
                { LineJoin = PenLineJoin.Round };
                pen.Freeze();
                dc.DrawGeometry(null, pen, geo);
            }
            return;
        }

        double half = StrokeThickness / 2.0;

        if (Fill != null) dc.DrawGeometry(Fill, null, geo);
        if (Stroke != null && StrokeThickness > 0)
            dc.DrawGeometry(null, new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round }, geo);

        // Keep the stroked outline inside the bounds so it is never clipped.
        _ = half;
    }

    private static StreamGeometry Build(double w, double h, double flare, double rb)
    {
        double f = Math.Max(0.0, Math.Min(flare, h / 2.0));
        double r = Math.Max(0.0, Math.Min(rb, Math.Min(h - f, (w - 2 * f) / 2.0)));

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            double xl = f;            // left edge of the body
            double xr = w - f;        // right edge of the body
            double bottom = h;

            c.BeginFigure(new Point(0, 0), true, true);

            if (f > 0.01)
                c.ArcTo(new Point(xl, f), new Size(f, f), 0, false, SweepDirection.Clockwise, true, false);
            else
                c.LineTo(new Point(0, 0), true, false);

            c.LineTo(new Point(xl, bottom - r), true, false);
            if (r > 0.01)
                c.ArcTo(new Point(xl + r, bottom), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(xr - r, bottom), true, false);
            if (r > 0.01)
                c.ArcTo(new Point(xr, bottom - r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);

            if (f > 0.01)
            {
                c.LineTo(new Point(xr, f), true, false);
                c.ArcTo(new Point(w, 0), new Size(f, f), 0, false, SweepDirection.Clockwise, true, false);
            }
            else
            {
                c.LineTo(new Point(w, 0), true, false);
            }
            // isClosed closes back to (0,0) along the flat top edge.
        }
        g.Freeze();
        return g;
    }
}
