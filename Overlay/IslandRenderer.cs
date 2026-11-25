using System.IO;
using SkiaSharp;
using FloatIsland.Models;

namespace FloatIsland.Overlay;

internal enum Surface { Collapsed, Music, Lyrics, Menu, Volume, Settings }
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

    // Audio-reactive accents (shared 16-band snapshot; never mutated by the renderer)
    public float[]? Bars;
    public bool AudioActive;
    public float Beat;

    /// <summary>Lyric timing nudge in ms, shown on the "Lyrics sync" menu row.</summary>
    public int LyricsOffsetMs;

    /// <summary>Fixed accent colour (from the Settings surface). Null = follow the album tint.</summary>
    public SKColor? AccentOverride;

    // Settings surface state
    public double UiScale = 1.0;
    public double Opacity = 1.0;
    public int MonitorIndex = -1;
    public int MonitorCount = 1;
    public AccentMode Accent = AccentMode.Album;
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

    // Settings surface: the two sliders expose a generous hit area (visual track is thin).
    public SKRect SettingsScale, SettingsOpacity, SettingsMonMinus, SettingsMonPlus, SettingsBack;
    public SKRect[] SettingsSwatches = Array.Empty<SKRect>();
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

    /// <summary>Widest the collapsed pill may grow to. The overlay surface is 720 DIP wide, so
    /// this leaves ~40 DIP on each side for the shadow and the hit-test flare. Long lyrics
    /// widen the pill up to here instead of being clipped at a fixed size.</summary>
    private const float MaxCollapsedDip = 640f;

    public float CollapsedWidthPx(string line, float s)
    {
        var p = P(_semi, 13 * s, White);
        return Math.Clamp(p.MeasureText(line ?? "") + 82 * s, 136 * s, MaxCollapsedDip * s);
    }

    /// <summary>Fixed collapsed width when the mascot is shown (cover + gap + cat + padding).</summary>
    public float Measure(SKTypefaceKind kind, string text, float sizePx)
    {
        var p = P(kind == SKTypefaceKind.Bold ? _bold : kind == SKTypefaceKind.Medium ? _med : _semi, sizePx, White);
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
            case Surface.Settings: DrawSettings(c, w, s, f, hit); break;
            default: DrawCollapsed(c, w, s, f, hit); break;
        }
        c.Restore();

        if (fade) c.Restore();
    }

    // ---- collapsed (LINE)
    private void DrawCollapsed(SKCanvas c, int w, float s, IslandFrame f, IslandLayout hit)
    {
        var tp = P(_semi, 13 * s, White);
        float bodyW = CollapsedWidthPx(f.Line, s);
        float cx = w / 2f, xl = cx - bodyW / 2f, xr = cx + bodyW / 2f, ph = 36 * s;

        if (f.Cover != null && f.TintAmount > 0.02f)
            Glow(c, xl + 19 * s, ph / 2f, 23 * s, AccentOf(f), 0.22f * f.TintAmount, s);

        CoverCircle(c, f, xl + 19 * s, ph / 2f, 11.5f * s, s);

        float tx = xl + 40 * s, maxX = xr - 40 * s;
        c.Save(); c.ClipRect(new SKRect(tx, 0, Math.Max(tx, maxX), ph));
        float baseY = Baseline(tp, ph / 2f);
        KaraokeText(c, f.Line, tx, baseY, f.Karaoke, _semi, 13 * s, maxX - tx);
        c.Restore();

        if (f.Playing && f.Bars != null)
            DrawBars(c, xr - 24 * s, ph / 2f, 13 * s, 13 * s, f.Bars, f.AudioActive, f.Beat, s,
                new SKColor(255, 255, 255, 220));

        // hairline playback progress tucked into the bottom edge
        if (f.HasTrack && f.Progress > 0.001)
        {
            float px0 = xl + 13 * s, px1 = xr - 13 * s, py = ph - 1.7f * s;
            using var bg = new SKPaint
            {
                IsAntialias = true, Color = new SKColor(255, 255, 255, 0x24),
                StrokeWidth = 1.3f * s, StrokeCap = SKStrokeCap.Round
            };
            c.DrawLine(px0, py, px1, py, bg);
            using var fg = new SKPaint
            {
                IsAntialias = true, Color = new SKColor(255, 255, 255, 215),
                StrokeWidth = 1.3f * s, StrokeCap = SKStrokeCap.Round
            };
            c.DrawLine(px0, py, px0 + (px1 - px0) * Math.Clamp((float)f.Progress, 0f, 1f), py, fg);
        }

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

        if (f.TintAmount > 0.02f)
        {
            var accentGlow = AccentOf(f);
            Glow(c, left + 32 * s, ctop + 32 * s, 78 * s, accentGlow, 0.28f * f.TintAmount, s);
            if (lyricsPage) Glow(c, right - 90 * s, bottom * 0.45f, 150 * s, accentGlow, 0.10f * f.TintAmount, s);
        }

        CoverRounded(c, f, left, ctop, 64 * s, 16 * s, s);

        float tx = left + (64 + 14) * s, tw = colW - (64 + 14 + 12) * s;
        var title = P(_semi, 16 * s, White);
        var artist = P(_med, 12.5f * s, Dim);
        {
            c.Save(); c.ClipRect(new SKRect(tx, ctop, tx + tw, ctop + 64 * s));
            if (f.MarqueeOffset < -0.5f)
                title.MeasureText(f.Title);   // warm
            c.DrawText(f.Title, tx + f.MarqueeOffset, ctop + 23 * s, title);
            c.DrawText(Clip(f.Artist, artist, tw), tx, ctop + 43 * s, artist);
            if (!lyricsPage && f.HasLyrics && !string.IsNullOrEmpty(f.LyricLine))
                KaraokeText(c, f.LyricLine, tx, ctop + 61 * s, f.Karaoke, _semi, 12 * s, tw);
            c.Restore();
        }

        if (!lyricsPage)
        {
            if (f.Bars != null)
                DrawBars(c, right - 57 * s, ctop + 26 * s, 14 * s, 15 * s, f.Bars, f.AudioActive, f.Beat, s,
                    new SKColor(255, 255, 255, 190));
            DrawCat(c, right - 30 * s, ctop + 24 * s, 26 * s, f.CatPhase);
        }

        float py = ctop + 64 * s + 8 * s;
        float trackL = left + 44 * s, trackR = left + colW - 44 * s;
        var sm = P(_med, 11.5f * s, Faint);
        {
            c.DrawText(f.Elapsed, left, py + 10 * s, sm);
            c.DrawText(f.Duration, trackR + 6 * s, py + 10 * s, sm);
        }
        Progress(c, trackL, py + 8 * s, trackR - trackL, s, (float)f.Progress, AccentOf(f), f.TintAmount);
        hit.SeekTrack = new SKRect(trackL, py, trackR, py + 14 * s);

        float cyy = py + 14 * s + 8 * s + 18 * s, cxm = left + colW / 2f;
        Hover(c, f.Hover == 1, cxm, cyy, 22 * s, s);
        Button(c, cxm, cyy, 44 * s, f.Playing, s, f.Hover == 1);
        hit.Play = new SKRect(cxm - 22 * s, cyy - 22 * s, cxm + 22 * s, cyy + 22 * s);
        Hover(c, f.Hover == 0, cxm - 40 * s, cyy, 15 * s, s);
        Icon(c, IconKind.Prev, cxm - 40 * s, cyy, 16 * s, White, s);
        hit.Prev = new SKRect(cxm - 55 * s, cyy - 15 * s, cxm - 25 * s, cyy + 15 * s);
        Hover(c, f.Hover == 2, cxm + 40 * s, cyy, 15 * s, s);
        Icon(c, IconKind.Next, cxm + 40 * s, cyy, 16 * s, White, s);
        hit.Next = new SKRect(cxm + 25 * s, cyy - 15 * s, cxm + 55 * s, cyy + 15 * s);

        float togx = left + colW - 14 * s;
        if (lyricsPage)
        {
            using var pill = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x14) };
            c.DrawRoundRect(new SKRect(togx - 17 * s, cyy - 13 * s, togx + 17 * s, cyy + 13 * s), 13 * s, 13 * s, pill);
        }
        Hover(c, f.Hover == 3, togx, cyy, 15 * s, s);
        Icon(c, IconKind.Lyrics, togx, cyy, 16 * s, lyricsPage ? White : Dim, s);
        hit.LyricsToggle = new SKRect(togx - 16 * s, cyy - 15 * s, togx + 16 * s, cyy + 15 * s);

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

        float lx = dx + 24 * s, lw = right - 18 * s - lx;
        c.Save();
        c.ClipRect(new SKRect(lx - 14 * s, ctop, lx + lw, bottom - pad));
        var lyStyle = P(_bold, 17.5f * s, White);
        float rowH = 31 * s, midY = ctop + (H - 2 * pad) / 2f;
        var slide = (1f - Math.Clamp(f.LyricsReveal, 0f, 1f)) * 14 * s;   // rows slide in from the right
        var accent = AccentOf(f);
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

            if (d == 0 && reveal > 0.5f)
            {
                byte aa = (byte)(200 * reveal * Math.Clamp(f.LineFade, 0f, 1f));
                using var mark = new SKPaint
                {
                    IsAntialias = true, StrokeCap = SKStrokeCap.Round,
                    Color = f.TintAmount > 0.05f ? accent.WithAlpha(aa) : new SKColor(255, 255, 255, aa)
                };
                c.DrawLine(lx - 10 * s, y - 11 * s, lx - 10 * s, y + 1 * s, mark);
            }
        }
        c.Restore();
    }

    /// <summary>Menu body height (in DIP) for a given number of rows; grows with the list, capped.</summary>
    public static float MenuHeightPx(int rows) => Math.Min(232f, 58f + 20f * Math.Max(1, rows));

    // ---- MENU (316 x MenuHeightPx)
    private void DrawMenu(SKCanvas c, int w, float s, IslandFrame f, IslandLayout hit)
    {
        var labels = f.MenuLabels.Length > 0 ? f.MenuLabels : new[] { "Exit" };
        float bodyW = 316 * s, H = MenuHeightPx(labels.Length) * s;
        float cx = w / 2f, xl = cx - bodyW / 2f, xr = cx + bodyW / 2f;
        hit.Collapse = new SKRect(xl, 0, xr, H);

        float pad = 10 * s;
        float header = 30 * s;

        // brand header: mark, wordmark, tagline, version
        if (f.Logo != null)
            c.DrawImage(f.Logo, new SKRect(xl + pad + 2 * s, pad + 3 * s, xl + pad + 26 * s, pad + 27 * s));
        var brand = P(_bold, 13 * s, White);
        c.DrawText("Float Island", xl + pad + 34 * s, pad + 12 * s, brand);
        var tag = P(_semi, 8.5f * s, new SKColor(255, 255, 255, 105));
        c.DrawText("SPOTIFY ISLAND", xl + pad + 34 * s, pad + 24 * s, tag);

        var ver = P(_semi, 9 * s, new SKColor(255, 255, 255, 145));
        string verText = VersionLabel();
        float verW = ver.MeasureText(verText);
        var verRect = new SKRect(xr - pad - verW - 14 * s, pad + 6 * s, xr - pad, pad + 22 * s);
        using (var vbg = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x12) })
            c.DrawRoundRect(verRect, 8 * s, 8 * s, vbg);
        c.DrawText(verText, verRect.Left + 7 * s, Baseline(ver, verRect.MidY), ver);

        float divY = pad + header;
        using (var div = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x14), StrokeWidth = 1 * s })
            c.DrawLine(xl + pad, divY, xr - pad, divY, div);

        float rowsTop = divY + 8 * s;
        float bottomPad = 10 * s;
        float rowH = (H - rowsTop - bottomPad) / labels.Length;
        hit.MenuRows = new SKRect[labels.Length];

        var label = P(_semi, 13 * s, new SKColor(255, 255, 255, 235));
        var value = P(_med, 11.5f * s, Faint);
        var accent = f.TintAmount > 0.05f ? AccentOf(f) : new SKColor(255, 255, 255, 205);

        for (int i = 0; i < labels.Length; i++)
        {
            string raw = labels[i] ?? "";
            string text = raw;
            string? right = null;
            bool toggle = false, on = false, sync = false;
            if (raw.StartsWith("Output: ", StringComparison.Ordinal)) { text = "Output"; right = raw[8..]; }
            else if (text.Equals("Lyrics sync", StringComparison.Ordinal)) { sync = true; }
            else if (raw.EndsWith(": On", StringComparison.Ordinal)) { text = raw[..^4]; toggle = true; on = true; }
            else if (raw.EndsWith(": Off", StringComparison.Ordinal)) { text = raw[..^5]; toggle = true; }

            float y = rowsTop + i * rowH;
            var row = new SKRect(xl + pad, y, xr - pad, y + rowH);
            hit.MenuRows[i] = row;

            // a hairline separates the destructive/close group
            if (i > 0 && text.StartsWith("Collapse", StringComparison.Ordinal))
                using (var gd = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x12), StrokeWidth = 1 * s })
                    c.DrawLine(row.Left + 6 * s, y, row.Right - 6 * s, y, gd);

            bool hot = f.MenuHover == i;
            if (hot)
            {
                using var hl = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x10) };
                c.DrawRoundRect(new SKRect(row.Left, y + 1 * s, row.Right, y + rowH - 1 * s), 8 * s, 8 * s, hl);
                using var mark = new SKPaint { IsAntialias = true, Color = accent };
                c.DrawRoundRect(new SKRect(row.Left + 3 * s, row.MidY - 7 * s, row.Left + 5 * s, row.MidY + 7 * s), 1 * s, 1 * s, mark);
            }

            var iconColor = hot ? White : new SKColor(255, 255, 255, 175);
            DrawMenuIcon(c, MenuIconFor(text), row.Left + 15 * s, row.MidY, 15 * s, iconColor, s);

            label.Color = hot ? White : new SKColor(255, 255, 255, 235);
            c.DrawText(text, row.Left + 32 * s, Baseline(label, row.MidY), label);

            if (toggle)
            {
                Switch(c, row.Right - 36 * s, row.MidY - 8 * s, 30 * s, 16 * s, on, s, accent);
            }
            else if (sync)
            {
                DrawSyncControl(c, row, f.LyricsOffsetMs, s, accent, hot);
            }
            else if (right != null)
            {
                float maxW = row.Width * 0.42f;
                string shown = Clip(right, value, maxW);
                c.DrawText(shown, row.Right - 8 * s - value.MeasureText(shown), Baseline(value, row.MidY), value);
            }
            else if (text.Equals("Music", StringComparison.Ordinal) || text.Equals("Lyrics", StringComparison.Ordinal)
                  || text.Equals("Settings", StringComparison.Ordinal))
            {
                using var ch = new SKPaint
                {
                    IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * s,
                    StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
                    Color = new SKColor(255, 255, 255, 110)
                };
                float chx = row.Right - 12 * s, chy = row.MidY;
                c.DrawLine(chx - 3 * s, chy - 4 * s, chx + 1 * s, chy, ch);
                c.DrawLine(chx + 1 * s, chy, chx - 3 * s, chy + 4 * s, ch);
            }
        }
    }

    // ---- VOLUME (236x44)
    private void DrawVolume(SKCanvas c, int w, float s, IslandFrame f, IslandLayout hit)
    {
        float bodyW = 236 * s, H = 44 * s;
        float cx = w / 2f, xl = cx - bodyW / 2f, xr = cx + bodyW / 2f;
        hit.Collapse = new SKRect(xl, 0, xr, H);
        float cy = H / 2f;

        float ix = xl + 26 * s;
        using (var badge = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x12) })
            c.DrawCircle(ix, cy, 14 * s, badge);

        using var ink = new SKPaint { IsAntialias = true, Color = White };
        using (var p = new SKPath())
        {
            p.MoveTo(ix - 7 * s, cy - 4 * s); p.LineTo(ix - 3 * s, cy - 4 * s); p.LineTo(ix + 2 * s, cy - 8 * s);
            p.LineTo(ix + 2 * s, cy + 8 * s); p.LineTo(ix - 3 * s, cy + 4 * s); p.LineTo(ix - 7 * s, cy + 4 * s);
            p.Close();
            c.DrawPath(p, ink);
        }
        using (var arc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * s, StrokeCap = SKStrokeCap.Round, Color = White })
        {
            if (!f.VolumeMuted)
            {
                c.DrawArc(new SKRect(ix + 0 * s, cy - 5 * s, ix + 12 * s, cy + 5 * s), -50, 100, false, arc);
                c.DrawArc(new SKRect(ix + 0 * s, cy - 9 * s, ix + 19 * s, cy + 9 * s), -50, 100, false, arc);
            }
            else
            {
                c.DrawLine(ix + 6 * s, cy - 4 * s, ix + 14 * s, cy + 4 * s, arc);
                c.DrawLine(ix + 14 * s, cy - 4 * s, ix + 6 * s, cy + 4 * s, arc);
            }
        }

        float bx0 = ix + 26 * s, bx1 = xr - 62 * s;
        Progress(c, bx0, cy, bx1 - bx0, s, Math.Clamp(f.Volume, 0, 1));

        var pct = P(_semi, 12 * s, White);
        string label = f.VolumeMuted ? "Mute" : $"{Math.Round(f.Volume * 100)}%";
        c.DrawText(label, bx1 + 14 * s, Baseline(pct, cy), pct);
    }

    /// <summary>Settings body height (in DIP): 2 sliders, an accent strip, a monitor stepper, a Back row.</summary>
    public static float SettingsHeightPx() => 232f;

    // ---- SETTINGS (316 x SettingsHeightPx)
    private void DrawSettings(SKCanvas c, int w, float s, IslandFrame f, IslandLayout hit)
    {
        float bodyW = 316 * s, H = SettingsHeightPx() * s;
        float cx = w / 2f, xl = cx - bodyW / 2f, xr = cx + bodyW / 2f;
        hit.Collapse = new SKRect(xl, 0, xr, H);

        float pad = 14 * s;
        float ix0 = xl + pad, ix1 = xr - pad;

        var title = P(_bold, 13 * s, White);
        c.DrawText("Settings", ix0, pad + 12 * s, title);

        var ver = P(_semi, 9 * s, new SKColor(255, 255, 255, 145));
        string vt = VersionLabel();
        float vw = ver.MeasureText(vt);
        var vr = new SKRect(ix1 - vw - 14 * s, pad + 4 * s, ix1, pad + 20 * s);
        using (var vbg = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x12) })
            c.DrawRoundRect(vr, 8 * s, 8 * s, vbg);
        c.DrawText(vt, vr.Left + 7 * s, Baseline(ver, vr.MidY), ver);

        float divY = pad + 24 * s;
        using (var div = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x14), StrokeWidth = 1 * s })
            c.DrawLine(ix0, divY, ix1, divY, div);

        var label = P(_semi, 11.5f * s, new SKColor(255, 255, 255, 225));
        var accent = AccentOf(f);

        float y = divY + 10 * s;
        y = SliderRow(c, "Scale", $"{f.UiScale:0.00}\u00d7", ix0, ix1, y, s,
            (float)((f.UiScale - 0.8) / 0.6), accent, hit, true);
        y = SliderRow(c, "Opacity", $"{Math.Round(f.Opacity * 100)}%", ix0, ix1, y, s,
            (float)((f.Opacity - 0.4) / 0.6), accent, hit, false);

        // accent swatches: Album follows the cover, the rest are fixed themes
        float sy = y + 2 * s;
        c.DrawText("Accent", ix0, Baseline(label, sy + 9 * s), label);
        var modes = (AccentMode[])Enum.GetValues(typeof(AccentMode));
        float sw = 15 * s, gap = 7 * s;
        float sx0 = ix1 - (modes.Length * sw + (modes.Length - 1) * gap);
        float scy = sy + 9 * s;
        hit.SettingsSwatches = new SKRect[modes.Length];
        for (int i = 0; i < modes.Length; i++)
        {
            float scx = sx0 + i * (sw + gap) + sw / 2f;
            hit.SettingsSwatches[i] = new SKRect(scx - sw / 2f, scy - sw / 2f, scx + sw / 2f, scy + sw / 2f);

            if (modes[i] == AccentMode.Album)
            {
                using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * s, Color = new SKColor(255, 255, 255, 0x66) };
                c.DrawCircle(scx, scy, sw / 2f, ring);
                using var dot = new SKPaint { IsAntialias = true, Color = f.TintAmount > 0.05f ? AccentOf(f) : new SKColor(255, 255, 255, 0x40) };
                c.DrawCircle(scx, scy, sw / 2f - 3 * s, dot);
            }
            else
            {
                using var fill = new SKPaint { IsAntialias = true, Color = AccentColorFor(modes[i]) };
                c.DrawCircle(scx, scy, sw / 2f, fill);
            }

            if (modes[i] == f.Accent)
                using (var sel = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 * s, Color = White })
                    c.DrawCircle(scx, scy, sw / 2f + 2.5f * s, sel);
        }

        // monitor stepper
        float my = sy + 30 * s;
        float mcy = my + 8 * s;
        c.DrawText("Monitor", ix0, Baseline(label, mcy), label);
        float plusX = ix1 - 10 * s, minusX = ix1 - 96 * s;
        SmallStep(c, minusX, mcy, 8 * s, false, s, false);
        SmallStep(c, plusX, mcy, 8 * s, true, s, false);
        hit.SettingsMonMinus = new SKRect(minusX - 12 * s, mcy - 12 * s, minusX + 12 * s, mcy + 12 * s);
        hit.SettingsMonPlus = new SKRect(plusX - 12 * s, mcy - 12 * s, plusX + 12 * s, mcy + 12 * s);
        var monVal = P(_semi, 11.5f * s, f.MonitorIndex < 0 ? Faint : White);
        string mt = f.MonitorIndex < 0 ? "Follow pointer" : $"{f.MonitorIndex + 1} / {f.MonitorCount}";
        c.DrawText(mt, (minusX + plusX) / 2f - monVal.MeasureText(mt) / 2f, Baseline(monVal, mcy), monVal);

        // Back
        float by = my + 30 * s;
        var back = new SKRect(ix0, by, ix1, by + 26 * s);
        hit.SettingsBack = back;
        using (var bb = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x10) })
            c.DrawRoundRect(back, 8 * s, 8 * s, bb);
        using (var ch = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f * s,
            StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = new SKColor(255, 255, 255, 200)
        })
        {
            float ax = back.Left + 14 * s, ay = back.MidY;
            c.DrawLine(ax + 4 * s, ay - 4 * s, ax, ay, ch);
            c.DrawLine(ax, ay, ax + 4 * s, ay + 4 * s, ch);
        }
        var backT = P(_semi, 11.5f * s, new SKColor(255, 255, 255, 220));
        c.DrawText("Back", back.Left + 26 * s, Baseline(backT, back.MidY), backT);
    }

    /// <summary>A label + value line over a draggable track; returns the y below it.</summary>
    private float SliderRow(SKCanvas c, string name, string valueText, float ix0, float ix1,
        float y, float s, float v01, SKColor accent, IslandLayout hit, bool isScale)
    {
        var label = P(_semi, 11.5f * s, new SKColor(255, 255, 255, 225));
        var value = P(_med, 11.5f * s, White);
        c.DrawText(name, ix0, Baseline(label, y + 8 * s), label);
        c.DrawText(valueText, ix1 - value.MeasureText(valueText), Baseline(value, y + 8 * s), value);

        float trackY = y + 21 * s;
        float r = 5 * s;
        v01 = Math.Clamp(v01, 0f, 1f);
        var track = new SKRect(ix0 + r, trackY - 2.5f * s, ix1 - r, trackY + 2.5f * s);
        using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x1E) })
            c.DrawRoundRect(track, 2.5f * s, 2.5f * s, bg);

        float kx = track.Left + v01 * track.Width;
        using (var fill = new SKPaint { IsAntialias = true, Color = accent.WithAlpha(230) })
            c.DrawRoundRect(new SKRect(track.Left, track.Top, kx, track.Bottom), 2.5f * s, 2.5f * s, fill);
        using (var kb = new SKPaint { IsAntialias = true, Color = new SKColor(10, 11, 14) })
            c.DrawCircle(kx, trackY, r, kb);
        using (var kr = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f * s, Color = White })
            c.DrawCircle(kx, trackY, r - 1 * s, kr);

        // rows are thin; a drag anywhere across the track band (or the label line) takes hold
        var area = new SKRect(ix0, y, ix1, trackY + 12 * s);
        if (isScale) hit.SettingsScale = area; else hit.SettingsOpacity = area;
        return trackY + 14 * s;
    }

    /// <summary>Fixed theme colours for the Settings accent picker.</summary>
    internal static SKColor AccentColorFor(AccentMode m) => m switch
    {
        AccentMode.Green => new SKColor(0x1E, 0xD7, 0x60),
        AccentMode.Blue => new SKColor(0x5B, 0x8D, 0xEF),
        AccentMode.Pink => new SKColor(0xF5, 0x6F, 0xA1),
        AccentMode.Amber => new SKColor(0xF5, 0xA6, 0x23),
        AccentMode.Mono => new SKColor(0xC8, 0xCC, 0xD4),
        _ => new SKColor(0x9F, 0xB4, 0xFF),
    };

    // ---- primitives
    // Paints and the body shaders are cached: creating them per frame at 60 fps was the
