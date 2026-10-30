using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Windows;
using QuietSelection.App.Platform;
using QuietSelection.App.Speech;
using QuietSelection.App.Speech.Downloads;
using QuietSelection.App.Translation;
using QuietSelection.App.Translation.Downloads;
using QuietSelection.App.Views;
using QuietSelection.App.Workers;

namespace QuietSelection.App;

/// <summary>
/// Application state: settings, hotkeys, the tray, resident voices, downloads and reading operations.
/// Windows only display this state and call its methods.
/// </summary>
internal sealed partial class AppShell : IDisposable
{
    private readonly Application application;
    private readonly SettingsStore store;
    private readonly WorkerClient worker = new();
    private readonly OperationController operations = new();
    private readonly Mutex mutex;
    private readonly bool ownsMutex;
    private readonly EventWaitHandle? openSettings;
    private TrayIcon? tray;
    private ReadingIndicator? indicator;
    private HotkeyManager? hotkeys;
    private SettingsWindow? settingsWindow;
    private bool exiting;
    private bool running;

    // Voice sets kept in memory per worker.
    private readonly ResidentModels piperModels, sileroModels, additionalModels;

    // One voice download/installation at a time.
    private CancellationTokenSource? voiceDownload;
    private Task? voicePreparation;

    // Translation model preparation or verification.
    private CancellationTokenSource? modelPreparation;
    private Task? modelTask;

    internal AppSettings Settings { get; private set; } = new();
    internal bool SettingsWritable { get; private set; } = true;
    internal VoiceInfo[] Voices { get; private set; } = [];
    internal string Status { get; private set; } = "Готово.";
    internal OperationState State { get; private set; }
    internal bool Busy => State != OperationState.Idle;
    internal event Action? Changed;

    internal string ModelStatus { get; private set; } = "Ожидает проверки.";
    internal bool ModelsReady { get; private set; }
    internal bool PreparingModels => modelPreparation is not null;

    /// <summary>Key of the voice package being prepared (see <see cref="VoiceCatalog"/>), or null.</summary>
    internal string? ActiveVoicePackage { get; private set; }
    internal string VoiceProgress { get; private set; } = "";
    internal int? VoiceProgressPercent { get; private set; }
    internal bool PreparingVoice => ActiveVoicePackage is not null;
    internal bool CanCancelVoiceDownload => voiceDownload is not null;
    internal void CancelVoiceDownload() => voiceDownload?.Cancel();

    internal IEnumerable<ResidentModels> ResidentVoiceSets => [sileroModels, piperModels, additionalModels];

    internal AppShell(Application app)
    {
        application = app;
        piperModels = new("Piper", worker.ConfigurePiperAsync, RaiseChanged);
        sileroModels = new("Silero", worker.ConfigureSileroAsync, RaiseChanged);
        additionalModels = new("Kitten, Pocket, Kokoro, VOICEVOX", worker.ConfigureAdditionalAsync, RaiseChanged);
        var suffix = WindowsIdentity.GetCurrent().User!.Value;
        mutex = new(true, "Local\\QuietSelection.Instance." + suffix, out ownsMutex);
        store = new(AppPaths.Settings);
        if (!ownsMutex)
        {
            try { EventWaitHandle.OpenExisting("Local\\QuietSelection.Settings." + suffix).Set(); } catch { }
            return;
        }
        var signal = openSettings = new(false, EventResetMode.AutoReset, "Local\\QuietSelection.Settings." + suffix);
        _ = Task.Run(() =>
        {
            try { while (signal.WaitOne()) application.Dispatcher.BeginInvoke(ShowSettings); }
            catch (ObjectDisposedException) { }
        });
        LoadSettings();
        operations.Changed += (state, error) => application.Dispatcher.BeginInvoke(() => OnOperationChanged(state, error));
    }

    private void LoadSettings()
    {
        try
        {
            Settings = store.Load();
            if (MigrateKittenMicro(Settings)) store.Save(Settings);
        }
        catch (QuietException ex) { Status = Messages.For(ex.Code); SettingsWritable = false; }
    }

