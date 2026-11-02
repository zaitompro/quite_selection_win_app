using QuietSelection.Core;

var tests = new List<(string Name, Func<Task> Body)>();
void Test(string name, Action body) => tests.Add((name, () => { body(); return Task.CompletedTask; }));
void Check(bool value) { if (!value) throw new Exception("ASSERTION_FAILED"); }
void Error(ErrorCode code, Action body)
{
    try { body(); } catch (QuietException ex) when (ex.Code == code) { return; }
    throw new Exception("EXPECTED_ERROR_NOT_RAISED");
}
AppSettings Config() => new()
{
    Primary = new() { Language = "ru", VoiceId = "ru-fixed", Speed = 40 },
    Additional = [new() { Language = "en", VoiceId = "en-fixed", Speed = 70 }, new() { Language = "ja", VoiceId = "ja-fixed", Speed = 25 }]
};
VoiceInfo[] voices = [new("ru-fixed", "Russian fixture", "ru-RU"), new("en-fixed", "English fixture", "en-US"), new("ja-fixed", "Japanese fixture", "ja-JP")];
Test("selection.empty", () => Error(ErrorCode.NoSelection, () => TextLimits.CombineSelection([])));
Test("speech.prefetch_batches_preserve_order_and_voice_boundaries", () =>
{
    foreach (string english in new[] { "kokoro:af_heart", "kitten:mini-0.8:bella", "pocket:english-2026-09_2:alba" })
    {
        SpeechSegment[] input = [new("First. ", "en", english, 50), new("Second.", "en", english, 50),
            new("日本語。", "ja", "voicevox:metan:normal", 50), new("次です。", "ja", "voicevox:metan:normal", 50),
            new("Faster.", "en", english, 60), new("Slower.", "en", english, 50),
            new("Piper one.", "en", "piper:en", 50), new("Piper two.", "en", "piper:en", 50)];
        var batches = LanguagePlanner.BatchForPrefetch(input).ToArray();
        Check(batches.Length == 6 && batches[0].Text == "First. Second." && batches[1].Text == "日本語。次です。");
        Check(string.Concat(batches.Select(x => x.Text)) == string.Concat(input.Select(x => x.Text)));
        Check(!LanguagePlanner.BatchForPrefetch([]).Any());
        SpeechSegment[] longInput = [new(new string('a', 600), "en", english, 50),
            new(new string('b', 600), "en", english, 50)];
        Check(LanguagePlanner.BatchForPrefetch(longInput).Count() == 2);
    }
});
Test("selection.caret", () => Error(ErrorCode.NoSelection, () => TextLimits.CombineSelection([""])));
Test("selection.whitespace", () => Error(ErrorCode.NoSelection, () => TextLimits.CombineSelection([" \n"])));
Test("selection.multiple_ranges", () => Check(TextLimits.CombineSelection(["First", "Second"]) == "First\nSecond"));
Test("selection.limit_exact", () => Check(TextLimits.CombineSelection([new('x', 60000)]).Length == 60000));
Test("selection.limit_exceeded", () => Error(ErrorCode.TooLong, () => TextLimits.CombineSelection([new('x', 60001)])));
Test("selection.limit_includes_separator", () => Error(ErrorCode.TooLong, () => TextLimits.CombineSelection([new('x', 60000), "y"])));
Test("language.preserve_order_and_punctuation", () =>
{
    const string input = "Привет мир! Hello world. こんにちは。\n123";
    var plan = LanguagePlanner.Plan(input, Config(), false, voices);
    Check(string.Concat(plan.Select(x => x.Text)) == input);
    Check(plan.Select(x => x.Language).SequenceEqual(new[] { "ru", "en", "ja", "ru" }));
});
Test("language.disabled_candidate", () =>
{
    var settings = Config(); settings.Additional[0].Enabled = false;
    Check(LanguagePlanner.Plan("Hello world", settings, false, voices).Single().Language == "ru");
});
Test("language.short_han_uncertain", () => Check(LanguagePlanner.Detect("公演期間：2027/03/03(水)", ["ru", "en", "ja"]).Single().Language is null));
Test("language.short_latin_uncertain", () => Check(LanguagePlanner.Detect("OK", ["ru", "en"]).Single().Language is null));
Test("speech.mixed_scripts_use_exact_voices_without_transliteration", () =>
{
    const string text = "Слово 日本語 означает Japanese language.";
    var plan = LanguagePlanner.Plan(text, Config(), false, voices);
    Check(string.Concat(plan.Select(x => x.Text)) == text);
    Check(plan.Select(x => x.Language).SequenceEqual(new[] { "ru", "ja", "ru", "en" }));
    Check(plan[1].Text == "日本語 " && plan[1].VoiceId == "ja-fixed" && plan[1].Speed == 25);
    Check(plan[^1].VoiceId == "en-fixed" && plan[^1].Speed == 70);
});
Test("speech.mixed_short_words_and_neutral_characters_are_not_lost", () =>
{
    const string text = "「AI」 и 日本語 👋 123 — English";
    var plan = LanguagePlanner.Plan(text, Config(), false, voices);
    Check(string.Concat(plan.Select(x => x.Text)) == text);
    Check(plan.Select(x => x.Language).SequenceEqual(new[] { "en", "ru", "ja", "en" }));
});
Test("speech.mixed_disabled_and_manual_languages_respected", () =>
{
    const string text = "Пример 日本語 and English";
    var settings = Config();
    settings.Additional[1].Enabled = false;
    Check(LanguagePlanner.Plan(text, settings, false, voices).All(x => x.Language != "ja"));
    settings.AutoDetect = false;
    Check(LanguagePlanner.Plan(text, settings, false, voices).All(x => x.Language == "ru"));
    settings.AutoDetect = true;
    Check(LanguagePlanner.Plan(text, settings, true, voices).All(x => x.Language == "ru"));
});
Test("speech.mixed_missing_voice_fails_before_playback", () =>
    Error(ErrorCode.VoiceMissing, () => LanguagePlanner.Plan("Пример 日本語", Config(), false, voices.Where(x => x.Language != "ja-JP").ToArray())));