// single biggest CPU cost (thousands of native allocations per second).
private readonly Dictionary<(int tf, int size, int color), SKPaint> _paintCache = new();
private SKPaint? _bodyFill, _bodySheen, _bodyEdge, _bodyTopEdge;
private (float w, float h, float top, int tint, float amt) _bodyKey;
private readonly Dictionary<(int x, int y, int size, int s), SKPaint> _coverSheenCache = new();

private SKPaint P(SKTypeface tf, float size, SKColor color)
{
    var key = (tf.GetHashCode(), (int)Math.Round(size * 4), color.GetHashCode());
    if (_paintCache.TryGetValue(key, out var cached)) return cached;
    var p = new SKPaint { Typeface = tf, TextSize = size, IsAntialias = true, Color = color };
    _paintCache[key] = p;
    return p;
}

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

    private void KaraokeText(SKCanvas c, string text, float x, float baseY, float karaoke, SKTypeface tf, float size, float maxW)
    {
        if (string.IsNullOrEmpty(text)) return;
        var probe = P(tf, size, White);
        string t = Clip(text, probe, maxW);
        float w = probe.MeasureText(t);

        var dim = P(tf, size, Faint); c.DrawText(t, x, baseY, dim);

        float reveal = Math.Clamp(karaoke, 0f, 1f) * w;
        if (reveal <= 0.5f) return;
        c.Save();
        c.ClipRect(new SKRect(x, baseY - size * 1.4f, x + reveal + 3f, baseY + size * 0.5f));
        var bright = P(tf, size, White);
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

        // a low, album-coloured halo makes the surface feel lit from behind
        if (a > 0.04f)
        {
            var accent = Accent(tint);
            for (int i = 0; i < 3; i++)
            {
                float wd = (12 + i * 12) * s;
                byte al = (byte)((i == 0 ? 18 : i == 1 ? 11 : 7) * a);
                using var gp = new SKPaint
                {
                    IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = wd,
                    Color = accent.WithAlpha(al)
                };
                c.DrawPath(path, gp);
            }
        }

        var key = (w, h, top, cTop.GetHashCode() ^ cBottom.GetHashCode(), a);
        if (_bodyFill == null || !key.Equals(_bodyKey))
        {
            _bodyKey = key;
            _bodyFill?.Dispose();
            _bodySheen?.Dispose();
            _bodyEdge?.Dispose();
            _bodyTopEdge?.Dispose();

            _bodyFill = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, top + h),
                    new[] { cTop, Mix(cTop, cBottom, 0.55f), cBottom }, new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp)
            };
            _bodySheen = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, top + h * 0.34f),
                    new[] { new SKColor(255, 255, 255, 17), SKColors.Transparent }, new[] { 0f, 1f }, SKShaderTileMode.Clamp)
            };
            _bodyEdge = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2 * s,
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, top + h * 0.5f), new SKPoint(0, top + h),
                    new[] { SKColors.Transparent, Edge }, new[] { 0f, 1f }, SKShaderTileMode.Clamp)
            };
            _bodyTopEdge = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1 * s,
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, top + h * 0.5f),
                    new[] { new SKColor(255, 255, 255, 64), SKColors.Transparent }, new[] { 0f, 1f }, SKShaderTileMode.Clamp)
            };
        }

        c.DrawPath(path, _bodyFill);

        c.Save(); c.ClipPath(path, SKClipOperation.Intersect, true);
        c.DrawRect(new SKRect(x0, top, x0 + w, top + h * 0.34f), _bodySheen);
        c.Restore();

        c.DrawPath(path, _bodyTopEdge);
        c.DrawPath(path, _bodyEdge);
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

        // a soft drop shadow grounds the artwork against the body
        using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 90) })
            c.DrawRoundRect(new SKRect(x, y + 3 * s, x + size, y + size + 3 * s), r, r, shadow);

        if (f.Cover != null)
        {
            c.Save(); c.ClipRoundRect(rr, SKClipOperation.Intersect, true);
            c.DrawImage(f.Cover, rect); c.Restore();
        }
        else using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x12) }) c.DrawRoundRect(rr, bg);
        if (f.Cover == null) DrawLogoInside(c, f.Logo, x, y, size, 0.62f);

        // inner light along the top edge, then a crisp hairline border
        var sheenKey = ((int)Math.Round(x * 2), (int)Math.Round(y * 2), (int)Math.Round(size * 2), (int)Math.Round(s * 2));
        if (!_coverSheenCache.TryGetValue(sheenKey, out var inner))
        {
            inner = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * s,
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, y), new SKPoint(0, y + size * 0.5f),
                    new[] { new SKColor(255, 255, 255, 70), SKColors.Transparent }, new[] { 0f, 1f }, SKShaderTileMode.Clamp)
            };
            _coverSheenCache[sheenKey] = inner;
        }
        c.DrawRoundRect(new SKRect(x + 0.75f * s, y + 0.75f * s, x + size - 0.75f * s, y + size - 0.75f * s), r, r, inner);

        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = s, Color = new SKColor(255, 255, 255, 0x24) };
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

    private static void Progress(SKCanvas c, float x, float y, float w, float s, float p,
        SKColor? accent = null, float accentAmt = 0f)
    {
        float h = 3 * s;
        using var bg = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x2E) };
        c.DrawRoundRect(new SKRect(x, y - h / 2, x + w, y + h / 2), h / 2, h / 2, bg);

        float pw = w * Math.Clamp(p, 0f, 1f);
        if (pw < 0.5f) return;

        var fill = accent != null && accentAmt > 0.03f
            ? Mix(White, accent.Value, Math.Clamp(accentAmt, 0f, 1f) * 0.8f)
            : new SKColor(245, 245, 248);
        using var fg = new SKPaint { IsAntialias = true, Color = fill };
        c.DrawRoundRect(new SKRect(x, y - h / 2, x + pw, y + h / 2), h / 2, h / 2, fg);

        // knob, with a dark rim so it reads on light album tints too
        using (var rim = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 70) })
            c.DrawCircle(x + pw, y, 4.6f * s, rim);
        using var knob = new SKPaint { IsAntialias = true, Color = White };
        c.DrawCircle(x + pw, y, 3.6f * s, knob);
    }

    private static void Hover(SKCanvas c, bool on, float cx, float cy, float r, float s)
    {
        if (!on) return;
        using var p = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x18) };
        c.DrawCircle(cx, cy, r + 6 * s, p);
    }

    private static void Button(SKCanvas c, float cx, float cy, float d, bool playing, float s, bool hover)
    {
        float r = d / 2f;

        using (var shade = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 80) })
            c.DrawCircle(cx, cy + 2 * s, r, shade);

        if (hover)
            using (var halo = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x1F) })
                c.DrawCircle(cx, cy, r + 5 * s, halo);

        using (var bg = new SKPaint { IsAntialias = true, Color = hover ? SKColors.White : new SKColor(242, 243, 247) })
            c.DrawCircle(cx, cy, r, bg);

        using var ink = new SKPaint { IsAntialias = true, Color = new SKColor(12, 13, 17) };
        if (playing)
        {
            float bh = 15 * s, bw = 4 * s;
            c.DrawRoundRect(new SKRect(cx - 6 * s, cy - bh / 2, cx - 6 * s + bw, cy + bh / 2), 1.4f * s, 1.4f * s, ink);
            c.DrawRoundRect(new SKRect(cx + 2 * s, cy - bh / 2, cx + 2 * s + bw, cy + bh / 2), 1.4f * s, 1.4f * s, ink);
        }
        else
        {
            using var p = new SKPath();
            p.MoveTo(cx - 4 * s, cy - 8.5f * s); p.LineTo(cx + 10 * s, cy); p.LineTo(cx - 4 * s, cy + 8.5f * s); p.Close();
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
        using var on = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 232) };
        using var off = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 80) };
        float mid = y + 2 * s;
        if (active == 0)
        {
            c.DrawRoundRect(new SKRect(cx - 17 * s, y, cx - 3 * s, y + 4 * s), 2 * s, 2 * s, on);
            c.DrawCircle(cx + 3 * s, mid, 2 * s, off);
        }
        else
        {
            c.DrawCircle(cx - 5 * s, mid, 2 * s, off);
            c.DrawRoundRect(new SKRect(cx + 1 * s, y, cx + 15 * s, y + 4 * s), 2 * s, 2 * s, on);
        }
    }

    // ---- finishing-touch primitives

    /// <summary>The UI accent for a frame: the fixed theme colour, or one lifted from the cover.</summary>
    private static SKColor AccentOf(IslandFrame f) => f.AccentOverride ?? Accent(f.Tint);

    /// <summary>Lifts a cover colour into a vivid, readable accent for small UI details.</summary>
    private static SKColor Accent(SKColor c)
    {
        int max = Math.Max(c.Red, Math.Max(c.Green, c.Blue));
        int min = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
        if (max < 60 || max - min < 20)
            return new SKColor(0x9F, 0xB4, 0xFF);   // neutral / greyscale art -> cool accent

        float k = Math.Max(1f, Math.Min(1.9f, 200f / max));
        return new SKColor(
            (byte)Math.Min(255, c.Red * k + 18),
            (byte)Math.Min(255, c.Green * k + 18),
            (byte)Math.Min(255, c.Blue * k + 18));
    }

    private readonly Dictionary<(int color, int alpha, int cx, int cy, int r), SKPaint> _glowCache = new();

    private void Glow(SKCanvas c, float cx, float cy, float r, SKColor color, float alpha, float s)
    {
        byte a = (byte)(255 * Math.Clamp(alpha, 0f, 1f));
        if (a == 0) return;

        var key = (color.GetHashCode(), a, (int)Math.Round(cx * 2), (int)Math.Round(cy * 2), (int)Math.Round(r * 2));
        if (!_glowCache.TryGetValue(key, out var p))
        {
            if (_glowCache.Count > 96)   // morph transitions can churn positions; keep the cache bounded
            {
                foreach (var old in _glowCache.Values) old.Dispose();
                _glowCache.Clear();
            }
            p = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), r,
                    new[] { color.WithAlpha(a), color.WithAlpha(0) }, new[] { 0f, 1f }, SKShaderTileMode.Clamp)
            };
            _glowCache[key] = p;
        }
        c.DrawCircle(cx, cy, r, p);
    }

    private static void DrawBars(SKCanvas c, float cx, float cy, float w, float h,
        float[] bars, bool active, float beat, float s, SKColor color)
    {
        const int n = 4;
        if (bars.Length == 0) return;
        float bw = 2.2f * s;
        float gap = (w - n * bw) / (n - 1);
        float boost = 1f + Math.Clamp(beat, 0f, 1f) * 0.18f;
        float bottom = cy + h / 2f;

        using var p = new SKPaint { IsAntialias = true, Color = color };
        for (int i = 0; i < n; i++)
        {
            int idx = (int)((long)i * bars.Length / n);
            float v = active
                ? Math.Min(1f, Math.Max(0.06f, bars[idx]) * boost)
                : 0.16f + (i % 2 == 0 ? 0.04f : 0f);
            float bh = Math.Max(2f * s, Math.Min(1f, v) * h);
            float x = cx - w / 2f + i * (bw + gap);
            c.DrawRoundRect(new SKRect(x, bottom - bh, x + bw, bottom), bw / 2f, bw / 2f, p);
        }
    }

    private static void Switch(SKCanvas c, float x, float y, float w, float h,
        bool on, float s, SKColor accent)
    {
        using var bg = new SKPaint
        {
            IsAntialias = true,
            Color = on ? accent.WithAlpha(225) : new SKColor(255, 255, 255, 0x24)
        };
        c.DrawRoundRect(new SKRect(x, y, x + w, y + h), h / 2f, h / 2f, bg);

        float r = h / 2f - 2 * s;
        float kx = on ? x + w - h / 2f : x + h / 2f;
        using var knob = new SKPaint
        {
            IsAntialias = true,
            Color = on ? new SKColor(10, 11, 14) : new SKColor(235, 235, 240)
        };
        c.DrawCircle(kx, y + h / 2f, r, knob);
    }

    /// <summary>The "Lyrics sync" row control: [ - ]  value  [ + ].</summary>
    private void DrawSyncControl(SKCanvas c, SKRect row, int offsetMs, float s, SKColor accent, bool hot)
    {
        float cy = row.MidY;
        float r = 8 * s;
        float plusX = row.Right - 12 * s;
        float minusX = row.Right - 100 * s;

        SmallStep(c, minusX, cy, r, false, s, hot);
        SmallStep(c, plusX, cy, r, true, s, hot);

        var val = P(_semi, 11.5f * s, offsetMs == 0 ? Faint : White);
        string t = FormatOffset(offsetMs);
        float vx = (minusX + plusX) / 2f;
        c.DrawText(t, vx - val.MeasureText(t) / 2f, Baseline(val, cy), val);
    }

    private static void SmallStep(SKCanvas c, float cx, float cy, float r, bool plus, float s, bool hot)
    {
        using (var bg = new SKPaint
        {
            IsAntialias = true,
            Color = hot ? new SKColor(255, 255, 255, 0x22) : new SKColor(255, 255, 255, 0x14)
        })
            c.DrawCircle(cx, cy, r, bg);

        using var ink = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * s,
            StrokeCap = SKStrokeCap.Round, Color = new SKColor(255, 255, 255, 225)
        };
        c.DrawLine(cx - 3.4f * s, cy, cx + 3.4f * s, cy, ink);
        if (plus) c.DrawLine(cx, cy - 3.4f * s, cx, cy + 3.4f * s, ink);
    }

    private static string FormatOffset(int ms)
    {
        if (ms == 0) return "0 s";
        return (ms > 0 ? "+" : "\u2212") + (Math.Abs(ms) / 1000.0).ToString("0.0") + " s";
    }

    private enum MenuIcon { None, Music, Lyrics, Sync, Output, Startup, Hide, Hotkeys, Settings, Collapse, Exit }

    private static MenuIcon MenuIconFor(string text)
    {
        if (text.Equals("Lyrics sync", StringComparison.Ordinal)) return MenuIcon.Sync;
        if (text.StartsWith("Music", StringComparison.Ordinal)) return MenuIcon.Music;
        if (text.StartsWith("Lyrics", StringComparison.Ordinal)) return MenuIcon.Lyrics;
        if (text.StartsWith("Media", StringComparison.Ordinal)) return MenuIcon.Hotkeys;
        if (text.StartsWith("Settings", StringComparison.Ordinal)) return MenuIcon.Settings;
        if (text.StartsWith("Output", StringComparison.Ordinal)) return MenuIcon.Output;
        if (text.StartsWith("Launch", StringComparison.Ordinal)) return MenuIcon.Startup;
        if (text.StartsWith("Hide", StringComparison.Ordinal)) return MenuIcon.Hide;
        if (text.StartsWith("Collapse", StringComparison.Ordinal)) return MenuIcon.Collapse;
        if (text.StartsWith("Exit", StringComparison.Ordinal)) return MenuIcon.Exit;
        return MenuIcon.None;
    }

    private static void DrawMenuIcon(SKCanvas c, MenuIcon kind, float cx, float cy, float size, SKColor color, float s)
    {
        using var stroke = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * s,
            StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = color
        };
        using var fill = new SKPaint { IsAntialias = true, Color = color };

        switch (kind)
        {
            case MenuIcon.Music:
                c.DrawCircle(cx - 3 * s, cy + 4 * s, 2.6f * s, fill);
                c.DrawLine(cx - 0.4f * s, cy + 4 * s, cx - 0.4f * s, cy - 5 * s, stroke);
                c.DrawLine(cx - 0.4f * s, cy - 5 * s, cx + 5 * s, cy - 6.5f * s, stroke);
                c.DrawLine(cx + 5 * s, cy - 6.5f * s, cx + 5 * s, cy + 2.5f * s, stroke);
                c.DrawCircle(cx + 2.6f * s, cy + 2.5f * s, 2.4f * s, fill);
                break;

            case MenuIcon.Lyrics:
            {
                var r = new SKRect(cx - size / 2f + 1 * s, cy - size / 2f + 3 * s,
                                   cx + size / 2f - 1 * s, cy + size / 2f - 3 * s);
                c.DrawRoundRect(r, 3 * s, 3 * s, stroke);
                c.DrawLine(r.Left + 4 * s, cy - 2 * s, r.Right - 4 * s, cy - 2 * s, stroke);
                c.DrawLine(r.Left + 4 * s, cy + 2 * s, r.Right - 7 * s, cy + 2 * s, stroke);
                break;
            }

            case MenuIcon.Output:
            {
                using var p = new SKPath();
                p.MoveTo(cx - 7 * s, cy - 2 * s); p.LineTo(cx - 3 * s, cy - 2 * s);
                p.LineTo(cx + 2 * s, cy - 7 * s); p.LineTo(cx + 2 * s, cy + 7 * s);
                p.LineTo(cx - 3 * s, cy + 2 * s); p.LineTo(cx - 7 * s, cy + 2 * s); p.Close();
                c.DrawPath(p, fill);
                c.DrawArc(new SKRect(cx + 0 * s, cy - 5 * s, cx + 12 * s, cy + 5 * s), -50, 100, false, stroke);
                break;
            }

            case MenuIcon.Startup:
            {
                using var p = new SKPath();
                p.MoveTo(cx + 1 * s, cy - 8 * s); p.LineTo(cx - 6 * s, cy + 1 * s);
                p.LineTo(cx - 1 * s, cy + 1 * s); p.LineTo(cx - 2 * s, cy + 8 * s);
                p.LineTo(cx + 6 * s, cy - 2 * s); p.LineTo(cx + 1 * s, cy - 2 * s); p.Close();
                c.DrawPath(p, fill);
                break;
            }

            case MenuIcon.Hide:
            {
                using var p = new SKPath();
                p.MoveTo(cx - 8 * s, cy);
                p.QuadTo(cx, cy - 8 * s, cx + 8 * s, cy);
                p.QuadTo(cx, cy + 8 * s, cx - 8 * s, cy);
                p.Close();
                c.DrawPath(p, stroke);
                c.DrawCircle(cx, cy, 2 * s, fill);
                c.DrawLine(cx - 7 * s, cy + 7 * s, cx + 7 * s, cy - 7 * s, stroke);
                break;
            }

            case MenuIcon.Sync:
            {
                c.DrawArc(new SKRect(cx - 6 * s, cy - 6 * s, cx + 6 * s, cy + 6 * s), 40, 250, false, stroke);
                using var p = new SKPath();
                p.MoveTo(cx + 3 * s, cy - 8 * s); p.LineTo(cx + 8 * s, cy - 4 * s); p.LineTo(cx + 3 * s, cy - 1 * s);
                p.Close();
                c.DrawPath(p, fill);
                break;
            }

            case MenuIcon.Hotkeys:
            {
                c.DrawRoundRect(new SKRect(cx - 8 * s, cy - 5 * s, cx + 8 * s, cy + 5 * s), 2 * s, 2 * s, stroke);
                for (int k = 0; k < 3; k++)
                    c.DrawRoundRect(new SKRect(cx - 6 * s + k * 4 * s, cy - 2.5f * s, cx - 4.5f * s + k * 4 * s, cy - 1 * s), 0.6f * s, 0.6f * s, fill);
                c.DrawRoundRect(new SKRect(cx - 4 * s, cy + 1 * s, cx + 4 * s, cy + 2.5f * s), 0.6f * s, 0.6f * s, fill);
                break;
            }

            case MenuIcon.Collapse:
                c.DrawLine(cx - 6 * s, cy + 2 * s, cx, cy - 4 * s, stroke);
                c.DrawLine(cx, cy - 4 * s, cx + 6 * s, cy + 2 * s, stroke);
                c.DrawLine(cx - 6 * s, cy + 7 * s, cx + 6 * s, cy + 7 * s, stroke);
                break;

            case MenuIcon.Settings:
            {
                c.DrawCircle(cx, cy, 3 * s, stroke);
                for (int k = 0; k < 6; k++)
                {
                    double ang = k * Math.PI / 3.0;
                    float dx = (float)Math.Cos(ang), dy = (float)Math.Sin(ang);
                    c.DrawLine(cx + dx * 3.4f * s, cy + dy * 3.4f * s,
                               cx + dx * 6.6f * s, cy + dy * 6.6f * s, stroke);
                }
                break;
            }

            case MenuIcon.Exit:
                c.DrawArc(new SKRect(cx - 6 * s, cy - 6 * s, cx + 6 * s, cy + 7 * s), -40, 260, false, stroke);
                c.DrawLine(cx, cy - 8 * s, cx, cy, stroke);
                break;

            default:
                c.DrawCircle(cx, cy, 1.6f * s, fill);
                break;
        }
    }

    private static string VersionLabel()
    {
        var v = typeof(IslandRenderer).Assembly.GetName().Version;
        return v == null ? "v2.1" : $"v{v.Major}.{v.Minor}";
    }

    public void Dispose()
    {
        foreach (var p in _paintCache.Values) p.Dispose();
        foreach (var p in _glowCache.Values) p.Dispose();
        foreach (var p in _coverSheenCache.Values) p.Dispose();
        _bodyFill?.Dispose(); _bodySheen?.Dispose(); _bodyEdge?.Dispose(); _bodyTopEdge?.Dispose();
        _bold.Dispose(); _semi.Dispose(); _med.Dispose();
    }
}
