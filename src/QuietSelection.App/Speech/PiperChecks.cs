using System.Text.Json;
using System.Windows;
using QuietSelection.App.Speech.Downloads;
using QuietSelection.App.Workers;

namespace QuietSelection.App.Speech;

internal static class PiperChecks
{
    internal static void Run(Application app, string[] args)
    {
        int reportIndex = Array.IndexOf(args, "--report-directory");
        if (reportIndex < 0 || reportIndex + 1 >= args.Length) throw new ArgumentException("REPORT_DIRECTORY_REQUIRED");
        string reportDirectory = Path.GetFullPath(args[reportIndex + 1]); Directory.CreateDirectory(reportDirectory);
        app.Startup += async (_, _) =>
        {
            var checks = new List<object>(); var measurements = new List<object>(); bool failed = false;
            async Task Test(string id, Func<Task<bool>> run)
            {
                try { bool passed = await run(); checks.Add(new { id, passed }); failed |= !passed; }
                catch (QuietException ex) { checks.Add(new { id, passed = false, error = ex.Code.ToString() }); failed = true; }
                catch { checks.Add(new { id, passed = false, error = "PIPER_CHECK_FAILURE" }); failed = true; }
            }
            int cacheIndex = Array.IndexOf(args, "--prepare-piper-cache");
            if (cacheIndex >= 0 && cacheIndex + 1 < args.Length)
                await Test("piper.prepare_from_cache", async () =>
                {
                    await PiperModels.PrepareAsync(Path.GetFullPath(args[cacheIndex + 1]), new Progress<string>(), CancellationToken.None); return true;
                });
            var worker = new WorkerClient();
            await Test("piper.integrity", async () => { await PiperModels.VerifyAsync(PiperModels.Root, PiperModels.Catalog.SelectMany(x => x.Files), CancellationToken.None); return true; });
            await Test("piper.enumeration", async () =>
            {
                var voices = (await worker.CallAsync(new("voices"), TimeSpan.FromSeconds(15), CancellationToken.None)).Voices!;
                return PiperModels.Catalog.All(m => voices.Any(v => v.Id == m.Id && v.Language == m.Language));
            });
            if (!failed)
            {
                foreach (var model in PiperModels.Catalog)
                {
                    string language = model.Language.Split('-')[0];
                    double[] durations = new double[3];
                    foreach (int speed in new[] { 0, 50, 100 })
                        await Test($"piper.synthesize_{language}_{speed}", async () =>
                        {
                            var text = LanguagePlanner.TestPhrase(language);
                            var result = await worker.CallAsync(new("synthesize-test", Text: text, VoiceId: model.Id, Speed: speed), TimeSpan.FromMinutes(2), CancellationToken.None);
                            durations[speed / 50] = result.AudioSeconds;
                            measurements.Add(new { language, scenario = "short", speed, characters = text.Length, result });
                            return result.AudioBytes > 44 && result.ProcessedCharacters == text.Length && result.PeakWorkingSetBytes > 0 && result.LoadWorkingSetBytes > 0;
                        });
                    await Test("piper.speed_changes_duration_" + language, () => Task.FromResult(durations[0] > durations[1] && durations[1] > durations[2]));
                    await Test("piper.long_text_" + language, async () =>
                    {
                        string text = string.Join(" ", Enumerable.Repeat(LanguagePlanner.TestPhrase(language), 25));
                        var result = await worker.CallAsync(new("synthesize-test", Text: text, VoiceId: model.Id), TimeSpan.FromMinutes(5), CancellationToken.None);
                        measurements.Add(new { language, scenario = "long", speed = 50, characters = text.Length, result });
                        return result.ProcessedCharacters == text.Length && result.Chunks >= 25 && result.AudioSeconds > durations[1] * 10;
                    });
                    await Test("piper.long_phrase_slow_" + language, async () =>
                    {
                        string phrase = language == "ru" ? "Синтетический пример для проверки длинной фразы и расхода памяти "
                            : "A synthetic example for testing a long phrase and measuring memory use ";
                        string text = string.Concat(Enumerable.Repeat(phrase, 5))[..220];
                        var result = await worker.CallAsync(new("synthesize-test", Text: text, VoiceId: model.Id, Speed: 0), TimeSpan.FromMinutes(2), CancellationToken.None);
                        measurements.Add(new { language, scenario = "long-phrase-slow", speed = 0, characters = text.Length, result });
                        return result.ProcessedCharacters == text.Length && result.AudioBytes > 44;
                    });
                    await Test("piper.cancel_and_restart_" + language, async () =>
                    {
                        using var cancellation = new CancellationTokenSource(1000);
                        try { await worker.CallAsync(new("speak", Text: string.Concat(Enumerable.Repeat(LanguagePlanner.TestPhrase(language), 50)), VoiceId: model.Id), TimeSpan.FromMinutes(1), cancellation.Token); return false; }
                        catch (OperationCanceledException) { }
                        return (await worker.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase(language), VoiceId: model.Id), TimeSpan.FromMinutes(2), CancellationToken.None)).AudioBytes > 44;
                    });
                    if (args.Contains("--play-piper-test"))
                        await Test("piper.play_fixed_phrase_" + language, async () =>
                            (await worker.CallAsync(new("speak", Text: LanguagePlanner.TestPhrase(language), VoiceId: model.Id), TimeSpan.FromMinutes(2), CancellationToken.None)).AudioBytes > 44);
                }
            }
            await Test("piper.missing_id_no_fallback", async () =>
            {
                try { await worker.CallAsync(new("synthesize-test", Text: "Synthetic test.", VoiceId: "piper:missing"), TimeSpan.FromSeconds(15), CancellationToken.None); return false; }
                catch (QuietException ex) { return ex.Code == ErrorCode.VoiceMissing; }
            });
            // Isolated synthetic files only; never damage installed voices to test recovery.
            string fixture = Path.Combine(reportDirectory, "piper-integrity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            try
            {
                var model = PiperModels.Catalog[0];
                await Test("piper.missing_model_no_fallback", async () =>
                {
                    try { await new PiperClient(fixture).CallAsync(new("synthesize-test", Text: "Synthetic test.", VoiceId: model.Id), TimeSpan.FromSeconds(15), CancellationToken.None); return false; }
                    catch (QuietException ex) { return ex.Code == ErrorCode.SpeechModelMissing; }
                });
                await Test("piper.corrupted_model_no_inference", async () =>
                {
                    var file = model.Files[0];
                    using (var output = File.Create(Path.Combine(fixture, file.Name))) output.SetLength(file.Bytes);
                    try { await new PiperClient(fixture).CallAsync(new("synthesize-test", Text: "Synthetic test.", VoiceId: model.Id), TimeSpan.FromSeconds(15), CancellationToken.None); return false; }
                    catch (QuietException ex) { return ex.Code == ErrorCode.SpeechModelCorrupted; }
                });
                await Test("piper.corrupted_config_same_length", async () =>
                {
                    var file = model.Files[1];
                    await File.WriteAllBytesAsync(Path.Combine(fixture, file.Name), new byte[file.Bytes]);
                    try { await PiperModels.VerifyAsync(fixture, [file], CancellationToken.None); return false; }
                    catch (QuietException ex) { return ex.Code == ErrorCode.SpeechModelCorrupted; }
                });
                if (args.Contains("--test-piper-download"))
                    foreach (var voice in PiperModels.Catalog)
                        await Test("piper.download_config_" + voice.Language, async () =>
                        {
                            var file = voice.Files[1]; string path = Path.Combine(fixture, "download-" + file.Name);
                            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                            await PiperModels.DownloadAsync(file, path, new Progress<string>(), deadline.Token);
                            await PiperModels.VerifyAsync(fixture, [file with { Name = "download-" + file.Name }], deadline.Token);
                            return true;
                        });
            }
            finally { Directory.Delete(fixture, true); }
            await Test("piper.cancel_preparation_preserves_models", async () =>
            {
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                try { await PiperModels.PrepareAsync(null, new Progress<string>(), cancellation.Token); return false; }
                catch (OperationCanceledException) { await PiperModels.VerifyAsync(PiperModels.Root, PiperModels.Catalog.SelectMany(x => x.Files), CancellationToken.None); return true; }
            });
            bool packaged = Platform.NativeMethods.IsPackaged;
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "piper.json"), JsonSerializer.Serialize(new
            {
                syntheticDataOnly = true, packaged, completedUtc = DateTimeOffset.UtcNow,
                measurementScope = "One fresh Python process per request; CPU, two inference threads; OS file cache may be warm. UI excluded. PrivateBytes is commit, not physical RAM.",
                checks, measurements
            }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(failed ? 1 : 0);
        };
    }
}
