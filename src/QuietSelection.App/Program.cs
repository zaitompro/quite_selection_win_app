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
        int exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
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
                else if (args.Contains("--translation-tests"))
                    Translation.TranslationChecks.Run(app, args);
                else
                {
                    var shell = new AppShell(app);
                    app.Startup += async (_, _) => await shell.StartAsync(args.Contains("--tray"));
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
