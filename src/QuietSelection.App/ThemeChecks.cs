using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuietSelection.App.Views;

namespace QuietSelection.App;

internal static class ThemeChecks
{
    internal static async Task<bool> RunAsync(Application app, string reportDirectory, string? russianVoice = null)
    {
        using var shell = new AppShell(app);
        await shell.RefreshVoicesAsync();
        if (russianVoice is not null)
        {
            // In-memory preview only; never call Save or change the user's language/English binding.
            var russian = new[] { shell.Settings.Primary }.Concat(shell.Settings.Additional).FirstOrDefault(x => x.Language == "ru");
            if (russian is not null) russian.VoiceId = russianVoice;
        }
        var settings = new SettingsWindow(shell) { ShowActivated = false };
        settings.Show();
        var backgrounds = new Color[2];
        var foregrounds = new Color[2];
        try
        {
            // Switch the same live window both ways. System settings are never modified.
            for (int i = 0; i < 2; i++)
            {
                bool dark = i == 1;
                SystemTheme.Preview(settings, dark);
                await settings.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                backgrounds[i] = (settings.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
                foregrounds[i] = (settings.Foreground as SolidColorBrush)?.Color ?? Colors.Transparent;
                for (int section = 0; section < 5; section++)
                {
                    settings.OpenSection(section);
                    await settings.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    settings.UpdateLayout();
                    var visual = (FrameworkElement)settings.Content;
                    // Render only our own visual tree. Normalize its margin/offset so the footer
                    // is not cropped; no native screen capture or background windows are involved.
                    var drawing = new DrawingVisual();
                    var bounds = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
                    using (var context = drawing.RenderOpen())
                    {
                        context.DrawRectangle(settings.Background, null, bounds);
                        context.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.Fill }, null, bounds);
                    }
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(drawing);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(reportDirectory, $"theme-{(dark ? "dark" : "light")}-{section}.png"));
                    encoder.Save(stream);
                }
                var help = settings.CreateVoiceHelp(); help.ShowActivated = false;
                try
                {
                    SystemTheme.Preview(help, dark); help.Show();
                    await help.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    help.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(help.ActualWidth), (int)Math.Ceiling(help.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(help);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(reportDirectory, $"theme-{(dark ? "dark" : "light")}-voice-installation.png"));
                    encoder.Save(output);
                }
                finally { help.Close(); }
            }
            double lightContrast = Contrast(foregrounds[0], backgrounds[0]);
            double darkContrast = Contrast(foregrounds[1], backgrounds[1]);
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "theme-colors.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                light = new { background = backgrounds[0].ToString(), foreground = foregrounds[0].ToString(), contrast = lightContrast },
                dark = new { background = backgrounds[1].ToString(), foreground = foregrounds[1].ToString(), contrast = darkContrast },
                sectionsRendered = 5, desktopCaptured = false
            }));
            // Fluent deliberately uses partially transparent text brushes. Validate the resulting
            // contrast, rather than rejecting its documented alpha values as an error.
            return backgrounds[0] != backgrounds[1] && foregrounds[0] != foregrounds[1]
                && backgrounds.All(x => x.A == 255) && lightContrast >= 4.5 && darkContrast >= 4.5;
        }
        finally { SystemTheme.Reset(settings); settings.Close(); }
    }

    private static double Contrast(Color foreground, Color background)
    {
        double alpha = foreground.A / 255d;
        static double Linear(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        static double Luminance(double r, double g, double b) => .2126 * Linear(r) + .7152 * Linear(g) + .0722 * Linear(b);
        double text = Luminance((foreground.R * alpha + background.R * (1 - alpha)) / 255,
            (foreground.G * alpha + background.G * (1 - alpha)) / 255, (foreground.B * alpha + background.B * (1 - alpha)) / 255);
        double surface = Luminance(background.R / 255d, background.G / 255d, background.B / 255d);
        return (Math.Max(text, surface) + .05) / (Math.Min(text, surface) + .05);
    }
}
