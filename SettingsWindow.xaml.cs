using System.Windows;
using System.IO;
using FloatIsland.Models;
using FloatIsland.Services;

namespace FloatIsland;

public partial class SettingsWindow : Window
{
    private readonly SettingsManager _manager;

    public SettingsWindow(SettingsManager manager)
    {
        _manager = manager;
        InitializeComponent();

        var s = manager.Current;
        LaunchBox.IsChecked = s.LaunchOnStartup;
        StartMinBox.IsChecked = s.StartMinimized;
        ShowOnPlayBox.IsChecked = s.ShowWhenMusicStarts;
        HideOnStopBox.IsChecked = s.HideWhenMusicStops;
        CollapseIdleBox.IsChecked = s.CollapseWhenIdle;
        AnimationsBox.IsChecked = s.EnableAnimations;
        VisualizerBox.IsChecked = s.VisualizerEnabled;
        LyricsSourceCombo.SelectedIndex = (int)s.LyricsSource;
        LyricsFolderBox.Text = s.LyricsFolder;
        AlignCombo.SelectedIndex = (int)s.HorizontalAlign;
        OffsetSlider.Value = s.VerticalOffset;
        OffsetValue.Text = $"{s.VerticalOffset} px";

        OffsetSlider.ValueChanged += (_, e) => OffsetValue.Text = $"{(int)e.NewValue} px";
    }

    private void BrowseLyrics_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select a folder containing .lrc or .txt lyrics",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(LyricsFolderBox.Text) ? LyricsFolderBox.Text : ""
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            LyricsFolderBox.Text = dlg.SelectedPath;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _manager.Current;
        s.LaunchOnStartup = LaunchBox.IsChecked == true;
        s.StartMinimized = StartMinBox.IsChecked == true;
        s.ShowWhenMusicStarts = ShowOnPlayBox.IsChecked == true;
        s.HideWhenMusicStops = HideOnStopBox.IsChecked == true;
        s.CollapseWhenIdle = CollapseIdleBox.IsChecked == true;
        s.EnableAnimations = AnimationsBox.IsChecked == true;
        s.VisualizerEnabled = VisualizerBox.IsChecked == true;
        s.LyricsSource = (LyricsSource)Math.Max(0, LyricsSourceCombo.SelectedIndex);
        s.LyricsFolder = LyricsFolderBox.Text.Trim();
        s.HorizontalAlign = (HorizontalAlign)Math.Max(0, AlignCombo.SelectedIndex);
        s.VerticalOffset = (int)OffsetSlider.Value;

        _manager.Save();
        Close();
    }
}
