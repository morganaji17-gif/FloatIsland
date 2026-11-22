using System.IO;
using SkiaSharp;

namespace FloatIsland.Overlay;

internal enum Surface { Collapsed, Music, Lyrics, Menu, Volume }
internal enum IconKind { Prev, Next, Lyrics }

internal sealed class IslandFrame
{
    public Surface Surface = Surface.Collapsed;
    public string Line = "";
    public string Title = "";
    public string Artist = "";
    public string LyricLine = "";
    public SKImage? Cover;
    public SKImage? Logo;                 // brand mark, used when there is no artwork
    public bool Playing;
    public bool HasTrack;
    public double Progress;
    public string Elapsed = "--:--", Duration = "--:--";
    public float CatPhase;                // seconds, drives the mascot animation
    public List<string> Lines = new();
    public int LyricIndex = -1;
    public float LyricScroll;
    public bool HasLyrics;

    // Finishing touches
    public float Karaoke;                 // 0..1 reveal of the current line
    public float MarqueeOffset;           // px, <= 0 when the title overflows
    public float WashAlpha;               // 0..1 album-tint wash opacity
    public SKColor WashLeft, WashMid, WashRight;
    public SKColor Tint;                  // album colour
    public float TintAmount;              // 0..1 how strongly the body follows the cover
    public float Volume;                  // 0..1 system volume
    public bool VolumeMuted;
    public int DotsActive;                // 0 = MUSIC, 1 = LYRICS
    /// <summary>0----1 reveal used to stagger the lyric page in (rows, divider).</summary>
    public float LyricsReveal = 1f;
    /// <summary>0----1 fade used to light up the newly active lyric line.</summary>
    public float LineFade = 1f;
    public int Hover = -1;                // 0 prev, 1 play, 2 next, 3 lyrics, 4 collapse
    public int MenuHover = -1;
    public string[] MenuLabels = { "Settings", "Hide island", "Exit" };
}

internal struct IslandMorph
{
    public float BodyW, BodyH, Radius, Flare, Alpha;
    public bool Ready => BodyW > 1 && BodyH > 1;
}

internal sealed class IslandLayout
{
    public SKRect Collapse, Prev, Play, Next, LyricsToggle, SeekTrack;
    public SKRect[] MenuRows = Array.Empty<SKRect>();
}

/// <summary>Skia renderer for the island surfaces (collapsed LINE, MUSIC, LYRICS, MENU, VOLUME).</summary>
internal sealed class IslandRenderer : IDisposable
{
    private readonly SKTypeface _bold, _semi, _med;

    private static readonly SKColor GradTop = new(0x0F, 0x10, 0x13);
    private static readonly SKColor GradBottom = new(0x04, 0x04, 0x05);
    private static readonly SKColor White = SKColors.White;
    private static readonly SKColor Dim = new(255, 255, 255, 158);
    private static readonly SKColor Faint = new(255, 255, 255, 128);
    private static readonly SKColor Edge = new(255, 255, 255, 26);

    public IslandRenderer(string fontDir)
    {
        _bold = Load(fontDir, "PlusJakartaSans-Bold.ttf");
        _semi = Load(fontDir, "PlusJakartaSans-SemiBold.ttf");
        _med = Load(fontDir, "PlusJakartaSans-Medium.ttf");
    }

    private static SKTypeface Load(string dir, string file)
    {
        var p = Path.Combine(dir, file);
        return File.Exists(p) ? SKTypeface.FromFile(p) : SKTypeface.Default;
    }

    public float CollapsedWidthPx(string line, float s)
    {
        using var p = P(_semi, 13 * s, White);
        return Math.Clamp(p.MeasureText(line ?? "") + 61 * s, 124 * s, 440 * s);
    }

    /// <summary>Fixed collapsed width when the mascot is shown (cover + gap + cat + padding).</summary>
    public float Measure(SKTypefaceKind kind, string text, float sizePx)
    {
        using var p = P(kind == SKTypefaceKind.Bold ? _bold : kind == SKTypefaceKind.Medium ? _med : _semi, sizePx, White);
        return p.MeasureText(text ?? "");
    }

