using SkiaSharp;
using FloatIsland.Models;
using FloatIsland.Services;
using System.IO;
using Windows.Storage.Streams;

namespace FloatIsland.Overlay;

/// <summary>
/// The <c>--skia</c> overlay: a raw Win32 layered window drawn with Skia, plus the island
/// state machine (collapsed / MUSIC / LYRICS / MENU), SMTC transport, artwork, lyrics,
/// audio bars, hover, auto-collapse, the karaoke sweep, the album wash, the title marquee
/// and a spring-morph for the body geometry (smooth, dt-based).
/// </summary>
internal static class SkiaMode
{
    private static readonly object Gate = new();

    private static Surface _surface = Surface.Collapsed;
    private static Surface _beforeMenu = Surface.Collapsed;
    private static string _title = "", _artist = "";
    private static SKImage? _cover;
    /// <summary>Covers replaced since the last painted frame. They are only disposed on the
    /// frame thread, after the frame that could still reference them has been drawn - a
    /// Dispose from the SMTC callback thread used to pull the pixels out from under
    /// SKImage.ReadPixels and take the process down with an access violation.</summary>
    private static readonly List<SKImage> Retired = new();
    /// <summary>Serialises refreshes: PlaybackChanged, SessionChanged and MediaChanged can all
    /// fire at once, and two overlapping thumbnail swaps are what freed a cover mid-read.</summary>
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static bool _playing, _hasTrack, _hasLyrics;
    private static double _progress;
    private static string _elapsed = "--:--", _duration = "--:--";
    private static readonly List<string> Lines = new();
    private static readonly List<TimeSpan> Times = new();
    private static int _index = -1;
    private static float _scroll, _scrollVel;
    private static float _lyricsReveal, _lineFade = 1f;
    private static int _lastIndex = -1;
    private static TimeSpan _pos, _durationTs;
    private static long _posStamp;
    private static string? _trackKey;

    private static readonly AudioLevels Levels = new();
    private static ProcessLoopbackCapture? _capture;
    private static uint _capturePid;
    private static SKImage? _logo;

    private static OverlayWindow _window = null!;
    private static IslandLayout _hit = new();
    private static IslandRenderer _renderer = null!;
    private static readonly LyricsService Lyrics = new();
    private static readonly SettingsManager Settings = new();
    private static readonly SMTCManager Smtc = new();

    // animated body metrics (px) + velocities
    private static float _mw, _mh, _mr, _mf;
    private static float _vw, _vh, _vr, _vf;
    private static long _lastTick;

    // input / hover / auto-collapse
    private static float _mouseX, _mouseY;
    private static bool _mouseInside;
    private static bool _shown;
    private static float _visAlpha, _visScale = 1f;
    private static long _forceShowUntil;
    private static long _leftAt;
    private static long _lastMonCheck;
    private static int _hover = -1, _menuHover = -1;

    // lyric timing nudge + fullscreen hide
    private static int _lyricsOffsetMs;
    private static bool _fullscreen;
    private static long _lastFsCheck;
    private static long _lastPosSync;

    // album wash
    private static SKColor _tint = new(8, 9, 11);
    private static bool _hasTint;
    private static float _tintAmt, _vtint;
    private static float _washAlpha;
    private static SKColor _washL, _washM, _washR;

    private const uint TimerMs = 16;
    private const int WinW = 720, WinH = 240;
    private const int CollapseAfterMs = 1500;
    private const int VolumeHoldMs = 1600;

    private static readonly string[] Menu = new string[11];
    private static int _monCount = 1;
    private static float _vol;
    private static bool _volMuted;
    private static long _volumeUntil;

    private static readonly StartupManager Startup = new();

    private static string MenuOutput() =>
        string.IsNullOrWhiteSpace(AudioOutput.DeviceName) ? "Output" : "Output: " + AudioOutput.DeviceName;
    private static string MenuStartup() => "Launch at startup: " + (Startup.IsEnabled ? "On" : "Off");
    private static string MenuHide() => "Hide when stopped: " + (Settings.Current.HideWhenMusicStops ? "On" : "Off");
    private static string MenuFullscreen() => "Hide when fullscreen: " + (Settings.Current.HideWhenFullscreen ? "On" : "Off");
    private static string MenuHotkeys() => "Media hotkeys: " + (Settings.Current.MediaHotkeys ? "On" : "Off");

    private static void RebuildMenu()
    {
        Menu[0] = "Music";
        Menu[1] = "Lyrics";
        Menu[2] = "Lyrics sync";            // special row: -  value  +
        Menu[3] = MenuOutput();
        Menu[4] = MenuStartup();
        Menu[5] = MenuHide();
        Menu[6] = MenuFullscreen();         // get out of the way of games / fullscreen video
        Menu[7] = MenuHotkeys();
        Menu[8] = "Settings";
        Menu[9] = "Collapse";
        Menu[10] = "Exit";
    }

    /// <summary>Ctrl+Alt+Space / Left / Right drive Spotify through SMTC, with a short peek
    /// so the keypress has visible feedback even when playback is paused.</summary>
    private static void OnHotkey(int id)
    {
        _ = id switch
        {
            OverlayWindow.HkPlayPause => Smtc.TogglePlayPauseAsync(),
            OverlayWindow.HkNext => Smtc.NextAsync(),
            OverlayWindow.HkPrev => Smtc.PreviousAsync(),
            _ => Task.CompletedTask,
        };
        Log.Info($"[skia] hotkey {id} -> smtc");
        _forceShowUntil = Environment.TickCount64 + 2500;
        if (_surface != Surface.Menu) SetSurface(Surface.Collapsed);
    }

    private static void ApplyHotkeys()
    {
        OverlayWindow.UnregisterHotkeys(_window.Handle);
        if (!Settings.Current.MediaHotkeys)
        {
            Log.Info("[skia] media hotkeys disabled");
            return;
        }
        uint mods = OverlayWindow.MOD_CONTROL | OverlayWindow.MOD_ALT | OverlayWindow.MOD_NOREPEAT;
        bool play = _window.RegisterHotkey(OverlayWindow.HkPlayPause, mods, OverlayWindow.VK_SPACE);
        bool next = _window.RegisterHotkey(OverlayWindow.HkNext, mods, OverlayWindow.VK_RIGHT);
        bool prev = _window.RegisterHotkey(OverlayWindow.HkPrev, mods, OverlayWindow.VK_LEFT);
        Log.Info($"[skia] media hotkeys Ctrl+Alt+Space={play} Right={next} Left={prev}" +
                 (play || next || prev ? "" : " (all failed - already taken?)"));
    }

