using System.Windows;
using QuietSelection.App.Workers;

namespace QuietSelection.App;

internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--runtime-check") return 0;
        if (args.Length == 2 && args[0] == "--worker") return WorkerHost.Run(args[1]);
        if (args.Length == 2 && args[0] == "--selection-worker") return WorkerHost.RunSelection(args[1]);
        if (args.Length == 2 && args[0] == "--native-speech-worker") return WorkerHost.RunNativeSpeech(args[1]);
        if (args.Length == 1 && args[0] == "--magnifier-crash-probe") return Magnifier.MagnifierChecks.CrashProbe();
        if (args.Length == 1 && args[0] == "--magnifier-read") return Magnifier.MagnifierChecks.ReadProbe();
        if (args.Length == 2 && args[0] == "--window-theme-worker") return WindowTheme.WindowThemeHost.Run(args[1]);
        int exitCode = 0;
        // WinRT activation info must be read on this MTA thread, not the WPF STA thread.
        bool launchedAtSignIn = args.Length == 0 && Platform.StartupTaskClient.LaunchedAtSignIn();
        var thread = new Thread(() =>
        {
            try
            {
                // Development helper: regenerates assets/icons from the drawn app mark.
                if (args.Length == 2 && args[0] == "--export-icons") { Views.AppIcon.Export(args[1]); return; }
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Views.SystemTheme.FollowWindows(app);
                if (args.Contains("--mixed-speech-tests"))
                    Speech.MixedSpeechChecks.Run(app, args);
                else if (args.Contains("--additional-speech-tests"))
                    Speech.AdditionalSpeechChecks.Run(app, args);
                else if (args.Contains("--silero-english-tests"))
                    Speech.SileroEnglishChecks.Run(app, args);
                else if (args.Contains("--silero-tests"))
                    Speech.SileroChecks.Run(app, args);
                else if (args.Contains("--piper-resident-tests"))
                    Speech.PiperResidentChecks.Run(app, args);
                else if (args.Contains("--piper-tests"))
                    Speech.PiperChecks.Run(app, args);
                else if (args.Contains("--api-tests") || args.Contains("--selection-tests"))
                    IntegrationChecks.Run(app, args);
                else if (args.Contains("--magnifier-tests"))
                    Magnifier.MagnifierChecks.Run(app, args);
                else if (args.Contains("--window-theme-tests"))
                    WindowTheme.WindowThemeChecks.Run(app, args);
                else if (args.Contains("--ocr-tests"))
                    OcrChecks.Run(app, args);
                else if (args.Contains("--translation-tests"))
                    Translation.TranslationChecks.Run(app, args);
                else
                {
                    var shell = new AppShell(app);
                    bool inTray = args.Contains("--tray") || launchedAtSignIn;
                    app.Startup += async (_, _) => await shell.StartAsync(inTray);
                    app.Exit += (_, _) => shell.Dispose();
                    app.SessionEnding += (_, _) => app.Shutdown();
                }
                exitCode = app.Run();
            }
            catch (Exception ex)
            {
                // Technical diagnostics only; never include exception text or a stack trace.
                Console.Error.WriteLine($"QS_STARTUP_FAILURE {ex.GetType().Name} 0x{ex.HResult:X8}");
                exitCode = 1;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        return exitCode;
    }
}
