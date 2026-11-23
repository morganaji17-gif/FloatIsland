using System.Text.Json.Serialization;

namespace FloatIsland.Models;

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

    /// <summary>Nudge for lyric timing, in ms. Positive shows lines later (delay), negative earlier.</summary>
    [JsonPropertyName("lyricsOffsetMs")]
    public int LyricsOffsetMs { get; set; }

    /// <summary>Global media hotkeys: Ctrl+Alt+Space (play/pause), Ctrl+Alt+Left/Right (prev/next).</summary>
    [JsonPropertyName("mediaHotkeys")]
    public bool MediaHotkeys { get; set; } = true;

    /// <summary>Hide the island while a fullscreen app (game, fullscreen video) is in the foreground.</summary>
    [JsonPropertyName("hideWhenFullscreen")]
    public bool HideWhenFullscreen { get; set; } = true;

    /// <summary>Overall size multiplier for the island (0.8 - 1.4). 1.0 = the designed size.</summary>
    [JsonPropertyName("uiScale")]
    public double UiScale { get; set; } = 1.0;

    /// <summary>Whole-island opacity (0.4 - 1.0).</summary>
    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 1.0;

    /// <summary>Accent colour override; <see cref="AccentMode.Album"/> follows the cover art.</summary>
    [JsonPropertyName("accent")]
    public AccentMode Accent { get; set; } = AccentMode.Album;

    /// <summary>Monitor to sit on: -1 follows the pointer, otherwise a 0-based monitor index.</summary>
    [JsonPropertyName("monitorIndex")]
    public int MonitorIndex { get; set; } = -1;
}

public enum AccentMode { Album = 0, Green = 1, Blue = 2, Pink = 3, Amber = 4, Mono = 5 }

public enum HorizontalAlign { Left = 0, Center = 1, Right = 2 }

public enum LyricsSource { Auto = 0, Lrclib = 1, Local = 2, None = 3 }
