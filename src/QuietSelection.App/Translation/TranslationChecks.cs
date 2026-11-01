using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using QuietSelection.App.Translation.Downloads;

namespace QuietSelection.App.Translation;

internal static class TranslationChecks
{
    internal static void Run(Application app, string[] args)
    {
        string Value(string flag) { int index = Array.IndexOf(args, flag); return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : throw new ArgumentException("MISSING_TEST_ARGUMENT"); }
        string reports = Value("--report-directory");
        string modelRoot = args.Contains("--model-root") ? Value("--model-root") : TranslationClient.DefaultModelRoot;
        app.Startup += async (_, _) =>
        {
            var checks = new List<object>(); bool failed = false;
            async Task Test(string id, Func<Task<bool>> action)
            {
                var watch = Stopwatch.StartNew();
                try { bool passed = await action(); checks.Add(new { id, passed, elapsedMs = watch.ElapsedMilliseconds }); failed |= !passed; }
                catch (QuietException ex) { checks.Add(new { id, passed = false, error = ex.Code.ToString(), elapsedMs = watch.ElapsedMilliseconds }); failed = true; }
                catch (Exception ex) { checks.Add(new { id, passed = false, error = ex.GetType().Name, elapsedMs = watch.ElapsedMilliseconds }); failed = true; }
            }
            if (args.Contains("--prepare-model-cache"))
                await Test("models.install_from_verified_cache", async () => { await ModelPreparation.PrepareAsync(Value("--prepare-model-cache"), new Progress<string>(), CancellationToken.None); return true; });
            await Test("models.integrity", async () => { await ModelPreparation.VerifyAsync(modelRoot, CancellationToken.None); return true; });
            await Test("models.fresh_worker_readiness", async () => { await new TranslationClient(modelRoot).ProbeAsync(CancellationToken.None); return true; });
            await Test("models.corruption_rejected", async () =>
            {
                string scratch = Path.Combine(reports, "corrupt-model-fixture");
                var first = ModelPreparation.Catalog[0].Files[0];
                string path = Path.Combine(scratch, first.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(modelRoot, first.Path)); bytes[0] ^= 1;
                await File.WriteAllBytesAsync(path, bytes);
                try { await ModelPreparation.VerifyAsync(scratch, CancellationToken.None); return false; }
                catch (QuietException ex) { return ex.Code == ErrorCode.ModelCorrupted; }
                finally { File.Delete(path); }
            });
            if (args.Contains("--prepare-model-cache"))
                await Test("models.cancel_preserves_ready_installation", async () =>
                {
                    using var cancel = new CancellationTokenSource(); cancel.Cancel();
                    try { await ModelPreparation.PrepareAsync(Value("--prepare-model-cache"), new Progress<string>(), cancel.Token); return false; }
                    catch (OperationCanceledException) { await ModelPreparation.VerifyAsync(modelRoot, CancellationToken.None); return true; }
                });
            var client = new TranslationClient(modelRoot);
            foreach (var example in new[]
            {
                ("english_short", "en", "Hello world."),
                ("japanese_short", "ja", "今日はいい天気です。"),
                ("english_letter", "en", "Dear colleague,\nPlease send the report by Friday.\nThank you."),
                ("japanese_date", "ja", "会議は2027年3月3日に開催されます。"),
                ("english_list", "en", "1. Open the file.\n2. Read the message.\n3. Close the window.")
            })
                await Test("translation." + example.Item1, async () =>
                {
                    string result = await client.TranslateAsync([new(example.Item3, example.Item2)], "ru", CancellationToken.None);
                    return result.Any(c => c is >= '\u0400' and <= '\u04ff') &&
                        (!example.Item3.Contains('\n') || result.Count(c => c == '\n') == example.Item3.Count(c => c == '\n')) &&
                        (example.Item1 != "japanese_date" || result.Contains("2027"));
                });
            await Test("translation.mixed_order_and_primary_passthrough", async () =>
            {
                var settings = new AppSettings(); settings.AutoDetect = false;
                var plan = TranslationPlanner.Plan("Начало. Hello world. これはテストです。 Конец.\n12345", settings);
                string result = await client.TranslateAsync(plan, "ru", CancellationToken.None);
                return result.StartsWith("Начало.", StringComparison.Ordinal) && result.EndsWith(" Конец.\n12345", StringComparison.Ordinal);
            });
            await Test("translation.primary_without_models", async () =>
                await new TranslationClient(Path.Combine(reports, "absent-models")).TranslateAsync([new("Исходный текст.\n123", "ru")], "ru", CancellationToken.None) == "Исходный текст.\n123");
            await Test("translation.direct_japanese_without_pivot_models", async () =>
            {
                string scratch = Path.GetFullPath(Path.Combine(reports, "direct-only-" + Guid.NewGuid().ToString("N")));
                if (!scratch.StartsWith(Path.GetFullPath(reports) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
                try
                {
                    foreach (var file in ModelPreparation.Catalog.Single(x => x.Converter == "marian-ja-ru-v1").Files)
                    {
                        string path = Path.Combine(scratch, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.Copy(Path.Combine(modelRoot, file.Path), path);
                    }
                    string result = await new TranslationClient(scratch).TranslateAsync([new("これは翻訳のテストです。", "ja")], "ru", CancellationToken.None);
                    return result.Any(c => c is >= '\u0400' and <= '\u04ff');
                }
                finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            });
            await Test("translation.japanese_to_english_retained", async () =>
            {
                string result = await client.TranslateAsync([new("これは翻訳のテストです。", "ja")], "en", CancellationToken.None);
                return result.Contains("test", StringComparison.OrdinalIgnoreCase);
            });
            await Test("translation.cancel_terminates_worker", async () =>
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                var watch = Stopwatch.StartNew();
                try { await client.TranslateAsync([new(string.Concat(Enumerable.Repeat("This is a cancellation test. ", 1000)), "en")], "ru", cancel.Token); return false; }
                catch (OperationCanceledException) { return watch.Elapsed < TimeSpan.FromSeconds(5); }
            });
            await Test("translation.restart_after_cancellation", async () => { await new TranslationClient(modelRoot).ProbeAsync(CancellationToken.None); return true; });
            await Test("translation.cancel_direct_japanese", async () =>
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                var watch = Stopwatch.StartNew();
                try { await client.TranslateAsync([new(string.Concat(Enumerable.Repeat("これは翻訳のテストです。", 1000)), "ja")], "ru", cancel.Token); return false; }
                catch (OperationCanceledException) { return watch.Elapsed < TimeSpan.FromSeconds(5); }
            });
            using (var resident = new ResidentTranslationClient(modelRoot))
            {
                const string english = "Please send the report by Friday. Thank you for your help.";
                const string japanese = "会議は明日の午後三時に始まります。";
                int? firstProcess = null; double firstMs = 0, secondMs = 0;
                await Test("resident.translate_and_keep_worker", async () =>
                {
                    var watch = Stopwatch.StartNew();
                    string first = await resident.TranslateAsync([new(english, "en")], "ru", CancellationToken.None);
                    firstMs = watch.Elapsed.TotalMilliseconds; firstProcess = resident.ProcessId;
                    watch.Restart();
                    string second = await resident.TranslateAsync([new(english, "en")], "ru", CancellationToken.None);
                    secondMs = watch.Elapsed.TotalMilliseconds;
                    checks.Add(new { id = "resident.timing", passed = true, firstMs, secondMs });
                    return first == second && first.Any(c => c is >= '\u0400' and <= '\u04ff')
                        && firstProcess is not null && resident.ProcessId == firstProcess && secondMs < firstMs;
                });
                await Test("resident.switch_language_pair", async () =>
                {
                    string ja = await resident.TranslateAsync([new(japanese, "ja")], "ru", CancellationToken.None);
                    string mixed = await resident.TranslateAsync([new("Hello world. ", "en"), new(japanese, "ja")], "ru", CancellationToken.None);
                    return ja.Any(c => c is >= '\u0400' and <= '\u04ff') && mixed.Any(c => c is >= '\u0400' and <= '\u04ff') && resident.ProcessId == firstProcess;
                });
                await Test("resident.cancel_then_recovery", async () =>
                {
                    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                    try { await resident.TranslateAsync([new(string.Concat(Enumerable.Repeat("This is a cancellation test. ", 1000)), "en")], "ru", cancel.Token); return false; }
                    catch (OperationCanceledException) { }
                    string recovered = await resident.TranslateAsync([new(english, "en")], "ru", CancellationToken.None);
                    return recovered.Length > 0 && resident.ProcessId != firstProcess;
                });
            }
            Directory.CreateDirectory(reports);
            await File.WriteAllTextAsync(Path.Combine(reports, "translation.json"), JsonSerializer.Serialize(new
            { syntheticDataOnly = true, offlineWorker = "socket audit denied; no loader in inference process", lastFailure = client.LastFailure, peakWorkingSetBytes = client.PeakWorkingSetBytes, completedUtc = DateTimeOffset.UtcNow, checks }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(failed ? 1 : 0);
        };
    }
}
