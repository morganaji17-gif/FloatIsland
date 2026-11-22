using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using SonoIsland.Models;
using Windowish = global::Windows.Media.Control;

namespace SonoIsland.Services;

/// <summary>
/// Command-line diagnostics used to validate Sono Island against a real Spotify
/// session. None of this runs in normal operation — it only activates when the
/// executable is started with an explicit diagnostic argument. No audio is ever
/// recorded; artwork is decoded exactly as the app decodes it.
/// </summary>
internal static class Diagnostics
{
    private const string P = "[diag]";

    private static Windowish.GlobalSystemMediaTransportControlsSession? SpotifySession()
    {
        var mgr = Windowish.GlobalSystemMediaTransportControlsSessionManager
            .RequestAsync().AsTask().GetAwaiter().GetResult();
        if (mgr == null) return null;
        foreach (var s in mgr.GetSessions())
            if (TrackInfo.IsSpotifyApp(s.SourceAppUserModelId)) return s;
        return null;
    }

    private static float MaxBar(AudioLevels l)
    {
        float m = 0;
        foreach (var b in l.Bars) if (b > m) m = b;
        return m;
    }

    private static string Trunc(string? s, int n = 42) =>
        s == null ? "" : (s.Length <= n ? s : s[..n] + "…");

    // ────────────────────────────────────────────────────────────── TEST 4
    public static void CaptureSelfTest(string target, int seconds)
    {
        uint pid;
        if (string.Equals(target, "spotify", StringComparison.OrdinalIgnoreCase))
        {
            var found = ProcessLoopbackCapture.FindSpotifyProcessId();
            if (found == null) { Log.Error($"{P} no Spotify process found"); return; }
            pid = found.Value;
        }
        else if (uint.TryParse(target, out var parsed)) pid = parsed;
        else { Log.Error($"{P} bad target '{target}'"); return; }

        using var capture = new ProcessLoopbackCapture();
        var levels = new AudioLevels();
        capture.Failed += (_, reason) => Log.Error($"{P} FAILED: {reason}");

        using var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        bool started = capture.Start(pid);
        Log.Info($"{P} capture target='{target}' pid={pid} started={started} seconds={seconds}");
        if (!started) return;

        float bass = 0, mid = 0, treble = 0, rms = 0, bar = 0;
        int picked = 0;
        long nextReport = 0;

        while (sw.ElapsedMilliseconds < seconds * 1000L)
        {
            Thread.Sleep(100);
            capture.ReadSnapshot(levels);
            if (!levels.Active) continue;
            picked++;
            bass = Math.Max(bass, levels.Bass);
            mid = Math.Max(mid, levels.Mid);
            treble = Math.Max(treble, levels.Treble);
            rms = Math.Max(rms, levels.Rms);
            foreach (var b in levels.Bars) if (b > bar) bar = b;

            if (sw.ElapsedMilliseconds >= nextReport)
            {
                nextReport += 1000;
                Log.Info($"{P} t={sw.ElapsedMilliseconds / 1000}s rms={levels.Rms:F3} bass={levels.Bass:F3} " +
                         $"mid={levels.Mid:F3} treble={levels.Treble:F3} beat={levels.Beat:F2} " +
                         $"maxBar={MaxBar(levels):F3}");
            }
        }

        var cpu = proc.TotalProcessorTime - cpu0;
        capture.Stop();
        Log.Info($"{P} SUMMARY pid={pid} samples={picked} maxRms={rms:F3} bass={bass:F3} mid={mid:F3} " +
                 $"treble={treble:F3} maxBar={bar:F3} cpuMs={cpu.TotalMilliseconds:F0} " +
                 $"({cpu.TotalMilliseconds / sw.ElapsedMilliseconds * 100:F2}% of 1 core)");
    }

