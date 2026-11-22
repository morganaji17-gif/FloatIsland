using System.IO;
using SkiaSharp;

namespace FloatIsland.Services;

/// <summary>
/// Draws the application icon with Skia and writes a multi-size .ico (PNG entries, which
/// Windows Vista+ accepts). No external image asset is involved.
/// Run with <c>--make-icon</c> after changing the artwork.
/// </summary>
internal static class IconMaker
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    public static void MakeIcon(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var images = new List<(int size, byte[] png)>();
        foreach (int size in Sizes)
        {
            using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
            Draw(surface.Canvas, size);
            using var img = surface.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            images.Add((size, data.ToArray()));
        }

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0);                 // reserved
        w.Write((ushort)1);                 // type: icon
        w.Write((ushort)images.Count);

        int offset = 6 + images.Count * 16;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));   // 0 means 256
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);               // palette
            w.Write((byte)0);               // reserved
            w.Write((ushort)1);             // planes
            w.Write((ushort)32);            // bpp
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);

        Log.Info($"[icon] wrote {path} ({images.Count} sizes)");
    }

    private static void Draw(SKCanvas c, int size)
    {
        float s = size / 100f;               // work in 100x100 units
        c.Clear(SKColors.Transparent);

        // The tile: a black squircle
        var tile = new SKRect(4 * s, 4 * s, 96 * s, 96 * s);
        using (var rr = new SKRoundRect(tile, 24 * s))
        {
            using var fill = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 4 * s), new SKPoint(0, 96 * s),
                    new[] { new SKColor(0x14, 0x16, 0x1A), new SKColor(0x05, 0x05, 0x07) },
                    new[] { 0f, 1f }, SKShaderTileMode.Clamp)
            };
            c.DrawRoundRect(rr, fill);

            using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * s, Color = new SKColor(255, 255, 255, 40) };
            c.DrawRoundRect(rr, edge);
        }

        // The notch: a white pill attached to the top edge
        float pw = 46 * s, ph = 15 * s, px = 50 * s - pw / 2, py = 4 * s;
        var pill = new SKRect(px, py, px + pw, py + ph);
        using (var rr = new SKRoundRect(pill, ph / 2))
        using (var white = new SKPaint { IsAntialias = true, Color = new SKColor(0xF2, 0xF3, 0xF5) })
            c.DrawRoundRect(rr, white);

        // Equaliser bars inside the pill (dark, so they read against the white)
        using (var bar = new SKPaint { IsAntialias = true, Color = new SKColor(0x0B, 0x0C, 0x0E) })
        {
            float bw = 3.2f * s, gap = 3.4f * s;
            float cx = 50 * s - (bw * 3 + gap * 2) / 2;
            float[] hs = { 5.4f, 8.2f, 4.0f };
            for (int i = 0; i < 3; i++)
            {
                float h = hs[i] * s;
                float bx = cx + i * (bw + gap);
                float by = py + ph / 2 - h / 2;
                c.DrawRoundRect(new SKRect(bx, by, bx + bw, by + h), bw / 2, bw / 2, bar);
            }
        }

        // A lyric line hint below: three rounded bars of decreasing width
        using (var line = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 205) })
        {
            float[] w = { 56f, 40f, 48f };
            float y = 40 * s;
            for (int i = 0; i < 3; i++)
            {
                float lh = 6.5f * s;
                c.DrawRoundRect(new SKRect(22 * s, y, 22 * s + w[i] * s, y + lh), lh / 2, lh / 2, line);
                y += 13.5f * s;
            }
        }

        // A faint album disc at the bottom right
        using (var disc = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 60) })
            c.DrawCircle(74 * s, 82 * s, 9 * s, disc);
    }
}
