using System.Runtime.InteropServices;

namespace QuietSelection.App.Platform;

internal enum StartupState { NotPackaged, Disabled, Enabled, DisabledByUser, DisabledByPolicy }

/// <summary>
/// Windows.ApplicationModel.StartupTask "QuietSelectionStartup" from the package manifest,
/// called through the native bridge. Windows asks the user once when it is first enabled.
/// </summary>
internal static class StartupTaskClient
{
    internal static Task<StartupState> GetStateAsync() => Task.Run(() => Call(enable: -1));
    internal static Task<StartupState> SetAsync(bool enabled) => Task.Run(() => Call(enabled ? 1 : 0));

    // WinRT StartupTaskState: Disabled 0, DisabledByUser 1, Enabled 2, DisabledByPolicy 3, EnabledByPolicy 4.
    private static StartupState Call(int enable)
    {
        if (!NativeMethods.IsPackaged) return StartupState.NotPackaged;
        try
        {
            if (qs_startup_task(enable, out int state) < 0) return StartupState.NotPackaged;
            return state switch
            {
                1 => StartupState.DisabledByUser,
                2 or 4 => StartupState.Enabled,
                3 => StartupState.DisabledByPolicy,
                _ => StartupState.Disabled
            };
        }
        catch (EntryPointNotFoundException) { return StartupState.NotPackaged; } // Older native bridge.
    }

    /// <summary>True when Windows started the app at sign-in; it then opens in the notification area.</summary>
    internal static bool LaunchedAtSignIn()
    {
        if (!NativeMethods.IsPackaged) return false;
        try { return qs_activated_by_startup_task(out int value) >= 0 && value != 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    [DllImport("QuietSelection.Native.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int qs_activated_by_startup_task(out int value);

    /// <param name="enable">-1 reads the state, 0 disables, 1 requests enabling.</param>
    [DllImport("QuietSelection.Native.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int qs_startup_task(int enable, out int state);
}
