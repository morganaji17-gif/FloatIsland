using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SonoIsland.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace SonoIsland.Controls;

/// <summary>
/// Thin audio-reactive bar display driven by <see cref="AudioLevels"/>.
/// The bar count adapts to the available width so the same control works as a
/// 36 px artwork accent and as a wider strip. Rendering is fully gated: the host
/// only calls <see cref="SetLevels"/> while Spotify is playing, so nothing is
/// drawn (or computed) when idle.
/// </summary>
public sealed class Visualizer : FrameworkElement
{
    private static readonly Brush BarBrush = Freeze(Colors.White);
    private static readonly Brush IdleBrush = Freeze(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));

    private readonly float[] _bars = new float[AudioLevels.BarCount];
    private bool _active;
    private float _bass, _beat;

    /// <summary>Signed magnitude of the last frame, used to skip redundant redraws.</summary>
    public float LastEnergy { get; private set; }

    public void SetLevels(AudioLevels lv)
    {
        float energy = lv.Bass + lv.Mid + lv.Treble + lv.Rms;
        Array.Copy(lv.Bars, _bars, _bars.Length);
        _active = lv.Active;
        _bass = lv.Bass;
        _beat = lv.Beat;
        LastEnergy = energy;
        InvalidateVisual();
    }

    public void SetIdle()
    {
        if (!_active && _bass == 0f && _beat == 0f) return;
        Array.Clear(_bars, 0, _bars.Length);
        _active = false; _bass = 0f; _beat = 0f;
        LastEnergy = 0f;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 3 || h <= 3) return;

        // 2 px bars, 2 px gaps, at least 4 of them.
        int n = Math.Clamp((int)((w + 2.0) / 4.0), 4, AudioLevels.BarCount);
        const double bw = 2.0;
        double gap = Math.Max(1.5, (w - n * bw) / (n - 1));
        double total = n * bw + (n - 1) * gap;
        double x0 = (w - total) / 2.0;
        double maxH = h - 1.0;
        double boost = 1.0 + _beat * 0.16 + _bass * 0.06;

        for (int i = 0; i < n; i++)
        {
            // Sample the 16-band spectrum down to the available bar count.
            float v = _bars[(int)((long)i * AudioLevels.BarCount / n)];
            if (!_active) v = 0.08f;
            else v = Math.Min(1f, v * (float)boost);

            double bh = Math.Max(2.0, v * maxH);
            if (bh > maxH) bh = maxH;

            dc.DrawRoundedRectangle(_active ? BarBrush : IdleBrush, null,
                new Rect(x0 + i * (bw + gap), h - bh, bw, bh), 1, 1);
        }
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>Animatable bottom-only corner radius for the notch capsule.</summary>
public static class CornerHelper
{
    public static readonly DependencyProperty BottomRadiusProperty = DependencyProperty.RegisterAttached(
        "BottomRadius", typeof(double), typeof(CornerHelper), new PropertyMetadata(0.0, OnChanged));

    /// <summary>Top flare: how far the top edge overhangs
    /// the body on each side before curving in. Read by <see cref="IslandShape"/>.</summary>
    public static readonly DependencyProperty FlareProperty = DependencyProperty.RegisterAttached(
        "Flare", typeof(double), typeof(CornerHelper), new PropertyMetadata(0.0));

    public static void SetBottomRadius(DependencyObject d, double v) => d.SetValue(BottomRadiusProperty, v);
    public static double GetBottomRadius(DependencyObject d) => (double)d.GetValue(BottomRadiusProperty);
    public static void SetFlare(DependencyObject d, double v) => d.SetValue(FlareProperty, v);
    public static double GetFlare(DependencyObject d) => (double)d.GetValue(FlareProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Border b && e.NewValue is double r)
            b.CornerRadius = new CornerRadius(0, 0, r, r);
    }
}
