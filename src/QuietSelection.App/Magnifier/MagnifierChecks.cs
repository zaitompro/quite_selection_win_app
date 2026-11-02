using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using static QuietSelection.App.Magnifier.MagnifierNative;

namespace QuietSelection.App.Magnifier;

/// <summary>
/// Magnifier checks. The live part zooms the screen 2× for about two seconds and restores it;
/// colors are not inverted. Caret detection uses an own synthetic text box only.
/// </summary>
internal static class MagnifierChecks
{
    internal static void Run(Application app, string[] args)
    {
        string directory = Path.GetFullPath(args[Array.IndexOf(args, "--report-directory") + 1]);
        Directory.CreateDirectory(directory);
        var window = new Window { Title = "QuietSelection — проверка лупы", Width = 640, Height = 240, FontSize = 20, WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true };
        var field = new TextBox { Text = "Синтетический текст для проверки каретки.", Margin = new(24) };
        window.Content = field;
        window.Loaded += async (_, _) =>
        {
            var checks = new List<object>(); bool failed = false;
            void Add(string id, bool passed, object? data = null) { checks.Add(new { id, passed, data }); failed |= !passed; }
            window.Activate(); field.Focus(); field.CaretIndex = 10;
            await Task.Delay(300);

            // Caret: inside the text box on screen, in physical pixels.
            var handle = new WindowInteropHelper(window).Handle;
            var rect = MagnifierService.CaretRectangle(handle);
            var source = PresentationSource.FromVisual(field)!;
            var topLeft = field.PointToScreen(new System.Windows.Point(0, 0));
            var bottomRight = field.PointToScreen(new System.Windows.Point(field.ActualWidth, field.ActualHeight));
            bool inside = rect is { } r && r.X >= topLeft.X - 2 && r.Right <= bottomRight.X + 2 && r.Y >= topLeft.Y - 2 && r.Bottom <= bottomRight.Y + 2;
            Add("caret.rectangle_inside_own_text_box", inside, new { rect, field = new { topLeft.X, topLeft.Y, Right = bottomRight.X, Bottom = bottomRight.Y } });

            // Live zoom would fight Windows Magnifier, which the user may be using right now.
            if (Process.GetProcessesByName("Magnify").Length > 0)
            {
                checks.Add(new { id = "zoom.live_checks", passed = true, skipped = "Windows Magnifier is running" });
                await File.WriteAllTextAsync(Path.Combine(directory, "magnifier.json"), JsonSerializer.Serialize(new { syntheticDataOnly = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close(); app.Shutdown(failed ? 1 : 0); return;
            }
            // Live zoom: 2× centered on the pointer, then back to 1×.
            using (var service = new MagnifierService())
            {
                string? failure = null; service.Failed += m => failure = m;
                service.Start(new MagnifierSettings { Enabled = true, FollowCaret = false });
                await Task.Delay(300);
                service.Toggle(); // 1× → last zoom (2×)
                await Task.Delay(900);
                MagGetFullscreenTransform(out float level, out int x, out int y);
                GetCursorPos(out var cursor);
                var expected = MagnifierMath.Center(new(cursor.X, cursor.Y), new(GetSystemMetrics(0), GetSystemMetrics(1)), 2);
                Add("zoom.applied_2x_centered_on_pointer", Math.Abs(level - 2) < 0.01 && Math.Abs(x - expected.X) <= 1 && Math.Abs(y - expected.Y) <= 1,
                    new { level, x, y, expected, failure });
                service.Toggle();
                await Task.Delay(700);
                MagGetFullscreenTransform(out level, out _, out _);
                Add("zoom.toggle_back_to_1x", Math.Abs(level - 1) < 0.01, new { level });
                service.Zoom(1);
                await Task.Delay(500);
            }
            MagGetFullscreenTransform(out float afterDispose, out _, out _);
            Add("zoom.dispose_restores_1x", Math.Abs(afterDispose - 1) < 0.01, new { level = afterDispose });

            // A magnifying process that dies must not leave the screen zoomed.
            using (var probe = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--magnifier-crash-probe") { UseShellExecute = false, CreateNoWindow = true })!)
            {
                await Task.Delay(1200);
                MagGetFullscreenTransform(out float whileRunning, out _, out _);
                probe.Kill(); await probe.WaitForExitAsync();
                await Task.Delay(700);
                MagGetFullscreenTransform(out float afterKill, out _, out _);
                Add("zoom.killed_process_restores_1x", Math.Abs(afterKill - 1) < 0.01, new { whileRunning, afterKill });
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "magnifier.json"), JsonSerializer.Serialize(new { syntheticDataOnly = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); app.Shutdown(failed ? 1 : 0);
        };
        app.Startup += (_, _) => window.Show();
    }

    /// <summary>Child for the crash check: zooms 1.5× and waits to be killed.</summary>
    internal static int CrashProbe()
    {
        if (!MagInitialize()) return 1;
        MagSetFullscreenTransform(1.5f, 0, 0);
        Thread.Sleep(30_000);
        return 0;
    }
}
