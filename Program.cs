using System.IO;
using System.Threading;
using FloatIsland.Overlay;
using FloatIsland.Services;

namespace FloatIsland;

/// <summary>
/// Entry point. The app is Skia-only: the default action is the Win32 layered overlay
/// (<see cref="SkiaMode"/>). The old WPF window and settings dialog are gone; every flag
/// here is inert unless explicitly passed.
/// </summary>
internal static class Program
{
    private const string MutexName = "FloatIsland_SingleInstance_v2";

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Domain: " + (e.ExceptionObject as Exception)?.Message);

        // Inert diagnostics - they only run when invoked on the command line.
        if (args.Length >= 1 && DiagnosticsFor(args) is { } diag)
        {
            try { Task.Run(diag).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Error("[diag] " + ex.Message); }
            return 0;
        }

        // Offline / one-shot modes must run even while an island instance is already up.
        // Behind the mutex they would silently no-op and "pass", which is exactly how a
        // render smoke test lies to you on a machine with the app running.
        if (OneShot(args) is { } once) return once;

        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // A second launch should not silently do nothing - ask the running island to show.
            var existing = OverlayWindow.FindExisting();
            if (existing != IntPtr.Zero)
            {
                OverlayWindow.RequestShow(existing);
                Log.Info("second launch: asked the running island to show");
            }
            else
            {
                Log.Info("second launch: another instance is running but its window was not found");
            }
            return 0;
        }

        Log.Info("=== Float Island starting ===");

        try
        {
            // Default, and `--skia`: the overlay.
            SkiaMode.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("fatal: " + ex);
            return 1;
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
        }
    }

    /// <summary>Runs a non-overlay mode (self-test, offscreen shots, benches, icon drawing).
    /// Returns null when the arguments do not select one.</summary>
    private static int? OneShot(string[] args)
    {
        try
        {
            if (args.Contains("--selftest", StringComparer.Ordinal))
                return SelfTest.Run();

            if (args.Contains("--make-icon", StringComparer.Ordinal))
            {
                IconMaker.MakeIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
                return 0;
            }

            if (args.Contains("--skia-shot", StringComparer.Ordinal))
            {
                SkiaMode.Shot();
                return 0;
            }

            if (args.Contains("--skia-bench", StringComparer.Ordinal))
            {
                SkiaMode.Bench(IntArg(args, "--skia-bench", 600));
                return 0;
            }

            if (args.Contains("--skia-demo", StringComparer.Ordinal))
            {
                SkiaMode.LiveDemo(IntArg(args, "--skia-demo", 6));
                return 0;
            }
        }
        catch (Exception ex)
        {
            Log.Error("fatal: " + ex);
            return 1;
        }
        return null;
    }

    private static int IntArg(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
    }

    private static Action? DiagnosticsFor(string[] args) => args[0] switch
    {
        "--selftest-capture" when args.Length >= 2 =>
            () => Diagnostics.CaptureSelfTest(args[1],
                args.Length >= 3 && int.TryParse(args[2], out var a) ? a : 10),
        "--smtc-probe" =>
            () => Diagnostics.SmtcProbe(args.Length >= 2 && int.TryParse(args[1], out var b) ? b : 15),
        "--smtc-cmd" when args.Length >= 2 =>
            () => Diagnostics.SmtcCommand(args[1].ToLowerInvariant()),
        "--art-probe" =>
            () => Diagnostics.ArtworkProbe(
                args.Length >= 2 && int.TryParse(args[1], out var c) ? c : 2,
                args.Length >= 3 && int.TryParse(args[2], out var d) ? d : 6),
        "--lyrics-probe" =>
            () => Diagnostics.LyricsProbe(args.Length >= 2 ? args[1] : "all"),
        _ => null,
    };
}
