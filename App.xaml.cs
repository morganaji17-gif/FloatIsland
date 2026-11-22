using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SonoIsland.Services;

namespace SonoIsland;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private const string MutexName = "SonoIsland_SingleInstance_v2";

    private void OnStartup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;

        // ── Diagnostics (see Services/Diagnostics.cs) ────────────────────
        // Run off the UI thread: the async helpers must not have their
        // continuations captured by the dispatcher that is blocking them.
        if (e.Args.Length >= 1)
        {
            Action? diag = e.Args[0] switch
            {
                "--selftest-capture" when e.Args.Length >= 2 =>
                    () => Diagnostics.CaptureSelfTest(e.Args[1],
                        e.Args.Length >= 3 && int.TryParse(e.Args[2], out var a) ? a : 10),
                "--smtc-probe" =>
                    () => Diagnostics.SmtcProbe(
                        e.Args.Length >= 2 && int.TryParse(e.Args[1], out var b) ? b : 15),
                "--smtc-cmd" when e.Args.Length >= 2 =>
                    () => Diagnostics.SmtcCommand(e.Args[1].ToLowerInvariant()),
                "--art-probe" =>
                    () => Diagnostics.ArtworkProbe(
                        e.Args.Length >= 2 && int.TryParse(e.Args[1], out var c) ? c : 2,
                        e.Args.Length >= 3 && int.TryParse(e.Args[2], out var d) ? d : 6),
                "--lyrics-probe" =>
                    () => Diagnostics.LyricsProbe(e.Args.Length >= 2 ? e.Args[1] : "all"),
                _ => null
            };

            if (diag != null)
            {
                Task.Run(diag).GetAwaiter().GetResult();
                Shutdown(0);
                return;
            }
        }

        bool createdNew;
        _mutex = new Mutex(true, MutexName, out createdNew);
        _ownsMutex = createdNew;
        if (!createdNew) { Shutdown(0); return; }

        Log.Info("=== Sono Island starting ===");

        // Self-test: pure-logic assertions, exits non-zero on failure (used by CI).
        if (e.Args.Contains("--selftest", StringComparer.Ordinal))
        {
            Shutdown(Services.SelfTest.Run());
            return;
        }

        // Skia path: raw layered window + Skia rasteriser (no WPF compositor).
        if (e.Args.Contains("--skia-shot", StringComparer.Ordinal))
        {
            Overlay.SkiaMode.Shot();
            Shutdown(0);
            return;
        }
        if (e.Args.Contains("--skia", StringComparer.Ordinal))
        {
            Overlay.SkiaMode.Run();
            Shutdown(0);
            return;
        }

        var settings = new SettingsManager();
        var main = new MainWindow();
        if (settings.Current.StartMinimized)
        {
            main.WindowState = WindowState.Minimized;
            main.ShowInTaskbar = false;
        }
        main.Show();

        // Diagnostic: run the real window and log its geometry on every state change.
        if (e.Args.Contains("--ui-probe", StringComparer.Ordinal))
        {
            int step = 3;
            int ix = Array.IndexOf(e.Args, "--ui-probe");
            if (ix >= 0 && ix + 2 < e.Args.Length && int.TryParse(e.Args[ix + 2], out var s3)) step = s3;
            main.EnableUiProbe(e.Args.Contains("cycle", StringComparer.Ordinal), step,
                e.Args.Contains("--lyrics-page", StringComparer.Ordinal));
        }
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        Log.Info($"=== Sono Island exiting ({e.ApplicationExitCode}) ===");
        if (_ownsMutex) { try { _mutex?.ReleaseMutex(); } catch { } }
        _mutex?.Dispose();
        _mutex = null;
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error($"Unhandled: {e.Exception}");
        e.Handled = true;
    }

    private void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Log.Error($"Domain: {ex?.Message}");
    }
}
