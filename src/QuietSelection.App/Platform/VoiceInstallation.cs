using System.Diagnostics;

namespace QuietSelection.App.Platform;

internal static class VoiceInstallation
{
    internal static void Validate(string language)
    {
        if (language is not ("ru-RU" or "en-US" or "ja-JP")) throw new ArgumentException("UNSUPPORTED_VOICE_LANGUAGE");
    }

    internal static async Task<int> InstallAsync(string language, Action<string> progress)
    {
        Validate(language);
        bool restartRequired = false;
        // Execute the signed OS servicing tool directly. No PowerShell script/policy, elevated
        // application, arbitrary executable, user text, alternate source or system language changes.
        foreach (string component in new[] { "Basic", "TextToSpeech" })
        {
            progress(component);
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "dism.exe"))
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (string argument in new[] { "/Online", "/Add-Capability", $"/CapabilityName:Language.{component}~~~{language}~0.0.1.0", "/NoRestart" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("VOICE_INSTALLER_START_FAILED");
            // Servicing may legitimately take a long time; closing the app must not kill DISM.
            await process.WaitForExitAsync();
            if (process.ExitCode == 3010) restartRequired = true;
            else if (process.ExitCode != 0) return process.ExitCode;
        }
        return restartRequired ? 3010 : 0;
    }
}
