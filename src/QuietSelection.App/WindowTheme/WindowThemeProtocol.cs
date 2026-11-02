namespace QuietSelection.App.WindowTheme;

/// <summary>Requests from the app to the dark-window helper. No window contents are sent.</summary>
internal sealed record ThemeRequest(string Kind, long Window = 0, bool Properties = false, string[]? Rules = null);

/// <summary>Applied: the window is dark now. Rule: its "process|class" for remembering.</summary>
internal sealed record ThemeResponse(bool Success, bool Applied = false, string Rule = "", int Overlays = 0, string Failure = "");
