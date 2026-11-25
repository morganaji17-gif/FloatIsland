using Windows.Media.Control;
using Windows.Storage.Streams;

namespace FloatIsland.Models;

public record TrackInfo
{
    /// <summary>Upper bound on the LastUpdatedTime age we trust when reconstructing the live
    /// position. Spotify's measured re-publish cadence is ~4.5 s; anything much older means the
    /// source is not advancing its timeline and should not be extrapolated.</summary>
    private static readonly TimeSpan MaxTimelineAge = TimeSpan.FromSeconds(8);

    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? SourceAppId { get; init; }
    public bool IsSpotify { get; init; }
    public int Status { get; init; } // 0=stopped, 1=paused, 2=playing
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public bool HasDuration => Duration > TimeSpan.Zero;
    public IRandomAccessStreamReference? Thumbnail { get; init; }
    public string? ThumbnailCacheKey { get; init; }
    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);

    public static TrackInfo FromSession(GlobalSystemMediaTransportControlsSession? session)
    {
        var result = new TrackInfo { SourceAppId = session?.SourceAppUserModelId ?? "" };
        if (session == null) return result;
        try
        {
            var playback = session.GetPlaybackInfo();
            result = result with
            {
                Status = playback?.PlaybackStatus switch
                {
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => 2,
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => 1,
                    _ => 0
                },
                IsSpotify = IsSpotifyApp(session.SourceAppUserModelId)
            };
        }
        catch { }
        try
        {
            var timeline = session.GetTimelineProperties();
            if (timeline != null)
            {
                var position = timeline.Position;

                // Windows (and Spotify) publish timeline properties periodically, not every
                // frame, and LastUpdatedTime says when the reported Position was true. Measured
                // against a live Spotify session, it re-publishes every ~4.5 s, so the raw
                // Position reads up to ~3 s behind the audio - which is exactly the "lyrics are
                // out of sync" symptom. While playing, the live position is the reported value
                // plus the time since that stamp. If an app reported live positions it would
                // also re-stamp LastUpdatedTime, so this stays correct; the age is capped as a
                // sanity bound for a misbehaving source.
                if (result.Status == 2 && timeline.LastUpdatedTime > DateTimeOffset.MinValue)
                {
                    var age = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                    if (age > TimeSpan.Zero && age < MaxTimelineAge)
                        position += age;
                }

                var duration = timeline.EndTime - timeline.StartTime;
                if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                if (duration > TimeSpan.Zero && position > duration) position = duration;

                result = result with { Position = position, Duration = duration };
            }
        }
        catch { }
        return result;
    }

    public static bool IsSpotifyApp(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var normalized = id.Replace("_", "").Replace("-", "");
        return normalized.Contains("spotify", StringComparison.OrdinalIgnoreCase);
    }
}