    public static void SmtcCommand(string command)
    {
        var s = SpotifySession();
        if (s == null) { Log.Info($"{P} smtc-cmd no Spotify session"); return; }

        var before = s.GetTimelineProperties()?.Position;
        bool ok = command switch
        {
            "play" => s.TryPlayAsync().AsTask().GetAwaiter().GetResult(),
            "pause" => s.TryPauseAsync().AsTask().GetAwaiter().GetResult(),
            "next" => s.TrySkipNextAsync().AsTask().GetAwaiter().GetResult(),
            "prev" => s.TrySkipPreviousAsync().AsTask().GetAwaiter().GetResult(),
            // ticks (100 ns), same unit the app now uses
            "seek" => s.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(75).Ticks)
                       .AsTask().GetAwaiter().GetResult(),
            _ => false
        };
        Thread.Sleep(2500);
        var after = s.GetTimelineProperties()?.Position;
        Log.Info($"{P} smtc-cmd {command} -> {ok}  position {before} => {after}");
    }

    // ────────────────────────────────────────────────────────────── TEST 1
    public static void SmtcProbe(int seconds)
    {
        var mgr = Windowish.GlobalSystemMediaTransportControlsSessionManager
            .RequestAsync().AsTask().GetAwaiter().GetResult();
        if (mgr == null) { Log.Info($"{P} smtc-probe manager == null"); return; }

        var sessions = mgr.GetSessions();
        Log.Info($"{P} smtc-probe sessions={sessions.Count}");
        Windowish.GlobalSystemMediaTransportControlsSession? spotify = null;
        foreach (var s in sessions)
        {
            var id = s.SourceAppUserModelId ?? "<null>";
            var pb = s.GetPlaybackInfo();
            bool claimed = TrackInfo.IsSpotifyApp(id);
            Log.Info($"{P}   id='{id}' claimed={claimed} status={pb?.PlaybackStatus}");
            if (claimed) spotify ??= s;
        }
        if (spotify == null) { Log.Info($"{P} no Spotify session"); return; }

        try
        {
            var p = spotify.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
            Log.Info($"{P} title='{p?.Title}' artist='{p?.Artist}' album='{p?.AlbumTitle}' " +
                     $"hasThumbnail={p?.Thumbnail != null}");
            var tl = spotify.GetTimelineProperties();
            Log.Info($"{P} position={tl?.Position} duration={(tl?.EndTime - tl?.StartTime)}");
        }
        catch (Exception ex) { Log.Error($"{P} {ex.Message}"); }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < seconds * 1000L)
        {
            Thread.Sleep(1500);
            var tl = spotify.GetTimelineProperties();
            Log.Info($"{P} t={sw.ElapsedMilliseconds / 1000}s status={spotify.GetPlaybackInfo()?.PlaybackStatus} " +
                     $"pos={tl?.Position}");
        }
    }

    // ────────────────────────────────────────────────────────────── TEST 2
    public static void ArtworkProbe(int rounds, int gapSeconds)
    {
        using var cache = new ArtworkCache();
        for (int r = 0; r < rounds; r++)
        {
            var s = SpotifySession();
            if (s == null) { Log.Info($"{P} art no Spotify session"); return; }

            var p = s.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
            var key = $"{s.SourceAppUserModelId}|{p?.Title}|{p?.Artist}";
            var sw = Stopwatch.StartNew();
            var bmp = cache.LoadAsync(p?.Thumbnail, key).GetAwaiter().GetResult();
            sw.Stop();
            Log.Info($"{P} art round={r} title='{Trunc(p?.Title, 34)}' artist='{Trunc(p?.Artist, 24)}' " +
                     $"streamRef={(p?.Thumbnail != null)} decoded={(bmp != null)} " +
                     $"px={(bmp != null ? $"{bmp.PixelWidth}x{bmp.PixelHeight}" : "-")} sha1={HashBitmap(bmp)} " +
                     $"decodeMs={sw.ElapsedMilliseconds}");

            var again = cache.LoadAsync(p?.Thumbnail, key).GetAwaiter().GetResult();
            Log.Info($"{P} art round={r} cacheHit={ReferenceEquals(bmp, again)} (same instance = served from cache)");

            var none = cache.LoadAsync(null, null).GetAwaiter().GetResult();
            Log.Info($"{P} art round={r} missingArtworkFallback={(none == null ? "null -> placeholder shown" : "UNEXPECTED image")}");

            if (r + 1 < rounds)
            {
                s.TrySkipNextAsync().AsTask().GetAwaiter().GetResult();
                Log.Info($"{P} art skipping to next track…");
                Thread.Sleep(gapSeconds * 1000);
            }
        }
    }

    private static string HashBitmap(BitmapImage? b)
    {
        if (b == null) return "-";
        try
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(b));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return Convert.ToHexString(MD5.HashData(ms.ToArray()))[..12].ToLowerInvariant();
        }
        catch (Exception ex) { return "err:" + ex.GetType().Name; }
    }

    // ────────────────────────────────────────────────────────────── TEST 3
    public static void LyricsProbe(string mode)
    {
        string? title = null, artist = null, album = null;
        TimeSpan pos = TimeSpan.Zero, dur = TimeSpan.Zero;

        if (mode.Contains('|'))
        {
            var bits = mode.Split('|');
            title = bits[0];
            artist = bits.Length > 1 ? bits[1] : null;
            album = bits.Length > 2 ? bits[2] : null;
            mode = "all";
            Log.Info($"{P} lyrics manual query title='{title}' artist='{artist}'");
        }
        else
        {
            var s = SpotifySession();
            if (s == null) { Log.Info($"{P} lyrics no Spotify session"); return; }
            var p = s.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
            title = p?.Title; artist = p?.Artist; album = p?.AlbumTitle;
            var tl = s.GetTimelineProperties();
            pos = tl?.Position ?? TimeSpan.Zero;
            dur = (tl?.EndTime - tl?.StartTime) ?? TimeSpan.Zero;
        }

        Log.Info($"{P} lyrics track='{title}' artist='{artist}' album='{album}' pos={pos} dur={dur}");

        using var svc = new LyricsService();
        var q = new LyricsQuery(title, artist, album, dur);

        // ---- provider: LRCLIB
        var sw = Stopwatch.StartNew();
        var lrclib = svc.GetAsync(q, new AppSettings { LyricsSource = LyricsSource.Lrclib },
                                  CancellationToken.None).GetAwaiter().GetResult();
        sw.Stop();
        Log.Info($"{P} lyrics LRCLIB kind={lrclib.Kind} lines={lrclib.Lines?.Count ?? 0} " +
                 $"plainLen={lrclib.Plain?.Length ?? 0} ms={sw.ElapsedMilliseconds}");

        if (lrclib.Lines is { Count: > 0 } lines)
        {
            bool monotonic = true;
            for (int i = 1; i < lines.Count; i++) if (lines[i].Time < lines[i - 1].Time) monotonic = false;
            Log.Info($"{P} lyrics first=[{lines[0].Time}] '{Trunc(lines[0].Text)}'");
            Log.Info($"{P} lyrics last =[{lines[^1].Time}] '{Trunc(lines[^1].Text)}'");
            Log.Info($"{P} lyrics timestampsMonotonic={monotonic}");

            int idx = -1;
            for (int i = lines.Count - 1; i >= 0; i--) if (lines[i].Time <= pos) { idx = i; break; }
            Log.Info($"{P} lyrics activeIndex@{pos}={idx} prev='{(idx > 0 ? Trunc(lines[idx - 1].Text, 28) : "-")}' " +
                     $"cur='{(idx >= 0 ? Trunc(lines[idx].Text, 28) : "-")}' " +
                     $"next='{(idx + 1 < lines.Count ? Trunc(lines[idx + 1].Text, 28) : "-")}'");
        }

        if (mode == "local")
        {
            var folder = Path.Combine(Path.GetTempPath(), "sono_lyrics_probe");
            var plainFolder = Path.Combine(Path.GetTempPath(), "sono_lyrics_probe_plain");
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(plainFolder);

            var baseName = $"{Clean(artist)} - {Clean(title)}";
            var lrc = Path.Combine(folder, baseName + ".lrc");
            File.WriteAllText(lrc,
                "[00:00.00] LOCAL LINE ONE\n[00:10.00] LOCAL LINE TWO\n[00:20.00] LOCAL LINE THREE\n");
            var txt = Path.Combine(plainFolder, baseName + ".txt");
            File.WriteAllText(txt, "PLAIN UNSYNCHRONISED LINE A\nPLAIN UNSYNCHRONISED LINE B\n");
            Log.Info($"{P} lyrics probe files: '{lrc}' / '{txt}'");

            var localOnly = svc.GetAsync(q, new AppSettings
            { LyricsSource = LyricsSource.Local, LyricsFolder = folder },
                CancellationToken.None).GetAwaiter().GetResult();
            Log.Info($"{P} lyrics LOCAL(.lrc) kind={localOnly.Kind} lines={localOnly.Lines?.Count ?? 0} " +
                     $"first='{(localOnly.Lines is { Count: > 0 } ll ? Trunc(ll[0].Text) : "-")}'");

            var auto = svc.GetAsync(q, new AppSettings
            { LyricsSource = LyricsSource.Auto, LyricsFolder = folder },
                CancellationToken.None).GetAwaiter().GetResult();
            Log.Info($"{P} lyrics AUTO(local first) kind={auto.Kind} " +
                     $"first='{(auto.Lines is { Count: > 0 } al ? Trunc(al[0].Text) : "-")}'");

            var plain = svc.GetAsync(q, new AppSettings
            { LyricsSource = LyricsSource.Local, LyricsFolder = plainFolder },
                CancellationToken.None).GetAwaiter().GetResult();
            Log.Info($"{P} lyrics LOCAL(.txt only) kind={plain.Kind} plainLen={plain.Plain?.Length ?? 0} " +
                     $"(Plain == unsynchronised path)");
        }

        var bogus = svc.GetAsync(new LyricsQuery("zzzz-not-a-real-track-9182736", "zzzz-nobody", null, TimeSpan.Zero),
                                 new AppSettings { LyricsSource = LyricsSource.Lrclib },
                                 CancellationToken.None).GetAwaiter().GetResult();
        Log.Info($"{P} lyrics bogusTrack kind={bogus.Kind} (None == 'No lyrics found' fallback)");
    }

    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "unknown";
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray();
        return new string(chars).Trim().Replace("  ", " ");
    }
}
