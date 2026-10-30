using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using QuietSelection.App.Speech.Downloads;

namespace QuietSelection.App.Views;

/// <summary>
/// Installable voice sets with size, license and state. Installation progress and cancellation
/// appear in the row itself; the window can stay open while a download runs.
/// </summary>
internal sealed class VoiceCatalogWindow : Window
{
    private readonly AppShell shell;
    private readonly List<Action> updaters = [];
    private readonly StackPanel rows = new();
    private string filter = "";

    internal VoiceCatalogWindow(AppShell owner)
    {
        shell = owner;
        SystemTheme.ApplyWindow(this);
        Title = "Каталог голосов";
        Icon = AppIcon.Window;
        FontSize = Ui.Body;
        Width = 820; Height = Math.Min(760, SystemParameters.WorkArea.Height - 40);
        MinWidth = 560; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new DockPanel();
        root.SetResourceReference(Panel.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        var header = new StackPanel { Margin = new(28, 20, 28, 12) };
        header.Children.Add(new TextBlock { Text = "Каталог голосов", FontSize = Ui.Title, FontWeight = FontWeights.SemiBold });
        header.Children.Add(Ui.Secondary("Голоса работают локально. Загрузка начинается только после подтверждения; файлы проверяются по SHA-256.", Ui.Body));
        var languages = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 12, 0, 0) };
        foreach (var (key, label) in new[] { ("", "Все") }.Concat(AppSettings.SupportedLanguages.Select(x => (x.Key, x.Value))))
        {
            var chip = new RadioButton { Content = label, GroupName = "language", IsChecked = key == filter, MinWidth = 0, Margin = new(0, 0, 20, 0) };
            AutomationProperties.SetName(chip, "Показать: " + label);
            chip.Checked += (_, _) => { filter = key; Render(); };
            languages.Children.Add(chip);
        }
        header.Children.Add(languages);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        var footer = new DockPanel { Margin = new(28, 12, 28, 16), LastChildFill = false };
        var close = Ui.Button("Закрыть", Close); close.IsCancel = true; close.Margin = new(0);
        DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
        footer.Children.Add(Ui.Button("Параметры речи Windows", () => Process.Start(new ProcessStartInfo("ms-settings:speech") { UseShellExecute = true })));
        footer.Children.Add(Ui.Button("Обновить список", async () => await shell.RefreshVoicesAsync(), automationName: "Обновить список голосов"));
        var footerBar = new Border { Child = footer, BorderThickness = new(0, 1, 0, 0) };
        footerBar.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        DockPanel.SetDock(footerBar, Dock.Bottom); root.Children.Add(footerBar);

        root.Children.Add(new ScrollViewer { Content = rows, Padding = new(28, 0, 28, 16), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        PreviewKeyDown += (_, e) => { if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W) { e.Handled = true; Close(); } };
        shell.Changed += Update;
        Closed += (_, _) => shell.Changed -= Update;
        Render();
    }

    private void Update() { foreach (var update in updaters) update(); }

    private void Render()
    {
        updaters.Clear();
        rows.Children.Clear();
        var visible = VoiceCatalog.All.Where(p => filter.Length == 0 || p.Languages.Contains(AppSettings.SupportedLanguages[filter], StringComparison.Ordinal)).ToArray();
        string? group = null;
        foreach (var package in visible)
        {
            string section = package.Kind switch { VoicePackageKind.RhVoice => "Системные голоса SAPI 5", VoicePackageKind.Windows => "Голоса Windows", _ => "Нейросетевые голоса" };
            if (section != group) { rows.Children.Add(Ui.Section(section)); group = section; }
            rows.Children.Add(Row(package));
        }
        if (filter == "ja") rows.Children.Add(Ui.Secondary("Японского голоса RHVoice нет; доступны VOICEVOX и голос Windows."));
        Update();
    }

    private Border Row(VoicePackageInfo package)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new(220) });

        var info = new StackPanel { Margin = new(0, 0, 16, 0) };
        var title = new WrapPanel();
        title.Children.Add(new TextBlock { Text = package.Title, FontWeight = FontWeights.SemiBold, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(Ui.Badge(package.LicenseBadge, package.Restricted ? Ui.Tone.Caution : Ui.Tone.Neutral));
        info.Children.Add(title);
        var facts = new List<string> { package.Languages };
        if (package.VoiceCount is int count) facts.Add(TextFormat.Voices(count));
        facts.Add(package.Bytes is long bytes ? TextFormat.Megabytes(bytes) : "размер определит Windows");
        facts.Add(package.Source);
        info.Children.Add(Ui.Secondary(string.Join(" · ", facts), Ui.Body));
        var summary = new TextBlock { Text = package.Summary, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) };
        info.Children.Add(summary);
        var license = Ui.Secondary("Лицензия: " + package.License); license.Margin = new(0, 2, 0, 0);
        info.Children.Add(license);
        grid.Children.Add(info);

        // State column: installed mark, install button, or progress with cancel.
        var state = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var installed = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 0, 0, 6) };
        var check = Ui.Icon("\uE73E", 14); check.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush"); check.Margin = new(0, 0, 6, 0);
        installed.Children.Add(check); installed.Children.Add(new TextBlock { Text = "Установлено" });
        state.Children.Add(installed);
        var install = Ui.Button("Установить", async () => await shell.InstallAsync(package), accent: true, automationName: "Установить: " + package.Title);
        install.HorizontalAlignment = HorizontalAlignment.Right; install.Margin = new(0);
        state.Children.Add(install);
        var progress = new ProgressBar { Height = 4, Margin = new(0, 4, 0, 6) };
        var progressText = Ui.Secondary("");
        progressText.TextAlignment = TextAlignment.Right;
        AutomationProperties.SetLiveSetting(progressText, AutomationLiveSetting.Polite);
        var cancel = Ui.Button("Отмена", shell.CancelVoiceDownload, automationName: "Отменить установку: " + package.Title);
        cancel.HorizontalAlignment = HorizontalAlignment.Right; cancel.Margin = new(0, 6, 0, 0); cancel.MinWidth = 0;
        state.Children.Add(progress); state.Children.Add(progressText); state.Children.Add(cancel);
        Grid.SetColumn(state, 1); grid.Children.Add(state);

        updaters.Add(() =>
        {
            bool active = shell.ActiveVoicePackage == package.Key;
            bool isInstalled = VoiceCatalog.IsInstalled(package, shell.Voices);
            installed.Visibility = isInstalled && !active ? Visibility.Visible : Visibility.Collapsed;
            install.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            install.Content = isInstalled ? (package.Kind is VoicePackageKind.Windows or VoicePackageKind.RhVoice ? "Установить снова" : "Проверить и обновить") : "Установить";
            install.SetResourceReference(StyleProperty, isInstalled ? typeof(Button) : "AccentButtonStyle");
            install.IsEnabled = !shell.PreparingVoice;
            progress.Visibility = progressText.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            progress.IsIndeterminate = shell.VoiceProgressPercent is null;
            progress.Value = shell.VoiceProgressPercent ?? 0;
            progressText.Text = active ? shell.VoiceProgress : "";
            cancel.Visibility = active && shell.CanCancelVoiceDownload ? Visibility.Visible : Visibility.Collapsed;
        });
        var card = Ui.Card(grid, 8);
        AutomationProperties.SetName(card, package.Title);
        return card;
    }
}
