using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuietSelection.App.Speech.Downloads;

namespace QuietSelection.App.Views;

internal sealed class SettingsWindow : Window
{
    private readonly AppShell shell;
    private readonly ListBox navigation = new() { MinWidth = 210, BorderThickness = new(0), Margin = new(0, 0, 18, 0) };
    private readonly StackPanel page = new() { Margin = new(4, 0, 8, 8) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 14, 0), FontSize = 16 };
    private int voiceCount = -1;
    private bool refreshing;
    private TextBlock? modelStatus;
    private Button? downloadModels;
    private Button? checkModels;
    private Button? cancelModels;
    private Button? cancelVoice;
    private TextBlock? piperStatus;
    private TextBlock? sileroStatus;
    private TextBlock? additionalStatus;
    // Slider changes apply in memory at once; the file is written once the value settles.
    private readonly System.Windows.Threading.DispatcherTimer pendingSave = new() { Interval = TimeSpan.FromMilliseconds(400) };

    internal SettingsWindow(AppShell owner)
    {
        shell = owner;
        pendingSave.Tick += (_, _) => FlushSave();
        SystemTheme.ApplyWindow(this);
        Title = "QuietSelection — Настройки";
        Width = Math.Min(960, SystemParameters.WorkArea.Width - 30);
        Height = Math.Min(720, SystemParameters.WorkArea.Height - 30);
        MinWidth = 480; MinHeight = 360; FontSize = 18;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new Grid { Margin = new(20) };
        root.SetResourceReference(Panel.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new() { Width = new(225) }); root.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        foreach (var section in new[] { "Общие", "Речь", "Перевод", "Сочетания клавиш", "Чтение с экрана" })
        {
            var item = new ListBoxItem { Content = section, Padding = new(10, 14, 8, 14), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(item, section); navigation.Items.Add(item);
        }
        root.Children.Add(navigation);
        var scroll = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1); root.Children.Add(scroll);
        var footer = new Grid { Margin = new(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite); footer.Children.Add(status);
        var stop = Button("Остановить", shell.Stop); Grid.SetColumn(stop, 1); footer.Children.Add(stop);
        var exit = Button("Завершить", shell.Exit); Grid.SetColumn(exit, 2); footer.Children.Add(exit);
        Grid.SetRow(footer, 1); Grid.SetColumnSpan(footer, 2); root.Children.Add(footer); Content = root;
        navigation.SelectionChanged += (_, _) => RenderPage();
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.W or Key.Q) { e.Handled = true; Close(); }
        };
        // No application control may read/write the system data transfer buffer.
        CommandManager.AddPreviewCanExecuteHandler(this, (_, e) =>
        {
            if (e.Command == ApplicationCommands.Copy || e.Command == ApplicationCommands.Cut || e.Command == ApplicationCommands.Paste)
            { e.CanExecute = false; e.Handled = true; }
        });
        CommandManager.AddPreviewExecutedHandler(this, (_, e) =>
        {
            if (e.Command == ApplicationCommands.Copy || e.Command == ApplicationCommands.Cut || e.Command == ApplicationCommands.Paste) e.Handled = true;
        });
        shell.Changed += Update;
        Closed += (_, _) => { FlushSave(); shell.Changed -= Update; };
        OpenGeneral(); Update();
    }

    private void ScheduleSave() { pendingSave.Stop(); pendingSave.Start(); }
    private void FlushSave()
    {
        if (!pendingSave.IsEnabled) return;
        pendingSave.Stop(); shell.Save();
    }
    internal void OpenGeneral() { navigation.SelectedIndex = 0; RenderPage(); }
    internal void OpenSection(int index) { navigation.SelectedIndex = index; RenderPage(); }
    private void Update()
    {
        status.Text = shell.Status;
        if (modelStatus is not null) modelStatus.Text = shell.ModelStatus;
        if (piperStatus is not null) piperStatus.Text = shell.PiperStatus;
        if (sileroStatus is not null) sileroStatus.Text = shell.SileroStatus;
        if (additionalStatus is not null) additionalStatus.Text = shell.AdditionalStatus;
        if (downloadModels is not null) downloadModels.IsEnabled = !shell.PreparingModels;
        if (checkModels is not null) checkModels.IsEnabled = !shell.PreparingModels;
        if (cancelModels is not null) cancelModels.IsEnabled = shell.PreparingModels;
        if (cancelVoice is not null) cancelVoice.IsEnabled = shell.CanCancelVoiceDownload;
        if (voiceCount != shell.Voices.Length) { voiceCount = shell.Voices.Length; RenderPage(); }
    }
    private void RenderPage()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            page.Children.Clear();
            modelStatus = null; downloadModels = null; checkModels = null; cancelModels = null;
            cancelVoice = null;
            piperStatus = null; sileroStatus = null; additionalStatus = null;
            switch (navigation.SelectedIndex)
            {
                case 0: General(); break;
                case 1: Speech(); break;
                case 2: Translation(); break;
                case 3: Shortcuts(); break;
                case 4: Ocr(); break;
            }
        }
        finally { refreshing = false; }
    }
    private void General()
    {
        Heading("QuietSelection"); Text("Версия 0.1.0 — разработка и проверка");
        SelectableText("Читайте выделенный текст, распознавайте область экрана и слушайте перевод. Обработка выполняется на вашем компьютере.");
        Text("Закрытие этого окна оставляет приложение в трее. Полностью выйти можно кнопкой «Завершить».");
        var startup = new CheckBox { Content = "Запускать при входе в Windows", IsChecked = shell.Settings.StartAtLogin, IsEnabled = false, Margin = new(0, 15, 0, 10) };
        AutomationProperties.SetName(startup, "Запускать при входе в Windows — доступно после установки"); page.Children.Add(startup);
        Text("Автозапуск станет доступен после подготовки стабильного пути установки.");
        Text("Буфер обмена не используется. История прочитанного не сохраняется. Загрузка компонентов выполняется только отдельным действием.");
    }
    private void Speech()
    {
        Heading("Основной язык");
        LanguageRow(shell.Settings.Primary, true);
        var keepPiper = new CheckBox { Content = "Держать выбранные голоса Piper в памяти", IsChecked = shell.Settings.KeepPiperLoaded, Margin = new(0, 12, 0, 8) };
        keepPiper.Checked += (_, _) => { shell.Settings.KeepPiperLoaded = true; shell.Save(); };
        keepPiper.Unchecked += (_, _) => { shell.Settings.KeepPiperLoaded = false; shell.Save(); };
        page.Children.Add(keepPiper);
        piperStatus = new TextBlock { Text = shell.PiperStatus, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
        AutomationProperties.SetLiveSetting(piperStatus, AutomationLiveSetting.Polite); page.Children.Add(piperStatus);
        var keepSilero = new CheckBox { Content = "Держать выбранные голоса Silero в памяти", IsChecked = shell.Settings.KeepSileroLoaded, Margin = new(0, 0, 0, 8) };
        keepSilero.Checked += (_, _) => { shell.Settings.KeepSileroLoaded = true; shell.Save(); };
        keepSilero.Unchecked += (_, _) => { shell.Settings.KeepSileroLoaded = false; shell.Save(); };
        page.Children.Add(keepSilero);
        sileroStatus = new TextBlock { Text = shell.SileroStatus, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
        AutomationProperties.SetLiveSetting(sileroStatus, AutomationLiveSetting.Polite); page.Children.Add(sileroStatus);
        var keepAdditional = new CheckBox { Content = "Держать выбранные голоса Kitten, Pocket, Kokoro и VOICEVOX в памяти", IsChecked = shell.Settings.KeepAdditionalSpeechLoaded, Margin = new(0, 0, 0, 8) };
        keepAdditional.Checked += (_, _) => { shell.Settings.KeepAdditionalSpeechLoaded = true; shell.Save(); };
        keepAdditional.Unchecked += (_, _) => { shell.Settings.KeepAdditionalSpeechLoaded = false; shell.Save(); };
        page.Children.Add(keepAdditional);
        additionalStatus = new TextBlock { Text = shell.AdditionalStatus, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
        AutomationProperties.SetLiveSetting(additionalStatus, AutomationLiveSetting.Polite); page.Children.Add(additionalStatus);
        var automatic = new CheckBox { Content = "Определять язык автоматически", IsChecked = shell.Settings.AutoDetect, Margin = new(0, 16, 0, 12) };
        automatic.Checked += (_, _) => { shell.Settings.AutoDetect = true; shell.Save(); };
        automatic.Unchecked += (_, _) => { shell.Settings.AutoDetect = false; shell.Save(); }; page.Children.Add(automatic);
        Heading("Дополнительные языки", 21);
        var rows = new StackPanel();
        page.Children.Add(new ScrollViewer { Content = rows, Height = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        foreach (var language in shell.Settings.Additional.ToArray()) LanguageRow(language, false, rows);
        var available = AppSettings.SupportedLanguages.Where(x => x.Key != shell.Settings.Primary.Language && shell.Settings.Additional.All(y => y.Language != x.Key)).ToArray();
        if (available.Length > 0)
        {
            var add = new ComboBox { ItemsSource = available, DisplayMemberPath = "Value", SelectedIndex = 0, Margin = new(0, 10, 0, 6), MinHeight = 34 };
            AutomationProperties.SetName(add, "Язык для добавления"); page.Children.Add(add);
            page.Children.Add(Button("Добавить язык", () => { if (add.SelectedItem is KeyValuePair<string, string> pair) { shell.Settings.Additional.Add(new() { Language = pair.Key }); shell.Save(); RenderPage(); } }));
        }
        page.Children.Add(Button("Установка голосов…", VoiceHelp));
        cancelVoice = Button("Отменить загрузку голоса", shell.CancelVoiceDownload);
        cancelVoice.IsEnabled = shell.CanCancelVoiceDownload; page.Children.Add(cancelVoice);
        page.Children.Add(Button("Обновить список голосов", async () => { await shell.RefreshVoicesAsync(); RenderPage(); }));
        Text("Доступны голоса Windows, SAPI 5 (RHVoice) и локальные модели Piper, Silero, Kitten, Pocket, Kokoro и VOICEVOX. При автоопределении русские, английские и японские вставки внутри фразы читаются голосами соответствующих включённых языков. Отдельные неоднозначные фрагменты читаются основным голосом. Следующий фрагмент готовится во время чтения; с медленной моделью возможны паузы.");
    }
    private void LanguageRow(LanguageSettings settings, bool primary, Panel? parent = null)
    {
        parent ??= page;
        var panel = new StackPanel { Margin = new(0, 0, 0, 14) };
        var top = new DockPanel();
        if (!primary)
        {
            var enabled = new CheckBox { IsChecked = settings.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new(14, 0, 6, 0) };
            AutomationProperties.SetName(enabled, "Включить дополнительный язык: " + AppSettings.SupportedLanguages[settings.Language]);
            enabled.Checked += (_, _) => { settings.Enabled = true; shell.Save(); }; enabled.Unchecked += (_, _) => { settings.Enabled = false; shell.Save(); };
            DockPanel.SetDock(enabled, Dock.Right); top.Children.Add(enabled);
            var remove = Button("Удалить", () => { shell.Settings.Additional.Remove(settings); shell.Save(); RenderPage(); });
            AutomationProperties.SetName(remove, "Удалить язык: " + AppSettings.SupportedLanguages[settings.Language]); DockPanel.SetDock(remove, Dock.Right); top.Children.Add(remove);
        }
        var language = new ComboBox { ItemsSource = AppSettings.SupportedLanguages.ToArray(), DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedValue = settings.Language, MinHeight = 34 };
        AutomationProperties.SetName(language, primary ? "Основной язык" : "Дополнительный язык");
        language.SelectionChanged += (_, _) =>
        {
            var selected = (string)language.SelectedValue;
            if (selected == settings.Language) return;
            if (shell.Settings.ActiveLanguages.Any(x => !ReferenceEquals(x, settings) && x.Language == selected) ||
                shell.Settings.Additional.Any(x => !ReferenceEquals(x, settings) && x.Language == selected))
            { shell.SetStatus("Этот язык уже добавлен. Сначала удалите его из дополнительных."); language.SelectedValue = settings.Language; return; }
            settings.Language = selected; settings.VoiceId = ""; shell.Save(); RenderPage();
        };
        top.Children.Add(language); panel.Children.Add(top);
        var matching = shell.Voices.Where(x => x.Language.Split('-')[0] == settings.Language).Select(x => new VoiceChoice(x.Id, x.Name + " · " + x.Language)).ToList();
        bool savedVoiceMissing = !string.IsNullOrEmpty(settings.VoiceId) && matching.All(x => x.Id != settings.VoiceId);
        matching.Insert(0, new("", savedVoiceMissing ? "Сохранённый голос недоступен" : "Выберите установленный голос"));
        var voice = new ComboBox { ItemsSource = matching, DisplayMemberPath = "Label", SelectedValuePath = "Id", SelectedValue = matching.Any(x => x.Id == settings.VoiceId) ? settings.VoiceId : "", Margin = new(0, 8, 0, 8), MinHeight = 34 };
        AutomationProperties.SetName(voice, "Голос: " + AppSettings.SupportedLanguages[settings.Language]);
        panel.Children.Add(voice);
        if (matching.Count == 1) panel.Children.Add(new TextBlock { Text = "Локальные голоса для этого языка не найдены.", TextWrapping = TextWrapping.Wrap });
        string SpeedLabel() => "Скорость: " + settings.Speed + (settings.VoiceId.StartsWith("silero:", StringComparison.Ordinal)
            ? " — " + (settings.Speed switch { < 25 => "очень медленно", < 50 => "медленно", 50 => "обычная", <= 75 => "быстро", _ => "очень быстро" })
            : "");
        var speedLabel = new TextBlock { Text = SpeedLabel(), TextWrapping = TextWrapping.Wrap };
        voice.SelectionChanged += (_, _) => { settings.VoiceId = (string?)voice.SelectedValue ?? ""; speedLabel.Text = SpeedLabel(); shell.Save(); };
        panel.Children.Add(speedLabel);
        var speed = new Slider { Minimum = 0, Maximum = 100, Value = settings.Speed, TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 10, Margin = new(0, 5, 0, 8) };
        AutomationProperties.SetName(speed, "Скорость: " + AppSettings.SupportedLanguages[settings.Language]);
        speed.ValueChanged += (_, _) => { settings.Speed = (int)speed.Value; speedLabel.Text = SpeedLabel(); ScheduleSave(); }; panel.Children.Add(speed);
        panel.Children.Add(Button("Прослушать", async () => await shell.PreviewAsync(settings)));
        parent.Children.Add(panel);
    }
    private void Translation()
    {
        Heading("Перевод на " + AppSettings.SupportedLanguages[shell.Settings.Primary.Language].ToLowerInvariant());
        Heading("Перевод с", 21);
        var rows = new StackPanel();
        foreach (var language in shell.Settings.Additional.Where(x => x.Enabled))
            rows.Children.Add(new TextBlock { Text = AppSettings.SupportedLanguages[language.Language] + " → " + AppSettings.SupportedLanguages[shell.Settings.Primary.Language] +
                (!TranslationPlanner.IsSupported(language.Language, shell.Settings.Primary.Language) ? "\nНе поддерживается этим набором." :
                "\nПрямая модель; статус набора ниже."), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 18) });
        page.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        modelStatus = new TextBlock { Text = shell.ModelStatus, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
        AutomationProperties.SetLiveSetting(modelStatus, AutomationLiveSetting.Polite); page.Children.Add(modelStatus);
        downloadModels = Button("Загрузить / восстановить модели…", async () => await shell.DownloadModelsAsync());
        checkModels = Button("Проверить готовность", async () => await shell.CheckModelsAsync());
        cancelModels = Button("Отменить подготовку", shell.CancelModelPreparation);
        downloadModels.IsEnabled = checkModels.IsEnabled = !shell.PreparingModels; cancelModels.IsEnabled = shell.PreparingModels;
        page.Children.Add(downloadModels); page.Children.Add(checkModels); page.Children.Add(cancelModels);
        Text("Перевод работает локально. Недостающие модели загружаются только кнопкой выше: до 601,5 МБ для полного набора, 288,6 МБ для добавления прямого японского перевода. Закрытие настроек оставляет подготовку в фоне; выход из приложения отменяет её. После установки сеть не нужна.");
    }
    private void Shortcuts()
    {
        Heading("Сочетания клавиш");
        Text("Назначение выполняется явно. Занятое сочетание не заменяет прежнее. Во время операции повтор любой команды останавливает чтение.");
        foreach (var command in Enum.GetValues<ReadingCommand>())
        {
            string title = command switch { ReadingCommand.Selection => "Прочитать выделенное", ReadingCommand.PrimaryVoice => "Прочитать основным голосом", ReadingCommand.Region => "Распознать и прочитать область", _ => "Перевести и прочитать" };
            Text(title);
            var choices = HotkeyChoices();
            var combo = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Label", SelectedValuePath = "Id", SelectedValue = shell.Settings.Hotkeys.GetValueOrDefault(command, ""), MinHeight = 36, Margin = new(0, 0, 0, 10) };
            AutomationProperties.SetName(combo, title);
            combo.SelectionChanged += (_, _) => { var selected = (string?)combo.SelectedValue ?? ""; if (!shell.ChangeHotkey(command, selected)) combo.SelectedValue = shell.Settings.Hotkeys.GetValueOrDefault(command, ""); };
            page.Children.Add(combo);
        }
        Text("Регистрация Windows проверяет занятость, но сочетание также может использоваться как обычная команда в другом приложении.");
    }
    private static VoiceChoice[] HotkeyChoices() => Platform.HotkeyManager.Choices.Select(x => new VoiceChoice(x, x.Length == 0 ? "Не назначено" : x)).ToArray();
    private void Ocr()
    {
        Heading("Чтение с экрана");
        Text("Выберите язык распознавания. Область ограничена монитором, на котором находится указатель мыши. Esc и правая кнопка отменяют выбор.");
        var languages = new ComboBox { ItemsSource = AppSettings.SupportedLanguages.ToArray(), DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedValue = shell.Settings.OcrLanguage, MinHeight = 36 };
        AutomationProperties.SetName(languages, "Язык распознавания");
        languages.SelectionChanged += (_, _) => { shell.Settings.OcrLanguage = (string)languages.SelectedValue; shell.Save(); }; page.Children.Add(languages);
        Text("Захватывается только обведённая область. Изображение хранится в памяти на время распознавания.");
        uint packageLength = 0;
        Text(Platform.NativeMethods.GetCurrentPackageFullName(ref packageLength, 0) == 15700
            ? "Для системного OCR запустите установленный пакет QuietSelection."
            : "Установленный пакет готов к использованию системного OCR.");
    }
    private void VoiceHelp()
    {
        CreateVoiceHelp().ShowDialog();
    }
    internal Window CreateVoiceHelp()
    {
        var help = new Window { Owner = this, Title = "Установка локальных голосов", Width = 640, Height = Math.Min(680, SystemParameters.WorkArea.Height - 30), FontSize = 19, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        SystemTheme.ApplyWindow(help);
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = "Piper — локальные нейросетевые голоса для процессора. Русские: Denis, Dmitri, Irina (женский), Ruslan (мужской). Английский: LJSpeech.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Button($"Голоса Piper — до {PiperModels.DownloadBytes / 1_000_000.0:F2} МБ…", async () => { help.Close(); await shell.InstallPiperAsync(); RenderPage(); }));
        panel.Children.Add(new TextBlock { Text = "Silero — пять русских нейросетевых голосов: Айдар, Байя, Ксения, Xenia, Евгений. Одна общая модель; бесплатное некоммерческое использование (CC BY-NC-SA 4.0).", TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) });
        panel.Children.Add(Button($"Русские голоса Silero — {SileroModels.Catalog.File.Bytes / 1_000_000.0:F2} МБ…", async () => { help.Close(); await shell.InstallSileroAsync(); RenderPage(); }));
        panel.Children.Add(new TextBlock { Text = "KittenTTS Mini 0.8 — восемь английских голосов: четыре женских и четыре мужских. Общая модель; Apache 2.0.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) });
        panel.Children.Add(Button("Английские голоса Kitten Mini — 81,55 МБ…", async () => { help.Close(); await shell.InstallAdditionalAsync(QuietSelection.App.Speech.SpeechEngine.Kitten); RenderPage(); }));
        panel.Children.Add(new TextBlock { Text = "Pocket TTS — 21 английский голос, потоковое чтение. Модель — CC BY 4.0, код — MIT. Cosette и Jean: только некоммерческое использование. Условия и авторство голосов сохранены в third-party/pocket.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) });
        panel.Children.Add(Button("Английские голоса Pocket TTS — 350,90 МБ…", async () => { help.Close(); await shell.InstallAdditionalAsync(QuietSelection.App.Speech.SpeechEngine.Pocket); RenderPage(); }));
        panel.Children.Add(new TextBlock { Text = "Kokoro INT8 — английские голоса США и Великобритании; все используют одну модель. Лицензия модели — Apache 2.0.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) });
        panel.Children.Add(Button("Английские голоса Kokoro — 120,58 МБ…", async () => { help.Close(); await shell.InstallAdditionalAsync(QuietSelection.App.Speech.SpeechEngine.Kokoro); RenderPage(); }));
        panel.Children.Add(new TextBlock { Text = "VOICEVOX:四国めたん (женский) и VOICEVOX:玄野武宏 (мужской), обычные варианты. Для распространяемого аудио требуется кредит выбранного голоса. Условия VOICEVOX и персонажей: third-party/voicevox внутри приложения.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) });
        panel.Children.Add(Button("Японские голоса VOICEVOX — 116,43 МБ…", async () => { help.Close(); await shell.InstallAdditionalAsync(QuietSelection.App.Speech.SpeechEngine.Voicevox); RenderPage(); }));
        // Silero English was rejected after listening tests and is no longer offered (see README).
        panel.Children.Add(new TextBlock { Text = "Бесплатные голоса RHVoice — локальное чтение через SAPI 5. Движок включён в загрузку.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) });
        foreach (var package in RhVoiceInstallation.Packages)
            panel.Children.Add(Button($"RHVoice {package.Name} — {AppSettings.SupportedLanguages[package.Language]}, {package.Bytes / 1_000_000.0:F2} МБ…",
                async () => { help.Close(); await shell.InstallRhVoiceAsync(package); RenderPage(); }));
        panel.Children.Add(new TextBlock { Text = "Японского голоса в каталоге RHVoice нет. Для японского доступен установленный голос Windows. После установки выберите голос и прослушайте тестовую фразу.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 18) });
        panel.Children.Add(new TextBlock { Text = "Помощник загрузит голос через Windows Update. Права администратора требуются только для установки. Само приложение остаётся без повышения прав.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Button("Установить русский голос…", async () => { help.Close(); await shell.InstallVoiceAsync("ru"); RenderPage(); }));
        panel.Children.Add(Button("Установить английский голос…", async () => { help.Close(); await shell.InstallVoiceAsync("en"); RenderPage(); }));
        panel.Children.Add(Button("Установить японский голос…", async () => { help.Close(); await shell.InstallVoiceAsync("ja"); RenderPage(); }));
        panel.Children.Add(new TextBlock { Text = "1. Откройте раздел «Речь» Windows.\n\n2. В блоке управления голосами выберите добавление голосов и нужный язык: русский или английский.\n\n3. Дождитесь завершения загрузки и вернитесь сюда.\n\nГолос считается доступным только после появления в списке QuietSelection и успешного прослушивания. Голоса Narrator могут быть недоступны другим приложениям.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Button("Открыть установку голосов Windows", () => Process.Start(new ProcessStartInfo("ms-settings:speech") { UseShellExecute = true })));
        panel.Children.Add(Button("Проверить доступные голоса", async () => { await shell.RefreshVoicesAsync(); RenderPage(); help.Close(); }));
        panel.Children.Add(Button("Закрыть", help.Close)); help.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; return help;
    }
    private void Heading(string text, double size = 27) => page.Children.Add(new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap });
    private void Text(string text) => page.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 14) });
    private void SelectableText(string text)
    {
        var field = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new(0), Background = Brushes.Transparent, ContextMenu = null, AllowDrop = false, Margin = new(0, 0, 0, 14) };
        AutomationProperties.SetName(field, "Описание приложения, доступное для выделения"); page.Children.Add(field);
    }
    private static Button Button(string label, Action click)
    {
        var button = new Button { Content = label, Padding = new(12, 7, 12, 7), Margin = new(0, 5, 8, 5), HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 38 };
        button.Click += (_, _) => click(); return button;
    }
    private sealed record VoiceChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }
}
