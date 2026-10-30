namespace QuietSelection.App.Platform;

/// <summary>Per-user data locations. Model data stays outside the MSIX so updates keep it.</summary>
internal static class AppPaths
{
    internal static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietSelection");
    internal static string Settings => Path.Combine(DataRoot, "settings.json");
    internal static string Models(string name) => Path.Combine(DataRoot, "models", name);
    internal static string Downloads(string name) => Path.Combine(DataRoot, "downloads", name);
}

internal static class AppInfo
{
    /// <summary>Package version when installed (for example 0.1.0.21), otherwise the assembly version.</summary>
    internal static string Version { get; } = NativeMethods.PackageVersion
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}
