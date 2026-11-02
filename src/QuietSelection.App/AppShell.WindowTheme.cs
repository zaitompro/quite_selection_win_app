using QuietSelection.App.Platform;
using QuietSelection.App.WindowTheme;

namespace QuietSelection.App;

internal sealed partial class AppShell
{
    private readonly WindowThemeClient windowTheme = new();
    private int darkOverlays; // Windows darkened by the shortcut or automatically, as last reported.
    internal int DarkWindowCount => darkOverlays;

    private bool WindowThemeNeeded => Settings.WindowTheme.Properties || Settings.WindowTheme.Rules.Count > 0;

    /// <summary>Sends the automatic rules to the helper; stops it when nothing is dark or planned.</summary>
    private async Task ApplyWindowThemeAsync()
    {
        if (!running || exiting) return;
        try
        {
            if (WindowThemeNeeded || windowTheme.Running)
            {
                var response = await windowTheme.SendAsync(new("theme-configure", Properties: Settings.WindowTheme.Properties, Rules: [.. Settings.WindowTheme.Rules]));
                darkOverlays = response.Overlays;
                if (!WindowThemeNeeded && darkOverlays == 0) await windowTheme.StopAsync();
            }
        }
        catch (QuietException) { SetStatus("Не удалось запустить тёмную тему для окон."); }
        RaiseChanged();
    }

    /// <summary>Darkens the active window or restores it; optionally remembers its type.</summary>
    internal async Task DarkenForegroundWindowAsync()
    {
        nint window = NativeMethods.GetForegroundWindow();
        try
        {
            if (!windowTheme.Running) await ApplyWindowThemeAsync();
            var response = await windowTheme.SendAsync(new("theme-toggle", (long)window));
            darkOverlays = response.Overlays;
            if (!response.Success) { SetStatus("Это окно нельзя затемнить."); return; }
            var rules = Settings.WindowTheme.Rules;
            bool changed = false;
            if (Settings.WindowTheme.Remember && WindowThemeSettings.IsValidRule(response.Rule))
            {
                if (response.Applied && !rules.Contains(response.Rule) && rules.Count < WindowThemeSettings.MaximumRules) { rules.Add(response.Rule); changed = true; }
                else if (!response.Applied && rules.Remove(response.Rule)) changed = true;
            }
            if (changed) Save();
            SetStatus(response.Applied
                ? "Окно затемнено." + (changed ? " Такие окна будут затемняться автоматически." : "")
                : "Окно снова в обычном виде." + (changed ? " Такие окна больше не затемняются." : ""));
        }
        catch (QuietException) { SetStatus("Не удалось затемнить окно."); }
    }

    internal void RemoveWindowRule(string rule)
    {
        if (Settings.WindowTheme.Rules.Remove(rule)) Save();
    }
}