    // Kitten Micro was removed after listening tests; its profiles map to Mini voices of the same name.
    private static bool MigrateKittenMicro(AppSettings settings)
    {
        const string oldPrefix = "kitten:micro-0.8:";
        bool migrated = false;
        foreach (var language in new[] { settings.Primary }.Concat(settings.Additional))
        {
            if (!language.VoiceId.StartsWith(oldPrefix, StringComparison.Ordinal)) continue;
            string replacement = "kitten:mini-0.8:" + language.VoiceId[oldPrefix.Length..];
            if (!AdditionalModels.Kitten.Voices.Any(v => v.Id == replacement)) continue;
            language.VoiceId = replacement; migrated = true;
        }
        return migrated;
    }

    private void OnOperationChanged(OperationState state, ErrorCode error)
    {
        State = state;
        Status = error != ErrorCode.None ? Messages.For(error) : state switch
        {
            OperationState.ReadingSelection => "Получение выделения…",
            OperationState.SelectingRegion => "Выберите область экрана.",
            OperationState.Recognizing => "Распознавание…",
            OperationState.Translating => "Перевод…",
            OperationState.Speaking => "Чтение…",
            _ => "Готово."
        };
        tray?.SetState(Busy, Status);
        // Hidden while a region is chosen or captured, so it never ends up in the OCR image.
        if (Settings.ShowReadingIndicator && state is OperationState.Translating or OperationState.Speaking)
            (indicator ??= new ReadingIndicator(Stop)).ShowState(state == OperationState.Translating ? "Перевод…" : "Чтение…");
        else indicator?.Hide();
        RaiseChanged();
        if (error is not (ErrorCode.None or ErrorCode.Cancelled) && settingsWindow is null) tray?.Notify(Status);
    }

    private void RaiseChanged() => Changed?.Invoke();

    internal async Task StartAsync(bool inTray)
    {
        if (!ownsMutex) { application.Shutdown(); return; }
        running = true;
        hotkeys = new(); hotkeys.Invoked += command => _ = ReadAsync(command);
        foreach (var binding in Settings.Hotkeys)
            if (!hotkeys.Set(binding.Key, binding.Value)) Status = "Не удалось зарегистрировать сочетание " + binding.Value;
        tray = new TrayIcon(ShowSettings, Stop, Exit);
        await RefreshStartupAsync();
        if (!inTray) ShowSettings();
        await RefreshVoicesAsync();
        await CheckModelsAsync();
    }

    internal void ShowSettings()
    {
        if (settingsWindow is null)
        {
            settingsWindow = new(this); settingsWindow.Closed += (_, _) => settingsWindow = null;
        }
        settingsWindow.Show();
        if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
        settingsWindow.Activate();
    }

