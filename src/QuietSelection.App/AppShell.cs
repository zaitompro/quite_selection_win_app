using System.Security.Principal;
using System.Diagnostics;
using System.Windows;
using QuietSelection.App.Platform;
using QuietSelection.App.Views;
using QuietSelection.App.Workers;
using QuietSelection.App.Translation;
using QuietSelection.App.Translation.Downloads;
using QuietSelection.App.Speech.Downloads;
using Forms = System.Windows.Forms;

namespace QuietSelection.App;

internal sealed class AppShell : IDisposable
{
    private readonly Application application;
    private readonly SettingsStore store;
    private readonly WorkerClient worker = new();
    private readonly OperationController operations = new();
    private readonly Mutex mutex;
    private readonly bool ownsMutex;
    private EventWaitHandle? openSettings;
    private Forms.NotifyIcon? tray;
    private HotkeyManager? hotkeys;
    private SettingsWindow? settingsWindow;
    private bool settingsWritable = true;
    private bool preparingVoice;
    private CancellationTokenSource? voiceDownload;
    private Task? voicePreparation;
    internal bool PreparingVoice => preparingVoice;
    internal bool CanCancelVoiceDownload => voiceDownload is not null;
    internal void CancelVoiceDownload() => voiceDownload?.Cancel();
    private CancellationTokenSource? modelPreparation;
    private Task? modelTask;
    private bool exiting;
    private bool running;
    private string? piperSignature;
    private CancellationTokenSource? piperCacheCancellation;
    private Task? piperCacheTask;
    private string? sileroSignature;
    private CancellationTokenSource? sileroCacheCancellation;
    private Task? sileroCacheTask;
    private string? additionalSignature;
    private CancellationTokenSource? additionalCacheCancellation;
    private Task? additionalCacheTask;
    internal string AdditionalStatus { get; private set; } = "Kitten / Pocket / Kokoro / VOICEVOX: модели загружаются при выборе голосов.";
    internal string SileroStatus { get; private set; } = "Silero: модели загружаются при выборе голосов.";
    internal string PiperStatus { get; private set; } = "Piper: модели загружаются при выборе голоса.";
    internal string ModelStatus { get; private set; } = "Проверка…";
    internal bool ModelsReady { get; private set; }
    internal bool PreparingModels => modelPreparation is not null;
    internal AppSettings Settings { get; private set; } = new();
    internal VoiceInfo[] Voices { get; private set; } = [];
    internal string Status { get; private set; } = "Готово.";
    internal event Action? Changed;

    internal AppShell(Application app)
    {
        application = app;
        var suffix = WindowsIdentity.GetCurrent().User!.Value;
        mutex = new(true, "Local\\QuietSelection.Instance." + suffix, out ownsMutex);
        store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietSelection", "settings.json"));
        if (!ownsMutex)
        {
            try { EventWaitHandle.OpenExisting("Local\\QuietSelection.Settings." + suffix).Set(); } catch { }
            return;
        }
        openSettings = new(false, EventResetMode.AutoReset, "Local\\QuietSelection.Settings." + suffix);
        _ = Task.Run(() =>
        {
            try { while (openSettings.WaitOne()) application.Dispatcher.BeginInvoke(ShowSettings); }
            catch (ObjectDisposedException) { }
        });
        try
        {
            Settings = store.Load();
            bool migrated = false;
            foreach (var language in new[] { Settings.Primary }.Concat(Settings.Additional))
            {
                const string oldPrefix = "kitten:micro-0.8:";
                if (!language.VoiceId.StartsWith(oldPrefix, StringComparison.Ordinal)) continue;
                string replacement = "kitten:mini-0.8:" + language.VoiceId[oldPrefix.Length..];
                if (!AdditionalModels.Kitten.Voices.Any(v => v.Id == replacement)) continue;
                language.VoiceId = replacement; migrated = true;
            }
            if (migrated) store.Save(Settings);
        }
        catch (QuietException ex) { Status = Messages.For(ex.Code); settingsWritable = false; }
        operations.Changed += (state, error) => application.Dispatcher.BeginInvoke(() =>
        {
            Status = error != ErrorCode.None ? Messages.For(error) : state switch
            {
                OperationState.ReadingSelection => "Получение выделения…",
                OperationState.SelectingRegion => "Выберите область экрана.",
                OperationState.Recognizing => "Распознавание…",
                OperationState.Translating => "Перевод…",
                OperationState.Speaking => "Чтение…",
                _ => "Готово."
            };
            Changed?.Invoke();
            if (error != ErrorCode.None && error != ErrorCode.Cancelled && settingsWindow is null && tray is not null)
                tray.ShowBalloonTip(3000, "QuietSelection", Status, Forms.ToolTipIcon.Info);
        });
    }

