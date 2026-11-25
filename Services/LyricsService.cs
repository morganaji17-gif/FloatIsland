using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FloatIsland.Models;

namespace FloatIsland.Services;

public enum LyricsKind { None, Synced, Plain, Instrumental }

public sealed record LrcLine(TimeSpan Time, string Text);

public sealed record LyricsQuery(string? Title, string? Artist, string? Album, TimeSpan Duration);

public sealed record LyricsResult(LyricsKind Kind, IReadOnlyList<LrcLine>? Lines, string? Plain)
{
    public static readonly LyricsResult NotFound = new(LyricsKind.None, null, null);
    public static readonly LyricsResult Instrumental = new(LyricsKind.Instrumental, null, null);
}

/// <summary>
/// A source of lyrics. Implementations must only use legitimate/authorised
/// sources. The abstraction exists so a properly licensed provider (Musixmatch,
/// LyricFind, …) can be dropped in without touching the UI.
/// </summary>
public interface ILyricsProvider
{
    string Name { get; }
    Task<LyricsResult?> GetAsync(LyricsQuery query, CancellationToken ct);
}

/// <summary>
/// Orchestrates the configured lyrics providers. Never fabricates lyrics: when a
/// source is unavailable the caller gets <see cref="LyricsResult.NotFound"/>.
/// </summary>
public sealed class LyricsService : IDisposable
{
    public async Task<LyricsResult> GetAsync(LyricsQuery query, AppSettings settings, CancellationToken ct)
    {
        try
        {
            foreach (var provider in BuildChain(settings))
            {
                ct.ThrowIfCancellationRequested();
                var result = await provider.GetAsync(query, ct);
                if (result is { Kind: LyricsKind.Synced or LyricsKind.Plain or LyricsKind.Instrumental })
                {
                    Log.Info($"Lyrics: '{query.Title}' via {provider.Name} → {result.Kind}");
                    return result;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Lyrics: {ex.Message}");
        }
        return LyricsResult.NotFound;
    }

    private static IEnumerable<ILyricsProvider> BuildChain(AppSettings s)
    {
        switch (s.LyricsSource)
        {
            case LyricsSource.None:
                yield break;
            case LyricsSource.Local:
                yield return new LocalLrcProvider(s.LyricsFolder);
                yield break;
            case LyricsSource.Lrclib:
                yield return new LrclibProvider();
                yield break;
            default: // Auto — prefer the user's own files, then LRCLIB
                if (!string.IsNullOrWhiteSpace(s.LyricsFolder))
                    yield return new LocalLrcProvider(s.LyricsFolder);
                yield return new LrclibProvider();
                yield break;
        }
    }

    public void Dispose() { }
}

/// <summary>Free, no-API-key community lyrics database (https://lrclib.net).</summary>
public sealed class LrclibProvider : ILyricsProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // LRCLIB is community data: some rows carry null (or string) numerics, so every
    // numeric field must be nullable or one malformed row kills the whole response.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    public string Name => "LRCLIB";

    public async Task<LyricsResult?> GetAsync(LyricsQuery q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q.Title)) return null;

        var query = Uri.EscapeDataString($"{q.Title} {q.Artist}".Trim());
        var json = await Http.GetStringAsync($"https://lrclib.net/api/search?q={query}", ct);
        var candidates = JsonSerializer.Deserialize<List<LrcTrack>>(json, JsonOpts);
        if (candidates == null || candidates.Count == 0) return null;

        // Run time alone is not a fit test. LRCLIB carries community rows whose declared
        // duration matches the studio cut while their timestamps are keyed to a different
        // edit - the official video, an extended mix - so the first line lands on the vocal
        // and every line after it drifts. That is exactly "in sync at the start, slides off
        // later". Rank on run time *and* on where the sheet's own last timestamp falls.
        double target = q.Duration.TotalSeconds;
        LrcTrack? best = null;

