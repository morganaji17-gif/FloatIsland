using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media.Control;
using SonoIsland.Models;

namespace SonoIsland.Services;

public sealed class SMTCManager : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _disposed;

    public event EventHandler? PlaybackChanged;
    public event EventHandler? SessionChanged;
    public event EventHandler? MediaChanged;
    public event EventHandler? InitFailed;

    public bool IsInitialized { get; private set; }
    public bool IsSpotifyActive => _session != null;
    public GlobalSystemMediaTransportControlsSession? CurrentSession => _session;

    private bool _eventGuard;

    public async Task InitializeAsync()
    {
        if (IsInitialized) return;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_manager == null) { InitFailed?.Invoke(this, EventArgs.Empty); return; }
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager.SessionsChanged += OnSessionsChanged;
            IsInitialized = true;
            AttachPreferredSession();
        }
        catch (Exception ex)
        {
            Log.Error($"SMTC init: {ex.Message}");
            InitFailed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnCurrentSessionChanged(object sender, CurrentSessionChangedEventArgs e)
    {
        if (_eventGuard) return;
        _eventGuard = true;
        try
        {
            AttachPreferredSession();
            SessionChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _eventGuard = false; }
    }

    private void OnSessionsChanged(object sender, SessionsChangedEventArgs e)
    {
        if (_eventGuard) return;
        _eventGuard = true;
        try
        {
            AttachPreferredSession();
            SessionChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _eventGuard = false; }
    }

    private void OnPlaybackInfoChanged(object sender, PlaybackInfoChangedEventArgs e)
        => PlaybackChanged?.Invoke(this, EventArgs.Empty);

    private void OnTimelineChanged(object sender, TimelinePropertiesChangedEventArgs e)
        => PlaybackChanged?.Invoke(this, EventArgs.Empty);

    private void OnMediaChanged(object sender, MediaPropertiesChangedEventArgs e)
        => MediaChanged?.Invoke(this, EventArgs.Empty);

    private void AttachPreferredSession()
    {
        if (_manager == null) return;
        var preferred = SelectSpotifySession();
        if (ReferenceEquals(preferred, _session)) return;
        DetachCurrent();
        _session = preferred;
        if (_session != null)
        {
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
            _session.MediaPropertiesChanged += OnMediaChanged;
        }
        Log.Info($"SMTC: spotify session -> {(_session != null ? $"'{_session.SourceAppUserModelId}'" : "none")}");
        MediaChanged?.Invoke(this, EventArgs.Empty);
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    private GlobalSystemMediaTransportControlsSession? SelectSpotifySession()
    {
        try
        {
            var sessions = _manager!.GetSessions();
            if (sessions == null || sessions.Count == 0) return null;
            foreach (var s in sessions)
            {
                var id = s.SourceAppUserModelId ?? "<null>";
                Log.Debug($"SMTC: session '{id}'");
            }
            for (int i = 0; i < sessions.Count; i++)
                if (TrackInfo.IsSpotifyApp(sessions[i].SourceAppUserModelId)) return sessions[i];
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"SMTC GetSessions: {ex.Message}");
            return null;
        }
    }

    private void DetachCurrent()
    {
        if (_session == null) return;
        try
        {
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
            _session.MediaPropertiesChanged -= OnMediaChanged;
        }
        catch { }
        _session = null;
    }

    public async Task<TrackInfo?> GetCurrentTrackAsync()
    {
        if (_session == null) return null;
        try
        {
            var snap = TrackInfo.FromSession(_session);
            try
            {
                var props = await _session.TryGetMediaPropertiesAsync();
                if (props != null)
                    return snap with
                    {
                        Title = props.Title,
                        Artist = props.Artist,
                        Album = props.AlbumTitle,
                        Thumbnail = props.Thumbnail,
                        ThumbnailCacheKey = $"{_session.SourceAppUserModelId}|{props.Title}|{props.Artist}"
                    };
            }
            catch (Exception ex) { Log.Debug($"SMTC props: {ex.Message}"); }
            return snap;
        }
        catch (Exception ex)
        {
            Log.Warn($"SMTC snapshot: {ex.Message}");
            return null;
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_session == null) return;
        try { await _session.TryTogglePlayPauseAsync(); }
        catch (Exception ex) { Log.Warn($"SMTC toggle: {ex.Message}"); }
    }

    public async Task NextAsync()
    {
        if (_session == null) return;
        try { await _session.TrySkipNextAsync(); }
        catch (Exception ex) { Log.Warn($"SMTC next: {ex.Message}"); }
    }

    public async Task PreviousAsync()
    {
        if (_session == null) return;
        try { await _session.TrySkipPreviousAsync(); }
        catch (Exception ex) { Log.Warn($"SMTC prev: {ex.Message}"); }
    }

    /// <summary>
    /// Seeks the current playback position to the given time.
    /// </summary>
    public async Task SeekAsync(TimeSpan position)
    {
        if (_session == null) return;
        try
        {
            // TryChangePlaybackPositionAsync takes a position in TICKS (100 ns), not
            // milliseconds — passing ms seeks to ~0. Verified against a live session.
            await _session.TryChangePlaybackPositionAsync(position.Ticks);
            Log.Debug($"SMTC seek → {position:mm\\:ss} ({position.Ticks} ticks)");
        }
        catch (Exception ex) { Log.Warn($"SMTC seek: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_manager != null)
            {
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                _manager.SessionsChanged -= OnSessionsChanged;
            }
            DetachCurrent();
            _manager = null;
        }
        catch { }
    }
}
