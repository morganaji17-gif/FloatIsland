using System.IO;
using System.Text.Json;
using FloatIsland.Models;

namespace FloatIsland.Services;

public sealed class SettingsManager
{
    private readonly string _path;
    private readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public AppSettings Current { get; private set; }

    public SettingsManager()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloatIsland");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        Current = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Settings load: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(Current, _opts);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            Log.Error($"Settings save: {ex.Message}");
        }
    }
}