        if (target > 0)
        {
            best = candidates
                .Where(c => (c.Duration ?? 0) > 0 && Math.Abs((c.Duration ?? 0) - target) <= 6)
                .OrderBy(c => FitScore(c.Duration, c.SyncedLyrics, target, c.Instrumental))
                .FirstOrDefault();
        }
        if (best == null && !string.IsNullOrWhiteSpace(q.Artist))
        {
            best = candidates
                .Where(c => c.ArtistName != null &&
                            c.ArtistName.Contains(q.Artist!, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => target > 0 ? FitScore(c.Duration, c.SyncedLyrics, target, c.Instrumental) : 0)
                .FirstOrDefault();
        }
        if (best == null)
            best = target > 0
                ? candidates.OrderBy(c => FitScore(c.Duration, c.SyncedLyrics, target, c.Instrumental)).First()
                : candidates[0];

        var detail = best.Id is > 0 ? await GetAsync(best.Id!.Value, ct) ?? best : best;
        if (detail.Instrumental) return LyricsResult.Instrumental;

        var synced = ParseLrc(detail.SyncedLyrics);
        if (synced.Count > 0)
        {
            double last = synced[^1].Time.TotalSeconds;
            // Last resort: every candidate available is timed for a longer edit. Stretching
            // the sheet onto the real run time is wrong in detail but tracks the vocal, where
            // leaving it alone is a guaranteed, growing offset.
            if (target > 0 && last > target * 1.08 && last > target + 6)
            {
                double k = target / last;
                synced = Stretch(synced, k);
                Log.Info($"LRCLIB: only sheet available ran to {last:F1}s on a {target:F1}s track " +
                         $"- stretched by {k:F3}x");
            }
            Log.Info($"LRCLIB: pick id={detail.Id} dur={(detail.Duration ?? 0):F0}s lines={synced.Count} " +
                     $"first={synced[0].Time:hh\\:mm\\:ss\\.ff} last={synced[^1].Time:hh\\:mm\\:ss\\.ff} " +
                     $"target={target:F1}s instrumental={detail.Instrumental}");
            return new LyricsResult(LyricsKind.Synced, synced, detail.PlainLyrics);
        }
        if (!string.IsNullOrWhiteSpace(detail.PlainLyrics))
            return new LyricsResult(LyricsKind.Plain, null, detail.PlainLyrics);
        return null;
    }

    /// <summary>How badly a search hit fits the playing track - lower is better. First term is
    /// the declared-duration error; second is how far the sheet's last timestamp overshoots
    /// the track. A sheet that simply ends early (outro, instrumental tail) is barely
    /// penalised; a sheet that keeps going past the end is what a lyrics sheet for a longer
    /// edit looks like, and playing it guarantees drift. Karaoke/instrumental rows carry a
    /// small constant penalty so a vocal sheet at roughly the same fit is preferred, while a
    /// genuinely instrumental track still resolves to its own row.</summary>
    internal static double FitScore(double? durationSec, string? syncedLyrics, double targetSec,
                                    bool instrumental = false)
    {
        double score = Math.Abs((durationSec ?? 0) - targetSec);
        if (instrumental) score += 8;

        var lines = ParseLrc(syncedLyrics);
        if (lines.Count < 2) return score;

        double over = lines[^1].Time.TotalSeconds - targetSec;
        if (over > 6) score += over * 3;                    // runs past the audio: wrong edit
        else if (over < -45) score += (-over - 45) * 0.25;  // stops far too early: probably too
        return score;
    }

    /// <summary>Scales every timestamp by <paramref name="k"/>. Used only when no correctly
    /// timed sheet exists for the track.</summary>
    internal static List<LrcLine> Stretch(List<LrcLine> lines, double k) =>
        lines.Select(l => l with { Time = TimeSpan.FromMilliseconds(l.Time.TotalMilliseconds * k) })
             .ToList();

