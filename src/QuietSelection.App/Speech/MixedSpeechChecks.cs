using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using QuietSelection.App.Workers;

namespace QuietSelection.App.Speech;

internal static class MixedSpeechChecks
{
    internal static void Run(Application app, string[] args)
    {
        int index = Array.IndexOf(args, "--report-directory");
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("REPORT_DIRECTORY_REQUIRED");
        string directory = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(directory);
        Dictionary<SpeechEngine, string>? roots = null;
        int cache = Array.IndexOf(args, "--model-cache-root");
        if (cache >= 0)
        {
            string root = Path.GetFullPath(args[cache + 1]);
            roots = new()
            {
                [SpeechEngine.Piper] = Path.Combine(root, "piper-downloads"),
                [SpeechEngine.Silero] = Path.Combine(root, "silero-downloads"),
                [SpeechEngine.Kokoro] = Path.Combine(root, "kokoro-downloads"),
                [SpeechEngine.Voicevox] = Path.Combine(root, "voicevox-downloads", "0.16.4")
            };
        }
        app.Startup += async (_, _) =>
        {
            var checks = new List<object>(); var measurements = new List<object>(); bool failed = false;
            using var router = new WorkerClient(roots);
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(12));
            bool packaged = Platform.NativeMethods.IsPackaged;
            async Task Report(bool complete) => await File.WriteAllTextAsync(Path.Combine(directory, "mixed-speech.json"),
                JsonSerializer.Serialize(new { syntheticDataOnly = true, packaged, complete, completedUtc = DateTimeOffset.UtcNow,
                    scope = "One playback queue, speed 50, retained models. Real-play first audio is time to waveOutWrite; gap includes waiting and device preparation between completed blocks. No acoustic measurement. Validation callbacks do not play sound. Worker RAM excludes UI.",
                    checks, measurements }, new JsonSerializerOptions { WriteIndented = true }));
            async Task Test(string id, Func<Task<bool>> action)
            {
                try { bool passed = await action(); checks.Add(new { id, passed }); failed |= !passed; }
                catch (QuietException ex) { checks.Add(new { id, passed = false, error = ex.Code.ToString() }); failed = true; }
                catch (Exception ex) { checks.Add(new { id, passed = false, error = ex.GetType().Name }); failed = true; }
                await Report(false);
            }
            const string ru = "silero:v5_6_ru:kseniya", ja = "voicevox:metan:normal", kokoro = "kokoro:af_heart", piper = "piper:en_US-ljspeech-medium";
            VoiceInfo[] voices = [new(ru, "Synthetic configuration RU", "ru-RU"),
                new(ja, "Synthetic configuration JA", "ja-JP"), new(kokoro, "Synthetic configuration EN", "en-US"), new(piper, "Synthetic comparison EN", "en-US")];
            var settings = new AppSettings { Primary = new() { Language = "ru", VoiceId = ru },
                Additional = [new() { Language = "en", VoiceId = kokoro }, new() { Language = "ja", VoiceId = ja }] };
            await Test("mixed.preload", async () =>
            {
                await router.ConfigureSileroAsync([ru], limit.Token);
                await router.ConfigureAdditionalAsync([kokoro, ja], limit.Token);
                return true;
            });
            foreach (var pair in new[] { (ru, "Проверка чтения."), (kokoro, "A reading test."), (ja, "これはテストです。") })
                await Test("stream.accounting." + pair.Item1, async () =>
                {
                    var result = await router.SpeakPlanAsync([new(pair.Item2, "", pair.Item1, 50)], limit.Token,
                        (pcm, _) => { pcm.Validate(); return Task.CompletedTask; });
                    measurements.Add(new { scenario = "stream-validation", voice = pair.Item1, result });
                    return result.Chunks > 0 && result.AudioBytes == result.Syntheses.Sum(x => x.AudioBytes - x.Chunks * 44)
                        && result.Syntheses.Sum(x => x.ProcessedCharacters) == pair.Item2.Length;
                });
            const string phrase = "Сейчас звучит русская часть предложения, затем Japanese language и слово 日本語, после чего снова русский текст.";
            var plan = LanguagePlanner.Plan(phrase, settings, false, voices);
            foreach (bool prefetch in new[] { false, true, true })
                await Test(prefetch ? "mixed.prefetch_play" : "mixed.serial_play", async () =>
                {
                    var result = await router.SpeakPlanAsync(plan, limit.Token, prefetch: prefetch);
                    measurements.Add(new { scenario = prefetch ? "prefetch" : "serial", english = "kokoro", result });
                    return result.Chunks >= plan.Count && result.Syntheses.Sum(x => x.ProcessedCharacters) == phrase.Length;
                });
            await Test("mixed.short_alternating_words", async () =>
            {
                const string shortPhrase = "Да, hello, 日本語, снова yes, ありがとう.";
                var result = await router.SpeakPlanAsync(LanguagePlanner.Plan(shortPhrase, settings, false, voices), limit.Token);
                measurements.Add(new { scenario = "short-alternation", english = "kokoro", result });
                return result.Syntheses.Sum(x => x.ProcessedCharacters) == shortPhrase.Length;
            });
            await Test("mixed.cancel_during_playback_and_reuse", async () =>
            {
                using var cancel = new CancellationTokenSource();
                using var player = new PcmPlayer();
                var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var running = router.SpeakPlanAsync(plan, cancel.Token, async (pcm, token) =>
                { playing.TrySetResult(); await player.PlayAsync(pcm, token); });
                try { await playing.Task.WaitAsync(TimeSpan.FromSeconds(60)); }
                catch { cancel.Cancel(); try { await running; } catch { } throw; }
                await Task.Delay(100);
                var clock = Stopwatch.StartNew(); cancel.Cancel();
                try { await running; return false; } catch (OperationCanceledException) { }
                double cancellationMs = clock.Elapsed.TotalMilliseconds;
                measurements.Add(new { scenario = "cancel-playback", milliseconds = clock.Elapsed.TotalMilliseconds });
                var recovered = await router.SpeakPlanAsync([new("Проверка.", "ru", ru, 50)], limit.Token,
                    (_, _) => Task.CompletedTask);
                return recovered.AudioBytes > 0 && cancellationMs < 1000;
            });
            foreach (string id in new[] { kokoro, ja })
                await Test("mixed.cancel_inference." + id, async () =>
                {
                    using var cancel = new CancellationTokenSource(100);
                    var clock = Stopwatch.StartNew();
                    try
                    {
                        await router.SpeakPlanAsync([new(LanguagePlanner.TestPhrase(id == ja ? "ja" : "en"), "", id, 50)], cancel.Token);
                        return false;
                    }
                    catch (OperationCanceledException) { }
                    measurements.Add(new { scenario = "cancel-inference", voice = id, milliseconds = clock.Elapsed.TotalMilliseconds });
                    var recovered = await router.SpeakPlanAsync([new(id == ja ? "テストです。" : "A test.", "", id, 50)],
                        limit.Token, (_, _) => Task.CompletedTask);
                    return recovered.AudioBytes > 0;
                });
            await Test("mixed.piper_comparison", async () =>
            {
                await router.ConfigureAdditionalAsync([ja], limit.Token);
                await router.ConfigurePiperAsync([piper], limit.Token);
                settings.Additional[0].VoiceId = piper;
                var piperPlan = LanguagePlanner.Plan(phrase, settings, false, voices);
                await router.SpeakPlanAsync([new("A test.", "en", piper, 50)], limit.Token, (_, _) => Task.CompletedTask);
                var result = await router.SpeakPlanAsync(piperPlan, limit.Token);
                measurements.Add(new { scenario = "prefetch", english = "piper", result });
                return result.AudioBytes > 0 && result.Syntheses.Sum(x => x.ProcessedCharacters) == phrase.Length;
            });
            foreach (bool sapi in new[] { true, false })
            await Test(sapi ? "native.sapi_wave_pipeline" : "native.winrt_wave_pipeline", async () =>
            {
                var available = (await router.CallAsync(new("voices"), TimeSpan.FromSeconds(20), limit.Token)).Voices ?? [];
                var voice = available.FirstOrDefault(x => x.Id.StartsWith("sapi5:", StringComparison.Ordinal) == sapi
                    && !x.Id.Contains(':') == !sapi && x.Language.StartsWith("en", StringComparison.Ordinal));
                if (voice is null) return false;
                var result = await router.SpeakPlanAsync([new("A native speech test.", "en", voice.Id, 50)], limit.Token);
                measurements.Add(new { scenario = "native-pipeline", result });
                return result.AudioBytes > 0;
            });
            await Report(true); app.Shutdown(failed ? 1 : 0);
        };
    }
}
