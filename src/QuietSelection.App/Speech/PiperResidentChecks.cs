using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using QuietSelection.App.Speech.Downloads;
using QuietSelection.App.Workers;

namespace QuietSelection.App.Speech;

internal static class PiperResidentChecks
{
    internal static void Run(Application app, string[] args)
    {
        int reportIndex = Array.IndexOf(args, "--report-directory");
        if (reportIndex < 0 || reportIndex + 1 >= args.Length) throw new ArgumentException("REPORT_DIRECTORY_REQUIRED");
        string reportDirectory = Path.GetFullPath(args[reportIndex + 1]); Directory.CreateDirectory(reportDirectory);
        app.Startup += async (_, _) =>
        {
            var checks = new List<object>(); var measurements = new List<object>(); bool failed = false;
            async Task Test(string id, Func<Task<bool>> action)
            {
                try { bool passed = await action(); checks.Add(new { id, passed }); failed |= !passed; }
                catch (QuietException ex) { checks.Add(new { id, passed = false, error = ex.Code.ToString() }); failed = true; }
                catch (Exception ex) { checks.Add(new { id, passed = false, error = ex.GetType().Name }); failed = true; }
            }
            int cacheIndex = Array.IndexOf(args, "--prepare-piper-cache");
            if (cacheIndex >= 0)
                await Test("resident.prepare_from_cache", async () =>
                {
                    await PiperModels.PrepareAsync(Path.GetFullPath(args[cacheIndex + 1]), new Progress<string>(), CancellationToken.None); return true;
                });
            await Test("resident.integrity", async () => { await PiperModels.VerifyAsync(PiperModels.Root, PiperModels.Catalog.SelectMany(x => x.Files), CancellationToken.None); return true; });
            await Test("resident.enumeration", async () =>
            {
                using var worker = new WorkerClient();
                var voices = (await worker.CallAsync(new("voices"), TimeSpan.FromSeconds(15), CancellationToken.None)).Voices ?? [];
                return PiperModels.Catalog.All(model => voices.Any(voice => voice.Id == model.Id && voice.Language == model.Language));
            });
            if (!failed)
            {
                foreach (var model in PiperModels.Catalog)
                {
                    string phrase = LanguagePlanner.TestPhrase(model.Language.Split('-')[0]);
                    double coldFirst = 0;
                    using var cold = new PiperClient();
                    await Test("resident.cold_baseline_" + model.Name, async () =>
                    {
                        var result = await cold.CallAsync(new("synthesize-test", Text: phrase, VoiceId: model.Id), TimeSpan.FromMinutes(2), CancellationToken.None);
                        coldFirst = result.ClientFirstAudioMilliseconds;
                        measurements.Add(new { voice = model.Name, scenario = "cold", result });
                        return !result.CacheHit && result.AudioBytes > 44 && cold.ProcessId is null;
                    });
                    using var resident = new PiperClient(keepLoaded: true);
                    int pid = 0;
                    await Test("resident.preload_" + model.Name, async () =>
                    {
                        var result = await resident.CallAsync(new("piper-configure", VoiceIds: [model.Id]), TimeSpan.FromMinutes(2), CancellationToken.None);
                        pid = result.ProcessId; measurements.Add(new { voice = model.Name, scenario = "preloaded-idle", result });
                        return result.RetainedModels == 1 && result.WorkingSetBytes > 0;
                    });
                    var latencies = new List<double>();
                    await Test("resident.repeated_warm_" + model.Name, async () =>
                    {
                        for (int iteration = 0; iteration < 5; iteration++)
                        {
                            var result = await resident.CallAsync(new("synthesize-test", Text: phrase, VoiceId: model.Id), TimeSpan.FromSeconds(30), CancellationToken.None);
                            measurements.Add(new { voice = model.Name, scenario = "warm", iteration, result });
                            latencies.Add(result.ClientFirstAudioMilliseconds);
                            if (!result.CacheHit || result.ProcessId != pid || result.ProcessedCharacters != phrase.Length) return false;
                        }
                        return latencies.Order().ElementAt(2) < coldFirst;
                    });
                    await Test("resident.idle_after_reads_" + model.Name, async () =>
                    {
                        using var child = Process.GetProcessById(pid); var before = child.TotalProcessorTime;
                        await Task.Delay(1500); child.Refresh();
                        var result = await resident.CallAsync(new("piper-status"), TimeSpan.FromSeconds(10), CancellationToken.None);
                        measurements.Add(new { voice = model.Name, scenario = "warm-idle", idleCpuMilliseconds = (child.TotalProcessorTime - before).TotalMilliseconds, idleSampleMilliseconds = 1500, result });
                        return result.RetainedModels == 1 && result.ProcessId == pid;
                    });
                    if (model.Name != "Denis")
                    {
                        await Test("resident.long_text_" + model.Name, async () =>
                        {
                            var text = string.Join(" ", Enumerable.Repeat(phrase, 25));
                            var result = await resident.CallAsync(new("synthesize-test", Text: text, VoiceId: model.Id), TimeSpan.FromMinutes(2), CancellationToken.None);
                            measurements.Add(new { voice = model.Name, scenario = "long", result });
                            return result.CacheHit && result.ProcessedCharacters == text.Length && result.Chunks >= 25;
                        });
                        await Test("resident.long_slow_phrase_" + model.Name, async () =>
                        {
                            string unit = model.Language.StartsWith("ru") ? "Синтетический пример для проверки длинной фразы и расхода памяти " : "A synthetic example for testing a long phrase and measuring memory use ";
                            var text = string.Concat(Enumerable.Repeat(unit, 5))[..220];
                            var result = await resident.CallAsync(new("synthesize-test", Text: text, VoiceId: model.Id, Speed: 0), TimeSpan.FromMinutes(2), CancellationToken.None);
                            measurements.Add(new { voice = model.Name, scenario = "long-phrase-slow", result });
                            return result.CacheHit && result.ProcessedCharacters == text.Length;
                        });
                    }
                    await Test("resident.cancel_inference_reuses_model_" + model.Name, async () =>
                    {
                        using var stop = new CancellationTokenSource(100); var timer = Stopwatch.StartNew();
                        try { await resident.CallAsync(new("synthesize-test", Text: string.Concat(Enumerable.Repeat(phrase, 50)), VoiceId: model.Id, Speed: 0), TimeSpan.FromMinutes(1), stop.Token); return false; }
                        catch (OperationCanceledException) { }
                        double stopMilliseconds = Math.Max(0, timer.Elapsed.TotalMilliseconds - 100);
                        var result = await resident.CallAsync(new("synthesize-test", Text: phrase, VoiceId: model.Id), TimeSpan.FromSeconds(30), CancellationToken.None);
                        measurements.Add(new { voice = model.Name, scenario = "after-cancel-inference", stopMilliseconds, result });
                        return result.CacheHit && result.ProcessId == pid && stopMilliseconds < 750;
                    });
                    if (args.Contains("--play-piper-test"))
                    {
                        await Test("resident.play_fixed_phrase_" + model.Name, async () =>
                        {
                            var result = await resident.CallAsync(new("speak", Text: phrase, VoiceId: model.Id), TimeSpan.FromSeconds(30), CancellationToken.None);
                            measurements.Add(new { voice = model.Name, scenario = "play", result });
                            return result.CacheHit && result.ProcessId == pid && result.AudioBytes > 44;
                        });
                        await Test("resident.cancel_playback_reuses_model_" + model.Name, async () =>
                        {
                            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var timer = new Stopwatch();
                            try
                            {
                                await resident.CallAsync(new("speak", Text: string.Concat(Enumerable.Repeat(phrase, 10)), VoiceId: model.Id), TimeSpan.FromSeconds(30), stop.Token,
                                    () => { timer.Start(); stop.CancelAfter(300); });
                                return false;
                            }
                            catch (OperationCanceledException) { }
                            double stopMilliseconds = Math.Max(0, timer.Elapsed.TotalMilliseconds - 300);
                            var result = await resident.CallAsync(new("synthesize-test", Text: phrase, VoiceId: model.Id), TimeSpan.FromSeconds(30), CancellationToken.None);
                            measurements.Add(new { voice = model.Name, scenario = "after-cancel-playback", stopMilliseconds, result });
                            return timer.IsRunning && result.CacheHit && result.ProcessId == pid && stopMilliseconds < 750;
                        });
                    }
                    await Test("resident.missing_id_no_fallback_" + model.Name, async () =>
                    {
                        try { await resident.CallAsync(new("synthesize-test", Text: phrase, VoiceId: "piper:missing"), TimeSpan.FromSeconds(10), CancellationToken.None); return false; }
                        catch (QuietException ex) { return ex.Code == ErrorCode.VoiceMissing && resident.ProcessId == pid; }
                    });
                    await Test("resident.disable_releases_process_" + model.Name, async () =>
                    {
                        using var child = Process.GetProcessById(pid);
                        await resident.ResetAsync(CancellationToken.None);
                        return child.HasExited && resident.ProcessId is null;
                    });
                }
                using var pair = new PiperClient(keepLoaded: true);
                string russian = "piper:ru_RU-dmitri-medium", english = "piper:en_US-ljspeech-medium";
                await Test("resident.two_selected_models", async () =>
                {
                    var ready = await pair.CallAsync(new("piper-configure", VoiceIds: [russian, english]), TimeSpan.FromMinutes(2), CancellationToken.None);
                    measurements.Add(new { voice = "Dmitri+LJSpeech", scenario = "preloaded-idle", result = ready });
                    for (int iteration = 0; iteration < 3; iteration++)
                        foreach (var item in new[] { (russian, "ru"), (english, "en") })
                        {
                            var result = await pair.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase(item.Item2), VoiceId: item.Item1), TimeSpan.FromSeconds(30), CancellationToken.None);
                            measurements.Add(new { voice = "Dmitri+LJSpeech", scenario = "alternating-" + item.Item2, iteration, result });
                            if (!result.CacheHit || result.ProcessId != ready.ProcessId || result.RetainedModels != 2) return false;
                        }
                    await Task.Delay(1500);
                    var idle = await pair.CallAsync(new("piper-status"), TimeSpan.FromSeconds(10), CancellationToken.None);
                    measurements.Add(new { voice = "Dmitri+LJSpeech", scenario = "warm-idle", result = idle });
                    return true;
                });
                await Test("resident.two_models_long_slow_phrase", async () =>
                {
                    var text = string.Concat(Enumerable.Repeat("A synthetic example for testing a long phrase and measuring memory use ", 5))[..220];
                    var result = await pair.CallAsync(new("synthesize-test", Text: text, VoiceId: english, Speed: 0), TimeSpan.FromMinutes(2), CancellationToken.None);
                    measurements.Add(new { voice = "Dmitri+LJSpeech", scenario = "long-phrase-slow", result });
                    await Task.Delay(1500);
                    var idle = await pair.CallAsync(new("piper-status"), TimeSpan.FromSeconds(10), CancellationToken.None);
                    measurements.Add(new { voice = "Dmitri+LJSpeech", scenario = "idle-after-slow", result = idle });
                    return result.CacheHit && result.RetainedModels == 2 && result.ProcessedCharacters == text.Length;
                });
                await Test("resident.changing_selection_releases_old_model", async () =>
                {
                    var result = await pair.CallAsync(new("piper-configure", VoiceIds: [english]), TimeSpan.FromMinutes(2), CancellationToken.None);
                    measurements.Add(new { voice = "LJSpeech-after-Dmitri", scenario = "selection-change-idle", result });
                    return result.RetainedModels == 1 && (await pair.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase("en"), VoiceId: english), TimeSpan.FromSeconds(30), CancellationToken.None)).CacheHit;
                });
                await Test("resident.worker_client_routing", async () =>
                {
                    using var router = new WorkerClient(); await router.ConfigurePiperAsync([english], CancellationToken.None);
                    var warm = await router.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase("en"), VoiceId: english), TimeSpan.FromSeconds(30), CancellationToken.None);
                    await router.ConfigurePiperAsync([], CancellationToken.None);
                    var cold = await router.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase("en"), VoiceId: english), TimeSpan.FromMinutes(2), CancellationToken.None);
                    return warm.CacheHit && !cold.CacheHit && warm.ProcessId != cold.ProcessId;
                });
            }
            await Test("resident.light_dark_settings", () => ThemeChecks.RunAsync(app, reportDirectory));
            uint length = 0; bool packaged = Platform.NativeMethods.GetCurrentPackageFullName(ref length, 0) != 15700;
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "piper-resident.json"), JsonSerializer.Serialize(new
            {
                syntheticDataOnly = true, packaged, completedUtc = DateTimeOffset.UtcNow,
                measurementScope = "CPU; single resident Python process; 2 threads/model with idle spinning disabled. Current working set excludes UI. Peak counters are process-lifetime peaks. ClientFirstAudioMilliseconds includes IPC/startup until first PCM is ready, not acoustic device latency. OS file cache may be warm.",
                checks, measurements
            }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(failed ? 1 : 0);
        };
    }
}
