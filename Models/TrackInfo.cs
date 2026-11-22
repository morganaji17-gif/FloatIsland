using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SonoIsland.Models;

public record TrackInfo
{
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
                result = result with { Position = timeline.Position, Duration = timeline.EndTime - timeline.StartTime };
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
