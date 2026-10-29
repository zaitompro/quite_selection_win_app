using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using QuietSelection.App.Platform;

namespace QuietSelection.App.Selection;

internal static class SelectionReader
{
    // Focus plus at most six parents; no descendants or desktop-wide queries.
    internal const int MaximumParentSteps = 6;
    internal static string Read(nint sourceWindow)
    {
        // A browser may expose its provider asynchronously. Refresh focus on each
        // bounded retry; the parent still enforces the two-second hard deadline.
        for (int attempt = 0; ; attempt++)
        {
            try { return ReadCurrent(sourceWindow); }
            catch (QuietException ex) when (attempt < 2 && ex.Code is
                ErrorCode.SelectionUnavailable or ErrorCode.NoFocusedElement or ErrorCode.ProviderFailure)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static string ReadCurrent(nint sourceWindow)
    {
        if (NativeMethods.GetForegroundWindow() != sourceWindow) throw new QuietException(ErrorCode.SourceChanged);
        NativeMethods.GetWindowThreadProcessId(sourceWindow, out var sourceProcess);
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null) throw new QuietException(ErrorCode.NoFocusedElement);
            if (focused.Current.IsPassword) throw new QuietException(ErrorCode.ProtectedField);
            var current = focused;
            for (int depth = 0; depth <= MaximumParentSteps && current is not null; depth++)
            {
                if (current.Current.ProcessId != sourceProcess)
                    throw new QuietException(depth == 0 ? ErrorCode.NoFocusedElement : ErrorCode.SelectionUnavailable);
                if (current.Current.IsPassword) throw new QuietException(ErrorCode.ProtectedField);
                if (current.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
                {
                    var ranges = ((TextPattern)pattern).GetSelection();
                    Array.Sort(ranges, (left, right) => left.CompareEndpoints(TextPatternRangeEndpoint.Start, right, TextPatternRangeEndpoint.Start));
                    var fragments = new List<string>();
                    int remaining = TextLimits.Maximum;
                    foreach (var range in ranges)
                    {
                        if (range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) == 0) continue;
                        if (fragments.Count > 0) remaining--;
                        if (remaining < 0) throw new QuietException(ErrorCode.TooLong);
                        var text = range.GetText(remaining + 1);
                        if (text.Length > remaining) throw new QuietException(ErrorCode.TooLong);
                        if (text.Length == 0) continue;
                        fragments.Add(text);
                        remaining -= text.Length;
                    }
                    if (NativeMethods.GetForegroundWindow() != sourceWindow) throw new QuietException(ErrorCode.SourceChanged);
                    return TextLimits.CombineSelection(fragments);
                }
                if (depth < MaximumParentSteps) current = TreeWalker.ControlViewWalker.GetParent(current);
            }
            throw new QuietException(ErrorCode.SelectionUnavailable);
        }
        catch (QuietException) { throw; }
        catch (UnauthorizedAccessException) { throw new QuietException(ErrorCode.AccessDenied); }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070005)) { throw new QuietException(ErrorCode.AccessDenied); }
        catch { throw new QuietException(ErrorCode.ProviderFailure); }
    }
}