Test("speech.mixed_routing_independent_of_primary_language", () =>
{
    foreach (var primary in new[] { "ru", "en", "ja" })
    {
        var settings = Config();
        var choices = settings.ActiveLanguages.ToArray();
        settings.Primary = choices.Single(x => x.Language == primary);
        settings.Additional = choices.Where(x => x.Language != primary).ToList();
        const string text = "English, русский и 日本語。";
        var plan = LanguagePlanner.Plan(text, settings, false, voices);
        Check(string.Concat(plan.Select(x => x.Text)) == text);
        Check(plan.Select(x => x.Language).SequenceEqual(new[] { "en", "ru", "ja" }));
    }
});
Test("speech.mathematical_symbols_do_not_trigger_foreign_voice", () =>
{
    Check(LanguagePlanner.Plan("Значение 2 × 3 ÷ 2", Config(), false, voices).Single().Language == "ru");
});
tests.Add(("pipeline.overlaps_preparation_preserves_order_and_bounds_queue", async () =>
{
    var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var finishFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var played = new List<int>(); var freed = new List<int>();
    int prepared = 0;
    var run = PrefetchPlayback.RunAsync<int>(async (send, token) =>
    {
        for (int i = 1; i <= 5; i++)
        {
            prepared = i;
            if (i == 3) third.SetResult();
            await send(i, token);
        }
    }, async (item, token) =>
    {
        played.Add(item);
        if (item == 1) { playing.SetResult(); await finishFirst.Task.WaitAsync(token); }
    }, item => { lock (freed) freed.Add(item); }, CancellationToken.None);
    await playing.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await third.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(prepared == 3 && played.SequenceEqual(new[] { 1 }));
    finishFirst.SetResult(); await run.WaitAsync(TimeSpan.FromSeconds(2));
    Check(played.SequenceEqual(Enumerable.Range(1, 5)) && freed.Order().SequenceEqual(Enumerable.Range(1, 5)));
}));
tests.Add(("pipeline.cancel_drains_and_joins_producer", async () =>
{
    using var stop = new CancellationTokenSource();
    var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var freed = new List<int>();
    int created = 0, played = 0; bool producerFinished = false;
    var run = PrefetchPlayback.RunAsync<int>(async (send, token) =>
    {
        try { for (int i = 1; i <= 100; i++) { created++; await send(i, token); } }
        finally { producerFinished = true; }
    }, async (_, token) => { played++; playing.TrySetResult(); await Task.Delay(Timeout.Infinite, token); },
        item => { lock (freed) freed.Add(item); }, stop.Token);
    await playing.Task.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel();
    try { await run.WaitAsync(TimeSpan.FromSeconds(2)); Check(false); }
    catch (OperationCanceledException) { }
    Check(played == 1 && producerFinished && freed.Distinct().Count() == created && freed.Count == created);
}));
tests.Add(("pipeline.producer_failure_is_propagated", async () =>
{
    int released = 0;
    try
    {
        await PrefetchPlayback.RunAsync<int>(async (send, token) => { await send(1, token); throw new InvalidDataException(); },
            (_, token) => Task.Delay(100, token), _ => Interlocked.Increment(ref released), CancellationToken.None);
        Check(false);
    }
    catch (InvalidDataException) { }
    Check(released == 1);
}));
tests.Add(("pipeline.playback_failure_joins_blocked_producer", async () =>
{
    int created = 0, released = 0; bool done = false;
    try
    {
        await PrefetchPlayback.RunAsync<int>(async (send, token) =>
        {
            try { for (int i = 0; i < 10; i++) { created++; await send(i, token); } }
            finally { done = true; }
        }, (_, _) => throw new InvalidDataException(), _ => Interlocked.Increment(ref released), CancellationToken.None);
        Check(false);
    }
    catch (InvalidDataException) { }
    Check(done && released == created);
}));
Test("language.numbers", () => Check(!LanguagePlanner.Detect("2027/03/03", ["ru", "en", "ja"]).Single().HasLetters));
Test("language.decimals_and_versions_stay_in_sentence", () =>
{
    const string input = "Задержка 0.16–0.21 с. Версия 0.1.0.14! Hello world.";
    var plan = LanguagePlanner.Plan(input, Config(), false, voices);
    Check(plan.Count == 3 && string.Concat(plan.Select(x => x.Text)) == input);
    Check(plan[0].Text == "Задержка 0.16–0.21 с." && plan[1].Text == " Версия 0.1.0.14!");
    Check(plan.Select(x => x.Language).SequenceEqual(new[] { "ru", "ru", "en" }));
});
Test("translation.independent_of_speech_auto_detect", () => { var settings = Config(); settings.AutoDetect = false; Check(TranslationPlanner.Plan("Hello world", settings).Single().Source == "en"); });
Test("translation.uncertain_requires_choice", () =>
{
    // No clue at all: Han without kana could be Chinese; an unsupported script has no language.
    Error(ErrorCode.SourceLanguageRequired, () => TranslationPlanner.Plan("公演", Config()));
    Error(ErrorCode.SourceLanguageRequired, () => TranslationPlanner.Plan("مرحبا", Config()));
});
Test("translation.context_resolves_ambiguous_sentences", () =>
{
    // Short Latin follows its neighbours; a lone short word is English (the only Latin language).
    Check(TranslationPlanner.Plan("Hello world. OK. See you later.", Config()).All(x => x.Source == "en"));
    Check(TranslationPlanner.Plan("OK", Config()).Single().Source == "en");
    // A Russian sentence with an English name stays Russian; an English one with a Russian word stays English.
    Check(TranslationPlanner.Plan("Установите Python и запустите скрипт.", Config()).Single().Source == "ru");
    Check(TranslationPlanner.Plan("Open the Настройки window and press Start.", Config()).Single().Source == "en");
    Check(TranslationPlanner.Plan("This café is open today.", Config()).Single().Source == "en");
    // Han-only sentences are Japanese when the selection also has kana.
    Check(TranslationPlanner.Plan("これはテストです。公演。", Config()).All(x => x.Source == "ja"));
});
Test("translation.temporary_choice_preserves_known_spans", () =>
{
    // The user's choice applies only to spans that context cannot resolve.
    const string text = "Начало. 公演。 Hello world.\n123";
    var plan = TranslationPlanner.Plan(text, Config(), "ja");
    Check(string.Concat(plan.Select(x => x.Text)) == text && plan.Select(x => x.Source).SequenceEqual(new[] { "ru", "ja", "en", "ru" }));
});
Test("translation.disabled_source_cannot_be_chosen", () => { var settings = Config(); settings.Additional[1].Enabled = false; Error(ErrorCode.SourceLanguageRequired, () => TranslationPlanner.Plan("公演", settings, "ja")); });
Test("translation.unsupported_target_checked_before_execution", () =>
{
    var settings = Config(); settings.Primary.Language = "ja"; settings.Additional[1].Language = "ru";
    Error(ErrorCode.UnsupportedPair, () => TranslationPlanner.Plan("Hello world", settings));
});
Test("language.unrecognised_script", () => Check(LanguagePlanner.Detect("مرحبا", ["ru", "en", "ja"]).Single().Language is null));
Test("language.primary_override_preserves_settings", () =>
{
    var settings = Config();
    Check(LanguagePlanner.Plan("Hello world", settings, true, voices).Single().Language == "ru");
    Check(settings.AutoDetect && settings.Primary.Language == "ru");
});
Test("language.manual_primary", () => { var settings = Config(); settings.AutoDetect = false; Check(LanguagePlanner.Plan("Hello world", settings, false, voices).Single().Language == "ru"); });
Test("voice.exact_id_and_speed", () =>
{
    var plan = LanguagePlanner.Plan("Hello world. こんにちは。", Config(), false, voices);
    Check(plan[0].VoiceId == "en-fixed" && plan[0].Speed == 70 && plan[1].VoiceId == "ja-fixed" && plan[1].Speed == 25);
});
Test("voice.missing_is_error", () => Error(ErrorCode.VoiceMissing, () => LanguagePlanner.Plan("Hello world", Config(), false, [])));
Test("voice.language_mismatch_is_error", () => Error(ErrorCode.VoiceMissing, () => LanguagePlanner.Plan("Hello world", Config(), false, [new("en-fixed", "wrong", "ru-RU")])));
Test("voice.rate_boundaries", () => Check(LanguagePlanner.SpeakingRate(0) == 0.5 && LanguagePlanner.SpeakingRate(50) == 1 && LanguagePlanner.SpeakingRate(100) == 6));
Test("magnifier.cursor_stays_centered_and_maps_back", () =>
{
    var screen = new MagPoint(1920, 1080);
    foreach (double scale in new[] { 1.5, 2.0, 3.0, 7.25 })
    {
        var cursor = new MagPoint(960.4, 540.7);
        var offset = MagnifierMath.Center(cursor, screen, scale);
        var shown = MagnifierMath.ToScreen(cursor, offset, scale);
        Check(Math.Abs(shown.X - 960) < 0.01 && Math.Abs(shown.Y - 540) < 0.01);
        // Away from the edges any cursor appears exactly in the middle.
        var other = new MagPoint(1000, 600);
        var shownOther = MagnifierMath.ToScreen(other, MagnifierMath.Center(other, screen, scale), scale);
        Check(Math.Abs(shownOther.X - 960) < 0.01 && Math.Abs(shownOther.Y - 540) < 0.01);
    }
});
Test("magnifier.edges_clamp_and_keep_cursor_visible", () =>
{
    var screen = new MagPoint(1920, 1080);
    var corner = MagnifierMath.Center(new(5, 5), screen, 2);
    Check(corner == new MagPoint(0, 0));
    var far = MagnifierMath.Center(new(1915, 1075), screen, 2);
    Check(far == new MagPoint(960, 540));
    var shown = MagnifierMath.ToScreen(new(1915, 1075), far, 2);
    Check(shown.X is > 0 and < 1920 && shown.Y is > 0 and < 1080);
});
Test("magnifier.wheel_scale_bounds", () =>
{
    Check(MagnifierMath.NextScale(1.0, 1, 1.25) == 1.25);
    Check(MagnifierMath.NextScale(1.25, -1, 1.25) == 1.0);
    Check(MagnifierMath.NextScale(1.1, -3, 1.25) == 1.0);
    Check(MagnifierMath.NextScale(15, 5, 1.25) == MagnifierMath.MaximumScale);
});
Test("magnifier.caret_moves_view_only_outside_center", () =>
{
    var screen = new MagPoint(1920, 1080);
    var offset = new MagPoint(400, 200); // 960 x 540 visible at 2x
    var inside = MagnifierMath.FollowCaret(offset, new(880, 470, 2, 20), screen, 2);
    Check(inside == offset);
    var right = MagnifierMath.FollowCaret(offset, new(1200, 470, 2, 20), screen, 2);
    Check(right.Y == offset.Y && right.X > offset.X && 1202 <= right.X + 960 * 0.75 + 0.01);
    var away = MagnifierMath.FollowCaret(offset, new(100, 1000, 2, 20), screen, 2);
    Check(away == MagnifierMath.Center(new(101, 1010), screen, 2));
});
Test("magnifier.settings_default_and_validation", () =>
{
    var legacy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"SchemaVersion\":1}")!;
    legacy.Validate();
    Check(!legacy.Magnifier.Enabled && legacy.Magnifier.Modifier == "Ctrl+Alt" && legacy.Magnifier.FollowCaret);
    var settings = Config(); settings.Magnifier.Modifier = "Win"; Error(ErrorCode.InvalidSettings, settings.Validate);
    settings = Config(); settings.Magnifier.Step = 5; Error(ErrorCode.InvalidSettings, settings.Validate);
});
Test("settings.snapshot_is_independent", () => { var original = Config(); var snapshot = original.Snapshot(); original.Primary.Speed = 99; Check(snapshot.Primary.Speed == 40); });
Test("settings.reject_duplicate_language", () => { var settings = Config(); settings.Additional.Add(new() { Language = "ru" }); Error(ErrorCode.InvalidSettings, settings.Validate); });
Test("settings.reject_future_schema", () => { var settings = Config(); settings.SchemaVersion = 2; Error(ErrorCode.InvalidSettings, settings.Validate); });
Test("settings.reject_duplicate_hotkey", () => { var settings = Config(); settings.Hotkeys[ReadingCommand.Selection] = "Ctrl+Shift+F1"; settings.Hotkeys[ReadingCommand.Region] = "Ctrl+Shift+F1"; Error(ErrorCode.InvalidSettings, settings.Validate); });
Test("settings.atomic_roundtrip_and_backup", () =>
{
    var scratchRoot = Path.GetFullPath(Path.Combine("artifacts", "test-scratch"));
    var directory = Path.GetFullPath(Path.Combine(scratchRoot, "QuietSelection-test-" + Guid.NewGuid().ToString("N")));
    if (!directory.StartsWith(scratchRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("INVALID_TEST_DIRECTORY");
    Directory.CreateDirectory(directory);
    try
    {
        var path = Path.Combine(directory, "settings.json"); var store = new SettingsStore(path);
        store.Save(Config()); var next = store.Load(); next.Primary.Speed = 80; store.Save(next);
        Check(store.Load().Primary.Speed == 80 && new SettingsStore(path + ".previous").Load().Primary.Speed == 40);
        File.WriteAllText(path, "{invalid"); Error(ErrorCode.InvalidSettings, () => store.Load());
        Check(File.ReadAllText(path) == "{invalid");
        // Reset keeps the unreadable file and lets defaults be saved at the original path.
        var aside = store.MoveAside();
        Check(aside is not null && File.ReadAllText(aside) == "{invalid" && !File.Exists(path));
        store.Save(new AppSettings());
        Check(store.Load().ShowReadingIndicator && store.MoveAside() is not null && store.MoveAside() is null);
    }
    finally { Directory.Delete(directory, true); }
});
Test("ocr.negative_origin_drag", () => Check(PixelRegion.FromDrag(-10, 50, -500, -30) == new PixelRegion(-500, -30, 490, 80)));
Test("ocr.too_small", () => Error(ErrorCode.RegionTooSmall, () => new PixelRegion(0, 0, 7, 20).Validate(4096)));
Test("ocr.too_large", () => Error(ErrorCode.RegionTooLarge, () => new PixelRegion(0, 0, 5000, 20).Validate(4096)));
tests.Add(("operation.repeated_command_cancels_without_starting", async () =>
{
    var controller = new OperationController(); var started = new TaskCompletionSource(); var released = new TaskCompletionSource();
    int calls = 0;
    var first = controller.RunOrStopAsync(async context => { calls++; context.Transition(OperationState.ReadingSelection); started.SetResult(); await released.Task; context.EnsureCurrent(); });
    await started.Task;
    await controller.RunOrStopAsync(_ => { calls++; return Task.CompletedTask; });
    Check(calls == 1 && controller.IsBusy);
    released.SetResult(); await first;
    Check(!controller.IsBusy && controller.State == OperationState.Idle);
}));
tests.Add(("operation.late_result_cannot_speak", async () =>
{
    var controller = new OperationController(); var release = new TaskCompletionSource(); bool spoke = false; ErrorCode completion = ErrorCode.None;
    controller.Changed += (_, error) => completion = error;
    var running = controller.RunOrStopAsync(async context => { await release.Task; context.Transition(OperationState.Speaking); spoke = true; });
    controller.Stop(); release.SetResult(); await running;
    Check(!spoke && completion == ErrorCode.Cancelled);
}));
foreach (var stage in Enum.GetValues<OperationState>().Where(x => x != OperationState.Idle))
{
    tests.Add(($"operation.cancel_{stage}", async () =>
    {
        var controller = new OperationController(); var started = new TaskCompletionSource();
        var running = controller.RunOrStopAsync(async context => { context.Transition(stage); started.SetResult(); await Task.Delay(Timeout.Infinite, context.Token); });
        await started.Task; controller.Stop(); await running;
        Check(controller.State == OperationState.Idle && !controller.IsBusy);
    }));
}
Test("speech.sapi_exact_id_survives_settings_roundtrip", () =>
{
    var settings = Config(); settings.Primary.VoiceId = "sapi5:HKEY_LOCAL_MACHINE\\SyntheticElena";
    var copy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
    copy.Validate();
    var installed = voices.Append(new VoiceInfo(settings.Primary.VoiceId, "Synthetic Elena [SAPI 5]", "ru-RU")).ToArray();
    Check(LanguagePlanner.Plan("Тест", copy, true, installed).Single().VoiceId == settings.Primary.VoiceId);
    Check(copy.Additional[0].VoiceId == "en-fixed" && copy.Additional[1].VoiceId == "ja-fixed");
});
Test("speech.sapi_missing_never_uses_similarly_named_voice", () =>
{
    var settings = Config(); settings.Primary.VoiceId = "sapi5:removed-token";
    var installed = voices.Append(new VoiceInfo("sapi5:another-token", "Synthetic Elena [SAPI 5]", "ru-RU")).ToArray();
    Error(ErrorCode.VoiceMissing, () => LanguagePlanner.Plan("Тест", settings, true, installed));
});
int failures = 0;
Test("settings.silero_keeps_english_binding_and_independent_cache_options", () =>
{
    var legacy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"SchemaVersion\":1}")!;
    legacy.Validate(); Check(legacy.KeepSileroLoaded && legacy.KeepPiperLoaded);
    legacy.Primary.VoiceId = "silero:v5_6_ru:xenia";
    legacy.Additional[0].VoiceId = "piper:en_US-ljspeech-medium";
    legacy.KeepSileroLoaded = false;
    var snapshot = legacy.Snapshot();
    Check(!snapshot.KeepSileroLoaded && snapshot.KeepPiperLoaded && snapshot.Additional[0].VoiceId == "piper:en_US-ljspeech-medium");
    var available = voices.Append(new VoiceInfo(legacy.Primary.VoiceId, "Synthetic Silero", "ru-RU"))
        .Append(new VoiceInfo(legacy.Additional[0].VoiceId, "Synthetic Piper", "en-US")).ToArray();
    Check(LanguagePlanner.Plan("Тест", snapshot, true, available).Single().VoiceId == legacy.Primary.VoiceId);
    Check(LanguagePlanner.Plan("Hello world", snapshot, false, available).Single().VoiceId == legacy.Additional[0].VoiceId);
});
Test("settings.resident_piper_legacy_default_and_opt_out", () =>
{
    var legacy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"SchemaVersion\":1}")!;
    legacy.Validate(); Check(legacy.KeepPiperLoaded);
    legacy.KeepPiperLoaded = false;
    Check(!legacy.Snapshot().KeepPiperLoaded);
});
foreach (var test in tests)
{
    try { await test.Body(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {test.Name} {ex.GetType().Name} 0x{ex.HResult:X8}"); }
}
Console.WriteLine($"TOTAL {tests.Count}; PASSED {tests.Count - failures}; FAILED {failures}");
return failures == 0 ? 0 : 1;
