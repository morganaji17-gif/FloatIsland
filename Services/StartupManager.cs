using Microsoft.Win32;

namespace SonoIsland.Services;

public sealed class StartupManager
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "SonoIsland";
    private readonly string _exePath = Environment.ProcessPath ?? "";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                return key?.GetValue(AppName) != null;
            }
            catch { return false; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                if (key == null) return;
                if (value && !string.IsNullOrEmpty(_exePath))
                    key.SetValue(AppName, $"\"{_exePath}\"");
                else
                    key.DeleteValue(AppName, false);
            }
            catch (Exception ex)
            {
                Log.Error($"Startup: {ex.Message}");
            }
        }
    }
}