    internal enum SKTypefaceKind { Semi, Medium, Bold }

    public void Render(SKCanvas c, int w, int h, float s, IslandFrame f, IslandLayout hit, IslandMorph m)
    {
        c.Clear(SKColors.Transparent);
        if (!m.Ready || m.Alpha <= 0.01f) return;

        // One layer for the whole frame so show/hide can fade + shrink as a unit.
        bool fade = m.Alpha < 0.999f;
        if (fade)
            c.SaveLayer(new SKPaint { Color = new SKColor(255, 255, 255, (byte)(Math.Clamp(m.Alpha, 0f, 1f) * 255)) });

        float cx = w / 2f;
        float xl = cx - m.BodyW / 2f, xr = cx + m.BodyW / 2f;

        using var path = BuildPath(xl, xr, m.Flare, m.Radius, m.BodyH, 0);
        Body(c, path, m.BodyW, m.BodyH, s, xl, 0, f.Tint, f.TintAmount);

        if (f.Surface == Surface.Lyrics) DrawWash(c, path, m.BodyW, m.BodyH, s, xl, f);

        c.Save();
        c.ClipPath(path, SKClipOperation.Intersect, true);
        switch (f.Surface)
        {
            case Surface.Music: DrawExpanded(c, w, s, f, hit, false); break;
            case Surface.Lyrics: DrawExpanded(c, w, s, f, hit, true); break;
            case Surface.Menu: DrawMenu(c, w, s, f, hit); break;
            case Surface.Volume: DrawVolume(c, w, s, f, hit); break;
            default: DrawCollapsed(c, w, s, f, hit); break;
        }
        c.Restore();

        if (fade) c.Restore();
    }

    // ---- collapsed (LINE)
    private void DrawCollapsed(SKCanvas c, int w, float s, IslandFrame f, IslandLayout hit)
    {
        using var tp = P(_semi, 13 * s, White);
        float bodyW = Math.Clamp(tp.MeasureText(f.Line) + 61 * s, 124 * s, 440 * s);
        float cx = w / 2f, xl = cx - bodyW / 2f, xr = cx + bodyW / 2f, ph = 36 * s;

        CoverCircle(c, f, xl + 18 * s, ph / 2f, 12 * s, s);

        float tx = xl + 40 * s, maxX = xr - 18 * s;
        c.Save(); c.ClipRect(new SKRect(tx, 0, Math.Max(tx, maxX), ph));
        float baseY = Baseline(tp, ph / 2f);
        KaraokeText(c, f.Line, tx, baseY, f.Karaoke, _semi, 13 * s, maxX - tx);
        c.Restore();

        float fx = 16 * s;
        hit.Collapse = new SKRect(xl - fx, 0, xr + fx, ph);
    }

