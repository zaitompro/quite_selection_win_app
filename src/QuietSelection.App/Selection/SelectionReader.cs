using System.Diagnostics;
using QuietSelection.App.Platform;

namespace QuietSelection.App.Selection;

/// <summary>Selected text plus a content-free trace of how it was searched for.</summary>
internal readonly record struct SelectionResult(string Text, ErrorCode Error, string Diagnostic);

/// <summary>
/// Reads the selection with the native UI Automation client (see SelectionUia.cpp): the focused
/// element and up to six parents, continuing past empty selections, then a bounded look into the
/// focused element's children.
/// </summary>
internal static class SelectionReader
{
    internal const int MaximumParentSteps = 6;
    // Browsers and Electron apps build their accessibility tree on first request, so later
    // attempts wait longer. Total waiting stays near 1 s, inside the caller's 2 s deadline.
    private static readonly int[] RetryDelays = [60, 150, 300, 500];

    internal static SelectionResult Read(nint sourceWindow)
    {
        var trace = new List<string>();
        var clock = Stopwatch.StartNew();
        for (int attempt = 0; ; attempt++)
        {
            var (error, text, diagnostic) = ReadOnce(sourceWindow);
            trace.Add(diagnostic);
            if (error == ErrorCode.None)
            {
                try { return new(TextLimits.CombineSelection([text]), ErrorCode.None, Summary(trace, attempt, clock)); }
                catch (QuietException ex) { return new("", ex.Code, Summary(trace, attempt, clock)); }
            }
            bool retry = error is ErrorCode.SelectionUnavailable or ErrorCode.NoFocusedElement or ErrorCode.ProviderFailure;
            if (!retry || attempt >= RetryDelays.Length) return new("", error, Summary(trace, attempt, clock));
            Thread.Sleep(RetryDelays[attempt]);
        }
    }

    /// <summary>
    /// Text under a physical screen point: the paragraph of a text control or the name of the element
    /// there. Elements of <paramref name="excludedProcess"/> (the app's own windows) are ignored.
    /// </summary>
    internal static SelectionResult ReadAtPoint(int x, int y, int excludedProcess)
    {
        var trace = new List<string>();
        var clock = Stopwatch.StartNew();
        for (int attempt = 0; ; attempt++)
        {
            int status = NativeMethods.qs_read_at_point(x, y, (uint)TextLimits.Maximum, (uint)excludedProcess, out var textPointer, out var diagnosticPointer);
            string text = textPointer == 0 ? "" : NativeMethods.TakeString(textPointer);
            trace.Add(diagnosticPointer == 0 ? "" : NativeMethods.TakeString(diagnosticPointer));
            var error = ToError(status);
            if (error == ErrorCode.None)
            {
                try { return new(TextLimits.CombineSelection([text]), ErrorCode.None, Summary(trace, attempt, clock)); }
                catch (QuietException ex) { return new("", ex.Code, Summary(trace, attempt, clock)); }
            }
            // Only provider hiccups are retried: an element without text stays without text.
            if (error != ErrorCode.ProviderFailure || attempt >= 1) return new("", error, Summary(trace, attempt, clock));
            Thread.Sleep(RetryDelays[1]);
        }
    }

    private static ErrorCode ToError(int status) => status switch
    {
        0 => ErrorCode.None,
        1 => ErrorCode.NoSelection,
        2 => ErrorCode.SelectionUnavailable,
        3 => ErrorCode.NoFocusedElement,
        4 => ErrorCode.AccessDenied,
        5 => ErrorCode.ProtectedField,
        6 => ErrorCode.TooLong,
        7 => ErrorCode.SourceChanged,
        _ => ErrorCode.ProviderFailure
    };

    private static string Summary(List<string> trace, int attempt, Stopwatch clock) =>
        $"attempts={attempt + 1}; ms={clock.ElapsedMilliseconds}; " + string.Join(" | ", trace.Distinct());

    private static (ErrorCode Error, string Text, string Diagnostic) ReadOnce(nint sourceWindow)
    {
        int status = NativeMethods.qs_read_selection(sourceWindow, (uint)TextLimits.Maximum, out var textPointer, out var diagnosticPointer);
        string text = textPointer == 0 ? "" : NativeMethods.TakeString(textPointer);
        string diagnostic = diagnosticPointer == 0 ? "" : NativeMethods.TakeString(diagnosticPointer);
        return (ToError(status), text, diagnostic);
    }
}

/// <summary>
/// Optional local log of selection attempts: time, program name, result and the search trace.
/// Never the selected text. Off by default; kept to the last 300 lines.
/// </summary>
internal static class SelectionDiagnostics
{
    private const int MaximumLines = 300;
    private static readonly object gate = new();
    internal static volatile bool Enabled;
    internal static string LogPath => Path.Combine(AppPaths.DataRoot, "diagnostics", "selection.log");

    internal static void Record(long sourceWindow, ErrorCode error, string diagnostic)
    {
        if (!Enabled) return;
        string program = "?";
        try
        {
            NativeMethods.GetWindowThreadProcessId((nint)sourceWindow, out var processId);
            using var process = Process.GetProcessById((int)processId);
            program = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {program} {(error == ErrorCode.None ? "OK" : error.ToString())} {diagnostic}";
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var lines = File.Exists(LogPath) ? File.ReadAllLines(LogPath).ToList() : [];
                lines.Add(line);
                File.WriteAllLines(LogPath, lines.Skip(Math.Max(0, lines.Count - MaximumLines)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