    private static async Task<LrcTrack?> GetAsync(int id, CancellationToken ct)
    {
        try
        {
            var json = await Http.GetStringAsync($"https://lrclib.net/api/get/{id}", ct);
            return JsonSerializer.Deserialize<LrcTrack>(json, JsonOpts);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Debug($"LRCLIB get: {ex.Message}"); return null; }
    }

    private sealed class LrcTrack
    {
        [JsonPropertyName("id")] public int? Id { get; set; }
        [JsonPropertyName("trackName")] public string? TrackName { get; set; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
        [JsonPropertyName("instrumental")] public bool Instrumental { get; set; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
    }

    internal static List<LrcLine> ParseLrc(string? lrc)
    {
        var result = new List<LrcLine>();
        if (string.IsNullOrWhiteSpace(lrc)) return result;

        var rx = new Regex(@"\[(\d{1,2}):(\d{2})(?:[.:](\d{1,3}))?\]");
        foreach (var raw in lrc.Split('\n'))
        {
            var matches = rx.Matches(raw);
            if (matches.Count == 0) continue;
            var text = rx.Replace(raw, string.Empty).Trim();
            if (text.Length == 0) continue;

            foreach (Match m in matches)
            {
                int min = int.Parse(m.Groups[1].Value);
                int sec = int.Parse(m.Groups[2].Value);
                int ms = 0;
                if (m.Groups[3].Success)
                {
                    var frac = m.Groups[3].Value;
                    ms = frac.Length switch { 1 => int.Parse(frac) * 100, 2 => int.Parse(frac) * 10, _ => int.Parse(frac) };
                }
                result.Add(new LrcLine(new TimeSpan(0, 0, min, sec, ms), text));
            }
        }
        return result.OrderBy(l => l.Time).ToList();
    }
}

/// <summary>User-supplied .lrc / .txt files from a local folder.</summary>
public sealed class LocalLrcProvider : ILyricsProvider
{
    private readonly string _folder;
    public LocalLrcProvider(string? folder) => _folder = folder ?? string.Empty;
    public string Name => "Local";

    public Task<LyricsResult?> GetAsync(LyricsQuery q, CancellationToken ct)
    {
        return Task.Run<LyricsResult?>(() =>
        {
            if (string.IsNullOrWhiteSpace(_folder) || !Directory.Exists(_folder) || string.IsNullOrWhiteSpace(q.Title))
                return null;

            string[] files;
            try { files = Directory.GetFiles(_folder, "*.lrc", SearchOption.TopDirectoryOnly); }
            catch { return null; }

            string title = Sanitize(q.Title!);
            string artist = Sanitize(q.Artist ?? "");

            string? bestLrc = null; int bestScore = 0;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var name = Sanitize(Path.GetFileNameWithoutExtension(file));
                if (!name.Contains(title, StringComparison.OrdinalIgnoreCase)) continue;
                int score = 1 + (artist.Length > 0 && name.Contains(artist, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
                if (score > bestScore) { bestScore = score; bestLrc = file; }
            }

            if (bestLrc != null)
            {
                var lines = LrclibProvider.ParseLrc(File.ReadAllText(bestLrc));
                if (lines.Count > 0) return new LyricsResult(LyricsKind.Synced, lines, null);
            }

            // Plain .txt fallback
            try
            {
                foreach (var file in Directory.GetFiles(_folder, "*.txt", SearchOption.TopDirectoryOnly))
                {
                    var name = Sanitize(Path.GetFileNameWithoutExtension(file));
                    if (!name.Contains(title, StringComparison.OrdinalIgnoreCase)) continue;
                    var text = File.ReadAllText(file).Trim();
                    if (text.Length > 0) return new LyricsResult(LyricsKind.Plain, null, text);
                }
            }
            catch { }

            return null;
        }, ct);
    }

    private static string Sanitize(string s)
    {
        var chars = s.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray();
        return new string(chars).Trim();
    }
}