    // ---- expanded: MUSIC (432x176) / LYRICS (576x176)
    private void DrawExpanded(SKCanvas c, int w, float s, IslandFrame f, IslandLayout hit, bool lyricsPage)
    {
        float bodyW = (lyricsPage ? 576 : 432) * s, H = 176 * s;
        float cx = w / 2f, xl = cx - bodyW / 2f, xr = cx + bodyW / 2f;
        float top = 0, bottom = top + H;

        hit.Collapse = new SKRect(xl, top, xr, bottom);

        float pad = 16 * s;
        float left = xl + pad, right = xr - pad, ctop = top + pad;
        float colW = lyricsPage ? (right - left - 1 * s) / 2f : right - left;

        CoverRounded(c, f, left, ctop, 64 * s, 14 * s, s);

        float tx = left + (64 + 14) * s, tw = colW - (64 + 14 + 12) * s;
        using (var title = P(_semi, 15 * s, White))
        using (var artist = P(_med, 12.5f * s, Dim))
        {
            c.Save(); c.ClipRect(new SKRect(tx, ctop, tx + tw, ctop + 64 * s));
            if (f.MarqueeOffset < -0.5f)
                title.MeasureText(f.Title);   // warm
            c.DrawText(f.Title, tx + f.MarqueeOffset, ctop + 22 * s, title);
            c.DrawText(Clip(f.Artist, artist, tw), tx, ctop + 42 * s, artist);
            if (!lyricsPage && f.HasLyrics && !string.IsNullOrEmpty(f.LyricLine))
                KaraokeText(c, f.LyricLine, tx, ctop + 60 * s, f.Karaoke, _semi, 12 * s, tw);
            c.Restore();
        }

        if (!lyricsPage)
            DrawCat(c, right - 30 * s, ctop + 24 * s, 26 * s, f.CatPhase);

        float py = ctop + 64 * s + 8 * s;
        float trackL = left + 44 * s, trackR = left + colW - 44 * s;
        using (var sm = P(_med, 11.5f * s, Faint))
        {
            c.DrawText(f.Elapsed, left, py + 10 * s, sm);
            c.DrawText(f.Duration, trackR + 6 * s, py + 10 * s, sm);
        }
        Progress(c, trackL, py + 8 * s, trackR - trackL, s, (float)f.Progress);
        hit.SeekTrack = new SKRect(trackL, py, trackR, py + 14 * s);

        float cyy = py + 14 * s + 8 * s + 20 * s, cxm = left + colW / 2f;
        Hover(c, f.Hover == 1, cxm, cyy, 20 * s, s);
        Button(c, cxm, cyy, 40 * s, f.Playing, s);
        hit.Play = new SKRect(cxm - 20 * s, cyy - 20 * s, cxm + 20 * s, cyy + 20 * s);
        Hover(c, f.Hover == 0, cxm - 40 * s, cyy, 14 * s, s);
        Icon(c, IconKind.Prev, cxm - 40 * s, cyy, 16 * s, White, s);
        hit.Prev = new SKRect(cxm - 54 * s, cyy - 14 * s, cxm - 26 * s, cyy + 14 * s);
        Hover(c, f.Hover == 2, cxm + 40 * s, cyy, 14 * s, s);
        Icon(c, IconKind.Next, cxm + 40 * s, cyy, 16 * s, White, s);
        hit.Next = new SKRect(cxm + 26 * s, cyy - 14 * s, cxm + 54 * s, cyy + 14 * s);
        float togx = left + colW - 14 * s;
        Hover(c, f.Hover == 3, togx, cyy, 14 * s, s);
        Icon(c, IconKind.Lyrics, togx, cyy, 16 * s, lyricsPage ? White : Dim, s);
        hit.LyricsToggle = new SKRect(togx - 14 * s, cyy - 14 * s, togx + 14 * s, cyy + 14 * s);

        if (!lyricsPage)
        {
            Dots(c, cx, bottom - pad - 4 * s, f.DotsActive, s);
            return;
        }

        float dx = left + colW + 0.5f * s;
        float dy0 = ctop + 30 * s, dy1 = bottom - pad;
        float drawn = dy0 + (dy1 - dy0) * Math.Clamp(f.LyricsReveal, 0f, 1f);
        using (var dv = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x17), StrokeWidth = 1 * s })
            c.DrawLine(dx, dy0, dx, drawn, dv);

        float lx = dx + 22 * s, lw = right - 18 * s - lx;
        c.Save();
        c.ClipRect(new SKRect(lx, ctop, lx + lw, bottom - pad));
        using var lyStyle = P(_bold, 17 * s, White);
        float rowH = 30 * s, midY = ctop + (H - 2 * pad) / 2f;
        float slide = (1f - Math.Clamp(f.LyricsReveal, 0f, 1f)) * 14 * s;   // rows slide in from the right
        for (int i = 0; i < f.Lines.Count; i++)
        {
            int d = i - f.LyricIndex;
            if (d < -4 || d > 4) continue;

            // stagger: nearer rows appear first as the page reveals
            float delay = Math.Abs(d) * 0.14f;
            float reveal = Math.Clamp((f.LyricsReveal - delay) / Math.Max(0.001f, 1f - delay), 0f, 1f);
            if (reveal <= 0.01f) continue;

            float y = midY + d * rowH - f.LyricScroll + 7 * s;
            byte baseA = d == 0 ? (byte)255 : Math.Abs(d) == 1 ? (byte)158 : Math.Abs(d) == 2 ? (byte)110 : (byte)70;
            // the newly active line brightens from the dim value over a short fade
            if (d == 0) baseA = (byte)(158 + (255 - 158) * Math.Clamp(f.LineFade, 0f, 1f));
            byte a = (byte)(baseA * reveal);
            lyStyle.Color = new SKColor(255, 255, 255, a);
            c.DrawText(Clip(f.Lines[i], lyStyle, lw), lx + slide * (1 - reveal), y, lyStyle);
        }
        c.Restore();
    }

    // ---- MENU (316x112)
    private void DrawMenu(SKCanvas c, int w, float s, IslandFrame f, IslandLayout hit)
    {
        float bodyW = 316 * s, H = 176 * s;
        float cx = w / 2f, xl = cx - bodyW / 2f, xr = cx + bodyW / 2f;
        hit.Collapse = new SKRect(xl, 0, xr, H);

        var labels = f.MenuLabels.Length > 0 ? f.MenuLabels : new[] { "Exit" };
        float pad = 8 * s;
        float header = 22 * s;

        // brand header: logo + name
        if (f.Logo != null)
            c.DrawImage(f.Logo, new SKRect(xl + pad, pad, xl + pad + header, pad + header));
        using (var brand = P(_bold, 12.5f * s, White))
        {
            var m0 = brand.FontMetrics;
            float by = pad + header / 2f - (m0.Ascent + m0.Descent) / 2f;
            c.DrawText("Float Island", xl + pad + header + 8 * s, by, brand);
        }

        float rowsTop = pad + header + 4 * s;
        float rowH = (H - rowsTop - pad) / labels.Length;
        hit.MenuRows = new SKRect[labels.Length];

        using var label = P(_semi, 13.5f * s, White);
        for (int i = 0; i < labels.Length; i++)
        {
            float y = rowsTop + i * rowH;
            var row = new SKRect(xl + pad, y, xr - pad, y + rowH);
            hit.MenuRows[i] = row;

            if (f.MenuHover == i)
            {
                using var hl = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x1A) };
                c.DrawRoundRect(new SKRoundRect(row, 6 * s), hl);
            }

            var m = label.FontMetrics;
            float baseY = y + rowH / 2f - (m.Ascent + m.Descent) / 2f;
            c.DrawText(labels[i], row.Left + 12 * s, baseY, label);
        }
    }

    // ---- VOLUME (236x44)
    private void DrawVolume(SKCanvas c, int w, float s, IslandFrame f, IslandLayout hit)
    {
        float bodyW = 236 * s, H = 44 * s;
        float cx = w / 2f, xl = cx - bodyW / 2f, xr = cx + bodyW / 2f;
        hit.Collapse = new SKRect(xl, 0, xr, H);
        float cy = H / 2f;

        float ix = xl + 24 * s;
        using var ink = new SKPaint { IsAntialias = true, Color = White };
        using (var p = new SKPath())
        {
            p.MoveTo(ix - 8 * s, cy - 4 * s); p.LineTo(ix - 3 * s, cy - 4 * s); p.LineTo(ix + 3 * s, cy - 9 * s);
            p.LineTo(ix + 3 * s, cy + 9 * s); p.LineTo(ix - 3 * s, cy + 4 * s); p.LineTo(ix - 8 * s, cy + 4 * s);
            p.Close();
            c.DrawPath(p, ink);
        }
        using (var arc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f * s, StrokeCap = SKStrokeCap.Round, Color = White })
        {
            if (!f.VolumeMuted)
            {
                c.DrawArc(new SKRect(ix + 1 * s, cy - 6 * s, ix + 15 * s, cy + 6 * s), -50, 100, false, arc);
                c.DrawArc(new SKRect(ix + 1 * s, cy - 10 * s, ix + 22 * s, cy + 10 * s), -50, 100, false, arc);
            }
            else
            {
                c.DrawLine(ix + 6 * s, cy - 5 * s, ix + 16 * s, cy + 5 * s, arc);
                c.DrawLine(ix + 16 * s, cy - 5 * s, ix + 6 * s, cy + 5 * s, arc);
            }
        }

        float bx0 = ix + 26 * s, bx1 = xr - 64 * s;
        Progress(c, bx0, cy, bx1 - bx0, s, Math.Clamp(f.Volume, 0, 1));

        using var pct = P(_semi, 12 * s, White);
        string label = f.VolumeMuted ? "Mute" : $"{Math.Round(f.Volume * 100)}%";
        c.DrawText(label, bx1 + 12 * s, Baseline(pct, cy), pct);
    }

    // ---- primitives
    private static SKPaint P(SKTypeface tf, float size, SKColor color) =>
        new() { Typeface = tf, TextSize = size, IsAntialias = true, Color = color };

    private static SKColor Mix(SKColor a, SKColor b, float k) => new(
        (byte)Math.Round(a.Red + (b.Red - a.Red) * k),
        (byte)Math.Round(a.Green + (b.Green - a.Green) * k),
        (byte)Math.Round(a.Blue + (b.Blue - a.Blue) * k),
        255);

    private static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    private static double Lum(SKColor c) => 0.2126 * Lin(c.Red / 255.0) + 0.7152 * Lin(c.Green / 255.0) + 0.0722 * Lin(c.Blue / 255.0);
    private static double Contrast(SKColor a, SKColor b)
    {
        double la = Lum(a), lb = Lum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static float Baseline(SKPaint paint, float cy)
    {
        var m = paint.FontMetrics;
        return cy - (m.Ascent + m.Descent) / 2f;
    }

    private static void KaraokeText(SKCanvas c, string text, float x, float baseY, float karaoke, SKTypeface tf, float size, float maxW)
    {
        if (string.IsNullOrEmpty(text)) return;
        using var probe = P(tf, size, White);
        string t = Clip(text, probe, maxW);
        float w = probe.MeasureText(t);

        using (var dim = P(tf, size, Faint)) c.DrawText(t, x, baseY, dim);

        float reveal = Math.Clamp(karaoke, 0f, 1f) * w;
        if (reveal <= 0.5f) return;
        c.Save();
        c.ClipRect(new SKRect(x, baseY - size * 1.4f, x + reveal + 3f, baseY + size * 0.5f));
        using var bright = P(tf, size, White);
        c.DrawText(t, x, baseY, bright);
        c.Restore();
    }

    private static string Clip(string text, SKPaint paint, float maxW)
    {
        if (string.IsNullOrEmpty(text) || maxW <= 0 || paint.MeasureText(text) <= maxW) return text;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (paint.MeasureText(text[..mid] + "\u2026") <= maxW) lo = mid; else hi = mid - 1;
        }
        return text[..lo] + "\u2026";
    }

    private void DrawWash(SKCanvas c, SKPath path, float w, float h, float s, float x0, IslandFrame f)
    {
        if (f.WashAlpha <= 0.01f) return;
        byte a = (byte)(Math.Clamp(f.WashAlpha, 0, 1) * 255);
        c.Save();
        c.ClipPath(path, SKClipOperation.Intersect, true);
        using (var p = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(x0, 0), new SKPoint(x0 + w, 0),
                new[]
                {
                    new SKColor(f.WashLeft.Red, f.WashLeft.Green, f.WashLeft.Blue, a),
                    new SKColor(f.WashMid.Red, f.WashMid.Green, f.WashMid.Blue, a),
                    new SKColor(f.WashRight.Red, f.WashRight.Green, f.WashRight.Blue, a),
                },
                new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp)
        })
            c.DrawRect(new SKRect(x0, 0, x0 + w, h), p);
        c.Restore();
    }

    private void Body(SKCanvas c, SKPath path, float w, float h, float s, float x0, float top, SKColor tint, float amt)
    {
        Shadow(c, path, s);

        // The body gradient follows the album colour: mix both stops toward the cover tint,
        // walking the mix down until white text keeps a 4.5 contrast ratio (else a bright
        // cover would wash the text out).
        float a = Math.Clamp(amt, 0f, 1f);
        var cTop = Mix(GradTop, tint, a);
        while (a > 0.05f && Contrast(SKColors.White, cTop) < 4.5) { a -= 0.05f; cTop = Mix(GradTop, tint, a); }
        var cBottom = Mix(GradBottom, tint, a * 0.75f);

        using (var fill = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, top + h),
                new[] { cTop, cBottom }, new[] { 0f, 1f }, SKShaderTileMode.Clamp)
        })
            c.DrawPath(path, fill);

        c.Save(); c.ClipPath(path, SKClipOperation.Intersect, true);
        using (var sheen = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, top + h * 0.3f),
                new[] { new SKColor(255, 255, 255, 15), SKColors.Transparent }, new[] { 0f, 1f }, SKShaderTileMode.Clamp)
        })
            c.DrawRect(new SKRect(x0, top, x0 + w, top + h * 0.3f), sheen);
        c.Restore();

        using (var edge = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2 * s,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, top + h * 0.5f), new SKPoint(0, top + h),
                new[] { SKColors.Transparent, Edge }, new[] { 0f, 1f }, SKShaderTileMode.Clamp)
        })
            c.DrawPath(path, edge);
    }

    private static void Shadow(SKCanvas c, SKPath path, float s)
    {
        float[] wd = { 7, 13, 20, 28 };
        float[] al = { 0.05f, 0.034f, 0.022f, 0.012f };
        for (int i = 0; i < wd.Length; i++)
            using (var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = wd[i] * s, Color = new SKColor(0, 0, 0, (byte)(al[i] * 255)) })
                c.DrawPath(path, p);
    }

    internal static SKPath BuildPath(float xl, float xr, float flare, float rb, float h, float top = 0)
    {
        const float K = 0.5522847f;
        var path = new SKPath();
        float f = Math.Max(0f, Math.Min(flare, h / 2f));
        float b = top + h;

        path.MoveTo(xl - f, top);
        if (f > 0.01f)
            path.CubicTo(xl - f + K * f, top, xl, top + f - K * f, xl, top + f);

        if (rb > 0.01f)
        {
            path.LineTo(xl, b - rb);
            path.CubicTo(xl, b - rb + K * rb, xl + rb - K * rb, b, xl + rb, b);
            path.LineTo(xr - rb, b);
            path.CubicTo(xr - rb + K * rb, b, xr, b - rb + K * rb, xr, b - rb);
        }
        else { path.LineTo(xl, b); path.LineTo(xr, b); }

        if (f > 0.01f)
        {
            path.LineTo(xr, top + f);
            path.CubicTo(xr, top + f - K * f, xr + f - K * f, top, xr + f, top);
        }
        else path.LineTo(xr, top);

        path.Close();
        return path;
    }

    private void CoverCircle(SKCanvas c, IslandFrame f, float cx, float cy, float r, float s)
    {
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = s, Color = new SKColor(255, 255, 255, 0x26) };
        if (f.Cover != null)
        {
            c.Save();
            using var clip = new SKPath(); clip.AddCircle(cx, cy, r);
            c.ClipPath(clip, SKClipOperation.Intersect, true);
            c.DrawImage(f.Cover, new SKRect(cx - r, cy - r, cx + r, cy + r));
            c.Restore();
        }
        else
        {
            using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x12) })
                c.DrawCircle(cx, cy, r, bg);
            DrawLogoInside(c, f.Logo, cx - r, cy - r, r * 2, 0.72f);
        }
        c.DrawCircle(cx, cy, r, stroke);
    }

    /// <summary>Draws the brand mark centred in a box (used as the no-artwork placeholder).</summary>
    private static void DrawLogoInside(SKCanvas c, SKImage? logo, float x, float y, float size, float fill)
    {
        if (logo == null) return;
        float d = size * fill;
        float cx = x + size / 2f, cy = y + size / 2f;
        c.DrawImage(logo, new SKRect(cx - d / 2f, cy - d / 2f, cx + d / 2f, cy + d / 2f));
    }

    private void CoverRounded(SKCanvas c, IslandFrame f, float x, float y, float size, float r, float s)
    {
        var rect = new SKRect(x, y, x + size, y + size);
        using var rr = new SKRoundRect(rect, r);
        if (f.Cover != null)
        {
            c.Save(); c.ClipRoundRect(rr, SKClipOperation.Intersect, true);
            c.DrawImage(f.Cover, rect); c.Restore();
        }
        else using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x12) }) c.DrawRoundRect(rr, bg);
        if (f.Cover == null) DrawLogoInside(c, f.Logo, x, y, size, 0.62f);
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = s, Color = new SKColor(255, 255, 255, 0x22) };
        c.DrawRoundRect(rr, stroke);
    }

    /// <summary>A cute animated mascot: bobbing head, wagging tail, blinking eyes, whiskers.</summary>
    private static void DrawCat(SKCanvas c, float cx, float cy, float size, float phase)
    {
        float bob = (float)Math.Sin(phase * 2.2) * size * 0.06f;
        cy += bob;
        float r = size * 0.38f;
        float wag = (float)Math.Sin(phase * 3.1) * size * 0.30f;

        using var fur = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 235) };
        using var face = new SKPaint { IsAntialias = true, Color = new SKColor(14, 15, 19) };

        // tail, wagging (left of the head)
        using (var tail = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = size * 0.09f, StrokeCap = SKStrokeCap.Round, Color = new SKColor(255, 255, 255, 220) })
        using (var p = new SKPath())
        {
            p.MoveTo(cx - r * 0.70f, cy + r * 0.55f);
            p.QuadTo(cx - r * 1.55f, cy + r * 0.30f + wag, cx - r * 1.30f, cy - r * 0.65f + wag);
            c.DrawPath(p, tail);
        }

        float twitch = (float)Math.Sin(phase * 6.0) * r * 0.06f;
        using (var p = new SKPath())
        {
            p.MoveTo(cx - r * 0.90f, cy - r * 0.40f + twitch); p.LineTo(cx - r * 0.30f, cy - r * 0.40f);
            p.LineTo(cx - r * 0.78f, cy - r * 1.25f + twitch); p.Close();
            c.DrawPath(p, fur);
        }
        using (var p = new SKPath())
        {
            p.MoveTo(cx + r * 0.90f, cy - r * 0.40f + twitch); p.LineTo(cx + r * 0.30f, cy - r * 0.40f);
            p.LineTo(cx + r * 0.78f, cy - r * 1.25f + twitch); p.Close();
            c.DrawPath(p, fur);
        }

        c.DrawCircle(cx, cy, r, fur);

        bool blink = phase % 3.2f > 3.05f;
        if (blink)
        {
            using var eye = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = r * 0.15f, StrokeCap = SKStrokeCap.Round, Color = new SKColor(18, 19, 24) };
            c.DrawLine(cx - r * 0.46f, cy - r * 0.06f, cx - r * 0.12f, cy - r * 0.06f, eye);
            c.DrawLine(cx + r * 0.12f, cy - r * 0.06f, cx + r * 0.46f, cy - r * 0.06f, eye);
        }
        else
        {
            c.DrawCircle(cx - r * 0.28f, cy - r * 0.08f, r * 0.13f, face);
            c.DrawCircle(cx + r * 0.28f, cy - r * 0.08f, r * 0.13f, face);
        }

        using (var p = new SKPath())
        {
            p.MoveTo(cx - r * 0.11f, cy + r * 0.24f); p.LineTo(cx + r * 0.11f, cy + r * 0.24f);
            p.LineTo(cx, cy + r * 0.40f); p.Close();
            c.DrawPath(p, face);
        }
        using (var w = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = r * 0.07f, StrokeCap = SKStrokeCap.Round, Color = new SKColor(255, 255, 255, 170) })
        {
            c.DrawLine(cx - r * 0.55f, cy + r * 0.18f, cx - r * 1.20f, cy + r * 0.06f, w);
            c.DrawLine(cx - r * 0.55f, cy + r * 0.34f, cx - r * 1.20f, cy + r * 0.38f, w);
            c.DrawLine(cx + r * 0.55f, cy + r * 0.18f, cx + r * 1.20f, cy + r * 0.06f, w);
            c.DrawLine(cx + r * 0.55f, cy + r * 0.34f, cx + r * 1.20f, cy + r * 0.38f, w);
        }
    }

    private static void Progress(SKCanvas c, float x, float y, float w, float s, float p)
    {
        using var bg = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x33) };
        c.DrawRoundRect(new SKRect(x, y - 1.5f * s, x + w, y + 1.5f * s), 1.5f * s, 1.5f * s, bg);
        using var fg = new SKPaint { IsAntialias = true, Color = White };
        c.DrawRoundRect(new SKRect(x, y - 1.5f * s, x + w * Math.Clamp(p, 0, 1), y + 1.5f * s), 1.5f * s, 1.5f * s, fg);
    }

    private static void Hover(SKCanvas c, bool on, float cx, float cy, float r, float s)
    {
        if (!on) return;
        using var p = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x18) };
        c.DrawCircle(cx, cy, r + 6 * s, p);
    }

    private static void Button(SKCanvas c, float cx, float cy, float d, bool playing, float s)
    {
        using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x26) })
            c.DrawCircle(cx, cy, d / 2f, bg);
        using var ink = new SKPaint { IsAntialias = true, Color = White };
        if (playing)
        {
            float bw = 4 * s, bh = 14 * s;
            c.DrawRect(new SKRect(cx - 6 * s, cy - bh / 2, cx - 2 * s, cy + bh / 2), ink);
            c.DrawRect(new SKRect(cx + 2 * s, cy - bh / 2, cx + 6 * s, cy + bh / 2), ink);
        }
        else
        {
            using var p = new SKPath();
            p.MoveTo(cx - 4 * s, cy - 8 * s); p.LineTo(cx + 9 * s, cy); p.LineTo(cx - 4 * s, cy + 8 * s); p.Close();
            c.DrawPath(p, ink);
        }
    }

    private static void Icon(SKCanvas c, IconKind kind, float cx, float cy, float size, SKColor color, float s)
    {
        using var p = new SKPaint { IsAntialias = true, Color = color };
        float h = size * 0.62f, w = size * 0.5f;
        switch (kind)
        {
            case IconKind.Prev:
                using (var t = new SKPath())
                {
                    t.MoveTo(cx + w / 2, cy - h / 2); t.LineTo(cx + w / 2, cy + h / 2); t.LineTo(cx - w / 2, cy); t.Close();
                    c.DrawPath(t, p);
                }
                c.DrawRect(new SKRect(cx - w / 2 - 3 * s, cy - h / 2, cx - w / 2 - 1 * s, cy + h / 2), p);
                break;
            case IconKind.Next:
                using (var t = new SKPath())
                {
                    t.MoveTo(cx - w / 2, cy - h / 2); t.LineTo(cx - w / 2, cy + h / 2); t.LineTo(cx + w / 2, cy); t.Close();
                    c.DrawPath(t, p);
                }
                c.DrawRect(new SKRect(cx + w / 2 + 1 * s, cy - h / 2, cx + w / 2 + 3 * s, cy + h / 2), p);
                break;
            default:
                var rect = new SKRect(cx - size / 2, cy - size * 0.35f, cx + size / 2, cy + size * 0.35f);
                using (var sp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * s, Color = color })
                    c.DrawRoundRect(rect, 2 * s, 2 * s, sp);
                c.DrawRect(new SKRect(cx - size * 0.3f, cy - 2 * s, cx + size * 0.3f, cy - 0.6f * s), p);
                c.DrawRect(new SKRect(cx - size * 0.3f, cy + 1 * s, cx + size * 0.1f, cy + 2.4f * s), p);
                break;
        }
    }

    private static void Dots(SKCanvas c, float cx, float y, int active, float s)
    {
        using var on = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 230) };
        using var off = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 77) };
        c.DrawRoundRect(new SKRect(cx - 18 * s, y, cx - 4 * s, y + 4 * s), 2 * s, 2 * s, active == 0 ? on : off);
        c.DrawRoundRect(new SKRect(cx, y, cx + 4 * s, y + 4 * s), 2 * s, 2 * s, active == 1 ? on : off);
    }

    public void Dispose() { _bold.Dispose(); _semi.Dispose(); _med.Dispose(); }
}
