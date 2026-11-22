using System.IO;

namespace FloatIsland.Services;

public static class Log
{
    private static readonly string LogDir;
    private static readonly object Locker = new();
    private const long MaxLogBytes = 5 * 1024 * 1024; // rotate at 5 MB

    static Log()
    {
        LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FloatIsland", "Logs");
        Directory.CreateDirectory(LogDir);
    }

    private static string CurrentFile => Path.Combine(LogDir, $"floatisland_{DateTime.Now:yyyyMMdd}.log");

    private static void Write(string level, string message)
    {
        lock (Locker)
        {
            try
            {
                var file = CurrentFile;

                // Rotate: oversized or stale log gets renamed and replaced
                if (File.Exists(file) && new FileInfo(file).Length > MaxLogBytes)
                {
                    var backup = Path.Combine(LogDir,
                        $"floatisland_{DateTime.Now:yyyyMMdd}_{DateTime.Now:HHmmss}.old.log");
                    File.Move(file, backup);
                    PruneOldBackups();
                }

                var entry = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
                File.AppendAllText(file, entry + "\n");
            }
            catch { /* never throw from logging */ }
        }
    }

    /// <summary>Keeps at most the 3 most recent rotated backups.</summary>
    private static void PruneOldBackups()
    {
        try
        {
            var backups = Directory.GetFiles(LogDir, "*.old.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .ToList();
            foreach (var old in backups.Skip(3))
                old.Delete();
        }
        catch { }
    }

    public static void Info(string m)  => Write("INFO", m);
    public static void Warn(string m)  => Write("WARN", m);
    public static void Error(string m) => Write("ERROR", m);
    public static void Debug(string m) => Write("DEBUG", m);
}
