using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using SonoIsland.Controls;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;

namespace SonoIsland;

/// <summary>
/// Island finishing touches:
/// the cover-tint "wash" the karaoke "sweep" lyric reveal (SweepLine),
/// the page dots (Dots) and the overflowing-title marquee (MarqueeText).
/// </summary>
public partial class MainWindow
{
    private static readonly Color WashBase = Color.FromRgb(0x08, 0x09, 0x0B);
    private static readonly Color DotActive = Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF);
    private static readonly Color DotIdle = Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF);

    private Color _coverTint = WashBase;

    private string _capsuleText = "";
    private bool _showingTitle;
    private const double CapsuleFixedDp = 61.0;   // cover + gap + paddings (fixed chrome width)
    private bool _sweepHooked;

    /// <summary>Per-display-frame sweep update: the 25 fps timer steps the karaoke clip
    /// visibly, so the moving highlight is refreshed on the render thread instead.</summary>
    private void OnSweepFrame(object? sender, EventArgs e)
    {
        if (_state == IslandState.Expanded && _lyricsPage) return;   // no sweep on the lyrics page
        UpdateSweep();
    }

    private void HookSweep(bool on)
    {
        if (on == _sweepHooked) return;
        _sweepHooked = on;
        if (on) CompositionTarget.Rendering += OnSweepFrame;
        else CompositionTarget.Rendering -= OnSweepFrame;
    }

    /// <summary>Collapsed-surface width: the measured text plus a fixed chrome
    /// width, clamped to 124..440 dp.</summary>
    private double CollapsedWidth()
    {
        var text = string.IsNullOrEmpty(_capsuleText) ? " " : _capsuleText;
        var family = (FontFamily)Application.Current.Resources["Jakarta"];
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            typeface, 13.0, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double textW = Math.Min(ft.WidthIncludingTrailingWhitespace, 379.0);
        return Math.Clamp(textW + CapsuleFixedDp, 124.0, 440.0);
    }

    /// <summary>The collapsed pill's one line: the current lyric, else "title ---- artist".</summary>
    private void SetCapsuleLyric(string text, bool isTitle = false)
    {
        _showingTitle = isTitle;
        if (_capsuleText == text) return;
        _capsuleText = text;
        CapsuleLyricBase.Text = text;
        CapsuleLyricTop.Text = text;
        CapsuleSweep.Rect = new Rect(0, 0, 0, 24);

        if (_state != IslandState.Expanded)
        {
            double w = CollapsedWidth();
            AnimateNotch(w, 36, 18, 16, 220);
            PositionWindow(w);
        }
    }

    private string TitleLine()
    {
        var t = TitleC.Text; var a = ArtistC.Text;
        return string.IsNullOrWhiteSpace(a) ? t : t + " \u2014 " + a;
    }

    /// <summary>Sets the MUSIC header lyric line: the dim base and the clipped bright copy.</summary>
    private void SetMusicLyric(string text)
    {
        ExpLyricCurrent.Text = text;
        ExpLyricTop.Text = text;
        ExpLyricSweep.Rect = new Rect(0, 0, 0, 24);
    }

    // ------------------------------------------------------------------ wash tint
    /// <summary>Representative colour of the cover: the mean of its saturated pixels,
    /// falling back to the plain mean for near-grey art.</summary>
    private static Color TintOf(BitmapSource? src)
    {
        if (src == null) return WashBase;
        var fmt = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = fmt.PixelWidth, h = fmt.PixelHeight;
        if (w <= 0 || h <= 0) return WashBase;

        var px = new byte[w * h * 4];
        fmt.CopyPixels(px, w * 4, 0);

        long r = 0, g = 0, b = 0; long n = 0;
        long sr = 0, sg = 0, sb = 0; long sn = 0;
        for (int y = 0; y < h; y += 2)
            for (int x = 0; x < w; x += 2)
            {
                int i = (y * w + x) * 4;
                byte B = px[i], G = px[i + 1], R = px[i + 2];
                r += R; g += G; b += B; n++;
                int mx = Math.Max(R, Math.Max(G, B)), mn = Math.Min(R, Math.Min(G, B));
                if (mx > 0 && mx - mn > 40) { sr += R; sg += G; sb += B; sn++; }
            }
        if (n == 0) return WashBase;
        return sn > 0
            ? Color.FromRgb((byte)(sr / sn), (byte)(sg / sn), (byte)(sb / sn))
            : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    private static Color Mix(Color a, Color b, double k) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * k),
        (byte)Math.Round(a.G + (b.G - a.G) * k),
        (byte)Math.Round(a.B + (b.B - a.B) * k));

    private static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double Lum(Color c) =>
        0.2126 * Lin(c.R / 255.0) + 0.7152 * Lin(c.G / 255.0) + 0.0722 * Lin(c.B / 255.0);

    private static double Contrast(Color a, Color b)
    {
        double la = Lum(a), lb = Lum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>The wash mixes the base with the tint, walking the mix
    /// ratio down until white (and the dimmed right end) stays readable.</summary>
    private Brush WashBrush()
    {
        double k = 0.62;
        var left = Mix(WashBase, _coverTint, k);
        while (k > 0.02 && Contrast(Colors.White, left) < 4.6) { k -= 0.02; left = Mix(WashBase, _coverTint, k); }

        double kr = Math.Min(k * 0.55, 0.34);
        var right = Mix(WashBase, _coverTint, kr);
        while (kr > 0.0 && Contrast(Mix(right, Colors.White, 0.62), right) < 4.6)
        {
            kr = Math.Max(kr - 0.02, 0.0);
            right = Mix(WashBase, _coverTint, kr);
        }

        var mid = Mix(left, right, 0.5);
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        b.GradientStops.Add(new GradientStop(left, 0));
        b.GradientStops.Add(new GradientStop(mid, 0.5));
        b.GradientStops.Add(new GradientStop(right, 1));
        b.Freeze();
        return b;
    }

    private void UpdateWash()
    {
        if (_lyricsPage) { WashShape.Fill = WashBrush(); }
        double to = _lyricsPage ? 1.0 : 0.0;
        WashShape.BeginAnimation(OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(320))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ---------------------------------------------------------------- page dots
    private void UpdateDots()
    {
        Dot0.Width = _lyricsPage ? 4 : 14;
        Dot1.Width = _lyricsPage ? 14 : 4;
        Dot0.Background = Solid(_lyricsPage ? DotIdle : DotActive);
        Dot1.Background = Solid(_lyricsPage ? DotActive : DotIdle);
    }

    private static Brush Solid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ------------------------------------------------------------- sweep (karaoke)
    /// <summary>Reveals the bright lyric copy up to the current line's progress,
    /// as a karaoke reveal (clip right edge = line width * progress).</summary>
    private void UpdateSweep()
    {
        double p = 0;
        if (_synced is { Count: > 0 } && _lyricIndex >= 0 && _lyricIndex < _synced.Count)
        {
            var start = _synced[_lyricIndex].Time;
            var end = _lyricIndex + 1 < _synced.Count ? _synced[_lyricIndex + 1].Time : _track?.Duration ?? start;
            double span = (end - start).TotalMilliseconds;
            p = span <= 1 ? 1.0 : Math.Clamp((CurrentPosition - start).TotalMilliseconds / span, 0.0, 1.0);
        }
        // No active lyric line: the pill shows "title ---- artist", which is not a karaoke
        // line, so reveal it fully instead of leaving it at 0 (which showed only the dim base).
        if (_showingTitle) p = 1.0;

        SetSweep(ExpLyricSweep, p * ExpLyricTop.ActualWidth + 4.0);
        SetSweep(CapsuleSweep, p * CapsuleLyricTop.ActualWidth + 4.0);
    }

    // Only touch the clip geometry when it actually moved: any change invalidates the
    // whole software-composited window for a frame.
    private static void SetSweep(System.Windows.Media.RectangleGeometry geo, double w)
    {
        if (Math.Abs(geo.Rect.Width - w) < 0.4) return;
        geo.Rect = new Rect(0, 0, w, 24);
    }

    // ------------------------------------------------------------------- marquee
    private void UpdateMarquee()
    {
        double avail = TitleClip.ActualWidth;
        double text = TitleC.ActualWidth;
        TitleShift.BeginAnimation(TranslateTransform.XProperty, null);

        if (avail <= 1 || text <= avail + 1) { TitleShift.X = 0; return; }

        double over = text - avail;
        var anim = new DoubleAnimation(0, -over, TimeSpan.FromSeconds(Math.Max(2.0, over / 30.0)))
        {
            BeginTime = TimeSpan.FromSeconds(1.2),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        TitleShift.BeginAnimation(TranslateTransform.XProperty, anim);
    }
}
