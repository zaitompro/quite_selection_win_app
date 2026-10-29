using System.Windows;

namespace QuietSelection.App.Views;

internal static class SystemTheme
{
    // Shipped in .NET 10 WPF; the programmatic API retains the WPF0001 annotation.
    // Scope suppression to this adapter instead of disabling diagnostics project-wide.
#pragma warning disable WPF0001
    internal static void FollowWindows(Application application)
    {
        // Eager loading matters for a code-created UI: implicit styles must be available
        // before controls are instantiated, including windows deriving from Window.
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/PresentationFramework.Fluent;component/Themes/Fluent.xaml", UriKind.Relative)
        });
        application.ThemeMode = ThemeMode.System;
    }
    internal static void Preview(Window window, bool dark) => window.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
    internal static void Reset(Window window) => window.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001

    internal static void ApplyWindow(Window window)
    {
        window.SetResourceReference(FrameworkElement.StyleProperty, typeof(Window));
        window.SetResourceReference(Window.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextFillColorPrimaryBrush");
    }
}
