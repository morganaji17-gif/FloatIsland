using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FloatIsland.Controls;
using FloatIsland.Models;
using FloatIsland.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace FloatIsland;

public partial class MainWindow : Window
{
    public static MainWindow Instance { get; private set; } = null!;
    public static readonly Converters.ProgressFillConverter ProgressFillInstance = new();

    private enum IslandState { Idle, Playing, Expanded }

    private const int CollapseDelayMs = 1500;
    private const int ResyncIntervalMs = 5000;

    private readonly SettingsManager _settings = new();
    private readonly StartupManager _startup = new();
    private readonly SMTCManager _smtc = new();
    private readonly ArtworkCache _artwork = new();
    private readonly LyricsService _lyrics = new();
    private readonly ProcessLoopbackCapture _capture = new();
    private readonly AudioLevels _levels = new();

    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(CollapseDelayMs) };

    // Spring-driven motion: geometry + lyric scroll are physically simulated so they
    // can be retargeted mid-flight instead of restarting a keyframe animation.
    private readonly Spring _springW = new(0.38, 0.86, 62);
    private readonly Spring _springH = new(0.38, 0.86, 42);
    private readonly Spring _springR = new(0.38, 0.86, 21);
    private readonly Spring _springLyricY = new(0.32, 0.92, 0);
    private bool _motionOn;
    private bool _geomMoving;
    private long _lastFrameTicks;

    private readonly Brush _lyricActive;
    private readonly Brush _lyricNear;
    private readonly Brush _lyricFar;
    private readonly Brush _lyricFaint;

    private TrayIconService? _tray;
    private IslandState _state = IslandState.Idle;

    private bool _mouseInside;
    private bool _idleHidden;
    private bool _animationsOn = true;
    private bool _lyricsPage;
    private bool _isDraggingSlider;
    private volatile bool _captureFailed;
    private uint _capturePid;

    private TrackInfo? _track;
    private string? _prevTitle;
    private string? _artworkKey;
    private CancellationTokenSource? _artworkCts;
    private CancellationTokenSource? _lyricsCts;

    private List<LrcLine>? _synced;
    private string? _plain;
    private LyricsKind _lyricsKind = LyricsKind.None;
    private int _lyricIndex = -1;
    private readonly List<TextBlock> _lyricBlocks = new();

    // Lyric line metrics are measured once per track (and per viewport width) so a
    // line change never triggers a text re-measure of the whole stack.
    private double[] _lineTop = Array.Empty<double>();
    private double[] _lineHeight = Array.Empty<double>();
    private double _measuredWidth = -1;

    private TimeSpan _pos;
    private long _posStamp;
    private long _lastProgressMs;
    private long _lastResyncMs;

    private Thumb? _sliderThumb;
    private IntPtr _hwnd;
    private int _panelGen;

    // diagnostics
    private bool _uiProbe;
    private bool _uiStateLock;

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();

        // Lyric ink: pure white, dim = white 62%, faint below. Opacity ramp in
        // UpdateLyricIndex supplies the rest (colour-only highlighting avoids re-measure).
        _lyricActive = Freeze(Colors.White);
        _lyricNear = Freeze(Color.FromArgb(0xD6, 0xFF, 0xFF, 0xFF));
        _lyricFar = Freeze(Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF));
        _lyricFaint = Freeze(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));

        _frameTimer.Tick += OnFrameTick;
        _collapseTimer.Tick += OnCollapseTick;
        _smtc.PlaybackChanged += OnPlaybackChanged;
        _smtc.MediaChanged += OnMediaChanged;
        _smtc.SessionChanged += OnSessionChanged;
        _smtc.InitFailed += OnInitFailed;
        _capture.Failed += OnCaptureFailed;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ---------------------------------------------------------------- lifecycle
    private void OnLoaded(object s, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.ApplyExStyle(_hwnd);
        InitTray();
        AttachSliderThumb();
        ApplySettings();
        ShowPanel(IslandState.Idle);
        _springW.Snap(124); _springH.Snap(36); _springR.Snap(18);
        ApplyGeometry(124, 36, 18, 16);
        PositionWindow(Notch.Width);
        Dispatcher.BeginInvoke(InitSmtcAsync, DispatcherPriority.Background);
    }

    private void InitTray()
    {
        _tray = new TrayIconService();
        _tray.ShowRequested += (_, _) => Dispatcher.Invoke(ToggleIsland);
        _tray.SettingsRequested += (_, _) => Dispatcher.Invoke(OpenSettings);
        _tray.ExitRequested += (_, _) => Dispatcher.Invoke(Close);
    }

    private async void InitSmtcAsync()
    {
        await _smtc.InitializeAsync();
        if (!_smtc.IsInitialized) { ShowIdleState("Media unavailable", false); return; }
        await RefreshAsync();
    }

    private void AttachSliderThumb()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var thumb = FindVisualChild<Thumb>(ProgressSlider);
            if (thumb == null) return;
            _sliderThumb = thumb;
            thumb.DragStarted += (_, _) => _isDraggingSlider = true;
            thumb.DragCompleted += async (_, _) =>
            {
                _isDraggingSlider = false;
                if (_track?.HasDuration == true) await _smtc.SeekAsync(TimeSpan.FromSeconds(ProgressSlider.Value));
            };
        }, DispatcherPriority.Loaded);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    // --------------------------------------------------------------------- SMTC
    private void OnInitFailed(object? s, EventArgs e) =>
        Dispatcher.BeginInvoke(() => ShowIdleState("Media unavailable", false));
    private void OnSessionChanged(object? s, EventArgs e) => _ = RefreshAsync();
    private async void OnMediaChanged(object? s, EventArgs e) => await RefreshAsync();
    private async void OnPlaybackChanged(object? s, EventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        var track = await _smtc.GetCurrentTrackAsync();
        var expected = CurrentPosition;
        var previousTitle = _track?.Title;
        _track = track;
        if (track != null) ApplyReportedPosition(track, expected, previousTitle);
        await Dispatcher.InvokeAsync(ApplyTrack);
    }

    /// <summary>
    /// Guards against SMTC momentarily reporting a reset-to-zero mid-song (observed
    /// with several players): that would yank the lyrics back to the first line.
    /// A deliberate backwards seek is still honoured, because it either lands past
    /// the 2 s floor or arrives with a different track title.
    /// </summary>
    private void ApplyReportedPosition(TrackInfo track, TimeSpan expected, string? previousTitle)
    {
        var reported = track.Position;
        bool sameTrack = string.Equals(previousTitle, track.Title, StringComparison.OrdinalIgnoreCase);
        bool spuriousReset = track.Status == 2
                             && sameTrack
                             && reported < TimeSpan.FromSeconds(2)
                             && expected > TimeSpan.FromSeconds(10);

        if (spuriousReset)
        {
            Log.Debug($"SMTC position guard: ignored {reported} (expected ~{expected})");
            return;
        }

        _pos = reported;
        _posStamp = Environment.TickCount64;
    }

    private void ApplyTrack()
    {
        var t = _track;
        if (t == null || !t.HasTrack || _smtc.CurrentSession == null)
        {
            ShowIdleState(t?.HasTrack == true ? "No track" : "No music", _smtc.CurrentSession != null);
            return;
        }

        bool newTrack = _prevTitle == null || !string.Equals(_prevTitle, t.Title, StringComparison.OrdinalIgnoreCase);
        if (newTrack)
        {
            _prevTitle = t.Title;
            _captureFailed = false;
            Log.Info($"Track: '{t.Title}' by {t.Artist} (status={t.Status})");
        }

        if (_idleHidden && _settings.Current.ShowWhenMusicStarts) ShowIsland();

        TitleC.Text = t.Title ?? "\u2014";
        ArtistC.Text = t.Artist ?? "";
        SetCapsuleLyric(TitleLine(), true);
        PlayIcon.Data = (Geometry)FindResource(t.Status == 2 ? "PauseGeo" : "PlayGeo");

        if (t.ThumbnailCacheKey != _artworkKey)
        {
            _artworkKey = t.ThumbnailCacheKey;
            _ = LoadArtworkAsync(t);
        }

        if (newTrack) StartLyricsLoad(t);

        if (!_uiStateLock && _state != IslandState.Expanded) SetState(BaseState());

        if (t.Status == 2) EnsureCapture();
        else StopCapture();

        UpdateFrameTimer();
        RefreshPanelData();
        _tray?.SetTooltip($"{t.Title} \u2014 {t.Artist}");
    }

    private void ShowIdleState(string message, bool hasSession)
    {
        _track = null;
        _prevTitle = null;
        _artworkKey = null;
        _lyricsKind = LyricsKind.None;
        _synced = null; _plain = null; _lyricIndex = -1;
        _lyricBlocks.Clear();
        LyricsLines.Children.Clear();
        ResetLineMetrics();
        LyricsFallback.Visibility = Visibility.Collapsed;
        LyricsTranslate.Y = 0;
        SetMusicLyric("");
        SetCapsuleLyric(TitleLine(), true);
        if (!hasSession) { IdleArt.Source = null; ShowPlaceholder(true); }

        ProgressSlider.Value = 0; ProgressSlider.Maximum = 0;
        CurrentTime.Text = Duration.Text = "";
        TitleC.Text = message;
        ArtistC.Text = "";

        StopCapture();
        SetState(IslandState.Idle);

        // Only vanish when Spotify is not running at all; a paused track keeps the
        // mini album island visible.
        if (!hasSession && _settings.Current.HideWhenMusicStops && _settings.Current.CollapseWhenIdle)
            HideIsland();
    }

    private void ShowPlaceholder(bool show)
    {
        IdlePlaceholder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        IdleNote.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ artwork
    private async Task LoadArtworkAsync(TrackInfo track)
    {
        _artworkCts?.Cancel();
        _artworkCts = new CancellationTokenSource();
        var token = _artworkCts.Token;
        var bmp = await _artwork.LoadAsync(track.Thumbnail, track.ThumbnailCacheKey);
        if (token.IsCancellationRequested || bmp == null) return;
        Dispatcher.Invoke(() =>
        {
            IdleArt.Source = bmp; ArtC.Source = bmp;
            _coverTint = TintOf(bmp);
            if (_lyricsPage) UpdateWash();
            ShowPlaceholder(false);
        });
    }

    // ------------------------------------------------------------------- lyrics
    private void StartLyricsLoad(TrackInfo t)
    {
        _lyricsCts?.Cancel();
        _lyricsCts = new CancellationTokenSource();
        var ct = _lyricsCts.Token;

        _synced = null; _plain = null; _lyricIndex = -1;
        _lyricsKind = LyricsKind.None;
        _lyricBlocks.Clear();
        LyricsLines.Children.Clear();
        ResetLineMetrics();
        LyricsTranslate.Y = 0;
        LyricsFallback.Text = "Loading lyrics\u2026";
        LyricsFallback.Visibility = Visibility.Visible;
        SetMusicLyric("");
        SetCapsuleLyric(TitleLine(), true);

        var query = new LyricsQuery(t.Title, t.Artist, t.Album, t.Duration);
        _ = LoadLyricsAsync(query, ct);
    }

    private async Task LoadLyricsAsync(LyricsQuery q, CancellationToken ct)
    {
        LyricsResult result;
        try { result = await _lyrics.GetAsync(q, _settings.Current, ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Log.Warn($"Lyrics load: {ex.Message}"); result = LyricsResult.NotFound; }

        if (ct.IsCancellationRequested) return;
        Dispatcher.Invoke(() => { if (!ct.IsCancellationRequested) ApplyLyrics(result); });
    }

    private void ResetLineMetrics()
    {
        _lineTop = Array.Empty<double>();
        _lineHeight = Array.Empty<double>();
        _measuredWidth = -1;
    }

    private void ApplyLyrics(LyricsResult r)
    {
        _lyricsKind = r.Kind;
        _synced = r.Lines?.ToList();
        _plain = r.Plain;
        _lyricIndex = -1;
        _lyricBlocks.Clear();
        LyricsLines.Children.Clear();
        ResetLineMetrics();
        LyricsTranslate.Y = 0;

        switch (r.Kind)
        {
            case LyricsKind.Synced:
                LyricsFallback.Visibility = Visibility.Collapsed;
                foreach (var line in _synced!)
                {
                    var tb = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(line.Text) ? "\u266a" : line.Text,
                        FontSize = 15.5,   // colour-only highlight (no weight change)
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = _lyricFaint,
                        Margin = new Thickness(0, 4, 0, 4)
                    };
                    _lyricBlocks.Add(tb);
                    LyricsLines.Children.Add(tb);
                }
                UpdateLyricIndex(CurrentPosition, force: true);
                break;

            case LyricsKind.Plain:
                LyricsFallback.Visibility = Visibility.Collapsed;
                foreach (var raw in (_plain ?? string.Empty).Split('\n'))
                {
                    var tb = new TextBlock
                    {
                        Text = raw.Trim(),
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = _lyricFar,
                        Opacity = 0.8,
                        Margin = new Thickness(0, 3, 0, 3)
                    };
                    _lyricBlocks.Add(tb);
                    LyricsLines.Children.Add(tb);
                }
                SetMusicLyric("Lyrics (not synchronized)");
                break;

            case LyricsKind.Instrumental:
                ShowLyricsFallback("Instrumental");
                break;

            default:
                ShowLyricsFallback("No lyrics available");
                break;
        }

        RefreshPanelData();
    }

    private void ShowLyricsFallback(string text)
    {
        LyricsFallback.Text = text;
        LyricsFallback.Visibility = Visibility.Visible;
        SetMusicLyric("");
        SetCapsuleLyric(TitleLine(), true);
    }

    private void UpdateLyricIndex(TimeSpan pos, bool force = false)
    {
        if (_synced is not { Count: > 0 }) return;
        int idx = -1;
        for (int i = _synced.Count - 1; i >= 0; i--)
            if (_synced[i].Time <= pos) { idx = i; break; }

        if (!force && idx == _lyricIndex) return;
        _lyricIndex = idx;

        // Colour + opacity only. A font-weight or per-line RenderTransform would
        // force WPF to re-rasterise / re-measure text on every repaint (the window
        // repaints 25x/sec for the visualizer), which measured ~2 pp of CPU.
        for (int i = 0; i < _lyricBlocks.Count; i++)
        {
            var tb = _lyricBlocks[i];
            if (i == idx)
            {
                tb.Foreground = _lyricActive;
                tb.Opacity = 1.0;
            }
            else
            {
                int d = Math.Abs(i - idx);
                tb.Foreground = d <= 2 ? _lyricNear : _lyricFar;
                tb.Opacity = d == 1 ? 0.62 : d == 2 ? 0.34 : 0.16;
            }
        }

        var line = idx >= 0 && idx < _lyricBlocks.Count ? _lyricBlocks[idx].Text : "";
        bool title = string.IsNullOrWhiteSpace(line);
        SetMusicLyric(title ? "" : line);
        SetCapsuleLyric(title ? TitleLine() : line, title);
        UpdateSweep();

        if (_lyricsPage) ScrollToActiveLine(idx);
    }

    /// <summary>
    /// Glides the lyric stack so the active line sits centred. Line offsets are
    /// cached, so a line change costs one arithmetic step plus a single transform
    /// animation ---- no text re-measure. Duration scales with distance so a one-line
    /// step feels immediate while a seek-driven jump still reads as a smooth scroll.
    /// </summary>
    private void ScrollToActiveLine(int idx, bool instant = false)
    {
        if (_lyricBlocks.Count == 0) return;
        if (!EnsureLinesMeasured()) return;      // wait until the width is final

        double viewport = LyricsViewport.ActualHeight;
        double target = 0;
        if (idx >= 0 && idx < _lineTop.Length)
            target = viewport / 2.0 - (_lineTop[idx] + _lineHeight[idx] / 2.0);

        if (instant || !_animationsOn)
        {
            _springLyricY.Snap(target);
            LyricsTranslate.Y = target;
            return;
        }

        // Retarget the spring: it keeps the current velocity, so a fast run of line
        // changes glides instead of restarting.
        _springLyricY.Aim(target);
        StartMotion();
    }

    /// <summary>
    /// Measures every lyric line once, with the scroll transform neutralised.
    /// Returns false while the capsule is still resizing or has not been laid out,
    /// because the viewport width (and therefore every wrapped line height) is not
    /// final yet.
    /// </summary>
    private bool EnsureLinesMeasured()
    {
        double width = LyricsViewport.ActualWidth;
        if (width < 120 || _geomMoving) return false;      // collapsed or mid-resize
        if (_measuredWidth == width && _lineTop.Length == _lyricBlocks.Count) return true;

        double saved = _springLyricY.Value;
        LyricsTranslate.Y = 0;
        LyricsLines.UpdateLayout();

        int n = _lyricBlocks.Count;
        if (_lineTop.Length != n) { _lineTop = new double[n]; _lineHeight = new double[n]; }
        for (int i = 0; i < n; i++)
        {
            var tb = _lyricBlocks[i];
            _lineTop[i] = tb.TransformToAncestor(LyricsLines).Transform(new Point(0, 0)).Y;
            _lineHeight[i] = tb.ActualHeight;
        }
        _measuredWidth = width;
        LyricsTranslate.Y = saved;
        return true;
    }

    // -------------------------------------------------------------------- audio
    private void EnsureCapture()
    {
        if (_captureFailed || !_settings.Current.VisualizerEnabled) return;

        var pid = ProcessLoopbackCapture.FindSpotifyProcessId();
        if (pid == null) return;
        if (_capture.IsRunning && _capturePid == pid.Value) return;

        StopCapture();
        _capturePid = pid.Value;
        if (!_capture.Start(pid.Value)) Log.Warn("Audio capture: failed to start");
    }

    private void StopCapture()
    {
        if (_capture.IsRunning) _capture.Stop();
        IdleVisuals();
    }

    private void OnCaptureFailed(object? s, string reason) => Dispatcher.BeginInvoke(() =>
    {
        _captureFailed = true;
        IdleVisuals();
        Log.Warn($"Audio capture reported: {reason}");
    });

    // --------------------------------------------------------------- frame loop
    private void UpdateFrameTimer()
    {
        bool need = IsVisible && !_idleHidden && _track?.Status == 2;
        if (need && !_frameTimer.IsEnabled) _frameTimer.Start();
        else if (!need && _frameTimer.IsEnabled) { _frameTimer.Stop(); IdleVisuals(); }
        HookSweep(need);
    }

    private void OnFrameTick(object? s, EventArgs e)
    {
        var t = _track;
        if (t?.Status != 2) return;

        if (_settings.Current.VisualizerEnabled && _state == IslandState.Expanded)
        {
            _capture.ReadSnapshot(_levels);
            CurrentVisualizer().SetLevels(_levels);
        }

        long now = Environment.TickCount64;
        if (now - _lastResyncMs >= ResyncIntervalMs) { _lastResyncMs = now; ResyncPosition(); }

        var pos = CurrentPosition;
        if (_state == IslandState.Expanded && now - _lastProgressMs >= 200)
        {
            _lastProgressMs = now;
            UpdateProgressUi(pos);
        }
        UpdateSweep();
        if (_lyricsKind == LyricsKind.Synced) UpdateLyricIndex(pos);
    }

    /// <summary>
    /// SMTC only refreshes its timeline every few seconds, so we interpolate. This
    /// nudges the estimate forward when SMTC reports a newer position than we have,
    /// which keeps lyrics tight without ever jumping backwards.
    /// </summary>
    private void ResyncPosition()
    {
        var session = _smtc.CurrentSession;
        if (session == null) return;
        try
        {
            var tl = session.GetTimelineProperties();
            if (tl == null) return;
            var reported = tl.Position;
            var expected = CurrentPosition;
            if ((reported - expected).TotalMilliseconds > 250)
            {
                _pos = reported;
                _posStamp = Environment.TickCount64;
            }
        }
        catch { }
    }

    private Visualizer CurrentVisualizer() => VizC;

    private void IdleVisuals()
    {
        VizC.SetIdle();
    }

    private TimeSpan CurrentPosition
    {
        get
        {
            var t = _track;
            if (t == null) return TimeSpan.Zero;
            var p = _pos;
            if (t.Status == 2)
            {
                p += TimeSpan.FromMilliseconds(Environment.TickCount64 - _posStamp);
                if (t.HasDuration && p > t.Duration) p = t.Duration;
            }
            return p;
        }
    }

    private void UpdateProgressUi() => UpdateProgressUi(CurrentPosition);

    private void UpdateProgressUi(TimeSpan pos)
    {
        var t = _track;
        if (t?.HasDuration != true)
        {
            ProgressSlider.Maximum = 0; ProgressSlider.Value = 0;
            CurrentTime.Text = Duration.Text = "";
            return;
        }
        double max = t.Duration.TotalSeconds;
        if (Math.Abs(ProgressSlider.Maximum - max) > 0.5) ProgressSlider.Maximum = max;
        if (!_isDraggingSlider) ProgressSlider.Value = pos.TotalSeconds;
        CurrentTime.Text = FormatTime(pos);
        Duration.Text = FormatTime(t.Duration);
    }

    // ------------------------------------------------------------ state machine
    private IslandState BaseState() => _track?.Status == 2 ? IslandState.Playing : IslandState.Idle;

    private FrameworkElement PanelOf(IslandState s) => s switch
    {
        // Playing stays collapsed as the pill;
        // the lyric viewport only appears on the expanded LYRICS surface.
        IslandState.Idle => IdlePanel,
        IslandState.Playing => IdlePanel,
        _ => ExpandedPanel
    };

    private void SetState(IslandState s)
    {
        if (_state == s) { RefreshPanelData(); return; }
        var prev = _state;
        _state = s;

        var (w, h, r, fl, ms) = Metrics(s, prev);
        ShowPanel(s);
        HidePanel(prev, s);
        AnimateNotch(w, h, r, fl, ms);
        FitWindow();
        PositionWindow(w);
        if (s == IslandState.Expanded) SyncPage();
        RefreshPanelData();
        UpdateSweep();

        if (_uiProbe)
        {
            var settled = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            settled.Tick += (_, _) => { settled.Stop(); LogUi("settled"); };
            settled.Start();
        }
    }

    private (double W, double H, double R, double Fl, int Ms) Metrics(IslandState s, IslandState prev)
    {
        bool small = prev == IslandState.Idle || s == IslandState.Idle;
        int ms = small ? 260 : 300;
        // Bottom radius + top flare from the design metrics: small
        // surfaces (COMPACT/LINE) use flare 16, the big MUSIC/MENU panels 28/20.
        // Collapsed: width follows the lyric text, height 36, radius = h/2.
        return s switch
        {
            // Collapsed = LINE surface. Width tracks the current lyric
            // line (clamp 124..440), height 36, corner = h/2.
            IslandState.Idle => (CollapsedWidth(), 36, 18, 16, 240),
            IslandState.Playing => (CollapsedWidth(), 36, 18, 16, ms),
            // Expanded: MUSIC 432x176 (page 1) or LYRICS 576x176 (page 2).
            _ => _lyricsPage ? (576, 176, 32, 28, ms) : (432, 176, 32, 28, ms)
        };
    }

    private void ShowPanel(IslandState s)
    {
        var el = PanelOf(s);
        // Watch?unwatch transitions (Idle?Playing) share one COMPACT panel: never
        // re-fade an element that is already up, or the pill would blink.
        if (el.Visibility == Visibility.Visible) { el.BeginAnimation(OpacityProperty, null); el.Opacity = 1; return; }
        _panelGen++;
        el.Visibility = Visibility.Visible;
        el.BeginAnimation(OpacityProperty, null);
        if (!_animationsOn) { el.Opacity = 1; return; }
        el.Opacity = 0;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        { BeginTime = TimeSpan.FromMilliseconds(60), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void HidePanel(IslandState from, IslandState to)
    {
        var el = PanelOf(from);
        if (el == PanelOf(to)) return;
        int gen = ++_panelGen;
        if (!_animationsOn)
        {
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = 1;
            el.Visibility = Visibility.Collapsed;
            return;
        }
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130));
        fade.Completed += (_, _) =>
        {
            if (gen != _panelGen) return;
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = 1;
            if (PanelOf(_state) != el) el.Visibility = Visibility.Collapsed;
        };
        el.BeginAnimation(OpacityProperty, fade);
    }

    private void AnimateNotch(double w, double h, double r, double fl, int ms)
    {
        if (!_animationsOn)
        {
            _springW.Snap(w); _springH.Snap(h); _springR.Snap(r);
            ApplyGeometry(w, h, r, fl);
            StopMotion();
            return;
        }

        _springW.Aim(w);
        _springH.Aim(h);
        _springR.Aim(r);
        CornerHelper.SetFlare(Notch, fl);
        CornerHelper.SetFlare(NotchShadow, fl);
        _geomMoving = true;
        StartMotion();
    }

    private void ApplyGeometry(double w, double h, double r, double fl)
    {
        Notch.Width = w; Notch.Height = h; CornerHelper.SetBottomRadius(Notch, r);
        CornerHelper.SetFlare(Notch, fl);
        // The background shapes overhang the body by ``fl`` on each side (the
        // flare), so they are 2*fl wider than the content box.
        NotchShadow.Width = w + 2 * fl; NotchShadow.Height = h;
        CornerHelper.SetBottomRadius(NotchShadow, r); CornerHelper.SetFlare(NotchShadow, fl);
        NotchShape.Width = w + 2 * fl; NotchShape.Height = h;
        WashShape.Width = w + 2 * fl; WashShape.Height = h;
        CornerHelper.SetBottomRadius(WashShape, r); CornerHelper.SetFlare(WashShape, fl);
    }

    // ------------------------------------------------------------- motion engine
    private void StartMotion()
    {
        if (_motionOn) return;
        _motionOn = true;
        _lastFrameTicks = 0;
        CompositionTarget.Rendering += OnCompositionFrame;
    }

    private void StopMotion()
    {
        if (!_motionOn) return;
        _motionOn = false;
        CompositionTarget.Rendering -= OnCompositionFrame;
    }

    private void OnCompositionFrame(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        double dt = _lastFrameTicks == 0 ? 0.016 : (now - _lastFrameTicks) / 1000.0;
        _lastFrameTicks = now;

        // Only touch the layout when the value actually moved: assigning an
        // unchanged Width/Height would invalidate layout and re-render every frame
        // even though only the lyric spring is running.
        double w = _springW.Step(dt);
        if (Math.Abs(Notch.Width - w) > 0.01)
        {
            Notch.Width = w;
            double fl = CornerHelper.GetFlare(Notch);
            NotchShape.Width = w + 2 * fl;
            NotchShadow.Width = w + 2 * fl;
            WashShape.Width = w + 2 * fl;
        }

        double h = _springH.Step(dt);
        if (Math.Abs(Notch.Height - h) > 0.01)
        {
            Notch.Height = h;
            NotchShape.Height = h;
            NotchShadow.Height = h;
            WashShape.Height = h;
        }

        double r = _springR.Step(dt);
        if (Math.Abs(CornerHelper.GetBottomRadius(Notch) - r) > 0.01)
        {
            CornerHelper.SetBottomRadius(Notch, r);
            CornerHelper.SetBottomRadius(NotchShadow, r);
        }

        double y = _springLyricY.Step(dt);
        if (Math.Abs(LyricsTranslate.Y - y) > 0.01) LyricsTranslate.Y = y;

        // Only once the capsule has reached its final size does the lyric viewport
        // have its real width ---- measuring sooner produces nonsense line offsets.
        if (_geomMoving && _springW.Settled && _springH.Settled && _springR.Settled)
        {
            _geomMoving = false;
            if (_lyricsPage)
            {
                EnsureLinesMeasured();
                ScrollToActiveLine(_lyricIndex, instant: true);
            }
        }

        bool busy = !(_springW.Settled && _springH.Settled && _springR.Settled && _springLyricY.Settled);
        if (!busy) StopMotion();
    }

    private void RefreshPanelData()
    {
        switch (_state)
        {
            case IslandState.Expanded:
                if (_lyricsPage) ScrollToActiveLine(_lyricIndex, instant: true);
                UpdateProgressUi();
                Dispatcher.BeginInvoke(new Action(() => { UpdateMarquee(); UpdateSweep(); }),
                    System.Windows.Threading.DispatcherPriority.Loaded);
                break;
        }
    }

    // -------------------------------------------------------------- interaction
    private void Notch_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _mouseInside = true;
        _collapseTimer.Stop();
    }

    private void Notch_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _mouseInside = false;
        if (_state == IslandState.Expanded && !_isDraggingSlider) _collapseTimer.Start();
    }

    private void OnCollapseTick(object? s, EventArgs e)
    {
        _collapseTimer.Stop();
        if (_mouseInside || _isDraggingSlider) return;
        if (_state == IslandState.Expanded) SetState(BaseState());
    }

    private void Idle_Click(object sender, RoutedEventArgs e)
    {
        if (_track?.HasTrack == true) SetState(IslandState.Expanded);
    }

    private void Playing_Click(object sender, RoutedEventArgs e) => SetState(IslandState.Expanded);

    private void Expanded_Click(object sender, RoutedEventArgs e) => SetState(BaseState());

    /// <summary>Toggles the expanded panel between the MUSIC and LYRICS pages.</summary>
    private void LyricsPage_Click(object sender, RoutedEventArgs e)
    {
        _lyricsPage = !_lyricsPage;
        SyncPage();
        if (_state == IslandState.Expanded)
        {
            var (w, h, r, fl, ms) = Metrics(IslandState.Expanded, IslandState.Expanded);
            AnimateNotch(w, h, r, fl, ms);
            FitWindow();
            PositionWindow(w);
            if (_lyricsPage) { EnsureLinesMeasured(); ScrollToActiveLine(_lyricIndex, instant: true); }
        }
        e.Handled = true;
    }

    private void SyncPage()
    {
        // MUSIC  = single column filling the panel.
        // LYRICS = left column (cover/title/progress/controls) + divider + lyric column.
        LeftCol.Width = new GridLength(1, GridUnitType.Star);
        DividerCol.Width = _lyricsPage ? GridLength.Auto : new GridLength(0);
        LyricsCol.Width = _lyricsPage ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        PageDivider.Visibility = _lyricsPage ? Visibility.Visible : Visibility.Collapsed;
        LyricsViewport.Visibility = _lyricsPage ? Visibility.Visible : Visibility.Collapsed;
        MusicLyricLine.Visibility = _lyricsPage ? Visibility.Collapsed : Visibility.Visible;
        LyricsTranslate.Y = 0;
        UpdateDots();
        UpdateWash();

        // Lyrics need one layout pass at their real width before they can be centred
        // (matters most when the track is paused and the frame loop is idle).
        if (_lyricsPage)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                EnsureLinesMeasured();
                ScrollToActiveLine(_lyricIndex, instant: true);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ----------------------------------------------------------------- controls
    private void PlayPause_Click(object sender, RoutedEventArgs e) => _ = _smtc.TogglePlayPauseAsync();
    private void Prev_Click(object sender, RoutedEventArgs e) => _ = _smtc.PreviousAsync();
    private void Next_Click(object sender, RoutedEventArgs e) => _ = _smtc.NextAsync();

    // --------------------------------------------------------------- visibility
    private void ShowIsland()
    {
        if (!IsVisible) { Show(); PositionWindow(Notch.Width); }
        Opacity = 1.0;
        _idleHidden = false;
        UpdateFrameTimer();
    }

    private void HideIsland()
    {
        Opacity = 0.0;
        Hide();
        _idleHidden = true;
        _frameTimer.Stop();
        StopMotion();
    }

    private void ToggleIsland()
    {
        if (_idleHidden || !IsVisible) ShowIsland();
        else HideIsland();
    }

    // ----------------------------------------------------------------- settings
    private void ApplySettings()
    {
        _animationsOn = _settings.Current.EnableAnimations;
        if (_startup.IsEnabled != _settings.Current.LaunchOnStartup)
            _startup.IsEnabled = _settings.Current.LaunchOnStartup;

        var viz = _settings.Current.VisualizerEnabled ? Visibility.Visible : Visibility.Collapsed;
        VizC.Visibility = viz;
        if (!_settings.Current.VisualizerEnabled) IdleVisuals();

        if (_settings.Current.StartMinimized) HideIsland();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        var win = new SettingsWindow(_settings) { Owner = IsVisible ? this : null };
        win.ShowDialog();
        ApplySettings();

        var (w, h, r, fl, _) = Metrics(_state, _state);
        AnimateNotch(w, h, r, fl, 0);
        PositionWindow(w);
        if (_track?.Status == 2) EnsureCapture();
    }

    private void CloseApp_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? s, EventArgs e)
    {
        _frameTimer.Stop();
        _collapseTimer.Stop();
        _artworkCts?.Cancel();
        _lyricsCts?.Cancel();
        _smtc.PlaybackChanged -= OnPlaybackChanged;
        _smtc.MediaChanged -= OnMediaChanged;
        _smtc.SessionChanged -= OnSessionChanged;
        _smtc.InitFailed -= OnInitFailed;
        _capture.Failed -= OnCaptureFailed;
        _capture.Dispose();
        _tray?.Dispose();
        _tray = null;
        _smtc.Dispose();
        _artwork.Dispose();
        _lyrics.Dispose();
    }

    // ------------------------------------------------------------- UI probe (dev)
    public void EnableUiProbe(bool cycle, int stepSeconds = 3, bool openLyricsPage = false)
    {
        _uiProbe = true;
        if (openLyricsPage) { _lyricsPage = true; SyncPage(); }
        Log.Info("[ui] probe enabled (cycle=" + cycle + ", lyricsPage=" + _lyricsPage + ")");
        LogUi("startup");

        var levelsProbe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        levelsProbe.Tick += (_, _) =>
        {
            _capture.ReadSnapshot(_levels);
            double vh = LyricsViewport.ActualHeight;
            double mid = -1;
            if (vh > 0 && _lyricIndex >= 0 && _lyricIndex < _lyricBlocks.Count)
            {
                var tb = _lyricBlocks[_lyricIndex];
                mid = tb.TransformToAncestor(LyricsViewport).Transform(new Point(0, 0)).Y + tb.ActualHeight / 2.0;
            }
            Log.Info($"[ui] levels active={_levels.Active} rms={_levels.Rms:F3} bass={_levels.Bass:F3} " +
                     $"mid={_levels.Mid:F3} treble={_levels.Treble:F3} beat={_levels.Beat:F2} " +
                     $"maxBar={MaxOf(_levels):F3} captureRunning={_capture.IsRunning} | " +
                     $"state={_state} lyricIdx={_lyricIndex} activeMid={mid:F1} wantMid={vh / 2.0:F1}");
        };
        levelsProbe.Start();

        if (!cycle)
        {
            var stop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
            stop.Tick += (_, _) => { stop.Stop(); LogUi("final"); Close(); };
            stop.Start();
            return;
        }

        _uiStateLock = true;
        var states = new[]
        {
            IslandState.Playing, IslandState.Expanded, IslandState.Playing,
            IslandState.Idle, IslandState.Playing
        };
        int i = 0;
        var step = new DispatcherTimer { Interval = TimeSpan.FromSeconds(stepSeconds) };
        step.Tick += (_, _) =>
        {
            if (i >= states.Length) { step.Stop(); _uiStateLock = false; Log.Info("[ui] cycle complete"); return; }
            SetState(states[i++]);
        };
        step.Start();

        var end = new DispatcherTimer { Interval = TimeSpan.FromSeconds(stepSeconds * states.Length + 2) };
        end.Tick += (_, _) => { end.Stop(); LogUi("final"); Close(); };
        end.Start();
    }

    private static float MaxOf(AudioLevels l)
    {
        float m = 0;
        foreach (var b in l.Bars) if (b > m) m = b;
        return m;
    }

    private void LogUi(string tag)
    {
        var src = PresentationSource.FromVisual(this);
        double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 0;
        double islandLeft = Left + (ActualWidth - Notch.ActualWidth) / 2.0;
        uint ex = _hwnd == IntPtr.Zero ? 0 : (uint)NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);

        double vh = LyricsViewport.ActualHeight;
        double aTop = -1, aH = -1, aMid = -1;
        if (_lyricsPage && vh > 0
            && _lyricIndex >= 0 && _lyricIndex < _lyricBlocks.Count)
        {
            var tb = _lyricBlocks[_lyricIndex];
            aTop = tb.TransformToAncestor(LyricsViewport).Transform(new Point(0, 0)).Y;
            aH = tb.ActualHeight;
            aMid = aTop + aH / 2.0;
        }

        Log.Info($"[ui] {tag} state={_state} notch={Notch.ActualWidth:F1}x{Notch.ActualHeight:F1} " +
                 $"win={ActualWidth:F1}x{ActualHeight:F1} winLeft={Left:F1} top={Top:F1} " +
                 $"islandLeft={islandLeft:F1} screenW={SystemParameters.PrimaryScreenWidth:F0} " +
                 $"screenCentre={(SystemParameters.PrimaryScreenWidth / 2):F1} " +
                 $"islandCentre={(islandLeft + Notch.ActualWidth / 2):F1} scale={scale:F2} " +
                 $"corner={CornerHelper.GetBottomRadius(Notch):F1} topmost={Topmost} " +
                 $"showInTaskbar={ShowInTaskbar} toolWindow={(ex & NativeMethods.WS_EX_TOOLWINDOW) != 0} " +
                 $"noActivate={(ex & NativeMethods.WS_EX_NOACTIVATE) != 0} " +
                 $"foregroundIsMe={_hwnd != IntPtr.Zero && NativeMethods.GetForegroundWindow() == _hwnd} " +
                 $"activated={IsActive} lyricIdx={_lyricIndex} lyricY={LyricsTranslate.Y:F1} " +
                 $"lyricLines={_lyricBlocks.Count} viewportH={vh:F1} activeTop={aTop:F1} " +
                 $"activeH={aH:F1} activeMid={aMid:F1} wantMid={vh / 2.0:F1} " +
                 $"idlePanel={IdlePanel.Visibility} lyricsPage={_lyricsPage} " +
                 $"musicLyricLine={MusicLyricLine.Visibility} " +
                 $"lyricViewLeft={LyricsViewport.TranslatePoint(new Point(0, 0), this).X:F0} " +
                 $"capsuleTitle={_showingTitle} sweepW={CapsuleSweep.Rect.Width:F0} capsule='{_capsuleText}' " +
                 $"tier={System.Windows.Media.RenderCapability.Tier >> 16} " +
                 $"expandedPanel={ExpandedPanel.Visibility} idleArt={(IdleArt.Source != null)} " +
                 $"placeholderShown={IdlePlaceholder.Visibility}");
    }

    // ------------------------------------------------------------------- layout
    /// <summary>Keep the layered window only as large as the current surface needs.
    /// A WPF <c>AllowsTransparency</c> window is composited in software, so its area
    /// directly sets the per-frame cost ---- a fixed 660x260 window wasted ~10x the pixels
    /// the collapsed pill actually uses. Resized on state change only, never per frame.</summary>
    private void FitWindow()
    {
        double iw = _state == IslandState.Expanded ? (_lyricsPage ? 576 : 432) : 440;
        double ih = _state == IslandState.Expanded ? 176 : 36;
        Width = iw + 2 * 16 + 48;    // flare + drop-shadow margin
        Height = ih + 48;
    }

    private void PositionWindow(double islandWidth)
    {
        var wa = SystemParameters.WorkArea;
        double x = _settings.Current.HorizontalAlign switch
        {
            HorizontalAlign.Left => wa.Left + 12,
            HorizontalAlign.Right => wa.Right - islandWidth - 12,
            _ => wa.Left + (wa.Width - islandWidth) / 2.0
        };

        Left = x - (Width - islandWidth) / 2.0;
        Top = _settings.Current.VerticalOffset;
    }

    private static string FormatTime(TimeSpan ts) =>
        ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
}

internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

    public static void ApplyExStyle(IntPtr hwnd)
    {
        var s = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, s | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }
}
