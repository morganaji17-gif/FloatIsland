using System.Text.Json.Serialization;

namespace SonoIsland.Models;

public sealed class AppSettings
{
    [JsonPropertyName("launchOnStartup")]
    public bool LaunchOnStartup { get; set; }

    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; set; }

    [JsonPropertyName("showWhenMusicStarts")]
    public bool ShowWhenMusicStarts { get; set; } = true;

    [JsonPropertyName("hideWhenMusicStops")]
    public bool HideWhenMusicStops { get; set; } = true;

    [JsonPropertyName("collapseWhenIdle")]
    public bool CollapseWhenIdle { get; set; } = true;

    [JsonPropertyName("enableAnimations")]
    public bool EnableAnimations { get; set; } = true;

    /// <summary>Pixels from the top of the display. 0 → flush against the top edge.</summary>
    [JsonPropertyName("verticalOffset")]
    public int VerticalOffset { get; set; }

    [JsonPropertyName("horizontalAlign")]
    public HorizontalAlign HorizontalAlign { get; set; } = HorizontalAlign.Center;

    [JsonPropertyName("visualizerEnabled")]
    public bool VisualizerEnabled { get; set; } = true;

    [JsonPropertyName("lyricsSource")]
    public LyricsSource LyricsSource { get; set; } = LyricsSource.Auto;

    /// <summary>Folder scanned for user-supplied .lrc / .txt lyrics.</summary>
    [JsonPropertyName("lyricsFolder")]
    public string LyricsFolder { get; set; } = string.Empty;
}

public enum HorizontalAlign { Left = 0, Center = 1, Right = 2 }

public enum LyricsSource { Auto = 0, Lrclib = 1, Local = 2, None = 3 }