    // Confirmation dialogs belong to whichever of our windows is in front (settings or voice catalog).
    private Window? DialogOwner => application.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w is not ReadingIndicator) ?? settingsWindow;
    private bool Confirm(string text, string title) =>
        (DialogOwner is { } owner ? MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Question)) == MessageBoxResult.Yes;

    internal async Task RefreshVoicesAsync()
    {
        try { Voices = (await worker.CallAsync(new("voices"), TimeSpan.FromSeconds(10), CancellationToken.None)).Voices ?? []; }
        catch (QuietException ex) { Status = Messages.For(ex.Code); }
        UpdateResidentVoices();
        RaiseChanged();
    }

    internal void Save()
    {
        if (!SettingsWritable) { SetStatus(Messages.For(ErrorCode.InvalidSettings)); return; }
        try { store.Save(Settings); }
        catch { SetStatus("Не удалось сохранить настройки."); }
        UpdateResidentVoices();
    }

    /// <summary>Moves an unreadable settings file aside and starts from defaults.</summary>
    internal void ResetSettings()
    {
        string? aside;
        try { aside = store.MoveAside(); }
        catch { SetStatus("Не удалось переместить повреждённый файл настроек."); return; }
        Settings = new();
        SettingsWritable = true;
        foreach (var command in Enum.GetValues<ReadingCommand>()) hotkeys?.Set(command, "");
        Save();
        SetStatus(aside is null ? "Настройки сброшены." : "Настройки сброшены. Прежний файл сохранён: " + Path.GetFileName(aside));
    }

    // Keep only the voices selected for active languages loaded, per worker and option.
    private void UpdateResidentVoices()
    {
        if (!running || exiting || PreparingVoice) return;
        string[] Selected(bool keep, Func<SpeechEngine, bool> engine) => keep
            ? Settings.ActiveLanguages.Select(x => x.VoiceId).Where(id => SpeechEngines.FromVoiceId(id) is { } e && engine(e)).Distinct().Order().ToArray()
            : [];
        piperModels.Update(Selected(Settings.KeepPiperLoaded, e => e == SpeechEngine.Piper));
        sileroModels.Update(Selected(Settings.KeepSileroLoaded, e => e == SpeechEngine.Silero));
        additionalModels.Update(Selected(Settings.KeepAdditionalSpeechLoaded, e => e.IsAdditional()));
    }

    internal void SetStatus(string value) { Status = value; RaiseChanged(); }

    internal bool ChangeHotkey(ReadingCommand command, string chord)
    {
        string normalized = chord.Length == 0 ? "" : Chord.Parse(chord)?.ToString() ?? chord;
        if (hotkeys is null || !hotkeys.Set(command, normalized))
        { SetStatus("Сочетание " + normalized + " занято или недоступно. Прежнее назначение сохранено."); return false; }
        Settings.Hotkeys[command] = normalized; Save();
        SetStatus(normalized.Length == 0 ? "Сочетание удалено." : "Назначено: " + normalized + ".");
        return true;
    }

    /// <summary>Start at sign-in uses the package's StartupTask, so it is available only when installed.</summary>
    internal StartupState Startup { get; private set; } = StartupState.NotPackaged;

    private async Task RefreshStartupAsync()
    {
        Startup = await StartupTaskClient.GetStateAsync();
        RaiseChanged();
    }

    internal async Task SetStartAtLoginAsync(bool enabled)
    {
        Startup = await StartupTaskClient.SetAsync(enabled);
        if (Startup == StartupState.DisabledByUser && enabled)
            SetStatus("Автозапуск отключён в параметрах Windows. Включите QuietSelection в разделе «Автозагрузка».");
        RaiseChanged();
    }

    internal void SuspendHotkeys() => hotkeys?.Suspend();
    internal void ResumeHotkeys()
    {
        var lost = hotkeys?.Resume() ?? [];
        if (lost.Count == 0) return;
        foreach (var command in lost) Settings.Hotkeys[command] = "";
        Save();
        SetStatus("Другое приложение заняло сочетание. Назначьте его заново в разделе «Сочетания клавиш».");
    }

    internal void Stop() => operations.Stop();

    internal void Exit() => _ = ExitAsync();
    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true; operations.Stop(); modelPreparation?.Cancel(); voiceDownload?.Cancel();
        foreach (var set in ResidentVoiceSets) set.Cancel();
        try
        {
            foreach (var task in new[] { modelTask, voicePreparation }.Concat(ResidentVoiceSets.Select(x => x.Running)))
                if (task is not null)
                    try { await task; }
                    catch { } // A failed background task must not prevent exit.
        }
        finally { application.Shutdown(); }
    }

    internal void CancelModelPreparation() => modelPreparation?.Cancel();
    internal Task CheckModelsAsync() => PreparingModels ? modelTask ?? Task.CompletedTask : modelTask = PrepareModelsAsync(false);
    internal async Task DownloadModelsAsync()
    {
        if (PreparingModels) return;
        if (!Confirm("Подготовить локальные модели перевода?\n\nУже проверенные модели будут сохранены. Прямой перевод японский → русский: 288,6 МБ с object.pouta.csc.fi (Helsinki-NLP / OPUS-MT, CC BY 4.0). Английский → русский и японский → английский: 312,9 МБ с argos-net.com (Argos, MIT/CC0 по заявлению издателя), если они ещё не установлены.\n\nПолный набор: до 601,5 МБ. Подготовка выполняется на этом компьютере. Закрытие настроек оставляет её в фоне; выход из приложения отменяет подготовку.",
            "Загрузка моделей перевода")) return;
        await (modelTask = PrepareModelsAsync(true));
    }

    private async Task PrepareModelsAsync(bool download)
    {
        if (PreparingModels) return;
        using var cancellation = new CancellationTokenSource(); modelPreparation = cancellation;
        ModelsReady = false; ModelStatus = download ? "Загрузка…" : "Проверка целостности…"; RaiseChanged();
        try
        {
            var progress = new Progress<string>(value => { if (!cancellation.IsCancellationRequested && modelPreparation == cancellation) { ModelStatus = value; RaiseChanged(); } });
            if (download) await ModelPreparation.PrepareAsync(null, progress, cancellation.Token);
            else
            {
                await ModelPreparation.VerifyAsync(TranslationClient.DefaultModelRoot, cancellation.Token);
                ModelStatus = "Проверка: локальный перевод тестовых фраз…"; RaiseChanged();
                await new TranslationClient(TranslationClient.DefaultModelRoot).ProbeAsync(cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            ModelsReady = true; ModelStatus = "Готово — подтверждено локальным переводом.";
        }
        catch (OperationCanceledException) { ModelStatus = cancellation.IsCancellationRequested ? "Подготовка отменена. Можно повторить." : "Нет ответа от сервера. Повторите загрузку."; }
        catch (QuietException ex) { ModelStatus = ex.Code == ErrorCode.ModelMissing ? "Модели не установлены." : Messages.For(ex.Code); }
        catch { ModelStatus = "Ошибка подготовки моделей. Можно повторить загрузку или проверку."; }
        finally { modelPreparation = null; RaiseChanged(); }
    }

    /// <summary>Starts preparation of a catalog entry after the user confirms its terms.</summary>
    internal Task InstallAsync(VoicePackageInfo package)
    {
        if (PreparingVoice) { SetStatus("Установка голоса уже выполняется."); return voicePreparation ?? Task.CompletedTask; }
        Task task = package.Kind switch
        {
            VoicePackageKind.Piper => InstallPiperAsync(package),
            VoicePackageKind.Silero => InstallSileroAsync(package),
            VoicePackageKind.Additional => InstallAdditionalAsync(package),
            VoicePackageKind.RhVoice => InstallRhVoiceAsync(package),
            _ => InstallWindowsVoiceAsync(package)
        };
        // Windows installers run elevated and continue on their own; exit does not wait for them.
        if (package.Kind is not (VoicePackageKind.Windows or VoicePackageKind.RhVoice)) voicePreparation = task;
        return task;
    }

    private Task InstallPiperAsync(VoicePackageInfo package)
    {
        if (!Confirm($"Подготовить локальные голоса Piper?\n\nРусские: Denis, Dmitri, Irina (женский), Ruslan (мужской). Английский: LJSpeech.\n\nДо {TextFormat.Megabytes(PiperModels.DownloadBytes)} из каталога rhasspy/piper-voices на Hugging Face. Проверенные модели не загружаются повторно. Движок уже входит в приложение.\n\nДвижок — GPL-3.0. Корпуса Denis/Dmitri — CC0, LJSpeech — public domain. Корпус Ruslan — CC BY-NC-SA 4.0 (некоммерческое использование); лицензия корпуса Irina в карточке не указана. Базовый голос Lessac имеет отдельные условия.\n\nЗагрузку можно отменить. Готовность будет проверена настоящим синтезом.",
            "Голоса Piper")) return Task.CompletedTask;
        return PrepareVoicesAsync(package, piperModels, (progress, token) => PiperModels.PrepareAsync(null, progress, token), "Piper");
    }

    private Task InstallSileroAsync(VoicePackageInfo package)
    {
        var catalog = SileroModels.Catalog;
        if (!Confirm($"Подготовить {TextFormat.Voices(catalog.Voices.Length)} Silero?\n\nОдна модель: {TextFormat.Megabytes(catalog.File.Bytes)} с официального сервера Silero. Движок уже входит в приложение.\n\nМодель — CC BY-NC-SA 4.0, бесплатное некоммерческое использование. Проверенные файлы используются повторно. Загрузку можно отменить.\n\nТекущий выбор голоса не изменится.",
            "Русские голоса Silero")) return Task.CompletedTask;
        return PrepareVoicesAsync(package, sileroModels, (progress, token) => SileroModels.PrepareAsync(null, progress, token, "ru"), "Silero");
    }

    private Task InstallAdditionalAsync(VoicePackageInfo package)
    {
        var engine = package.Engine!.Value;
        string terms = engine switch
        {
            SpeechEngine.Kitten => "KittenTTS Mini 0.8: восемь английских голосов, общая модель. Модель — Apache 2.0.",
            SpeechEngine.Pocket => "Pocket TTS: 21 английский голос. Модель — CC BY 4.0, код — MIT. Cosette и Jean предназначены только для некоммерческого использования; условия остальных голосов и указание авторства — в third-party/pocket.",
            SpeechEngine.Kokoro => "Kokoro INT8: английские голоса, общая модель. Модель — Apache 2.0.",
            _ => "VOICEVOX:四国めたん / VOICEVOX:玄野武宏 — только обычные варианты. Бесплатное использование по условиям VOICEVOX и персонажей. При распространении аудио указывайте соответствующий кредит. Полные условия находятся в third-party/voicevox внутри приложения."
        };
        if (!Confirm($"{terms}\n\nЗагрузка с {package.Source}: до {TextFormat.Megabytes(package.Bytes ?? 0)}. Файлы загружаются последовательно и проверяются. Текущий выбор голоса сохраняется.",
            package.Title)) return Task.CompletedTask;
        return PrepareVoicesAsync(package, additionalModels, (progress, token) => AdditionalModels.PrepareAsync(engine, null, progress, token), package.Title);
    }

    // Shared flow for model sets that replace files the resident worker may hold open.
    private async Task PrepareVoicesAsync(VoicePackageInfo package, ResidentModels resident,
        Func<IProgress<string>, CancellationToken, Task> prepare, string name)
    {
        using var cancellation = BeginVoiceTask(package, withCancellation: true)!;
        try
        {
            await resident.ReleaseAsync(cancellation.Token);
            await prepare(VoiceProgressReporter(cancellation), cancellation.Token);
            await RefreshVoicesAsync();
            SetStatus(name + ": голоса готовы. Выберите голос в разделе «Речь» и нажмите «Прослушать».");
        }
        catch (OperationCanceledException) { SetStatus(name + ": подготовка отменена; прежние голоса сохранены."); }
        catch (QuietException ex) { SetStatus(Messages.For(ex.Code)); }
        catch { SetStatus(name + ": не удалось подготовить голоса. Прежние модели сохранены; можно повторить."); }
        finally { EndVoiceTask(); }
    }

    private async Task InstallRhVoiceAsync(VoicePackageInfo info)
    {
        var package = info.RhVoice!;
        if (!Confirm($"Загрузить и установить RHVoice {package.Name}?\n\nРазмер: {TextFormat.Megabytes(package.Bytes)}. Источник: официальный репозиторий RHVoice на GitHub. Лицензия голоса: {package.License}. Движок входит в пакет.\n\nУстановщик не подписан; приложение проверяет закреплённые размер и SHA-256. Windows запросит права администратора для установки SAPI. После установки голос работает локально.\n\nЗагрузку можно отменить. После запуска системного установщика дождитесь завершения.",
            "Голос RHVoice")) return;
        var cancellation = BeginVoiceTask(info, withCancellation: true)!;
        try
        {
            string path = await RhVoiceInstallation.DownloadAsync(package, AppPaths.Downloads("rhvoice"), VoiceProgressReporter(cancellation), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            voiceDownload = null; VoiceProgressPercent = null;
            SetVoiceProgress("Установка… Подтвердите запрос Windows.");
            int code = await RhVoiceInstallation.InstallAsync(path, package);
            if (code is not (0 or 3010)) { SetStatus("RHVoice: установка не завершена, код " + code + "."); return; }
            await RefreshVoicesAsync();
            var voice = Voices.FirstOrDefault(x => x.Id.StartsWith("sapi5:", StringComparison.Ordinal)
                && x.Name.Contains(package.Name, StringComparison.OrdinalIgnoreCase) && x.Language.Split('-')[0] == package.Language);
            if (voice is null) { SetStatus("RHVoice ещё не появился в SAPI. Обновите список или перезапустите Windows."); return; }
            await worker.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase(package.Language), VoiceId: voice.Id), TimeSpan.FromSeconds(30), CancellationToken.None);
            SetStatus("RHVoice " + package.Name + " установлен и прошёл проверку.");
        }
        catch (OperationCanceledException) { SetStatus("Загрузка RHVoice отменена."); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { SetStatus("Установка RHVoice отменена."); }
        catch { SetStatus("Не удалось загрузить или проверить RHVoice. Выбранные голоса сохранены."); }
        finally { cancellation.Dispose(); EndVoiceTask(); }
    }

    private async Task InstallWindowsVoiceAsync(VoicePackageInfo info)
    {
        string language = info.WindowsLanguage!;
        var tag = language switch { "ru" => "ru-RU", "en" => "en-US", "ja" => "ja-JP", _ => throw new ArgumentException("UNSUPPORTED_VOICE_LANGUAGE") };
        var label = AppSettings.SupportedLanguages[language];
        if (!Confirm("Windows загрузит компоненты языка и синтеза речи: " + label + ".\n\nИсточник: Windows Update. Точный размер заранее не определён. Потребуется отдельное подтверждение прав администратора. Язык интерфейса Windows не изменится.\n\nЕсли закрыть это окно, запущенная установка продолжится. Начать установку?",
            "Голос Windows")) return;
        BeginVoiceTask(info, withCancellation: false);
        try
        {
            SetVoiceProgress("Ожидание Windows…");
            int code = await VoiceInstallation.InstallAsync(tag, stage => SetVoiceProgress(stage == "Basic" ? "Компоненты языка…" : "Локальный голос…"));
            if (code != 0 && code != 3010) { SetStatus("Windows не завершила установку голоса. Код: " + code + "."); return; }
            await RefreshVoicesAsync();
            var voice = Voices.FirstOrDefault(x => x.Language == tag);
            if (voice is null) { SetStatus("Установка завершилась, но голос ещё не виден. Возможно, требуется перезапуск Windows."); return; }
            await worker.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase(language), VoiceId: voice.Id), TimeSpan.FromSeconds(30), CancellationToken.None);
            SetStatus("Голос доступен: " + label + ".");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { SetStatus("Установка голоса отменена."); }
        catch { SetStatus("Не удалось подготовить голос. Проверьте статус установки в настройках Windows."); }
        finally { EndVoiceTask(); }
    }

    private CancellationTokenSource? BeginVoiceTask(VoicePackageInfo package, bool withCancellation)
    {
        ActiveVoicePackage = package.Key; VoiceProgress = "Подготовка…"; VoiceProgressPercent = null;
        voiceDownload = withCancellation ? new CancellationTokenSource() : null;
        RaiseChanged();
        return voiceDownload;
    }

    private void EndVoiceTask()
    {
        voiceDownload = null; ActiveVoicePackage = null; VoiceProgress = ""; VoiceProgressPercent = null;
        UpdateResidentVoices(); RaiseChanged();
    }

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex PercentPattern();

    private Progress<string> VoiceProgressReporter(CancellationTokenSource cancellation) => new(value =>
    {
        if (voiceDownload != cancellation || cancellation.IsCancellationRequested) return;
        var match = PercentPattern().Match(value);
        VoiceProgressPercent = match.Success ? Math.Min(100, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)) : null;
        SetVoiceProgress(value);
    });

    private void SetVoiceProgress(string value) { VoiceProgress = value; RaiseChanged(); }

    internal Task PreviewAsync(LanguageSettings language)
    {
        var snapshot = new LanguageSettings { Language = language.Language, VoiceId = language.VoiceId, Speed = language.Speed };
        return operations.RunOrStopAsync(async operation =>
        {
            if (!Voices.Any(x => x.Id == snapshot.VoiceId)) throw new QuietException(ErrorCode.VoiceMissing);
            operation.Transition(OperationState.Speaking);
            // Same buffered player as reading; the workers' own playback reopened the device per block.
            await worker.SpeakPlanAsync([new(LanguagePlanner.TestPhrase(snapshot.Language), snapshot.Language, snapshot.VoiceId, snapshot.Speed)], operation.Token);
        });
    }

    private Task ReadAsync(ReadingCommand command)
    {
        nint source = NativeMethods.GetForegroundWindow();
        var snapshot = Settings.Snapshot(); var installed = Voices.ToArray();
        return operations.RunOrStopAsync(async operation =>
        {
            string text;
            if (command == ReadingCommand.Region)
            {
                if (!NativeMethods.IsPackaged) throw new QuietException(ErrorCode.PackageIdentityRequired);
                operation.Transition(OperationState.SelectingRegion);
                var region = await RegionPicker.SelectAsync(source, operation.Token);
                region.Validate(4096);
                operation.Transition(OperationState.Recognizing);
                text = (await worker.CallAsync(new("ocr", (long)source, Language: snapshot.OcrLanguage, Region: region), TimeSpan.FromSeconds(60), operation.Token)).Text;
            }
            else
            {
                operation.Transition(OperationState.ReadingSelection);
                text = (await worker.CallAsync(new("selection", (long)source), TimeSpan.FromSeconds(2), operation.Token)).Text;
            }
            if (command == ReadingCommand.Translate)
            {
                operation.Transition(OperationState.Translating);
                IReadOnlyList<TranslationSegment> plan;
                try { plan = TranslationPlanner.Plan(text, snapshot); }
                catch (QuietException ex) when (ex.Code == ErrorCode.SourceLanguageRequired)
                {
                    string chosen = await SourceLanguagePicker.ChooseAsync(snapshot.ActiveLanguages.Select(x => x.Language), operation.Token);
                    plan = TranslationPlanner.Plan(text, snapshot, chosen);
                }
                // Validate the output voice and every route before doing expensive work.
                _ = LanguagePlanner.Plan(LanguagePlanner.TestPhrase(snapshot.Primary.Language), snapshot, true, installed);
                if (plan.Any(x => x.Source != snapshot.Primary.Language))
                {
                    if (PreparingModels) throw new QuietException(ErrorCode.ModelMissing);
                    await ModelPreparation.VerifyCachedAsync(TranslationClient.DefaultModelRoot, operation.Token);
                    text = await new TranslationClient(TranslationClient.DefaultModelRoot).TranslateAsync(plan, snapshot.Primary.Language, operation.Token);
                }
                operation.Token.ThrowIfCancellationRequested();
            }
            var speech = LanguagePlanner.Plan(text, snapshot, command is ReadingCommand.PrimaryVoice or ReadingCommand.Translate, installed);
            operation.Transition(OperationState.Speaking);
            await worker.SpeakPlanAsync(speech, operation.Token);
        });
    }

    public void Dispose()
    {
        operations.Stop(); modelPreparation?.Cancel(); voiceDownload?.Cancel();
        foreach (var set in ResidentVoiceSets) set.Cancel();
        worker.Dispose(); tray?.Dispose(); indicator?.Close(); hotkeys?.Dispose(); openSettings?.Dispose();
        if (ownsMutex) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
