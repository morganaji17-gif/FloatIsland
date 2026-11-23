using Microsoft.Win32;

namespace FloatIsland.Services;

public sealed class StartupManager
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "FloatIsland";
    private readonly string _launchCommand = Command();

    /// <summary>The command that relaunches the app the same way it is running now. When started
    /// through <c>dotnet FloatIsland.dll</c> (the SKU Trend Micro blocks), register the runtime
    /// plus the assembly rather than a bare dotnet.exe that would open nothing.</summary>
    private static string Command()
    {
        var proc = Environment.ProcessPath ?? "";
        var entry = Environment.GetCommandLineArgs().FirstOrDefault() ?? "";
        if (proc.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entry))
            return $"\"{proc}\" \"{entry}\"";
        return $"\"{proc}\"";
    }

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
                if (value && !string.IsNullOrEmpty(_launchCommand))
                    key.SetValue(AppName, _launchCommand);
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
