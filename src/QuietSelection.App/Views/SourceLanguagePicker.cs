using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace QuietSelection.App.Views;

internal static class SourceLanguagePicker
{
    internal static async Task<string> ChooseAsync(IEnumerable<string> candidates, CancellationToken token)
    {
        var window = new Window { Title = "Исходный язык", Width = 460, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, FontSize = Ui.Body, ResizeMode = ResizeMode.NoResize,
            // Opened from a hotkey while another app is in front: stay visible above it.
            Topmost = true, Icon = AppIcon.Window };
        SystemTheme.ApplyWindow(window);
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = "Выберите язык фрагментов, которые не удалось определить. Выбор действует только для текущего чтения.", TextWrapping = TextWrapping.Wrap });
        var languages = new ComboBox { ItemsSource = candidates.Select(x => new KeyValuePair<string, string>(x, AppSettings.SupportedLanguages[x])).ToArray(),
            DisplayMemberPath = "Value", SelectedValuePath = "Key", Margin = new(0, 16, 0, 16), MinHeight = 36 };
        AutomationProperties.SetName(languages, "Исходный язык неопределённых фрагментов"); panel.Children.Add(languages);
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accept = new Button { Content = "Продолжить", IsEnabled = false, Padding = new(12), Margin = new(0, 0, 0, 8) };
        languages.SelectionChanged += (_, _) => accept.IsEnabled = languages.SelectedValue is string;
        accept.Click += (_, _) => { if (languages.SelectedValue is string code) { done.TrySetResult(code); window.Close(); } };
        panel.Children.Add(accept);
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new(12) };
        cancel.Click += (_, _) => window.Close(); panel.Children.Add(cancel);
        window.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; window.Close(); } };
        window.Closed += (_, _) => done.TrySetCanceled(); window.Content = panel;
        using var registration = token.Register(() => window.Dispatcher.BeginInvoke(() => window.Close()));
        // An unanswered choice must not leave the reading operation waiting forever.
        var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        timeout.Tick += (_, _) => { timeout.Stop(); window.Close(); };
        token.ThrowIfCancellationRequested(); window.Show(); window.Activate(); timeout.Start();
        string result;
        try { result = await done.Task; }
        finally { timeout.Stop(); }
        token.ThrowIfCancellationRequested();
        return result;
    }
}
