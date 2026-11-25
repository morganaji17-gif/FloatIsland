using System.Text.RegularExpressions;
using FloatIsland.Models;

namespace FloatIsland.Services;

/// <summary>
/// Dependency-free assertions over the pure logic (LRC parsing, Spotify session matching).
/// Run with <c>--selftest</c>; exits non-zero on failure so CI can gate on it.
/// </summary>
internal static class SelfTest
{
    private static int _fail;

    public static int Run()
    {
        Log.Info("=== self-test ===");

        // ---- LRC parsing ---------------------------------------------------
        var l1 = LrclibProvider.ParseLrc("[00:12.34] Hello\n[00:15.00] World\n");
        Eq(l1.Count, 2, "lrc: line count");
        Eq(l1[0].Text, "Hello", "lrc: first text");
        Eq(Math.Round(l1[0].Time.TotalSeconds, 2), 12.34, "lrc: centiseconds");
        Eq(Math.Round(l1[1].Time.TotalSeconds, 2), 15.00, "lrc: seconds");

        var l2 = LrclibProvider.ParseLrc("[00:01.5] a\n[00:03.123] c");
        Eq(Math.Round(l2[0].Time.TotalSeconds, 2), 1.50, "lrc: tenths");
        Eq(Math.Round(l2[1].Time.TotalSeconds, 3), 3.123, "lrc: milliseconds");

        var l3 = LrclibProvider.ParseLrc("[00:01.00][00:05.00] hey");
        Eq(l3.Count, 2, "lrc: multi-timestamp expands");
        Eq(l3[1].Time.TotalSeconds, 5.0, "lrc: multi-timestamp time");

        var l4 = LrclibProvider.ParseLrc("[00:05.00] b\n[00:01.00] a");
        Eq(l4[0].Text, "a", "lrc: output sorted by time");

        Eq(LrclibProvider.ParseLrc("").Count, 0, "lrc: empty input");
        Eq(LrclibProvider.ParseLrc(null).Count, 0, "lrc: null input");
        Eq(LrclibProvider.ParseLrc("[00:01.00]   ").Count, 0, "lrc: blank text dropped");

        // ---- lyrics sheet selection ----------------------------------------
        // Two rows, identical declared run time, different edit: the sheet that runs past the
        // track must lose even though its duration looks like a perfect match. This is the
        // "starts in sync, drifts later" case (a video-timed sheet filed under the studio cut).
        const double track = 230.733;
        const string fits = "[00:07.19] first\n[03:45.16] last";     // ends 225.2 s
        const string video = "[00:57.03] first\n[04:47.61] last";    // ends 287.6 s
        Check(LrclibProvider.FitScore(231, fits, track) < 1, "lrc fit: matching sheet scores near zero");
        Check(LrclibProvider.FitScore(231, video, track) > 100, "lrc fit: longer-edit sheet penalised");
        Check(LrclibProvider.FitScore(231, video, track) > LrclibProvider.FitScore(231, fits, track),
              "lrc fit: fitting sheet wins a run-time tie");
        Check(LrclibProvider.FitScore(220, fits, track) > LrclibProvider.FitScore(231, fits, track),
              "lrc fit: declared duration still counts");
        Check(LrclibProvider.FitScore(231, fits, track, instrumental: true) >
              LrclibProvider.FitScore(231, fits, track),
              "lrc fit: vocal sheet preferred over a karaoke row");
        var stretchedSheet = LrclibProvider.Stretch(LrclibProvider.ParseLrc(video), track / 287.61);
        Eq(Math.Round(stretchedSheet[^1].Time.TotalSeconds, 1), Math.Round(track, 1),
           "lrc: stretched sheet ends on the track");

        // ---- Spotify session matching -------------------------------------
        Check(TrackInfo.IsSpotifyApp("Spotify.exe"), "spotify: Spotify.exe");
        Check(TrackInfo.IsSpotifyApp("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"), "spotify: package AUMID");
        Check(!TrackInfo.IsSpotifyApp("chrome.exe"), "spotify: reject chrome");
        Check(!TrackInfo.IsSpotifyApp(null), "spotify: reject null");

        Log.Info($"[selftest] {(_fail == 0 ? "ALL PASS" : "FAILURES: " + _fail)}");
        return _fail == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        Log.Info($"[selftest] {(ok ? "PASS" : "FAIL")}  {what}");
        if (!ok) _fail++;
    }

    private static void Eq<T>(T got, T want, string what) =>
        Check(EqualityComparer<T>.Default.Equals(got, want), $"{what} (got {got}, want {want})");
}