    internal async Task StartAsync(bool inTray)
    {
        if (!ownsMutex) { application.Shutdown(); return; }
        running = true;
        hotkeys = new(); hotkeys.Invoked += command => _ = ReadAsync(command);
        foreach (var binding in Settings.Hotkeys)
            if (!hotkeys.Set(binding.Key, binding.Value)) Status = "Не удалось зарегистрировать сочетание " + binding.Value;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("QuietSelection 0.1.0", null, (_, _) => { }).Enabled = false;
        menu.Items.Add("Настройки…", null, (_, _) => ShowSettings());
        menu.Items.Add("Завершить", null, (_, _) => Exit());
        tray = new() { Text = "QuietSelection 0.1.0", Icon = System.Drawing.SystemIcons.Information, ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) menu.Show(Forms.Cursor.Position); };
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
        settingsWindow.OpenGeneral(); settingsWindow.Show();
        if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
        settingsWindow.Activate();
    }

    internal async Task RefreshVoicesAsync()
    {
        try { Voices = (await worker.CallAsync(new("voices"), TimeSpan.FromSeconds(10), CancellationToken.None)).Voices ?? []; }
        catch (QuietException ex) { Status = Messages.For(ex.Code); }
        UpdatePiperCache();
        UpdateSileroCache();
        UpdateAdditionalCache();
        Changed?.Invoke();
    }

    internal void Save()
    {
        if (!settingsWritable) { SetStatus(Messages.For(ErrorCode.InvalidSettings)); return; }
        try { store.Save(Settings); }
        catch { SetStatus("Не удалось сохранить настройки."); }
        UpdatePiperCache();
        UpdateSileroCache();
        UpdateAdditionalCache();
    }

    private void UpdatePiperCache()
    {
        if (!running || exiting || preparingVoice) return;
        string[] ids = Settings.KeepPiperLoaded ? Settings.ActiveLanguages.Select(x => x.VoiceId)
            .Where(x => x.StartsWith("piper:", StringComparison.Ordinal)).Distinct().Order().ToArray() : [];
        string signature = string.Join("|", ids);
        if (signature == piperSignature) return;
        piperSignature = signature;
        piperCacheCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); piperCacheCancellation = cancellation;
        piperCacheTask = ConfigurePiperAsync(ids, cancellation);
    }

    private async Task ConfigurePiperAsync(string[] ids, CancellationTokenSource cancellation)
    {
        PiperStatus = ids.Length == 0 ? "Piper: освобождение памяти…" : "Piper: предварительная загрузка выбранных голосов…";
        Changed?.Invoke();
        try
        {
            await worker.ConfigurePiperAsync(ids, cancellation.Token);
            if (piperCacheCancellation != cancellation) return;
            PiperStatus = ids.Length == 0 ? "Piper: в памяти нет моделей." : $"Piper готов: в памяти голосов — {ids.Length}.";
        }
        catch (OperationCanceledException) { }
        catch (QuietException ex) { if (piperCacheCancellation == cancellation) { PiperStatus = Messages.For(ex.Code); piperSignature = null; } }
        catch { if (piperCacheCancellation == cancellation) { PiperStatus = "Не удалось предварительно загрузить Piper. Повторите выбор голоса."; piperSignature = null; } }
        finally
        {
            if (piperCacheCancellation == cancellation) piperCacheCancellation = null;
            cancellation.Dispose(); Changed?.Invoke();
        }
    }
    private void UpdateSileroCache()
    {
        if (!running || exiting || preparingVoice) return;
        string[] ids = Settings.KeepSileroLoaded ? Settings.ActiveLanguages.Select(x => x.VoiceId)
            .Where(x => x.StartsWith("silero:", StringComparison.Ordinal)).Distinct().Order().ToArray() : [];
        string signature = string.Join("|", ids);
        if (signature == sileroSignature) return;
        sileroSignature = signature;
        sileroCacheCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); sileroCacheCancellation = cancellation;
        sileroCacheTask = ConfigureSileroAsync(ids, cancellation);
    }

    private async Task ConfigureSileroAsync(string[] ids, CancellationTokenSource cancellation)
    {
        SileroStatus = ids.Length == 0 ? "Silero: освобождение памяти…" : "Silero: предварительная загрузка выбранных голосов…";
        Changed?.Invoke();
        try
        {
            await worker.ConfigureSileroAsync(ids, cancellation.Token);
            if (sileroCacheCancellation != cancellation) return;
            SileroStatus = ids.Length == 0 ? "Silero: в памяти нет моделей." : "Silero готов: выбранные модели в памяти.";
        }
        catch (OperationCanceledException) { }
        catch (QuietException ex) { if (sileroCacheCancellation == cancellation) { SileroStatus = Messages.For(ex.Code); sileroSignature = null; } }
        catch { if (sileroCacheCancellation == cancellation) { SileroStatus = "Не удалось предварительно загрузить Silero. Повторите выбор голоса."; sileroSignature = null; } }
        finally
        {
            if (sileroCacheCancellation == cancellation) sileroCacheCancellation = null;
            cancellation.Dispose(); Changed?.Invoke();
        }
    }
    private void UpdateAdditionalCache()
    {
        if (!running || exiting || preparingVoice) return;
        string[] ids = Settings.KeepAdditionalSpeechLoaded ? Settings.ActiveLanguages.Select(x => x.VoiceId)
            .Where(x => (x.StartsWith("kokoro:", StringComparison.Ordinal) || x.StartsWith("voicevox:", StringComparison.Ordinal) || x.StartsWith("kitten:", StringComparison.Ordinal) || x.StartsWith("pocket:", StringComparison.Ordinal))).Distinct().Order().ToArray() : [];
        string signature = string.Join("|", ids);
        if (signature == additionalSignature) return;
        additionalSignature = signature;
        additionalCacheCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); additionalCacheCancellation = cancellation;
        additionalCacheTask = ConfigureAdditionalAsync(ids, cancellation);
    }

    private async Task ConfigureAdditionalAsync(string[] ids, CancellationTokenSource cancellation)
    {
        AdditionalStatus = ids.Length == 0 ? "Kitten / Pocket / Kokoro / VOICEVOX: освобождение памяти…" : "Kitten / Pocket / Kokoro / VOICEVOX: предварительная загрузка выбранных голосов…";
        Changed?.Invoke();
        try
        {
            await worker.ConfigureAdditionalAsync(ids, cancellation.Token);
            if (additionalCacheCancellation != cancellation) return;
            AdditionalStatus = ids.Length == 0 ? "Kitten / Pocket / Kokoro / VOICEVOX: в памяти нет моделей." : "Kitten / Pocket / Kokoro / VOICEVOX готовы: выбранные модели в памяти.";
        }
        catch (OperationCanceledException) { }
        catch (QuietException ex) { if (additionalCacheCancellation == cancellation) { AdditionalStatus = Messages.For(ex.Code); additionalSignature = null; } }
        catch { if (additionalCacheCancellation == cancellation) { AdditionalStatus = "Не удалось предварительно загрузить Kitten / Pocket / Kokoro / VOICEVOX. Повторите выбор голоса."; additionalSignature = null; } }
        finally
        {
            if (additionalCacheCancellation == cancellation) additionalCacheCancellation = null;
            cancellation.Dispose(); Changed?.Invoke();
        }
    }
    internal void SetStatus(string value) { Status = value; Changed?.Invoke(); }
    internal bool ChangeHotkey(ReadingCommand command, string chord)
    {
        if (hotkeys is null || !hotkeys.Set(command, chord))
        { SetStatus("Сочетание занято. Предыдущее назначение сохранено."); return false; }
        Settings.Hotkeys[command] = chord; Save(); return true;
    }
    internal void Stop() => operations.Stop();
    internal async void Exit()
    {
        if (exiting) return;
        exiting = true; operations.Stop(); modelPreparation?.Cancel(); voiceDownload?.Cancel(); piperCacheCancellation?.Cancel(); sileroCacheCancellation?.Cancel(); additionalCacheCancellation?.Cancel();
        if (modelTask is not null) await modelTask;
        if (voicePreparation is not null) await voicePreparation;
        if (piperCacheTask is not null) await piperCacheTask;
        if (sileroCacheTask is not null) await sileroCacheTask;
        if (additionalCacheTask is not null) await additionalCacheTask;
        application.Shutdown();
    }

    internal void CancelModelPreparation() => modelPreparation?.Cancel();
    internal Task CheckModelsAsync() => PreparingModels ? modelTask ?? Task.CompletedTask : modelTask = PrepareModelsAsync(false);
    internal async Task DownloadModelsAsync()
    {
        if (PreparingModels) return;
        if (MessageBox.Show(settingsWindow, "Подготовить локальные модели?\n\nУже проверенные модели будут сохранены. Новый прямой перевод японский → русский: 288,6 МБ с object.pouta.csc.fi (Helsinki-NLP / OPUS-MT, CC BY 4.0). Английский → русский и японский → английский: 312,9 МБ с argos-net.com (Argos, MIT/CC0 по заявлению издателя), если они ещё не установлены.\n\nПолный набор: до 601,5 МБ загрузки. Подготовка выполняется на этом компьютере. Закрытие настроек оставляет её в фоне; выход из приложения отменяет подготовку.",
            "Загрузка локальных моделей", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await (modelTask = PrepareModelsAsync(true));
    }

    private async Task PrepareModelsAsync(bool download)
    {
        if (PreparingModels) return;
        using var cancellation = new CancellationTokenSource(); modelPreparation = cancellation;
        ModelsReady = false; ModelStatus = download ? "Загрузка…" : "Проверка целостности…"; Changed?.Invoke();
        try
        {
            var progress = new Progress<string>(value => { if (!cancellation.IsCancellationRequested && modelPreparation == cancellation) { ModelStatus = value; Changed?.Invoke(); } });
            if (download) await ModelPreparation.PrepareAsync(null, progress, cancellation.Token);
            else
            {
                await ModelPreparation.VerifyAsync(TranslationClient.DefaultModelRoot, cancellation.Token);
                ModelStatus = "Проверка: локальный перевод тестовых фраз…"; Changed?.Invoke();
                await new TranslationClient(TranslationClient.DefaultModelRoot).ProbeAsync(cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            ModelsReady = true; ModelStatus = "Готово — подтверждено локальным переводом.";
        }
        catch (OperationCanceledException) { ModelStatus = cancellation.IsCancellationRequested ? "Подготовка отменена. Можно повторить." : "Нет ответа от сервера. Повторите загрузку."; }
        catch (QuietException ex) { ModelStatus = ex.Code == ErrorCode.ModelMissing ? "Нужна загрузка или восстановление моделей." : Messages.For(ex.Code); }
        catch { ModelStatus = "Ошибка подготовки моделей. Можно повторить загрузку или проверку."; }
        finally { modelPreparation = null; Changed?.Invoke(); }
    }

    internal async Task InstallVoiceAsync(string language)
    {
        if (preparingVoice) { SetStatus("Установка голоса уже выполняется."); return; }
        var tag = language switch { "ru" => "ru-RU", "en" => "en-US", "ja" => "ja-JP", _ => throw new ArgumentException("UNSUPPORTED_VOICE_LANGUAGE") };
        var label = AppSettings.SupportedLanguages[language];
        if (MessageBox.Show(settingsWindow,
            "Windows загрузит компоненты языка и синтеза речи: " + label + ".\n\nИсточник: Windows Update. Точный размер заранее не определён. Потребуется отдельное подтверждение прав администратора. Язык интерфейса Windows не изменится.\n\nЕсли закрыть это окно, запущенная установка продолжится. Начать установку?",
            "Установка локального голоса", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        preparingVoice = true;
        try
        {
            SetStatus("Windows устанавливает голос: " + label + ". Ожидайте завершения.");
            int code = await VoiceInstallation.InstallAsync(tag, stage => SetStatus("Установка: " + label + (stage == "Basic" ? " — компоненты языка…" : " — локальный голос…")));
            if (code != 0 && code != 3010) { SetStatus("Windows не завершила установку голоса. Код: " + code + ". Можно воспользоваться штатными настройками."); return; }
            await RefreshVoicesAsync();
            var voice = Voices.FirstOrDefault(x => x.Language == tag);
            if (voice is null) { SetStatus("Установка завершилась, но речевой API ещё не видит голос. Возможно, требуется перезапуск Windows."); return; }
            await worker.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase(language), VoiceId: voice.Id), TimeSpan.FromSeconds(30), CancellationToken.None);
            SetStatus("Голос доступен: " + label + ". Выберите его в разделе «Речь» и нажмите «Прослушать».");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { SetStatus("Установка голоса отменена."); }
        catch { SetStatus("Не удалось подготовить голос. Проверьте статус установки в настройках Windows."); }
        finally { preparingVoice = false; }
    }

    internal async Task InstallRhVoiceAsync(RhVoicePackage package)
    {
        if (preparingVoice) { SetStatus("Установка голоса уже выполняется."); return; }
        if (MessageBox.Show(settingsWindow,
            $"Загрузить и установить RHVoice {package.Name}?\n\nРазмер: {package.Bytes / 1_000_000.0:F2} МБ. Источник: официальный репозиторий RHVoice на GitHub. Лицензия голоса: {package.License}. Движок входит в пакет.\n\nУстановщик не подписан; приложение проверяет закреплённые размер и SHA-256. Windows запросит права администратора для установки SAPI. После установки голос работает локально.\n\nЗагрузку можно отменить. После запуска системного установщика дождитесь завершения.",
            "Бесплатный голос RHVoice", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        preparingVoice = true;
        using var cancellation = new CancellationTokenSource(); voiceDownload = cancellation;
        try
        {
            var progress = new Progress<string>(value => { if (voiceDownload == cancellation && !cancellation.IsCancellationRequested) SetStatus(value); });
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietSelection", "downloads", "rhvoice");
            string path = await RhVoiceInstallation.DownloadAsync(package, directory, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); voiceDownload = null;
            SetStatus("Установка RHVoice " + package.Name + "… Подтвердите запрос Windows.");
            int code = await RhVoiceInstallation.InstallAsync(path, package);
            if (code is not (0 or 3010)) { SetStatus("RHVoice: установка не завершена, код " + code + "."); return; }
            await RefreshVoicesAsync();
            var voice = Voices.FirstOrDefault(x => x.Id.StartsWith("sapi5:", StringComparison.Ordinal)
                && x.Name.Contains(package.Name, StringComparison.OrdinalIgnoreCase) && x.Language.Split('-')[0] == package.Language);
            if (voice is null) { SetStatus("RHVoice ещё не появился в SAPI. Обновите список или перезапустите Windows."); return; }
            await worker.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase(package.Language), VoiceId: voice.Id), TimeSpan.FromSeconds(30), CancellationToken.None);
            SetStatus("RHVoice " + package.Name + " установлен и прошёл проверку. Выберите его в списке и нажмите «Прослушать».");
        }
        catch (OperationCanceledException) { SetStatus("Загрузка RHVoice отменена."); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { SetStatus("Установка RHVoice отменена."); }
        catch { SetStatus("Не удалось загрузить или проверить RHVoice. Повторите установку; выбранные голоса сохранены."); }
        finally { voiceDownload = null; preparingVoice = false; Changed?.Invoke(); }
    }

    internal Task InstallPiperAsync()
    {
        if (preparingVoice) { SetStatus("Установка голоса уже выполняется."); return voicePreparation ?? Task.CompletedTask; }
        return voicePreparation = InstallPiperCoreAsync();
    }
    private async Task InstallPiperCoreAsync()
    {
        if (preparingVoice) { SetStatus("Установка голоса уже выполняется."); return; }
        if (MessageBox.Show(settingsWindow,
            $"Подготовить локальные голоса Piper?\n\nРусский: Denis, Dmitri, Irina (женский), Ruslan (мужской). Английский: LJSpeech.\n\nПолный набор: до {PiperModels.DownloadBytes / 1_000_000.0:F2} МБ из каталога rhasspy/piper-voices на Hugging Face. Проверенные модели не загружаются повторно. Добавление Irina и Ruslan к прежнему набору: 126,41 МБ. Движок уже входит в приложение.\n\nДвижок — GPL-3.0. Корпуса Denis/Dmitri — CC0, LJSpeech — public domain. Корпус Ruslan — CC BY-NC-SA 4.0 (некоммерческое использование); лицензия корпуса Irina в карточке не указана. Базовый голос Lessac имеет отдельные условия. Модели доступны для бесплатной загрузки; набор предназначен для локального сравнения.\n\nЗагрузку можно отменить. Готовность будет проверена настоящим синтезом.",
            "Компактные голоса Piper", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        preparingVoice = true;
        using var cancellation = new CancellationTokenSource(); voiceDownload = cancellation;
        try
        {
            var progress = new Progress<string>(value => { if (voiceDownload == cancellation && !cancellation.IsCancellationRequested) SetStatus(value); });
            piperCacheCancellation?.Cancel();
            if (piperCacheTask is not null) await piperCacheTask;
            await worker.ConfigurePiperAsync([], cancellation.Token);
            piperSignature = null;
            await PiperModels.PrepareAsync(null, progress, cancellation.Token);
            await RefreshVoicesAsync();
            SetStatus("Piper готов. Выберите голос в списке и нажмите «Прослушать». Русские голоса: Denis, Dmitri, Irina, Ruslan; английский: LJSpeech.");
        }
        catch (OperationCanceledException) { SetStatus("Подготовка Piper отменена; прежние голоса сохранены."); }
        catch (QuietException ex) { SetStatus(Messages.For(ex.Code)); }
        catch { SetStatus("Не удалось подготовить Piper. Прежние голоса сохранены; можно повторить загрузку."); }
        finally { voiceDownload = null; preparingVoice = false; UpdatePiperCache(); UpdateSileroCache(); UpdateAdditionalCache(); Changed?.Invoke(); }
    }

    internal Task InstallSileroAsync(string language = "ru")
    {
        if (preparingVoice) { SetStatus("Установка голоса уже выполняется."); return voicePreparation ?? Task.CompletedTask; }
        return voicePreparation = InstallSileroCoreAsync(language);
    }
    private async Task InstallSileroCoreAsync(string language)
    {
        var catalog = SileroModels.ForLanguage(language);
        string title = language == "en" ? "Английские голоса Silero" : "Русские голоса Silero";
        if (MessageBox.Show(settingsWindow,
            $"Подготовить {catalog.Voices.Length} голосов Silero?\n\nОдна модель: {catalog.File.Bytes / 1_000_000.0:F2} МБ с официального сервера Silero. Движок уже входит в приложение.\n\nМодель — CC BY-NC-SA 4.0, бесплатное некоммерческое использование. Проверенные файлы используются повторно. Загрузку можно отменить.\n\nПосле установки выберите голос и нажмите «Прослушать». Текущий выбор не изменится.",
            title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        preparingVoice = true;
        using var cancellation = new CancellationTokenSource(); voiceDownload = cancellation;
        try
        {
            var progress = new Progress<string>(value => { if (voiceDownload == cancellation && !cancellation.IsCancellationRequested) SetStatus(value); });
            sileroCacheCancellation?.Cancel();
            if (sileroCacheTask is not null) await sileroCacheTask;
            await worker.ConfigureSileroAsync([], cancellation.Token);
            sileroSignature = null;
            await SileroModels.PrepareAsync(null, progress, cancellation.Token, language);
            await RefreshVoicesAsync();
            SetStatus("Silero готов. Выберите голос и нажмите «Прослушать».");
        }
        catch (OperationCanceledException) { SetStatus("Подготовка Silero отменена; прежние голоса сохранены."); }
        catch (QuietException ex) { SetStatus(Messages.For(ex.Code)); }
        catch { SetStatus("Не удалось подготовить Silero. Прежние голоса сохранены; можно повторить загрузку."); }
        finally { voiceDownload = null; preparingVoice = false; UpdatePiperCache(); UpdateSileroCache(); UpdateAdditionalCache(); Changed?.Invoke(); }
    }

    internal Task InstallAdditionalAsync(Speech.SpeechEngine engine)
    {
        if (preparingVoice) return voicePreparation ?? Task.CompletedTask;
        return voicePreparation = InstallAdditionalCoreAsync(engine);
    }
    private async Task InstallAdditionalCoreAsync(Speech.SpeechEngine engine)
    {
        var catalog = AdditionalModels.Catalog(engine);
        string terms = engine == Speech.SpeechEngine.Kitten
            ? "KittenTTS Mini 0.8: восемь английских голосов, общая модель. Модель — Apache 2.0."
            : engine == Speech.SpeechEngine.Pocket
            ? "Pocket TTS: 21 английский голос. Модель — CC BY 4.0, код — MIT. Cosette и Jean предназначены только для некоммерческого использования; условия остальных голосов и указание авторства — в third-party/pocket."
            : engine == Speech.SpeechEngine.Kokoro
            ? "Kokoro INT8: английские голоса, общая модель. Модель — Apache 2.0."
            : "VOICEVOX:四国めたん / VOICEVOX:玄野武宏 — только обычные варианты. Бесплатное использование по условиям VOICEVOX и персонажей. При распространении аудио указывайте соответствующий кредит. Полные условия находятся в third-party/voicevox внутри приложения.";
        string source = engine is Speech.SpeechEngine.Kitten or Speech.SpeechEngine.Pocket ? "Hugging Face" : "GitHub";
        if (MessageBox.Show(settingsWindow, $"{terms}\n\nЗагрузка с {source}: до {catalog.Files.Sum(f => f.Bytes) / 1_000_000.0:F2} МБ. Файлы загружаются последовательно и проверяются. Текущий выбор голоса сохраняется.",
            "Установка локальных голосов", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        using var cancellation = new CancellationTokenSource(); voiceDownload = cancellation; preparingVoice = true; Changed?.Invoke();
        try
        {
            additionalCacheCancellation?.Cancel();
            if (additionalCacheTask is not null) await additionalCacheTask;
            await worker.ConfigureAdditionalAsync([], cancellation.Token); additionalSignature = null;
            var progress = new Progress<string>(SetStatus);
            await AdditionalModels.PrepareAsync(engine, null, progress, cancellation.Token);
            await RefreshVoicesAsync(); SetStatus("Голоса готовы. Выберите голос и нажмите «Прослушать».");
        }
        catch (OperationCanceledException) { SetStatus("Подготовка отменена; прежние голоса сохранены."); }
        catch (QuietException ex) { SetStatus(Messages.For(ex.Code)); }
        catch { SetStatus("Не удалось подготовить голоса. Прежние модели сохранены; можно повторить загрузку."); }
        finally { voiceDownload = null; preparingVoice = false; UpdateAdditionalCache(); Changed?.Invoke(); }
    }

    internal Task PreviewAsync(LanguageSettings language)
    {
        var snapshot = new LanguageSettings { Language = language.Language, VoiceId = language.VoiceId, Speed = language.Speed };
        return operations.RunOrStopAsync(async operation =>
        {
            if (!Voices.Any(x => x.Id == snapshot.VoiceId)) throw new QuietException(ErrorCode.VoiceMissing);
            operation.Transition(OperationState.Speaking);
            await worker.CallAsync(new("speak", Text: LanguagePlanner.TestPhrase(snapshot.Language), VoiceId: snapshot.VoiceId, Speed: snapshot.Speed), TimeSpan.FromMinutes(3), operation.Token);
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
                uint packageLength = 0;
                if (NativeMethods.GetCurrentPackageFullName(ref packageLength, 0) == 15700)
                    throw new QuietException(ErrorCode.PackageIdentityRequired);
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
                    await ModelPreparation.VerifyAsync(TranslationClient.DefaultModelRoot, operation.Token);
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
        operations.Stop(); modelPreparation?.Cancel(); voiceDownload?.Cancel(); piperCacheCancellation?.Cancel(); sileroCacheCancellation?.Cancel(); additionalCacheCancellation?.Cancel(); worker.Dispose(); tray?.Dispose(); hotkeys?.Dispose(); openSettings?.Dispose();
        if (ownsMutex) mutex.ReleaseMutex(); mutex.Dispose();
    }
}