    public static void Run()
    {
        _window = new OverlayWindow(WinW, WinH);
        _renderer = new IslandRenderer(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts"));

        float s = (float)_window.Scale;
        _mw = _renderer.CollapsedWidthPx("", s); _mh = 36 * s; _mr = 18 * s; _mf = 16 * s;

        try
        {
            var lp = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
            if (File.Exists(lp)) _logo = SKImage.FromEncodedData(File.ReadAllBytes(lp));
        }
        catch (Exception ex) { Log.Warn("[skia] logo: " + ex.Message); }

        _window.OnFrame = Frame;
        _window.OnLeftClick = Click;
        _window.OnRightClick = () => { _beforeMenu = _surface; SetSurface(Surface.Menu); Log.Info("[skia] menu open"); };
        _window.OnMouseMove = (x, y) => { _mouseInside = true; _mouseX = x; _mouseY = y; };
        _window.OnMouseLeave = () => { _mouseInside = false; _leftAt = Environment.TickCount64; _hover = -1; _menuHover = -1; };
        // Only the body is clickable; the rest of the 900x300 surface lets clicks through.
        _window.HitTest = (x, y) => _hit.Collapse.Width > 0 && Inside(_hit.Collapse, x, y);
        _window.OnLeftDown = BeginSliderDrag;
        _window.OnDrag = ApplySlider;
        _window.OnLeftUp = EndSliderDrag;

        _lyricsOffsetMs = Settings.Current.LyricsOffsetMs;
        RebuildMenu();

        // Apply the persisted look/placement up front, before the first frame.
        _monCount = OverlayWindow.MonitorCount();
        _window.WindowAlpha = (byte)Math.Round(255 * Math.Clamp(Settings.Current.Opacity, 0.4, 1.0));
        if (Math.Abs(Settings.Current.UiScale - 1.0) > 0.001)
            _window.Resize(WinW * Settings.Current.UiScale, WinH * Settings.Current.UiScale);
        if (Settings.Current.MonitorIndex >= 0)
            _window.PlaceOnMonitor(Settings.Current.MonitorIndex);

        AudioOutput.Start();
        AudioOutput.VolumeChanged += (v, m) =>
        {
            _vol = v; _volMuted = m;
            _volumeUntil = Environment.TickCount64 + VolumeHoldMs;
            if (_surface != Surface.Menu) { _beforeMenu = _surface; SetSurface(Surface.Volume); }
        };
        _vol = AudioOutput.Volume; _volMuted = AudioOutput.Muted;

        var smtc = Smtc;
        _ = Task.Run(async () =>
        {
            try
            {
                await smtc.InitializeAsync();
                smtc.PlaybackChanged += (_, _) => _ = RefreshAsync();
                smtc.SessionChanged += (_, _) => _ = RefreshAsync();
                // A track change inside the same session raises MediaPropertiesChanged (not
                // PlaybackChanged), so without this the island kept the previous song - and its
                // lyrics - for however long until the next playback event. This is that delay.
                // The second pass catches Spotify handing out the previous track's metadata
                // for a moment right after the skip.
                smtc.MediaChanged += (_, _) => { _ = RefreshAsync(); _ = RefreshAfterAsync(600); };
                await RefreshAsync();
                if (!smtc.IsSpotifyActive)
                    Log.Info("[skia] no Spotify session yet - launch the Spotify desktop app. " +
                             "The island peeks for a few seconds at startup, then stays hidden until playback.");
            }
            catch (Exception ex) { Log.Error("[skia] smtc: " + ex.Message); }
        });

        // Peek on launch so the user can see the island exists even before Spotify plays.
        _forceShowUntil = Environment.TickCount64 + 4500;
        _window.Show();
        _window.StartTimer(TimerMs);
        _window.EnableTray(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"), "Float Island - left click to show");
        _window.OnTrayShow = () => { _forceShowUntil = Environment.TickCount64 + 5000; SetSurface(Surface.Collapsed); };
        _window.OnShowRequested = () => { _forceShowUntil = Environment.TickCount64 + 6000; SetSurface(Surface.Collapsed); };
        _window.OnTrayExit = () => _window.Close();
        _window.OnHotkey = OnHotkey;
        ApplyHotkeys();
        Log.Info("[skia] overlay running (skia path)");
        OverlayWindow.RunLoop();
        OverlayWindow.UnregisterHotkeys(_window.Handle);
        _window.RemoveTray();
        _capture?.Stop();
        Log.Info("[skia] overlay ended");
    }

    // ────────────────────────────────────────────────────────────────────── frame
    private static bool Frame()
    {
        long now = Environment.TickCount64;
        float dt = _lastTick == 0 ? 1f / 60f : Math.Clamp((now - _lastTick) / 1000f, 0.001f, 0.05f);
        _lastTick = now;

        TickIndex();

        // Re-anchor the lyric clock against SMTC about once a second while playing. The
        // timeline events Spotify raises are sparse, so extrapolating from the last one can
        // drift; this keeps the sweep on the audio without polling metadata.
        if (_playing && now - _lastPosSync > 1000)
        {
            _lastPosSync = now;
            _ = ResyncClockAsync();
        }

        // Get out of the way of fullscreen apps (games, fullscreen video), polled cheaply.
        if (now - _lastFsCheck > 500)
        {
            _lastFsCheck = now;
            bool was = _fullscreen;
            _fullscreen = Settings.Current.HideWhenFullscreen && OverlayWindow.ForegroundIsFullscreen(_window.Handle);
            if (_fullscreen != was) Log.Info($"[skia] foreground fullscreen={_fullscreen}");
        }

        // Show/hide with a fade + shrink (instant ShowWindow on start, animated on pause).
        bool forced = now < _forceShowUntil;
        bool want = !_fullscreen && (_playing || (!Settings.Current.HideWhenMusicStops && _hasTrack) || forced);
        if (want && !_shown)
        {
            _shown = true; _visAlpha = 0f; _window.Show2();
            Log.Info($"[skia] show (playing={_playing} track={_hasTrack} forced={forced})");
        }
        if (_shown)
        {
            _visAlpha = want ? Math.Min(1f, _visAlpha + dt / 0.22f)
                             : Math.Max(0f, _visAlpha - dt / 0.18f);
            if (!want && _visAlpha <= 0.001f)
            {
                _window.Hide(); _shown = false;
                Log.Info(_fullscreen ? "[skia] hide (foreground fullscreen)" : "[skia] hide (idle: nothing playing)");
            }
        }
        if (!_shown) { _window.SetFrameInterval(200); return false; }
        _visScale = 0.70f + 0.30f * _visAlpha;
        _window.SetFrameInterval(TimerMs);

        // the volume surface auto-dismisses back to whatever was showing before
        if (_surface == Surface.Volume && now > _volumeUntil) SetSurface(_beforeMenu);

        if (Settings.Current.MonitorIndex < 0 && now - _lastMonCheck > 300) { _lastMonCheck = now; _window.FollowCursorMonitor(); }

        if (!_mouseInside && _surface != Surface.Collapsed && _surface != Surface.Menu
            && _surface != Surface.Settings
            && _leftAt != 0 && now - _leftAt > CollapseAfterMs)
            SetSurface(Surface.Collapsed);

        float s = (float)_window.Scale * (float)Settings.Current.UiScale;
        var f = new IslandFrame { Surface = _surface, MenuLabels = Menu, CatPhase = now / 1000f, Logo = _logo };

        lock (Gate)
        {
            // The frame that could still have referenced these has already been painted, so
            // this is the safe point to free a cover that was swapped out by a refresh.
            if (Retired.Count > 0)
            {
                foreach (var old in Retired) old.Dispose();
                Retired.Clear();
            }
            f.Title = _title; f.Artist = _artist; f.Cover = _cover;
            f.Playing = _playing; f.HasTrack = _hasTrack; f.Progress = _progress;
            f.Elapsed = _elapsed; f.Duration = _duration;
            f.HasLyrics = _hasLyrics;
            f.Lines = new List<string>(Lines);
            f.LyricIndex = _index;
            f.LyricLine = _index >= 0 && _index < Lines.Count ? Lines[_index] : "";
            f.Line = !string.IsNullOrEmpty(f.LyricLine)
                ? f.LyricLine
                : !string.IsNullOrWhiteSpace(_title)
                    ? (string.IsNullOrWhiteSpace(_artist) ? _title : _title + " \u2014 " + _artist)
                    : "Waiting for Spotify\u2026";
            if (_playing) _capture?.ReadSnapshot(Levels);
            f.Bars = Levels.Bars;
            f.AudioActive = _playing && Levels.Active;
            f.Beat = Levels.Beat;
            f.LyricsOffsetMs = _lyricsOffsetMs;
            f.Volume = _vol; f.VolumeMuted = _volMuted;
        }

        // ── page reveal (staggered lyric rows) + active-line fade
        if (_surface == Surface.Lyrics) _lyricsReveal = Math.Min(1f, _lyricsReveal + dt / 0.45f);
        else _lyricsReveal = 0f;
        f.LyricsReveal = _lyricsReveal;

        if (_index != _lastIndex) { _lastIndex = _index; _lineFade = 0f; }
        _lineFade = Math.Min(1f, _lineFade + dt / 0.28f);
        f.LineFade = _lineFade;

        // body targets + spring
        float targetW = _surface == Surface.Collapsed ? _renderer.CollapsedWidthPx(f.Line, s)
                     : _surface == Surface.Music ? 432 * s
                     : _surface == Surface.Lyrics ? 576 * s
                     : _surface == Surface.Volume ? 236 * s : 316 * s;
        float targetH = _surface == Surface.Collapsed ? 36 * s
                     : _surface == Surface.Menu ? IslandRenderer.MenuHeightPx(Menu.Length) * s
                     : _surface == Surface.Settings ? IslandRenderer.SettingsHeightPx() * s
                     : _surface == Surface.Volume ? 44 * s : 176 * s;
        float targetR = _surface == Surface.Collapsed ? 18 * s : _surface == Surface.Menu ? 26 * s
                     : _surface == Surface.Settings ? 28 * s
                     : _surface == Surface.Volume ? 22 * s : 32 * s;
        float targetF = _surface == Surface.Collapsed || _surface == Surface.Volume ? 16 * s : 28 * s;

        Step(targetW, ref _mw, ref _vw, dt);
        Step(targetH, ref _mh, ref _vh, dt);
        Step(targetR, ref _mr, ref _vr, dt);
        Step(targetF, ref _mf, ref _vf, dt);

        _scrollVel += (0f - _scroll) * 240f * dt - _scrollVel * 22f * dt;
        _scroll += _scrollVel * dt;
        f.LyricScroll = _scroll;

        // karaoke reveal
        f.Karaoke = KaraokeProgress();
        f.DotsActive = _surface == Surface.Lyrics ? 1 : 0;

        // title marquee
        f.MarqueeOffset = MarqueeOffset(f.Title, _surface, s, now);

        // album wash (lyrics page only)
        UpdateWash(f, dt);

        // body follows the album colour
        Step(_hasTint ? 0.62f : 0f, ref _tintAmt, ref _vtint, dt);
        f.Tint = _tint;
        f.TintAmount = _tintAmt;

        // hover
        f.Hover = _mouseInside ? HitControl(_mouseX, _mouseY) : -1;
        f.MenuHover = _mouseInside && _surface == Surface.Menu ? HitMenuRow(_mouseX, _mouseY) : -1;
        _hover = f.Hover; _menuHover = f.MenuHover;
        bool settingsHot = _mouseInside && _surface == Surface.Settings && SettingsHot(_mouseX, _mouseY);
        _window.CursorKind = (_surface == Surface.Collapsed || f.Hover >= 0 || f.MenuHover >= 0 || settingsHot) ? 1 : 0;

        // Settings surface: live values + the fixed accent theme (null = follow cover art).
        f.UiScale = Settings.Current.UiScale;
        f.Opacity = Settings.Current.Opacity;
        f.MonitorIndex = Settings.Current.MonitorIndex;
        f.MonitorCount = _monCount;
        f.Accent = Settings.Current.Accent;
        f.AccentOverride = Settings.Current.Accent == AccentMode.Album
            ? null : IslandRenderer.AccentColorFor(Settings.Current.Accent);

        var morph = new IslandMorph
        {
            BodyW = _mw * _visScale, BodyH = _mh * _visScale,
            Radius = _mr * _visScale, Flare = _mf * _visScale,
            Alpha = _visAlpha
        };
        var info = new SKImageInfo(_window.Width, _window.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info, _window.Bits, _window.Stride);
        if (surface == null) return false;
        _renderer.Render(surface.Canvas, _window.Width, _window.Height, s, f, _hit, morph);
        surface.Canvas.Flush();
        return true;
    }

    /// <summary>Pulls the playback clock from SMTC (status + timeline only) and re-anchors
    /// <see cref="_pos"/>, so a sparse timeline-event stream cannot let the lyric sweep drift
    /// away from the audio.</summary>
    private static async Task ResyncClockAsync()
    {
        try
        {
            var t = await Smtc.GetTimelineAsync();
            if (t == null || t.Status != 2) return;
            lock (Gate)
            {
                long now = Environment.TickCount64;
                // The clock already running, extrapolated to this instant. A resync must never
                // drag it backwards: Spotify republishes its timeline seconds apart, so a
                // value that lands behind the running clock would snap the sweep back inside
                // the line. A real backward seek arrives as its own timeline event, which
                // runs a full refresh instead.
                var projected = _pos + (_playing
                    ? TimeSpan.FromMilliseconds(now - _posStamp)
                    : TimeSpan.Zero);
                var position = t.Position > projected ? t.Position : projected;

                _pos = position;
                _posStamp = now;
                _playing = true;
                if (t.Duration > TimeSpan.Zero)
                {
                    _durationTs = t.Duration;
                    _progress = Math.Clamp(position.TotalMilliseconds / t.Duration.TotalMilliseconds, 0, 1);
                    _duration = Fmt(t.Duration);
                    _elapsed = Fmt(position);
                }
            }
        }
        catch (Exception ex) { Log.Debug($"[skia] clock resync: {ex.Message}"); }
    }

    private static void Step(float target, ref float v, ref float vel, float dt)
    {
        if (!Settings.Current.EnableAnimations) { v = target; vel = 0; return; }
        // stiffness/damping tuned for a soft, non-overshooting settle (~0.35 s)
        vel += ((target - v) * 240f - vel * 25f) * dt;
        v += vel * dt;
        if (Math.Abs(target - v) < 0.05f && Math.Abs(vel) < 0.4f) { v = target; vel = 0; }
    }

    /// <summary>The playback clock as of right now. <see cref="_pos"/> is only true as of
    /// <see cref="_posStamp"/>, so while playing the elapsed time is added on top; paused it
    /// must not move at all. The lyric offset is subtracted here, so a positive value shows a
    /// line that many milliseconds later (menu row "Lyrics sync").</summary>
    private static TimeSpan LivePosition()
    {
        var pos = _pos;
        if (_playing) pos += TimeSpan.FromMilliseconds(Environment.TickCount64 - _posStamp);
        pos -= TimeSpan.FromMilliseconds(_lyricsOffsetMs);
        if (pos < TimeSpan.Zero) return TimeSpan.Zero;
        return _durationTs > TimeSpan.Zero && pos > _durationTs ? _durationTs : pos;
    }

    private static float KaraokeProgress()
    {
        float s = (float)_window.Scale;
        if (string.IsNullOrEmpty(_title)) return 1f;
        if (_index < 0 || _index >= Lines.Count) return 1f;                 // title line: fully shown
        if (!_playing) return 1f;
        var start = Times[_index];
        var end = _index + 1 < Times.Count ? Times[_index + 1] : _durationTs;
        double span = (end - start).TotalMilliseconds;
        var pos = LivePosition();
        return span <= 1 ? 1f : (float)Math.Clamp((pos - start).TotalMilliseconds / span, 0, 1);
    }

    private static float MarqueeOffset(string title, Surface surf, float s, long now)
    {
        if (surf != Surface.Music && surf != Surface.Lyrics) return 0;
        float colW = surf == Surface.Music ? 400 * s : (576 - 32 - 1) / 2f * s;
        float tw = colW - (64 + 14 + 12) * s;
        float w = _renderer.Measure(IslandRenderer.SKTypefaceKind.Semi, title, 15 * s);
        if (w <= tw + 1) return 0;

        float overflow = w - tw;
        const double period = 6.0;
        double phase = (now % (long)(period * 1000)) / (period * 1000.0);
        double tri = phase < 0.5 ? phase * 2 : (1 - phase) * 2;   // 0→1→0
        return (float)(-overflow * tri);
    }

    private static void UpdateWash(IslandFrame f, float dt)
    {
        float target = _surface == Surface.Lyrics ? 1f : 0f;
        _washAlpha += (target - _washAlpha) * Math.Min(1f, dt * 5f);
        if (_washAlpha < 0.01f) _washAlpha = 0;

        f.WashAlpha = _washAlpha;
        f.WashLeft = _washL; f.WashMid = _washM; f.WashRight = _washR;
    }

    // ──────────────────────────────────────────────────────────────────── wash tint
    private static void ComputeTint(SKImage? img)
    {
        var tint = new SKColor(8, 9, 11);
        bool hasTint = false;
        try
        {
            if (img != null && img.Width > 0 && img.Height > 0)
            {
                // Sample through a 64x64 raster surface instead of SKBitmap.FromImage. FromImage
                // copies the whole decoded image through sk_image_read_pixels, and when the cover
                // was swapped and disposed by another refresh mid-copy the native read walked into
                // freed memory and killed the process with 0xc0000005. Here the source is a local
                // the caller owns for the whole call, and only 64x64 pixels are ever touched.
                int w = Math.Min(img.Width, 64), h = Math.Min(img.Height, 64);
                var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info);
                if (surface != null)
                {
                    surface.Canvas.Clear(SKColors.Black);
                    using (var paint = new SKPaint { IsAntialias = false })
                        surface.Canvas.DrawImage(img, new SKRect(0, 0, w, h), paint);
                    surface.Canvas.Flush();
                    using var pix = surface.PeekPixels();
                    if (pix != null)
                    {
                        long r = 0, g = 0, b = 0, n = 0, sr = 0, sg = 0, sb = 0, sn = 0;
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                var p = pix.GetPixelColor(x, y);
                                r += p.Red; g += p.Green; b += p.Blue; n++;
                                int mx = Math.Max(p.Red, Math.Max(p.Green, p.Blue)), mn = Math.Min(p.Red, Math.Min(p.Green, p.Blue));
                                if (mx > 0 && mx - mn > 40) { sr += p.Red; sg += p.Green; sb += p.Blue; sn++; }
                            }
                        if (n > 0)
                        {
                            tint = sn > 0 ? new SKColor((byte)(sr / sn), (byte)(sg / sn), (byte)(sb / sn))
                                          : new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n));
                            hasTint = true;
                        }
                    }
                }
            }
        }
        catch (Exception ex) { Log.Debug("[skia] tint: " + ex.Message); }

        // Keep light covers from turning the island a washed-out grey: darken a bright tint
        // while preserving its hue, so white/cream art still reads as a dark tinted surface.
        if (hasTint) tint = DarkenIfBright(tint);

        var baseC = new SKColor(8, 9, 11);
        double k = 0.62;
        var left = Mix(baseC, tint, k);
        while (k > 0.02 && Contrast(SKColors.White, left) < 4.6) { k -= 0.02; left = Mix(baseC, tint, k); }
        double kr = Math.Min(k * 0.55, 0.34);
        var right = Mix(baseC, tint, kr);
        while (kr > 0 && Contrast(Mix(right, SKColors.White, 0.62), right) < 4.6)
        {
            kr = Math.Max(kr - 0.02, 0); right = Mix(baseC, tint, kr);
        }

        lock (Gate)
        {
            _tint = tint; _hasTint = hasTint;
            _washL = left; _washR = right; _washM = Mix(left, right, 0.5);
        }
        Log.Info($"[skia] tint #{tint.Red:X2}{tint.Green:X2}{tint.Blue:X2} hasTint={hasTint}");
    }

    /// <summary>Swaps the cover and retires the old one. The old image is not disposed here:
    /// the frame thread may still be drawing it, or a frame may have snapshotted it.</summary>
    private static void PublishCover(SKImage? img)
    {
        lock (Gate)
        {
            if (_cover != null) Retired.Add(_cover);
            _cover = img;
        }
    }

    private static SKColor DarkenIfBright(SKColor c)
    {
        double lum = 0.2126 * Lin(c.Red / 255.0) + 0.7152 * Lin(c.Green / 255.0) + 0.0722 * Lin(c.Blue / 255.0);
        if (lum <= 0.35) return c;
        double f = 0.35 / lum;
        return new SKColor((byte)Math.Min(255, c.Red * f), (byte)Math.Min(255, c.Green * f), (byte)Math.Min(255, c.Blue * f));
    }

    private static SKColor Mix(SKColor a, SKColor b, double k) => new(
        (byte)Math.Round(a.Red + (b.Red - a.Red) * k),
        (byte)Math.Round(a.Green + (b.Green - a.Green) * k),
        (byte)Math.Round(a.Blue + (b.Blue - a.Blue) * k));

    private static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    private static double Lum(SKColor c) => 0.2126 * Lin(c.Red / 255.0) + 0.7152 * Lin(c.Green / 255.0) + 0.0722 * Lin(c.Blue / 255.0);
    private static double Contrast(SKColor a, SKColor b)
    {
        double la = Lum(a), lb = Lum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // ──────────────────────────────────────────────────────────────────── input
    private static int HitControl(float x, float y)
    {
        if (_surface != Surface.Music && _surface != Surface.Lyrics) return -1;
        if (Inside(_hit.Prev, x, y)) return 0;
        if (Inside(_hit.Play, x, y)) return 1;
        if (Inside(_hit.Next, x, y)) return 2;
        if (Inside(_hit.LyricsToggle, x, y)) return 3;
        return -1;
    }

    private static int HitMenuRow(float x, float y)
    {
        for (int i = 0; i < _hit.MenuRows.Length; i++)
            if (Inside(_hit.MenuRows[i], x, y)) return i;
        return -1;
    }

    private static void Click(int x, int y)
    {
        if (_surface == Surface.Menu)
        {
            int row = HitMenuRow(x, y);
            Log.Info($"[skia] menu click {x},{y} row={row} rows={_hit.MenuRows.Length} scale={_window.Scale:0.###}");
            switch (row)
            {
                case 0: SetSurface(Surface.Music); return;
                case 1: SetSurface(Surface.Lyrics); return;
                case 2: AdjustLyricsSync(x); return;
                case 3: AudioOutput.CycleDevice(); RebuildMenu(); SetSurface(Surface.Menu); return;
                case 4: Startup.IsEnabled = !Startup.IsEnabled; RebuildMenu(); SetSurface(Surface.Menu); return;
                case 5: Settings.Current.HideWhenMusicStops = !Settings.Current.HideWhenMusicStops; Settings.Save(); RebuildMenu(); SetSurface(Surface.Menu); return;
                case 6:
                    // Turning it off must bring the island back immediately, not on the next
                    // half-second fullscreen poll.
                    Settings.Current.HideWhenFullscreen = !Settings.Current.HideWhenFullscreen;
                    Settings.Save();
                    if (!Settings.Current.HideWhenFullscreen) { _fullscreen = false; _lastFsCheck = 0; }
                    Log.Info($"[skia] hide when fullscreen {(Settings.Current.HideWhenFullscreen ? "On" : "Off")}");
                    RebuildMenu(); SetSurface(Surface.Menu); return;
                case 7: Settings.Current.MediaHotkeys = !Settings.Current.MediaHotkeys; Settings.Save(); ApplyHotkeys(); RebuildMenu(); SetSurface(Surface.Menu); return;
                case 8: SetSurface(Surface.Settings); return;
                case 9: SetSurface(Surface.Collapsed); return;
                case 10: _window.Close(); return;
                default: SetSurface(_beforeMenu); return;
            }
        }

        if (_surface == Surface.Settings) { ClickSettings(x, y); return; }

        if (_surface == Surface.Collapsed) { SetSurface(Surface.Music); return; }

        if (Inside(_hit.LyricsToggle, x, y)) { SetSurface(_surface == Surface.Lyrics ? Surface.Music : Surface.Lyrics); return; }
        if (Inside(_hit.Play, x, y)) { _ = Smtc.TogglePlayPauseAsync(); return; }
        if (Inside(_hit.Prev, x, y)) { _ = Smtc.PreviousAsync(); return; }
        if (Inside(_hit.Next, x, y)) { _ = Smtc.NextAsync(); return; }
        if (Inside(_hit.SeekTrack, x, y) && _durationTs.TotalMilliseconds > 0)
        {
            double frac = Math.Clamp((x - _hit.SeekTrack.Left) / _hit.SeekTrack.Width, 0, 1);
            _ = Smtc.SeekAsync(_durationTs * frac);
            // Move our own clock with the seek now, so the sweep is right before SMTC's
            // timeline event lands.
            lock (Gate) { _pos = _durationTs * frac; _posStamp = Environment.TickCount64; }
            return;
        }
        SetSurface(Surface.Collapsed);
    }

    /// <summary>The "Lyrics sync" menu row: left third = earlier, middle = reset, right third = later.</summary>
    private static void AdjustLyricsSync(float x)
    {
        if (_hit.MenuRows.Length < 3) return;
        var row = _hit.MenuRows[2];
        if (row.Width <= 1) return;

        float rel = (x - row.Left) / row.Width;
        // 100 ms steps: after the SMTC clock fix the residue is small, and 0.5 s could not
        // land on a tight alignment.
        int step = rel < 0.42f ? -100 : rel > 0.58f ? 100 : int.MinValue;
        _lyricsOffsetMs = step == int.MinValue ? 0 : Math.Clamp(_lyricsOffsetMs + step, -5000, 5000);
        Settings.Current.LyricsOffsetMs = _lyricsOffsetMs; Settings.Save();
        Log.Info($"[skia] lyrics offset {_lyricsOffsetMs} ms");
        RebuildMenu(); SetSurface(Surface.Menu);
    }

    private static void ClickSettings(float x, float y)
    {
        if (Inside(_hit.SettingsBack, x, y)) { SetSurface(Surface.Menu); return; }

        for (int i = 0; i < _hit.SettingsSwatches.Length; i++)
            if (Inside(_hit.SettingsSwatches[i], x, y))
            {
                Settings.Current.Accent = (AccentMode)i;
                Settings.Save();
                Log.Info($"[skia] accent {Settings.Current.Accent}");
                SetSurface(Surface.Settings);
                return;
            }

        if (Inside(_hit.SettingsMonMinus, x, y)) { CycleMonitor(-1); return; }
        if (Inside(_hit.SettingsMonPlus, x, y)) { CycleMonitor(1); return; }
    }

    /// <summary>Steps the target monitor through follow-pointer (-1) and each display in turn.</summary>
    private static void CycleMonitor(int dir)
    {
        _monCount = OverlayWindow.MonitorCount();
        int next = Settings.Current.MonitorIndex + dir;
        if (next < -1) next = _monCount - 1;
        if (next > _monCount - 1) next = -1;
        Settings.Current.MonitorIndex = next;
        Settings.Save();
        _window.PlaceOnMonitor(next);
        Log.Info($"[skia] monitor {next} (count={_monCount})");
        SetSurface(Surface.Settings);
    }

    // Settings sliders: click-and-drag anywhere on the row band. Values are saved on release.
    private static bool _dragScale, _dragOpacity;

    private static bool BeginSliderDrag(float x, float y)
    {
        if (_surface != Surface.Settings) return false;
        if (Inside(_hit.SettingsScale, x, y)) { _dragScale = true; ApplySlider(x, y); return true; }
        if (Inside(_hit.SettingsOpacity, x, y)) { _dragOpacity = true; ApplySlider(x, y); return true; }
        return false;
    }

    private static void ApplySlider(float x, float y)
    {
        float s = (float)_window.Scale * (float)Settings.Current.UiScale;
        float r = 5 * s;

        if (_dragScale)
        {
            var a = _hit.SettingsScale;
            float span = a.Width - 2 * r;
            float v = span <= 1 ? 0 : Math.Clamp((x - a.Left - r) / span, 0f, 1f);
            double k = Math.Round((0.8 + v * 0.6) / 0.05) * 0.05;
            if (Math.Abs(k - Settings.Current.UiScale) < 0.001) return;
            Settings.Current.UiScale = k;
            _window.Resize(WinW * k, WinH * k);
            Log.Info($"[skia] ui scale {k:0.00}");
        }
        else if (_dragOpacity)
        {
            var a = _hit.SettingsOpacity;
            float span = a.Width - 2 * r;
            float v = span <= 1 ? 0 : Math.Clamp((x - a.Left - r) / span, 0f, 1f);
            double o = Math.Round((0.4 + v * 0.6) / 0.05) * 0.05;
            if (Math.Abs(o - Settings.Current.Opacity) < 0.001) return;
            Settings.Current.Opacity = o;
            _window.WindowAlpha = (byte)Math.Round(255 * o);
            Log.Info($"[skia] opacity {o:0.00}");
        }
    }

    private static void EndSliderDrag()
    {
        if (!_dragScale && !_dragOpacity) return;
        _dragScale = _dragOpacity = false;
        Settings.Save();
        SetSurface(Surface.Settings);
    }

    private static bool SettingsHot(float x, float y)
    {
        if (Inside(_hit.SettingsBack, x, y)) return true;
        if (Inside(_hit.SettingsMonMinus, x, y) || Inside(_hit.SettingsMonPlus, x, y)) return true;
        foreach (var r in _hit.SettingsSwatches) if (Inside(r, x, y)) return true;
        return Inside(_hit.SettingsScale, x, y) || Inside(_hit.SettingsOpacity, x, y);
    }

    private static bool Inside(SKRect r, float x, float y) => x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom;

    private static void SetSurface(Surface s)
    {
        if (s != Surface.Menu) _beforeMenu = s;
        _surface = s;
        _scroll = 0; _scrollVel = 0;
        _leftAt = Environment.TickCount64;
    }

    // ────────────────────────────────────────────────────────────────── smtc/lyrics
    private static async Task RefreshAsync()
    {
        await RefreshGate.WaitAsync();
        try
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try { await RefreshOnceAsync(); return; }
                catch (Exception ex)
                {
                    Log.Warn($"[skia] refresh {attempt}/3: {ex.Message}");
                    if (attempt == 3) { Log.Error("[skia] refresh gave up: " + ex.Message); return; }
                    await Task.Delay(400 * attempt);
                }
            }
        }
        finally { RefreshGate.Release(); }
    }

    /// <summary>Runs a refresh a short moment later. Used after a track change, where Spotify
    /// can still be serving the previous track's metadata for the first few hundred ms.</summary>
    private static async Task RefreshAfterAsync(int ms)
    {
        try { await Task.Delay(ms); await RefreshAsync(); }
        catch (Exception ex) { Log.Debug($"[skia] deferred refresh: {ex.Message}"); }
    }

    private static async Task RefreshOnceAsync()
    {
        var t = await Smtc.GetCurrentTrackAsync();
        if (t == null) return;

            bool newTrack = !string.Equals(_trackKey, t.ThumbnailCacheKey ?? t.Title, StringComparison.Ordinal);
            if (newTrack)
            {
                _trackKey = t.ThumbnailCacheKey ?? t.Title;
                _index = -1; _scroll = 0; _scrollVel = 0;
                lock (Gate) { Lines.Clear(); Times.Clear(); _hasLyrics = false; }
            }

            lock (Gate)
            {
                _title = t.Title ?? ""; _artist = t.Artist ?? "";
                _hasTrack = t.HasTrack;
                _playing = t.Status == 2;
                _pos = t.Position; _posStamp = Environment.TickCount64; _durationTs = t.Duration;
                _progress = t.Duration.TotalMilliseconds > 0
                    ? Math.Clamp(t.Position.TotalMilliseconds / t.Duration.TotalMilliseconds, 0, 1) : 0;
                _elapsed = Fmt(t.Position); _duration = Fmt(t.Duration);
            }

            if (t.Thumbnail != null && newTrack)
            {
                try
                {
                    using var stream = await t.Thumbnail.OpenReadAsync();
                    using var ms = new MemoryStream();
                    using (var rs = stream.AsStreamForRead()) await rs.CopyToAsync(ms);
                    var img = SKImage.FromEncodedData(ms.ToArray());
                    if (img != null)
                    {
                        // Tint first, publish second: the image is a local until it is published,
                        // so nothing can dispose it while the pixels are being read.
                        ComputeTint(img);
                        PublishCover(img);
                    }
                    else Log.Debug("[skia] cover: decode returned null");
                }
                catch { }
            }

            if (t.Status == 2) EnsureCapture();

        if (newTrack && t.HasTrack) await LoadLyricsAsync(t);
    }

    private static string Fmt(TimeSpan t) => t <= TimeSpan.Zero ? "--:--" : $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    private static async Task LoadLyricsAsync(TrackInfo t)
    {
        try
        {
            var q = new LyricsQuery(t.Title, t.Artist, t.Album, t.Duration);
            var r = await Lyrics.GetAsync(q, Settings.Current, CancellationToken.None);
            if (r.Kind != LyricsKind.Synced || r.Lines == null) return;
            lock (Gate)
            {
                Lines.Clear(); Times.Clear();
                foreach (var l in r.Lines) { Times.Add(l.Time); Lines.Add(string.IsNullOrWhiteSpace(l.Text) ? "\u266a" : l.Text); }
                _hasLyrics = Lines.Count > 0;
            }
        }
        catch { }
    }

    private static void EnsureCapture()
    {
        var pid = ProcessLoopbackCapture.FindSpotifyProcessId();
        if (pid == null) return;
        if (_capture != null && _capture.IsRunning && _capturePid == pid.Value) return;
        _capture?.Stop();
        _capture = new ProcessLoopbackCapture();
        _capturePid = pid.Value;
        _capture.Start(pid.Value);
    }

    private static void TickIndex()
    {
        if (!_playing || Times.Count == 0) return;
        var pos = LivePosition();
        int idx = -1;
        for (int i = Times.Count - 1; i >= 0; i--) if (Times[i] <= pos) { idx = i; break; }
        if (idx == _index) return;

        int delta = idx - _index;
        float s = (float)_window.Scale;
        if (_index >= 0 && Math.Abs(delta) <= 6) _scroll += delta * 30f * s;
        _index = idx;
    }

    /// <summary>Times the renderer: <c>--skia-bench [frames]</c>. Reports ms per frame and
    /// the implied share of one core at 60 fps, without needing Spotify.</summary>
    public static void Bench(int frames)
    {
        var fontDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
        using var r = new IslandRenderer(fontDir);
        var f = new IslandFrame
        {
            Title = "See You Again", Artist = "Tyler, The Creator",
            Line = "And I'll tell you all about it",
            LyricLine = "And I'll tell you all about it",
            Lines = Enumerable.Range(0, 45).Select(i => "lyric line number " + i).ToList(),
            LyricIndex = 20, HasLyrics = true, Playing = true, Progress = 0.42,
            Elapsed = "01:12", Duration = "03:49", LyricsReveal = 1f, LineFade = 1f,
            DotsActive = 1, Hover = 1,
            MenuLabels = new[] { "Music", "Lyrics", "Output: Speakers", "Startup: Off", "Hide: On", "Settings", "Collapse", "Exit" },
        };
        try
        {
            var lp = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
            if (File.Exists(lp)) f.Logo = SKImage.FromEncodedData(File.ReadAllBytes(lp));
        }
        catch { }

        int w = 900, h = 300;
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var hit = new IslandLayout();

        foreach (var surf in new[] { Surface.Collapsed, Surface.Music, Surface.Lyrics })
        {
            f.Surface = surf;
            float bodyW = surf == Surface.Collapsed ? r.CollapsedWidthPx(f.Line, 1.25f) : surf == Surface.Music ? 540 : 720;
            var morph = new IslandMorph { BodyW = bodyW, BodyH = surf == Surface.Collapsed ? 45 : 220, Radius = 32 * 1.25f, Flare = 28 * 1.25f, Alpha = 1f };

            for (int i = 0; i < 40; i++) r.Render(surface.Canvas, w, h, 1.25f, f, hit, morph);   // warm up

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < frames; i++)
            {
                f.CatPhase = i * 0.016f;
                f.Karaoke = (i % 60) / 60f;
                r.Render(surface.Canvas, w, h, 1.25f, f, hit, morph);
            }
            sw.Stop();

            double ms = sw.Elapsed.TotalMilliseconds / frames;
            Log.Info($"[bench] {surf}: {ms:F3} ms/frame -> {ms * 60 / 10:F2}% of one core at 60 fps");
        }
    }

    /// <summary>Live self-check: <c>--skia-demo [seconds]</c>. Opens the real layered window on
    /// the desktop, draws a synthetic player for a few seconds, logs whether the window is
    /// visible and whether the bitmap actually carries opaque pixels, then closes. Proves the
    /// on-screen pipeline without needing Spotify or any media session.</summary>
    public static void LiveDemo(int seconds)
    {
        seconds = Math.Clamp(seconds, 2, 30);
        using var window = new OverlayWindow(WinW, WinH);
        using var renderer = new IslandRenderer(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts"));

        SKImage? logo = null;
        try
        {
            var lp = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
            if (File.Exists(lp)) logo = SKImage.FromEncodedData(File.ReadAllBytes(lp));
        }
        catch { }

        using var cover = MakeDemoCover();
        float s = (float)window.Scale;

        var f = new IslandFrame
        {
            Surface = Surface.Music,
            Title = "See You Again",
            Artist = "Tyler, The Creator",
            Cover = cover,
            Logo = logo,
            Line = "And I'll tell you all about it when I see you again",
            LyricLine = "And I'll tell you all about it",
            Playing = true,
            HasTrack = true,
            Progress = 0.42,
            Elapsed = "01:12",
            Duration = "03:49",
            HasLyrics = true,
            Tint = new SKColor(0x2E, 0x7D, 0x32),
            TintAmount = 0.45f,
            Bars = new[] { 0.22f, 0.38f, 0.61f, 0.48f, 0.76f, 0.92f, 0.67f, 0.54f, 0.81f, 0.63f, 0.44f, 0.58f, 0.72f, 0.36f, 0.51f, 0.28f },
            AudioActive = true,
            Beat = 0.25f,
            LineFade = 1f,
            LyricsReveal = 1f,
            MenuLabels = new[] { "Music", "Lyrics", "Output: Speakers", "Launch at startup: On", "Hide when stopped: On", "Settings", "Collapse", "Exit" },
        };

        var hit = new IslandLayout();
        window.HitTest = (x, y) => hit.Collapse.Width > 0 && Inside(hit.Collapse, x, y);
        var morph = new IslandMorph { BodyW = 432 * s, BodyH = 176 * s, Radius = 32 * s, Flare = 28 * s, Alpha = 1f };
        long start = Environment.TickCount64;
        int frames = 0;
        int probeX = window.Width / 2, probeY = (int)(90 * s);

        window.OnFrame = () =>
        {
            float t = (Environment.TickCount64 - start) / 1000f;
            f.CatPhase = t;
            f.Karaoke = 0.5f + 0.5f * (float)Math.Sin(t * 1.6);

            var info = new SKImageInfo(window.Width, window.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, window.Bits, window.Stride);
            if (surface == null) { Log.Error("[demo] SKSurface.Create failed"); return false; }
            renderer.Render(surface.Canvas, window.Width, window.Height, s, f, hit, morph);
            surface.Canvas.Flush();
            frames++;

            if (frames == 20)
                Log.Info($"[demo] live window visible={window.IsVisible} size={window.Width}x{window.Height} " +
                         $"centreAlpha={window.SampleAlpha(probeX, probeY)}");
            return true;
        };

        window.Show();
        window.StartTimer(TimerMs);
        using var closer = new System.Threading.Timer(_ => window.PostClose(), null, seconds * 1000, Timeout.Infinite);
        OverlayWindow.RunLoop();

        Log.Info($"[demo] finished after {frames} frames ({seconds}s) - if the island was on screen, the live path works");
        window.Dispose();
    }

    // ─────────────────────────────────────────── offline self-check (--skia-shot)
    public static void Shot()
    {
        var fontDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
        using var r = new IslandRenderer(fontDir);
        var dir = Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(dir);
        const float s = 1.25f;

        AudioOutput.Start();
        SKImage? logo = null;
        try
        {
            var lp = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
            if (File.Exists(lp)) logo = SKImage.FromEncodedData(File.ReadAllBytes(lp));
        }
        catch { }
        Log.Info($"[shot] audio default='{AudioOutput.DeviceName}' vol={AudioOutput.Volume:F2} mute={AudioOutput.Muted} devices=[{string.Join(" | ", AudioOutput.ActiveDevices())}]");

        var lines = new List<string> { "It's been a long day", "without you my friend", "And I'll tell you all about it", "when I see you again", "We've come a long way" };
        using var demoCover = MakeDemoCover();
        var f = new IslandFrame
        {
            Title = "See You Again",
            Logo = logo,
            Cover = demoCover,
            Artist = "Tyler, The Creator",
            Line = "And I'll tell you all about it when I see you again",
            LyricLine = "And I'll tell you all about it",
            Lines = lines,
            LyricIndex = 2,
            HasLyrics = true,
            Playing = true,
            Progress = 0.42,
            Elapsed = "01:12",
            Duration = "03:49",
            Karaoke = 0.45f,
            DotsActive = 1,
            Tint = new SKColor(0x2E, 0x7D, 0x32),
            TintAmount = 0.45f,
            WashAlpha = 0.8f,
            WashLeft = new SKColor(0x1A, 0x2E, 0x24),
            WashMid = new SKColor(0x12, 0x1E, 0x18),
            WashRight = new SKColor(0x0C, 0x14, 0x10),
            Hover = 1,
            MenuHover = 2,
            Bars = new[] { 0.22f, 0.38f, 0.61f, 0.48f, 0.76f, 0.92f, 0.67f, 0.54f, 0.81f, 0.63f, 0.44f, 0.58f, 0.72f, 0.36f, 0.51f, 0.28f },
            AudioActive = true,
            Beat = 0.25f,
            LyricsOffsetMs = 500,
            MenuLabels = new[] { "Music", "Lyrics", "Lyrics sync", "Output: Speakers", "Launch at startup: On", "Hide when stopped: On", "Hide when fullscreen: On", "Media hotkeys: On", "Settings", "Collapse", "Exit" },
        };

        f.Karaoke = 1f;
        ShotOne(r, dir, 650, 120, s, Surface.Collapsed, "collapsed", f);

        // Proof that the collapsed pill follows a long lyric instead of clipping at a fixed
        // width: with a long line it grows toward the 640 DIP cap (the window is 720).
        string shortLine = f.Line;
        f.Line = "We've come a long way from where we began and I'll tell you all about it when I see you again";
        ShotOne(r, dir, WinW, WinH, s, Surface.Collapsed, "collapsed-long", f);
        f.Line = shortLine;

        f.Karaoke = 0.72f;
        ShotOne(r, dir, WinW, WinH, s, Surface.Music, "music", f);
        ShotOne(r, dir, WinW, WinH, s, Surface.Lyrics, "lyrics", f);
        ShotOne(r, dir, WinW, WinH, s, Surface.Menu, "menu", f);
        f.Volume = 0.62f;
        ShotOne(r, dir, WinW, WinH, s, Surface.Volume, "volume", f);

        // settings surface
        f.Surface = Surface.Settings;
        f.UiScale = 1.05; f.Opacity = 0.95; f.MonitorIndex = 0; f.MonitorCount = 2;
        f.Accent = AccentMode.Green;
        f.AccentOverride = IslandRenderer.AccentColorFor(AccentMode.Green);
        ShotOne(r, dir, WinW, WinH, s, Surface.Settings, "settings", f);
        f.AccentOverride = null;

        // proof that the body colour follows the cover
        ProofTint(r, dir, s, f, new SKColor(0x2E, 0x7D, 0x32), "green");
        ProofTint(r, dir, s, f, new SKColor(0xB7, 0x1C, 0x1C), "red");
        ProofTint(r, dir, s, f, new SKColor(0xF5, 0xF5, 0xF5), "white");

        // proof that the lyric page reveal animates (staggered rows + active-line fade)
        f.Surface = Surface.Lyrics; f.TintAmount = 0f; f.WashAlpha = 0f;
        Log.Info($"[shot] lyrics reveal pixels: full={LyricPixels(r, s, f, 1f, 1f)} mid={LyricPixels(r, s, f, 0.35f, 0.2f)}");

        // proof that the show/hide fade dims the whole frame
        f.Surface = Surface.Music;
        Log.Info($"[shot] fade alpha: full={BodyAlpha(r, s, f, 1f)} mid={BodyAlpha(r, s, f, 0.45f)}");
    }

    private static int BodyAlpha(IslandRenderer r, float s, IslandFrame f, float alpha)
    {
        int w = (int)(WinW * s), h = (int)(WinH * s);
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        r.Render(surface.Canvas, w, h, s, f, new IslandLayout(),
            new IslandMorph { BodyW = 432 * s, BodyH = 176 * s, Radius = 32 * s, Flare = 28 * s, Alpha = alpha });
        using var bmp = SKBitmap.FromImage(surface.Snapshot());
        return bmp.GetPixel(w / 2, (int)(90 * s)).Alpha;
    }

    private static int LyricPixels(IslandRenderer r, float s, IslandFrame f, float reveal, float lineFade)
    {
        f.LyricsReveal = reveal; f.LineFade = lineFade;
        int w = (int)(WinW * s), h = (int)(WinH * s);
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        r.Render(surface.Canvas, w, h, s, f, new IslandLayout(),
            new IslandMorph { BodyW = 576 * s, BodyH = 176 * s, Radius = 32 * s, Flare = 28 * s, Alpha = 1f });
        using var bmp = SKBitmap.FromImage(surface.Snapshot());
        int n = 0;
        for (int y = 20; y < 200; y++)
            for (int x = 470; x < 800; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Alpha > 250 && p.Red > 30) n++;
            }
        return n;
    }

    private static void ProofTint(IslandRenderer r, string dir, float s, IslandFrame f, SKColor tint, string name)
    {
        f.Surface = Surface.Music;
        f.Tint = DarkenIfBright(tint); f.TintAmount = 0.62f;
        int w = (int)(WinW * s), h = (int)(WinH * s);
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        r.Render(surface.Canvas, w, h, s, f, new IslandLayout(),
            new IslandMorph { BodyW = 432 * s, BodyH = 176 * s, Radius = 32 * s, Flare = 28 * s, Alpha = 1f });
        using var img = surface.Snapshot();
        using (var data = img.Encode(SKEncodedImageFormat.Png, 90))
            File.WriteAllBytes(Path.Combine(dir, "tint_" + name + ".png"), data.ToArray());

        using var bmp = SKBitmap.FromImage(img);
        long rr = 0, gg = 0, bb = 0, n = 0;
        for (int y = 40; y < 60; y++)
            for (int x = 200; x < 260; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Alpha > 250) { rr += p.Red; gg += p.Green; bb += p.Blue; n++; }
            }

        // equaliser region (top-right): sample the bar colour
        long br = 0, bg = 0, bb2 = 0, bn = 0;
        for (int y = 42; y < 60; y++)
            for (int x = 650; x < 680; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Alpha > 250 && Math.Max(p.Red, Math.Max(p.Green, p.Blue)) > 60) { br += p.Red; bg += p.Green; bb2 += p.Blue; bn++; }
            }

        Log.Info($"[shot] tint {name} #{tint.Red:X2}{tint.Green:X2}{tint.Blue:X2} -> body avg #{rr / Math.Max(1, n):X2}{gg / Math.Max(1, n):X2}{bb / Math.Max(1, n):X2} (n={n}) bars avg #{(bn > 0 ? br / bn : 0):X2}{(bn > 0 ? bg / bn : 0):X2}{(bn > 0 ? bb2 / bn : 0):X2} (n={bn})");
    }

    /// <summary>Synthetic cover art for the offline render samples (no third-party artwork).</summary>
    private static SKImage MakeDemoCover()
    {
        const int n = 256;
        var info = new SKImageInfo(n, n, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;

        using (var bg = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(n, n),
                new[] { new SKColor(0x0A, 0x1B, 0x12), new SKColor(0x1E, 0x4A, 0x2C), new SKColor(0x0B, 0x12, 0x0E) },
                new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp)
        })
            c.DrawRect(0, 0, n, n, bg);

        using (var sun = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(new SKPoint(176, 78), 96,
                new[] { new SKColor(0xF4, 0xD9, 0x8A, 235), new SKColor(0xE8, 0xA9, 0x4B, 140), new SKColor(0xE8, 0xA9, 0x4B, 0) },
                new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp)
        })
            c.DrawCircle(176, 78, 96, sun);

        using (var hill = new SKPaint { IsAntialias = true, Color = new SKColor(0x0B, 0x1A, 0x12) })
        using (var p = new SKPath())
        {
            p.MoveTo(0, 190);
            p.CubicTo(50, 160, 96, 178, 140, 196);
            p.CubicTo(180, 212, 220, 190, 256, 170);
            p.LineTo(256, 256); p.LineTo(0, 256); p.Close();
            c.DrawPath(p, hill);
        }

        using (var hill2 = new SKPaint { IsAntialias = true, Color = new SKColor(0x06, 0x10, 0x0B) })
        using (var p = new SKPath())
        {
            p.MoveTo(0, 218);
            p.CubicTo(60, 200, 110, 224, 160, 214);
            p.CubicTo(200, 206, 230, 214, 256, 200);
            p.LineTo(256, 256); p.LineTo(0, 256); p.Close();
            c.DrawPath(p, hill2);
        }

        using (var star = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 90) })
            for (int i = 0; i < 14; i++)
                c.DrawCircle(20 + (i * 53) % 220, 18 + (i * 37) % 110, i % 3 == 0 ? 1.4f : 0.8f, star);

        return surface.Snapshot();
    }

    private static void ShotOne(IslandRenderer r, string dir, int wDip, int hDip, float s, Surface surf, string name, IslandFrame f)
    {
        f.Surface = surf;
        int w = (int)(wDip * s), h = (int)(hDip * s);
        float bodyW = surf switch { Surface.Collapsed => r.CollapsedWidthPx(f.Line, s), Surface.Music => 432 * s, Surface.Lyrics => 576 * s, Surface.Volume => 236 * s, _ => 316 * s };
        float bodyH = surf == Surface.Collapsed ? 36 * s
            : surf == Surface.Menu ? IslandRenderer.MenuHeightPx(f.MenuLabels.Length) * s
            : surf == Surface.Settings ? IslandRenderer.SettingsHeightPx() * s
            : surf == Surface.Volume ? 44 * s : 176 * s;
        float rb = surf == Surface.Collapsed ? 18 * s : surf == Surface.Menu ? 26 * s
            : surf == Surface.Settings ? 28 * s : surf == Surface.Volume ? 22 * s : 32 * s;
        float flare = (surf == Surface.Collapsed || surf == Surface.Volume) ? 16 * s : 28 * s;

        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        r.Render(surface.Canvas, w, h, s, f, new IslandLayout(),
            new IslandMorph { BodyW = bodyW, BodyH = bodyH, Radius = rb, Flare = flare, Alpha = 1f });
        using var img = surface.Snapshot();
        using (var data = img.Encode(SKEncodedImageFormat.Png, 90))
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());

        using var bmp = SKBitmap.FromImage(img);
        Log.Info($"[shot] {name} png {w}x{h} body={bodyW:F0}px h={bodyH:F0} top=0 centred at {w / 2f:F0}");
        foreach (int y in new[] { 0, 2, 20, h / 2, h - 3 })
        {
            if (y < 0 || y >= h) continue;
            int l = -1, rr = -1;
            for (int x = 0; x < w; x++) { if (bmp.GetPixel(x, y).Alpha > 200) { if (l < 0) l = x; rr = x; } }
            Log.Info($"[shot]   y={y,4} L={l,4} R={rr,4} W={(l < 0 ? 0 : rr - l + 1),4} centre={(l < 0 ? -1 : (l + rr) / 2),4}");
        }
    }
}
