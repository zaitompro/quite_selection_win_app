using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuietSelection.App.Platform;
using QuietSelection.App.Speech;
using QuietSelection.App.Speech.Downloads;

namespace QuietSelection.App.Views;

/// <summary>
/// Settings in the Windows 11 Settings layout. The page is rebuilt only when the page or the
/// installed voice list changes; live state (status, progress, memory) is updated in place.
/// </summary>
internal sealed partial class SettingsWindow : Window
{
    private static readonly (string Glyph, string Title)[] Sections =
        [("\uE80F", "Общие"), ("\uE767", "Речь"), ("\uE774", "Перевод"), ("\uE765", "Сочетания клавиш"), ("\uE7A8", "Чтение с экрана"), ("\uE71E", "Лупа"), ("\uE708", "Тёмные окна")];

    private readonly AppShell shell;
    private readonly ListBox navigation = new() { BorderThickness = new(0), Background = Brushes.Transparent };
    private readonly StackPanel page = new() { MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ScrollViewer scroll;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock statusIcon = Ui.Icon("\uE73E", 16);
    private readonly Button stop;
    // Live parts of the current page, refreshed on every state change.
    private readonly List<Action> updaters = [];
    // Slider changes apply in memory at once; the file is written once the value settles.
    private readonly DispatcherTimer pendingSave = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private string voiceSignature = "";
    private ReadingCommand? recording;

    internal SettingsWindow(AppShell owner)
    {
        shell = owner;
        SystemTheme.ApplyWindow(this);
        Title = "QuietSelection — настройки";
        Icon = AppIcon.Window;
        FontSize = Ui.Body;
        Width = Math.Min(1000, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(760, SystemParameters.WorkArea.Height - 40);
        MinWidth = 640; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        pendingSave.Tick += (_, _) => FlushSave();

        var root = new Grid();
        root.SetResourceReference(Panel.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        root.ColumnDefinitions.Add(new() { Width = new(260) });
        root.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });

        // Navigation: app identity, then sections with icons.
        var side = new DockPanel { Margin = new(12, 16, 8, 12) };
        var identity = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(8, 0, 0, 20) };
        identity.Children.Add(new Image { Source = AppIcon.Render(32), Width = 32, Height = 32, Margin = new(0, 0, 12, 0) });
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = "QuietSelection", FontWeight = FontWeights.SemiBold });
        name.Children.Add(Ui.Secondary("Версия " + AppInfo.Version));
        identity.Children.Add(name);
        DockPanel.SetDock(identity, Dock.Top); side.Children.Add(identity);
        navigation.ItemContainerStyle = Ui.NavigationItemStyle();
        foreach (var (glyph, title) in Sections)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Ui.Icon(glyph, 16); icon.Margin = new(0, 0, 12, 0);
            row.Children.Add(icon); row.Children.Add(new TextBlock { Text = title });
            var item = new ListBoxItem { Content = row };
            AutomationProperties.SetName(item, title);
            navigation.Items.Add(item);
        }
        side.Children.Add(navigation);
        root.Children.Add(side);

        scroll = new ScrollViewer { Content = page, Padding = new(28, 20, 28, 24), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1); root.Children.Add(scroll);

        // Footer: what the app is doing now and the one control that acts on it.
        var footer = new Grid { Margin = new(0) };
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        statusIcon.Margin = new(0, 0, 10, 0); statusIcon.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(statusIcon);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        Grid.SetColumn(status, 1); footer.Children.Add(status);
        stop = Ui.Button("Остановить", shell.Stop, automationName: "Остановить чтение");
        stop.Margin = new(12, 0, 0, 0);
        Grid.SetColumn(stop, 2); footer.Children.Add(stop);
        var footerBar = new Border { Child = footer, BorderThickness = new(0, 1, 0, 0), Padding = new(20, 10, 20, 10) };
        footerBar.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        footerBar.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorSecondaryBrush");
        Grid.SetRow(footerBar, 1); Grid.SetColumnSpan(footerBar, 2); root.Children.Add(footerBar);
        Content = root;

        navigation.SelectionChanged += (_, _) => RenderPage(keepPosition: false);
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => StopRecording();
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
        Closed += (_, _) => { StopRecording(); FlushSave(); shell.Changed -= Update; };
        voiceSignature = VoiceSignature();
        navigation.SelectedIndex = 0;
        Update();
    }

    internal void OpenSection(int index) { navigation.SelectedIndex = index; RenderPage(keepPosition: false); }

    private string VoiceSignature() => string.Join("|", shell.Voices.Select(x => x.Id));

    private void Update()
    {
        status.Text = shell.Status;
        statusIcon.Text = shell.Busy ? "\uE767" : shell.Status == "Готово." ? "\uE73E" : "\uE946";
        statusIcon.SetResourceReference(TextBlock.ForegroundProperty, shell.Busy ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        stop.IsEnabled = shell.Busy;
        foreach (var update in updaters) update();
        string signature = VoiceSignature();
        if (signature != voiceSignature) { voiceSignature = signature; RenderPage(keepPosition: true); }
    }

    // Rebuilding keeps the scroll position and the focused control, so screen readers do not lose their place.
    private void RenderPage(bool keepPosition)
    {
        StopRecording();
        double offset = keepPosition ? scroll.VerticalOffset : 0;
        string? focused = keepPosition && Keyboard.FocusedElement is DependencyObject element && IsDescendant(page, element)
            ? AutomationProperties.GetName(element) : null;
        updaters.Clear();
        page.Children.Clear();
        switch (navigation.SelectedIndex)
        {
            case 0: General(); break;
            case 1: Speech(); break;
            case 2: Translation(); break;
            case 3: Shortcuts(); break;
            case 4: Ocr(); break;
            case 5: MagnifierPage(); break;
            case 6: DarkWindowsPage(); break;
        }
        foreach (var update in updaters) update();
        scroll.ScrollToVerticalOffset(offset);
        if (!string.IsNullOrEmpty(focused))
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                FindByName(page, focused)?.Focus());
    }

    private static bool IsDescendant(DependencyObject root, DependencyObject element)
    {
        for (var current = element; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current == root) return true;
        return false;
    }

    private static UIElement? FindByName(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Focusable: true, IsVisible: true } candidate && AutomationProperties.GetName(child) == name) return candidate;
            if (FindByName(child, name) is { } found) return found;
        }
        return null;
    }

    private void Add(UIElement element) => page.Children.Add(element);

    private void ScheduleSave() { pendingSave.Stop(); pendingSave.Start(); }
    private void FlushSave()
    {
        if (!pendingSave.IsEnabled) return;
        pendingSave.Stop(); shell.Save();
    }

    // ---- General -------------------------------------------------------------------------

    private void General()
    {
        Add(Ui.PageTitle("Общие"));
        if (!shell.SettingsWritable)
            Add(Ui.InfoBar(Ui.Tone.Critical, "Файл настроек повреждён",
                "Изменения сейчас не сохраняются. Сброс сохранит прежний файл рядом и начнёт с настроек по умолчанию.",
                Ui.Button("Сбросить настройки", ConfirmReset)));

        var hero = new Grid();
        hero.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        hero.Children.Add(new Image { Source = AppIcon.Render(56), Width = 56, Height = 56, Margin = new(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top });
        var about = new StackPanel();
        about.Children.Add(new TextBlock { Text = "QuietSelection", FontSize = Ui.Subtitle, FontWeight = FontWeights.SemiBold });
        about.Children.Add(Ui.Secondary("Версия " + AppInfo.Version + " · локальное чтение, распознавание и перевод", Ui.Body));
        var privacy = Ui.Secondary("Всё выполняется на этом компьютере. Буфер обмена не используется, история прочитанного не сохраняется.");
        privacy.Margin = new(0, 8, 0, 0); about.Children.Add(privacy);
        Grid.SetColumn(about, 1); hero.Children.Add(about);
        Add(Ui.Card(hero, 16));

        // Shortcuts live only in their own section; here just a prompt when none is set.
        if (!shell.Settings.Hotkeys.Values.Any(x => x.Length > 0))
            Add(Ui.InfoBar(Ui.Tone.Neutral, "Сочетания клавиш не назначены",
                "Назначьте сочетание, чтобы читать выделенный текст в любом приложении.",
                Ui.Button("Назначить…", () => OpenSection(3), accent: true)));

        Add(Ui.Section("Поведение"));
        Add(Ui.SettingRow("\uE823", "Запускать при входе в Windows", StartupDescription(), StartupToggle()));
        Add(Ui.SettingRow("\uE7B3", "Индикатор чтения", "Небольшая плашка с кнопкой «Стоп» в углу экрана во время чтения и перевода.",
            Ui.Toggle(shell.Settings.ShowReadingIndicator, value => { shell.Settings.ShowReadingIndicator = value; shell.Save(); }, "Индикатор чтения")));
        Add(Ui.SettingRow("\uE9D9", "Журнал захвата выделения",
            "Записывает на этом компьютере, в каких программах и на каком шаге не удалось получить выделение. Сам текст не записывается. Файл: " + Selection.SelectionDiagnostics.LogPath,
            Ui.Toggle(shell.Settings.SelectionDiagnostics, value => { shell.Settings.SelectionDiagnostics = value; shell.Save(); }, "Журнал захвата выделения")));
        Add(Ui.SettingRow("\uE7E8", "Завершить работу", "Закрытие окна оставляет QuietSelection в области уведомлений.",
            Ui.Button("Завершить", shell.Exit, automationName: "Завершить QuietSelection")));
    }

    private void ConfirmReset()
    {
        if (MessageBox.Show(this, "Сбросить настройки? Повреждённый файл будет сохранён рядом с новым.", "Сброс настроек",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { shell.ResetSettings(); RenderPage(keepPosition: false); }
    }

    private string StartupDescription() => shell.Startup switch
    {
        StartupState.NotPackaged => "Доступно в установленной версии QuietSelection.",
        StartupState.DisabledByUser => "Отключено в диспетчере задач или параметрах Windows. Включите там.",
        StartupState.DisabledByPolicy => "Запрещено политикой организации.",
        _ => "Приложение запускается свёрнутым в область уведомлений."
    };

    private CheckBox StartupToggle()
    {
        var toggle = Ui.Toggle(shell.Startup == StartupState.Enabled, value => _ = SetStartupAsync(value), "Запускать при входе в Windows");
        toggle.IsEnabled = shell.Startup is StartupState.Enabled or StartupState.Disabled;
        return toggle;
    }

    private async Task SetStartupAsync(bool value)
    {
        await shell.SetStartAtLoginAsync(value);
        if (navigation.SelectedIndex == 0) RenderPage(keepPosition: true);
    }

    // ---- Speech --------------------------------------------------------------------------

    private void Speech()
    {
        Add(Ui.PageTitle("Речь"));
        if (shell.Voices.Length == 0)
            Add(Ui.InfoBar(Ui.Tone.Caution, "Голоса не найдены", "Установите локальные голоса или голоса Windows.",
                Ui.Button("Каталог голосов…", OpenCatalog, accent: true)));
        Add(Ui.SettingRow("\uE8C1", "Определять язык автоматически",
            "Русские, английские и японские вставки читаются голосами своих языков. Остальное — основным голосом.",
            Ui.Toggle(shell.Settings.AutoDetect, value => { shell.Settings.AutoDetect = value; shell.Save(); }, "Определять язык автоматически")));

        Add(Ui.Section("Основной язык"));
        Add(LanguageCard(shell.Settings.Primary, primary: true));
        Add(Ui.Section("Дополнительные языки"));
        foreach (var language in shell.Settings.Additional.ToArray()) Add(LanguageCard(language, primary: false));
        var available = AppSettings.SupportedLanguages.Where(x => x.Key != shell.Settings.Primary.Language && shell.Settings.Additional.All(y => y.Language != x.Key)).ToArray();
        if (available.Length > 0)
        {
            var add = new ComboBox { ItemsSource = available, DisplayMemberPath = "Value", SelectedIndex = 0, MinWidth = 160, Margin = new(0, 0, 8, 0) };
            AutomationProperties.SetName(add, "Язык для добавления");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(add);
            row.Children.Add(Ui.Button("Добавить", () =>
            {
                if (add.SelectedItem is not KeyValuePair<string, string> pair) return;
                shell.Settings.Additional.Add(new() { Language = pair.Key }); shell.Save(); RenderPage(keepPosition: true);
            }, automationName: "Добавить язык"));
            Add(Ui.SettingRow("\uE710", "Добавить язык", null, row));
        }

        Add(Ui.Section("Голоса"));
        var catalogButtons = new StackPanel { Orientation = Orientation.Horizontal };
        catalogButtons.Children.Add(Ui.Button("Обновить", async () => await shell.RefreshVoicesAsync(), automationName: "Обновить список голосов"));
        catalogButtons.Children.Add(Ui.Button("Каталог голосов…", OpenCatalog, accent: true));
        Add(Ui.SettingRow("\uE896", "Установка голосов", "Silero, Piper, Kokoro, Kitten, Pocket, VOICEVOX, RHVoice и голоса Windows.", catalogButtons));

        // Technical options stay collapsed: defaults suit most people.
        var memory = new StackPanel();
        memory.Children.Add(Ui.Secondary("Выбранные модели остаются в памяти, и чтение начинается быстрее. Выключите, чтобы освободить память: модель будет загружаться при каждом чтении.", Ui.Body));
        var sets = shell.ResidentVoiceSets.ToArray();
        AddMemoryOption(memory, sets[0], () => shell.Settings.KeepSileroLoaded, v => shell.Settings.KeepSileroLoaded = v);
        AddMemoryOption(memory, sets[1], () => shell.Settings.KeepPiperLoaded, v => shell.Settings.KeepPiperLoaded = v);
        AddMemoryOption(memory, sets[2], () => shell.Settings.KeepAdditionalSpeechLoaded, v => shell.Settings.KeepAdditionalSpeechLoaded = v);
        var expander = new Expander { Header = "Память и скорость отклика", Content = memory, Margin = new(0, 16, 0, 0) };
        AutomationProperties.SetName(expander, "Память и скорость отклика");
        Add(expander);
    }

    private void AddMemoryOption(Panel parent, ResidentModels set, Func<bool> get, Action<bool> set_)
    {
        var box = new CheckBox { Content = "Держать в памяти: " + set.Label, IsChecked = get(), Margin = new(0, 12, 0, 0) };
        box.Checked += (_, _) => { set_(true); shell.Save(); };
        box.Unchecked += (_, _) => { set_(false); shell.Save(); };
        var state = Ui.Secondary(set.Status); state.Margin = new(28, 0, 0, 0);
        AutomationProperties.SetLiveSetting(state, AutomationLiveSetting.Polite);
        updaters.Add(() => state.Text = set.Status);
        parent.Children.Add(box); parent.Children.Add(state);
    }

    private sealed record VoiceChoice(string Id, string Label, string Group, int Order)
    {
        public override string ToString() => Label;
    }

    private static readonly string[] GroupOrder = ["Silero", "Kokoro", "Pocket", "Kitten", "Piper", "VOICEVOX", "SAPI 5", "Windows"];

    [GeneratedRegex(@"\s*\[[^\]]*\]\s*$")]
    private static partial Regex BracketSuffix();

    private Border LanguageCard(LanguageSettings settings, bool primary)
    {
        string languageName = AppSettings.SupportedLanguages[settings.Language];
        var body = new StackPanel();

        // Header: language, and for additional languages the on/off switch and removal.
        var header = new DockPanel { Margin = new(0, 0, 0, 12) };
        if (!primary)
        {
            var remove = Ui.Button("Удалить", () => { shell.Settings.Additional.Remove(settings); shell.Save(); RenderPage(keepPosition: true); },
                automationName: "Удалить язык: " + languageName);
            remove.MinWidth = 0; remove.Margin = new(8, 0, 0, 0);
            DockPanel.SetDock(remove, Dock.Right); header.Children.Add(remove);
            var enabled = Ui.Toggle(settings.Enabled, value => { settings.Enabled = value; shell.Save(); }, "Использовать язык: " + languageName);
            enabled.Margin = new(8, 0, 0, 0);
            DockPanel.SetDock(enabled, Dock.Right); header.Children.Add(enabled);
        }
        var language = new ComboBox { ItemsSource = AppSettings.SupportedLanguages.ToArray(), DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedValue = settings.Language, MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(language, primary ? "Основной язык" : "Дополнительный язык: " + languageName);
        language.SelectionChanged += (_, _) =>
        {
            var selected = (string)language.SelectedValue;
            if (selected == settings.Language) return;
            if (new[] { shell.Settings.Primary }.Concat(shell.Settings.Additional).Any(x => !ReferenceEquals(x, settings) && x.Language == selected))
            { shell.SetStatus("Этот язык уже добавлен."); language.SelectedValue = settings.Language; return; }
            settings.Language = selected; settings.VoiceId = ""; shell.Save(); RenderPage(keepPosition: true);
        };
        header.Children.Add(language);
        body.Children.Add(header);

        // Voice picker grouped by engine, with search for long lists.
        var matching = shell.Voices.Where(x => x.Language.Split('-')[0] == settings.Language).ToArray();
        bool regions = matching.Select(x => x.Language).Distinct().Count() > 1;
        var choices = matching.Select(x =>
        {
            string group = SpeechEngines.Group(x);
            string label = BracketSuffix().Replace(x.Name, "");
            if (group != "Windows" && group != "SAPI 5" && label.StartsWith(group + " ", StringComparison.Ordinal)) label = label[(group.Length + 1)..];
            if (regions) label += " · " + x.Language;
            int order = Array.IndexOf(GroupOrder, group);
            return new VoiceChoice(x.Id, label, group, order < 0 ? GroupOrder.Length : order);
        }).OrderBy(x => x.Order).ThenBy(x => x.Label, StringComparer.CurrentCulture).ToList();
        var view = new ListCollectionView(choices);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(VoiceChoice.Group)));
        // Not synchronized with the view: an unset voice must stay visibly unset.
        var voice = new ComboBox { ItemsSource = view, SelectedValuePath = "Id", MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch, IsSynchronizedWithCurrentItem = false, SelectedIndex = -1 };
        voice.GroupStyle.Add(new GroupStyle { HeaderTemplate = Ui.GroupHeader() });
        if (choices.Any(x => x.Id == settings.VoiceId)) voice.SelectedValue = settings.VoiceId;
        AutomationProperties.SetName(voice, "Голос: " + languageName);
        var voiceLabel = Ui.Secondary("Голос"); voiceLabel.Margin = new(0, 0, 0, 4);
        body.Children.Add(voiceLabel);
        if (choices.Count > 12)
        {
            var search = new TextBox { Margin = new(0, 0, 0, 6) };
            AutomationProperties.SetName(search, "Поиск голоса: " + languageName);
            var hint = Ui.Secondary("Поиск голоса", Ui.Body); hint.IsHitTestVisible = false; hint.Margin = new(12, 6, 0, 0);
            search.TextChanged += (_, _) =>
            {
                hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                string query = search.Text.Trim();
                view.Filter = query.Length == 0 ? null : o => o is VoiceChoice c && (c.Id == settings.VoiceId
                    || c.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase) || c.Group.Contains(query, StringComparison.CurrentCultureIgnoreCase));
            };
            var searchBox = new Grid(); searchBox.Children.Add(search); searchBox.Children.Add(hint);
            body.Children.Add(searchBox);
        }
        body.Children.Add(voice);
        bool savedMissing = settings.VoiceId.Length > 0 && choices.All(x => x.Id != settings.VoiceId);
        var notice = Ui.Secondary(choices.Count == 0 ? "Для этого языка нет установленных голосов. Откройте каталог голосов."
            : savedMissing ? "Сохранённый голос недоступен. Выберите другой или установите его снова."
            : settings.VoiceId.Length == 0 ? "Выберите голос." : "");
        notice.Margin = new(0, 4, 0, 0);
        if (choices.Count == 0 || savedMissing) notice.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        if (notice.Text.Length > 0) body.Children.Add(notice);

        // Speed and preview on one row.
        var speedRow = new Grid { Margin = new(0, 12, 0, 0) };
        speedRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        speedRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        speedRow.ColumnDefinitions.Add(new() { Width = new(112) });
        speedRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var speedLabel = new TextBlock { Text = "Скорость", Margin = new(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        speedRow.Children.Add(speedLabel);
        var speed = new Slider { Minimum = 0, Maximum = 100, Value = settings.Speed, TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 10, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(speed, "Скорость: " + languageName);
        Grid.SetColumn(speed, 1); speedRow.Children.Add(speed);
        var speedValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 0, 0) };
        string SpeedText() => settings.Speed + (settings.VoiceId.StartsWith("silero:", StringComparison.Ordinal)
            ? " · " + (settings.Speed switch { < 25 => "очень медленно", < 50 => "медленно", 50 => "обычно", <= 75 => "быстро", _ => "очень быстро" }) : "");
        speedValue.Text = SpeedText();
        Grid.SetColumn(speedValue, 2); speedRow.Children.Add(speedValue);
        var preview = Ui.Button("Прослушать", async () => await shell.PreviewAsync(settings), automationName: "Прослушать: " + languageName);
        preview.Margin = new(8, 0, 0, 0); preview.IsEnabled = settings.VoiceId.Length > 0 && !savedMissing;
        Grid.SetColumn(preview, 3); speedRow.Children.Add(preview);
        body.Children.Add(speedRow);

        voice.SelectionChanged += (_, _) =>
        {
            settings.VoiceId = (string?)voice.SelectedValue ?? "";
            speedValue.Text = SpeedText(); preview.IsEnabled = settings.VoiceId.Length > 0;
            shell.Save();
        };
        speed.ValueChanged += (_, _) => { settings.Speed = (int)speed.Value; speedValue.Text = SpeedText(); ScheduleSave(); };

        var card = Ui.Card(body, 8);
        if (!primary && !settings.Enabled) body.Opacity = 0.75;
        return card;
    }

    private void OpenCatalog() => CreateVoiceCatalog().ShowDialog();
    internal Window CreateVoiceCatalog() => new VoiceCatalogWindow(shell) { Owner = this };

    // ---- Translation ---------------------------------------------------------------------

    private void Translation()
    {
        string target = AppSettings.SupportedLanguages[shell.Settings.Primary.Language];
        Add(Ui.PageTitle("Перевод"));
        Add(Ui.SettingRow("\uE774", "Перевод на " + target.ToLowerInvariant(), "Текст переводится на основной язык и читается основным голосом. Язык меняется в разделе «Речь».", null));

        Add(Ui.Section("Направления"));
        var routes = new StackPanel();
        var sources = shell.Settings.Additional.Where(x => x.Enabled).ToArray();
        if (sources.Length == 0) routes.Children.Add(Ui.Secondary("Включите дополнительный язык в разделе «Речь».", Ui.Body));
        foreach (var language in sources)
        {
            bool supported = TranslationPlanner.IsSupported(language.Language, shell.Settings.Primary.Language);
            var line = new DockPanel { Margin = new(0, 4, 0, 4) };
            var badge = Ui.Badge(supported ? "Прямая модель" : "Не поддерживается", supported ? Ui.Tone.Neutral : Ui.Tone.Caution);
            DockPanel.SetDock(badge, Dock.Right); line.Children.Add(badge);
            line.Children.Add(new TextBlock { Text = AppSettings.SupportedLanguages[language.Language] + " → " + target, VerticalAlignment = VerticalAlignment.Center });
            routes.Children.Add(line);
        }
        Add(Ui.Card(routes));

        Add(Ui.Section("Модели"));
        var state = new StackPanel();
        var line1 = new DockPanel();
        var icon = Ui.Icon("\uE73E", 20); icon.Margin = new(0, 0, 12, 0);
        line1.Children.Add(icon);
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        line1.Children.Add(text);
        state.Children.Add(line1);
        var progress = new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new(32, 10, 0, 0) };
        state.Children.Add(progress);
        var buttons = new WrapPanel { Margin = new(32, 12, 0, 0) };
        var download = Ui.Button("Загрузить или восстановить…", async () => await shell.DownloadModelsAsync(), accent: true);
        var check = Ui.Button("Проверить", async () => await shell.CheckModelsAsync(), automationName: "Проверить модели перевода");
        var cancel = Ui.Button("Отменить", shell.CancelModelPreparation, automationName: "Отменить подготовку моделей");
        buttons.Children.Add(download); buttons.Children.Add(check); buttons.Children.Add(cancel);
        state.Children.Add(buttons);
        Add(Ui.Card(state));
        updaters.Add(() =>
        {
            text.Text = shell.ModelStatus;
            bool busy = shell.PreparingModels;
            progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            download.IsEnabled = check.IsEnabled = !busy;
            icon.Text = busy ? "\uE895" : shell.ModelsReady ? "\uE73E" : "\uE7BA";
            icon.SetResourceReference(TextBlock.ForegroundProperty, busy ? "AccentTextFillColorPrimaryBrush" : shell.ModelsReady ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
        });
        var details = Ui.Secondary("Перевод работает локально, сеть после установки не нужна. Полный набор — до 601,5 МБ; прямой перевод с японского — 288,6 МБ. Закрытие окна оставляет подготовку в фоне, выход из приложения отменяет её.");
        details.Margin = new(4, 8, 0, 0);
        Add(details);
    }

    // ---- Shortcuts -----------------------------------------------------------------------

    private static string CommandTitle(ReadingCommand command) => command switch
    {
        ReadingCommand.Selection => "Прочитать выделенное",
        ReadingCommand.PrimaryVoice => "Прочитать основным голосом",
        ReadingCommand.Region => "Распознать и прочитать область",
        ReadingCommand.Translate => "Перевести и прочитать",
        ReadingCommand.ToggleMagnifier => "Лупа: включить или выключить",
        ReadingCommand.InvertColors => "Лупа: негатив",
        ReadingCommand.FollowCaret => "Лупа: следовать за кареткой",
        _ => "Тёмная тема для окна"
    };

    private static string CommandDescription(ReadingCommand command) => command switch
    {
        ReadingCommand.Selection => "Читает выделенный текст; вставки на других языках — их голосами.",
        ReadingCommand.PrimaryVoice => "Читает всё выделенное одним основным голосом.",
        ReadingCommand.Region => "Обведите область экрана — текст будет распознан и прочитан.",
        ReadingCommand.Translate => "Переводит выделенное на основной язык и читает перевод.",
        ReadingCommand.ToggleMagnifier => "Возвращает обычный масштаб или последнее увеличение.",
        ReadingCommand.InvertColors => "Инвертирует цвета всего экрана.",
        ReadingCommand.FollowCaret => "Включает или выключает слежение за текстовым курсором при наборе.",
        _ => "Затемняет активное окно или возвращает ему обычный вид."
    };

    private static string CommandGlyph(ReadingCommand command) => command switch
    {
        ReadingCommand.Selection => "\uE8D2", ReadingCommand.PrimaryVoice => "\uE767", ReadingCommand.Region => "\uE7A8", ReadingCommand.Translate => "\uE774",
        ReadingCommand.ToggleMagnifier => "\uE71E", ReadingCommand.InvertColors => "\uE790", ReadingCommand.FollowCaret => "\uE8AC", _ => "\uE708"
    };

    private readonly Dictionary<ReadingCommand, Button> shortcutButtons = [];

    private void Shortcuts()
    {
        Add(Ui.PageTitle("Сочетания клавиш"));
        Add(Ui.Secondary("Все клавиши и кнопки мыши QuietSelection собраны здесь. Нажмите кнопку с сочетанием и введите новое: Ctrl или Alt вместе с обычной клавишей.", Ui.Body));
        shortcutButtons.Clear();

        Add(Ui.Section("Чтение"));
        foreach (var command in new[] { ReadingCommand.Selection, ReadingCommand.PrimaryVoice, ReadingCommand.Region, ReadingCommand.Translate })
            Add(ShortcutRow(command));
        var repeat = Ui.Secondary("Во время чтения повтор любой команды чтения останавливает его.");
        repeat.Margin = new(4, 4, 0, 0); Add(repeat);

        var options = shell.Settings.Magnifier;
        Add(Ui.Section("Лупа"));
        var modifier = new ComboBox { ItemsSource = MagnifierSettings.Modifiers.Select(x => x + " + колесо").ToArray(), SelectedIndex = Math.Max(0, MagnifierSettings.Modifiers.ToList().IndexOf(options.Modifier)), MinWidth = 200 };
        modifier.SelectionChanged += (_, _) => { options.Modifier = MagnifierSettings.Modifiers[modifier.SelectedIndex]; shell.Save(); RenderPage(keepPosition: true); };
        Add(Ui.SettingRow("\uE962", "Масштаб колесом", "Пока клавиши зажаты, колесо меняет масштаб лупы, а не прокручивает окно.", modifier));
        string[] middleLabels = ["Не использовать", options.Modifier + " + щелчок колесом", "Щелчок колесом без клавиш"];
        var middle = new ComboBox { ItemsSource = middleLabels, MinWidth = 200,
            SelectedIndex = Math.Max(0, MagnifierSettings.MiddleClickModes.ToList().IndexOf(options.MiddleClick)) };
        middle.SelectionChanged += (_, _) => { options.MiddleClick = MagnifierSettings.MiddleClickModes[middle.SelectedIndex]; shell.Save(); RenderPage(keepPosition: true); };
        Add(Ui.SettingRow("\uE962", "Включение лупы щелчком колеса",
            options.MiddleClick == "Alone"
                ? "Щелчок колесом включает и выключает увеличение. В программах он больше не работает (например, ссылка в новой вкладке или закрытие вкладки); с Ctrl, Alt или Shift проходит как обычно."
                : "Щелчок средней кнопкой включает увеличение на последнем уровне и выключает его.", middle));
        foreach (var command in new[] { ReadingCommand.ToggleMagnifier, ReadingCommand.InvertColors, ReadingCommand.FollowCaret })
            Add(ShortcutRow(command));

        Add(Ui.Section("Тёмные окна"));
        Add(ShortcutRow(ReadingCommand.DarkenWindow));

        var note = Ui.Secondary("Windows проверяет, что сочетание свободно, но оно может совпадать с командой внутри другого приложения — тогда сработает QuietSelection.");
        note.Margin = new(4, 8, 0, 0);
        Add(note);
    }

    private Border ShortcutRow(ReadingCommand command)
    {
        string chord = shell.Settings.Hotkeys.GetValueOrDefault(command, "");
        var record = Ui.Button(chord.Length == 0 ? "Не назначено" : chord, () => StartRecording(command), automationName: CommandTitle(command) + ": изменить сочетание");
        record.MinWidth = 140;
        shortcutButtons[command] = record;
        var clear = Ui.Button("Удалить", () => { shell.ChangeHotkey(command, ""); RenderPage(keepPosition: true); }, automationName: CommandTitle(command) + ": удалить сочетание");
        clear.MinWidth = 0; clear.Margin = new(0); clear.IsEnabled = chord.Length > 0;
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(record); controls.Children.Add(clear);
        return Ui.SettingRow(CommandGlyph(command), CommandTitle(command), CommandDescription(command), controls);
    }

    private void StartRecording(ReadingCommand command)
    {
        StopRecording();
        recording = command;
        shell.SuspendHotkeys(); // Pressing an assigned combination must not start reading now.
        var button = shortcutButtons[command];
        button.Content = "Нажмите сочетание…";
        button.Focus();
        shell.SetStatus("Нажмите новое сочетание для «" + CommandTitle(command) + "». Esc — отмена.");
    }

    private void StopRecording()
    {
        if (recording is not { } command) return;
        recording = null;
        if (shortcutButtons.TryGetValue(command, out var button))
        {
            string chord = shell.Settings.Hotkeys.GetValueOrDefault(command, "");
            button.Content = chord.Length == 0 ? "Не назначено" : chord;
        }
        shell.ResumeHotkeys();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (recording is { } command)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { StopRecording(); shell.SetStatus("Готово."); return; }
            if (Chord.FromKeyPress(key, Keyboard.Modifiers) is { } chord)
            {
                recording = null;
                shell.ChangeHotkey(command, chord.ToString()); // Checked while suspended.
                shell.ResumeHotkeys();
                RenderPage(keepPosition: true);
            }
            else if (key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift))
                shell.SetStatus("Нужен Ctrl или Alt вместе с обычной клавишей. Esc — отмена.");
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.W or Key.Q) { e.Handled = true; Close(); }
    }

    // ---- Screen reading ------------------------------------------------------------------

    private void Ocr()
    {
        Add(Ui.PageTitle("Чтение с экрана"));
        if (NativeMethods.IsPackaged)
            Add(Ui.InfoBar(Ui.Tone.Success, "Системное распознавание доступно", null));
        else
            Add(Ui.InfoBar(Ui.Tone.Caution, "Нужна установленная версия", "Распознавание текста Windows работает только в установленном пакете QuietSelection."));
        var languages = new ComboBox { ItemsSource = AppSettings.SupportedLanguages.ToArray(), DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedValue = shell.Settings.OcrLanguage, MinWidth = 180 };
        languages.SelectionChanged += (_, _) => { shell.Settings.OcrLanguage = (string)languages.SelectedValue; shell.Save(); };
        Add(Ui.SettingRow("\uE8C1", "Язык распознавания", "Язык текста в выбранной области.", languages));
        string chord = shell.Settings.Hotkeys.GetValueOrDefault(ReadingCommand.Region, "");
        Add(Ui.SettingRow("\uE7A8", "Как пользоваться",
            (chord.Length > 0 ? "Нажмите " + chord : "Назначьте сочетание «Распознать и прочитать область»") + " и обведите область на мониторе с указателем. Esc или правая кнопка мыши — отмена.",
            chord.Length > 0 ? null : Ui.Button("Назначить…", () => OpenSection(3))));
        var privacy = Ui.Secondary("Захватывается только обведённая область. Изображение хранится в памяти на время распознавания.");
        privacy.Margin = new(4, 8, 0, 0);
        Add(privacy);
    }

    // ---- Magnifier ------------------------------------------------------------------------

    private static readonly (double Step, string Label)[] ZoomSteps = [(1.1, "Плавный (×1,1)"), (1.25, "Обычный (×1,25)"), (1.5, "Крупный (×1,5)")];

    private void MagnifierPage()
    {
        var options = shell.Settings.Magnifier;
        Add(Ui.PageTitle("Лупа"));
        Add(Ui.SettingRow("\uE71E", "Лупа", "Увеличение всего экрана. Указатель остаётся в центре; клик попадает туда, где нарисован указатель.",
            Ui.Toggle(options.Enabled, value => { options.Enabled = value; shell.Save(); RenderPage(keepPosition: true); }, "Лупа")));
        if (!options.Enabled)
        {
            var hint = Ui.Secondary("Включите лупу, чтобы увеличивать экран колесом мыши с зажатыми клавишами.");
            hint.Margin = new(4, 8, 0, 0); Add(hint);
            return;
        }

        // Current zoom with a quick on/off, updated live while the wheel turns.
        var zoomText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0), MinWidth = 60 };
        var toggle = Ui.Button("", shell.ToggleMagnifier, automationName: "Включить или выключить увеличение");
        var zoomControls = new StackPanel { Orientation = Orientation.Horizontal };
        zoomControls.Children.Add(zoomText); zoomControls.Children.Add(toggle);
        Add(Ui.SettingRow("\uE8A3", "Масштаб", "От 100 до 6400 %. Последний уровень запоминается и восстанавливается при включении.", zoomControls));
        updaters.Add(() =>
        {
            double scale = shell.MagnifierScale;
            zoomText.Text = Math.Round(scale * 100) + " %";
            toggle.Content = scale > 1 ? "Обычный масштаб" : "Увеличить до " + Math.Round(options.LastScale * 100) + " %";
        });

        var step = new ComboBox { ItemsSource = ZoomSteps.Select(x => x.Label).ToArray(), MinWidth = 180,
            SelectedIndex = Math.Max(0, Array.FindIndex(ZoomSteps, x => Math.Abs(x.Step - options.Step) < 0.01)) };
        step.SelectionChanged += (_, _) => { options.Step = ZoomSteps[step.SelectedIndex].Step; shell.Save(); };
        Add(Ui.SettingRow("\uE9E9", "Шаг масштаба", "Насколько меняется увеличение за один щелчок колеса.", step));

        var exact = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, TextAlignment = TextAlignment.Right };
        updaters.Add(() => exact.Text = shell.MagnifierInputTransform switch
        {
            true => "Включено",
            false => "Недоступно: нужен доверенный сертификат",
            null => shell.MagnifierRunning ? "Проверится при увеличении" : "—"
        });
        Add(Ui.SettingRow("\uE8B0", "Точное попадание в заголовки", "Щелчки по заголовкам и кнопкам окон под увеличением. Требует права UIAccess для процесса лупы (см. docs/installation.md).", exact));

        Add(Ui.Section("Цвета и слежение"));
        Add(Ui.SettingRow("\uE790", "Негатив", "Инвертирует цвета всего экрана. Работает и без увеличения.",
            Ui.Toggle(options.Invert, shell.SetMagnifierInvert, "Негатив")));
        var style = new ComboBox { ItemsSource = new[] { "Классический", "С сохранением оттенков" }, MinWidth = 220,
            SelectedIndex = options.InvertStyle == "Smart" ? 1 : 0 };
        style.SelectionChanged += (_, _) => { options.InvertStyle = style.SelectedIndex == 1 ? "Smart" : "Classic"; shell.Save(); };
        Add(Ui.SettingRow("\uE790", "Вид негатива", "«С сохранением оттенков» делает светлое тёмным, но красное остаётся красным, синее — синим.", style));
        Add(Ui.SettingRow("\uE8AC", "Следовать за кареткой", "Во время набора текста увеличенная область сдвигается за текстовым курсором. Движение мыши возвращает слежение за указателем.",
            Ui.Toggle(options.FollowCaret, shell.SetMagnifierFollowCaret, "Следовать за кареткой")));
        Add(Ui.SettingRow("\uE7B3", "Скрывать указатель при наборе", "Указатель не закрывает каретку; появляется снова при движении мыши.",
            Ui.Toggle(options.HideCursorWhileTyping, value => { options.HideCursorWhileTyping = value; shell.Save(); }, "Скрывать указатель при наборе")));

        var keys = Ui.Secondary("Клавиши масштаба, щелчок колесом и быстрые команды лупы настраиваются в разделе «Сочетания клавиш».");
        keys.Margin = new(4, 12, 0, 0);
        Add(keys);
        var note = Ui.Secondary("Не включайте одновременно экранную лупу Windows. В окнах, запущенных от имени администратора, колесо и каретка не отслеживаются.");
        note.Margin = new(4, 8, 0, 0);
        Add(note);
    }

    // ---- Dark windows ---------------------------------------------------------------------

    private void DarkWindowsPage()
    {
        var options = shell.Settings.WindowTheme;
        Add(Ui.PageTitle("Тёмные окна"));
        Add(Ui.InfoBar(Ui.Tone.Neutral, "Для окон без тёмного режима",
            "Светлое окно затемняется наложением: яркость инвертируется, оттенки сохраняются. Щелчки и ввод проходят в окно как обычно."));
        Add(Ui.SettingRow("\uE8B7", "Свойства файлов, папок и дисков", "Классические окна свойств (и другие окна с вкладками в старом стиле) затемняются автоматически, если они светлые.",
            Ui.Toggle(options.Properties, value => { options.Properties = value; shell.Save(); }, "Свойства файлов, папок и дисков")));
        Add(Ui.SettingRow("\uE718", "Запоминать затемнённые окна", "Окно, затемнённое сочетанием клавиш, запоминается по программе и типу окна; такие окна затемняются сами. Повторное нажатие на затемнённом окне забывает его.",
            Ui.Toggle(options.Remember, value => { options.Remember = value; shell.Save(); }, "Запоминать затемнённые окна")));
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        updaters.Add(() => count.Text = shell.DarkWindowCount.ToString());
        Add(Ui.SettingRow("\uE708", "Сейчас затемнено окон", null, count));

        Add(Ui.Section("Запомненные окна"));
        if (options.Rules.Count == 0)
            Add(Ui.Secondary("Пока нет. Сделайте окно активным и нажмите сочетание «Тёмная тема для окна» (назначается в разделе «Сочетания клавиш»).", Ui.Body));
        foreach (var rule in options.Rules.ToArray())
        {
            var parts = rule.Split('|');
            var remove = Ui.Button("Удалить", () => { shell.RemoveWindowRule(rule); RenderPage(keepPosition: true); }, automationName: "Забыть окна " + parts[0]);
            remove.MinWidth = 0;
            Add(Ui.SettingRow("\uE737", parts[0], "Тип окна: " + parts[1], remove));
        }
        var note = Ui.Secondary("Меню и подсказки поверх окна остаются светлыми. При перемещении и прокрутке возможна задержка в кадр. Окна администратора тоже затемняются, но запоминание для них может не сработать.");
        note.Margin = new(4, 12, 0, 0);
        Add(note);
    }
}
