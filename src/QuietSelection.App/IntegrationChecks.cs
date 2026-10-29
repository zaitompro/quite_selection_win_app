using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuietSelection.App.Platform;
using QuietSelection.App.Views;
using QuietSelection.App.Workers;

namespace QuietSelection.App;

internal static class IntegrationChecks
{
    internal static void Run(Application app, string[] args)
    {
        int reportIndex = Array.IndexOf(args, "--report-directory");
        if (reportIndex < 0 || reportIndex + 1 >= args.Length) throw new ArgumentException("REPORT_DIRECTORY_REQUIRED");
        string reportDirectory = Path.GetFullPath(args[reportIndex + 1]);
        Directory.CreateDirectory(reportDirectory);
        var window = new Window { Title = "QuietSelection — синтетическая проверка", Width = 800, Height = 360, FontSize = 24, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var field = new TextBox { Text = "前置き。これは読み上げのテストです。後書き。", Margin = new(24), TextWrapping = TextWrapping.Wrap, ContextMenu = null };
        window.Content = field;
        async void RunChecks(object? sender, RoutedEventArgs eventArgs)
        {
            var checks = new List<object>(); using var worker = new WorkerClient(); bool failed = false;
            async Task Test(string id, Func<Task<bool>> run)
            {
                try { bool passed = await run(); checks.Add(new { id, passed }); failed |= !passed; }
                catch (QuietException ex) { checks.Add(new { id, passed = false, error = ex.Code.ToString() }); failed = true; }
                catch { checks.Add(new { id, passed = false, error = "API_CHECK_FAILURE" }); failed = true; }
            }
            // Exercise focus-sensitive UIA first, while the newly opened fixture owns focus.
            // Long speech checks must not rely on foreground activation several minutes later.
            window.Topmost = true; window.Activate(); field.Focus();
            field.Select(4, "これは読み上げのテストです。".Length);
            await Task.Delay(150);
            var handle = new WindowInteropHelper(window).Handle;
            int selectionPid = 0;
            await Test("uia.only_selected_range", async () =>
            {
                var result = await worker.CallAsync(new("selection", (long)handle), TimeSpan.FromSeconds(2), CancellationToken.None);
                selectionPid = result.ProcessId;
                return result.Text == "これは読み上げのテストです。";
            });
            await Test("uia.repeat_without_reselection_same_process", async () =>
            {
                for (int i = 0; i < 3; i++)
                {
                    await Task.Delay(100);
                    var result = await worker.CallAsync(new("selection", (long)handle), TimeSpan.FromSeconds(2), CancellationToken.None);
                    if (result.ProcessId != selectionPid || result.Text != "これは読み上げのテストです。") return false;
                }
                return selectionPid > 0;
            });
            field.Select(0, 4);
            await Test("uia.changed_selection_is_fresh", async () =>
            {
                var result = await worker.CallAsync(new("selection", (long)handle), TimeSpan.FromSeconds(2), CancellationToken.None);
                return result.ProcessId == selectionPid && result.Text == "前置き。";
            });
            field.Select(0, 0);
            await Test("uia.empty_caret", async () =>
            {
                try { await worker.CallAsync(new("selection", (long)handle), TimeSpan.FromSeconds(2), CancellationToken.None); return false; }
                catch (QuietException ex) { return ex.Code == ErrorCode.NoSelection; }
            });
            field.Select(0, 4);
            await Test("uia.normal_error_keeps_helper", async () =>
            {
                var result = await worker.CallAsync(new("selection", (long)handle), TimeSpan.FromSeconds(2), CancellationToken.None);
                return result.ProcessId == selectionPid && result.Text == "前置き。";
            });
            await Test("uia.source_change_then_recovery", async () =>
            {
                try { await worker.CallAsync(new("selection", 0), TimeSpan.FromSeconds(2), CancellationToken.None); return false; }
                catch (QuietException ex) when (ex.Code == ErrorCode.SourceChanged) { }
                var result = await worker.CallAsync(new("selection", (long)handle), TimeSpan.FromSeconds(2), CancellationToken.None);
                return result.Text == "前置き。";
            });
            await Test("uia.cancel_then_recovery", async () =>
            {
                using var cancel = new CancellationTokenSource();
                cancel.Cancel();
                try { await worker.CallAsync(new("selection", (long)handle), TimeSpan.FromSeconds(2), cancel.Token); return false; }
                catch (OperationCanceledException) { }
                var result = await worker.CallAsync(new("selection", (long)handle), TimeSpan.FromSeconds(2), CancellationToken.None);
                return result.Text == "前置き。";
            });
            await Test("uia.helper_exit_then_recovery", async () =>
            {
                var before = await worker.CallAsync(new("selection", (long)handle), TimeSpan.FromSeconds(2), CancellationToken.None);
                using var helper = System.Diagnostics.Process.GetProcessById(before.ProcessId);
                helper.Kill(); await helper.WaitForExitAsync();
                var after = await worker.CallAsync(new("selection", (long)handle), TimeSpan.FromSeconds(2), CancellationToken.None);
                return after.ProcessId != before.ProcessId && after.Text == "前置き。";
            });
            await Test("uia.deadline_then_recovery", async () =>
            {
                try { await worker.CallAsync(new("selection", (long)handle), TimeSpan.Zero, CancellationToken.None); return false; }
                catch (QuietException ex) when (ex.Code == ErrorCode.SelectionTimeout) { }
                var result = await worker.CallAsync(new("selection", (long)handle), TimeSpan.FromSeconds(2), CancellationToken.None);
                return result.Text == "前置き。";
            });
            if (args.Contains("--selection-tests"))
            {
                uint length = 0;
                bool identity = NativeMethods.GetCurrentPackageFullName(ref length, 0) != 15700;
                worker.Dispose();
                await File.WriteAllTextAsync(Path.Combine(reportDirectory, "selection.json"),
                    JsonSerializer.Serialize(new { syntheticDataOnly = true, packaged = identity, checks },
                        new JsonSerializerOptions { WriteIndented = true }));
                window.Close(); app.Shutdown(failed ? 1 : 0); return;
            }
            window.Topmost = false;
            VoiceInfo[] voices = [];
            await Test("winrt.enumerate_voices", async () =>
            {
                voices = (await worker.CallAsync(new("voices"), TimeSpan.FromSeconds(15), CancellationToken.None)).Voices ?? [];
                return voices.Length > 0;
            });
            foreach (string language in new[] { "ru", "en", "ja" })
            {
                var voice = voices.FirstOrDefault(x => !x.Id.StartsWith("sapi5:", StringComparison.Ordinal) && x.Language.Split('-')[0] == language);
                await Test("winrt.voice_available_" + language, () => Task.FromResult(voice is not null));
                if (voice is null) continue;
                long slow = 0, normal = 0, fast = 0;
                await Test("winrt.synthesize_" + language + "_speed_0", async () => { slow = (await worker.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase(language), VoiceId: voice.Id, Speed: 0), TimeSpan.FromSeconds(30), CancellationToken.None)).AudioBytes; return slow > 44; });
                await Test("winrt.synthesize_" + language + "_speed_50", async () => { normal = (await worker.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase(language), VoiceId: voice.Id, Speed: 50), TimeSpan.FromSeconds(30), CancellationToken.None)).AudioBytes; return normal > 44; });
                await Test("winrt.synthesize_" + language + "_speed_100", async () => { fast = (await worker.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase(language), VoiceId: voice.Id, Speed: 100), TimeSpan.FromSeconds(30), CancellationToken.None)).AudioBytes; return fast > 44; });
                checks.Add(new { id = "winrt.speed_changes_audio_duration_" + language, passed = slow > normal && normal > fast, slowBytes = slow, normalBytes = normal, fastBytes = fast });
                failed |= !(slow > normal && normal > fast);
            }
            if (args.Contains("--require-rhvoice"))
                failed |= !await Speech.SpeechChecks.RunAsync(voices, worker, checks, args);
            await Test("winrt.enumerate_ocr_languages", async () =>
            {
                var result = await worker.CallAsync(new("ocr-languages"), TimeSpan.FromSeconds(15), CancellationToken.None);
                return new[] { "ru", "en", "ja" }.All(language => result.Languages!.Any(x => x.Split('-')[0] == language));
            });
            if (args.Contains("--play-english-test"))
                await Test("winrt.play_english_fixed_phrase", async () =>
                {
                    var voice = voices.FirstOrDefault(x => x.Language.Split('-')[0] == "en");
                    if (voice is null) return false;
                    await worker.CallAsync(new("speak", Text: LanguagePlanner.TestPhrase("en"), VoiceId: voice.Id, Speed: 50), TimeSpan.FromSeconds(30), CancellationToken.None);
                    return true;
                });
            foreach (var example in new[] { ("ru", "Тест чтения"), ("en-US", "Reading test"), ("ja", "読み上げのテスト") })
            {
                await Test("winrt.ocr_fixture_" + example.Item1, async () =>
                {
                    var result = await worker.CallAsync(new("ocr-fixture", Text: example.Item2, Language: example.Item1), TimeSpan.FromSeconds(30), CancellationToken.None);
                    return result.Text.Replace(" ", "").Contains(example.Item2.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
                });
            }
            await Test("ocr.refuses_capture_without_source", async () =>
            {
                uint length = 0;
                bool packaged = NativeMethods.GetCurrentPackageFullName(ref length, 0) != 15700;
                try { await worker.CallAsync(new("ocr", Region: new PixelRegion(0, 0, 80, 80), Language: "en-US"), TimeSpan.FromSeconds(5), CancellationToken.None); return false; }
                catch (QuietException ex) { return ex.Code == ErrorCode.SourceChanged || (!packaged && ex.Code == ErrorCode.PackageIdentityRequired); }
            });
            await Test("wpf.light_dark_all_sections", () => ThemeChecks.RunAsync(app, reportDirectory));
            uint packageLength = 0;
            bool packaged = NativeMethods.GetCurrentPackageFullName(ref packageLength, 0) != 15700;
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "windows-api.json"), JsonSerializer.Serialize(new { syntheticDataOnly = true, packaged, completedUtc = DateTimeOffset.UtcNow, checks }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); app.Shutdown(failed ? 1 : 0);
        }
        if (args.Contains("--manual-start"))
        {
            window.Content = null;
            var start = new Button { Content = "Запустить проверки", Margin = new(24), Padding = new(16) };
            var panel = new StackPanel(); panel.Children.Add(field); panel.Children.Add(start); window.Content = panel;
            start.Click += (sender, eventArgs) => { start.IsEnabled = false; RunChecks(sender, eventArgs); };
        }
        else window.Loaded += RunChecks;
        app.Startup += (_, _) => window.Show();
    }
}
