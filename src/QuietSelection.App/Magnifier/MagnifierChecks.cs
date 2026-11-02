using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
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
            try { await RunChecksAsync(); }
            catch (Exception ex)
            {
                // Synthetic test only: the exception text holds no user data.
                await File.WriteAllTextAsync(Path.Combine(directory, "magnifier-error.txt"), ex.ToString());
                window.Close(); app.Shutdown(1);
            }
        };
        async Task RunChecksAsync()
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
                // The pointer may be moved by hand meanwhile: measure only while it stays still.
                float level = 0; int x = 0, y = 0; MagPoint expected = default;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    GetCursorPos(out var before);
                    (level, x, y) = await ReadScreenAsync();
                    GetCursorPos(out var cursor);
                    expected = MagnifierMath.Center(new(cursor.X, cursor.Y), new(GetSystemMetrics(0), GetSystemMetrics(1)), 2);
                    if (before.X == cursor.X && before.Y == cursor.Y && Math.Abs(x - expected.X) <= 1 && Math.Abs(y - expected.Y) <= 1) break;
                    await Task.Delay(200);
                }
                Add("zoom.applied_2x_centered_on_pointer", Math.Abs(level - 2) < 0.01 && Math.Abs(x - expected.X) <= 1 && Math.Abs(y - expected.Y) <= 1,
                    new { level, x, y, expected, failure });
                service.Toggle();
                await Task.Delay(700);
                (level, _, _) = await ReadScreenAsync();
                Add("zoom.toggle_back_to_1x", Math.Abs(level - 1) < 0.01, new { level });
                service.Zoom(1);
                await Task.Delay(500);
            }
            GetCursorPos(out var userCursor); // Restored after the pointer checks.
            using (var service = new MagnifierService())
            {
                service.Start(new MagnifierSettings { Enabled = true, FollowCaret = true, HideCursorWhileTyping = true, Modifier = "Ctrl+Alt", MiddleClick = "Alone" });
                await Task.Delay(300);
                // Ctrl+Alt + two wheel notches over the own window: zoom 1 → 1.5625, the box must not scroll.
                SetCursorPos((int)((topLeft.X + bottomRight.X) / 2), (int)((topLeft.Y + bottomRight.Y) / 2));
                double before = service.Scale;
                PressKeys(0x11, 0x12); SendWheel(120); SendWheel(120); ReleaseKeys(0x12, 0x11);
                await Task.Delay(800);
                var (wheelLevel, _, _) = await ReadScreenAsync();
                Add("wheel.ctrl_alt_zooms", before == 1 && Math.Abs(service.Scale - 1.5625) < 0.01 && Math.Abs(wheelLevel - 1.5625) < 0.02,
                    new { before, after = service.Scale, wheelLevel });
                SendWheel(120); // Without the modifier the wheel is untouched.
                await Task.Delay(200);
                Add("wheel.plain_wheel_not_captured", Math.Abs(service.Scale - 1.5625) < 0.01, new { scale = service.Scale });

                // A plain middle click turns zoom off, the next one restores the remembered level.
                SendMiddleClick(); await Task.Delay(700);
                var (middleOff, _, _) = await ReadScreenAsync();
                SendMiddleClick(); await Task.Delay(700);
                var (middleOn, _, _) = await ReadScreenAsync();
                Add("middle.click_toggles_and_restores", Math.Abs(middleOff - 1) < 0.01 && Math.Abs(middleOn - 1.5625) < 0.02, new { middleOff, middleOn });

                // Beyond Windows Magnifier's 16x: the wheel limit is 64x.
                service.Zoom(20); await Task.Delay(1500);
                var (high, _, _) = await ReadScreenAsync();
                Add("zoom.beyond_16x", high > 16 && high <= MagnifierMath.MaximumScale + 0.01, new { high });
                service.Zoom(-30); service.Zoom(2); await Task.Delay(800); // Back to about 1.56x.

                // Caret: pointer in the top-left corner, then the caret moves in the centered box.
                service.Zoom(1); // About 1.95x.
                SetCursorPos(0, 0);
                await Task.Delay(700);
                var (_, cornerX, cornerY) = await ReadScreenAsync();
                field.CaretIndex = field.Text.Length;
                await Task.Delay(900);
                var (caretLevel, caretX, caretY) = await ReadScreenAsync();
                var caretRect = MagnifierService.CaretRectangle(handle);
                var screen = new MagPoint(GetSystemMetrics(0), GetSystemMetrics(1));
                bool caretVisible = caretRect is { } c && c.X >= caretX && c.Right <= caretX + screen.X / caretLevel && c.Y >= caretY && c.Bottom <= caretY + screen.Y / caretLevel;
                Add("caret.view_moves_to_caret", cornerX == 0 && cornerY == 0 && (caretX > 0 || caretY > 0) && caretVisible,
                    new { corner = new { cornerX, cornerY }, view = new { caretX, caretY, caretLevel }, caretRect });
                // Moving the mouse returns to pointer following.
                SetCursorPos(40, 40);
                await Task.Delay(600);
                var (backLevel, backX, backY) = await ReadScreenAsync();
                var pointerView = MagnifierMath.Center(new(40, 40), screen, backLevel);
                Add("caret.mouse_movement_returns_to_pointer", Math.Abs(backX - pointerView.X) <= 1 && Math.Abs(backY - pointerView.Y) <= 1, new { backX, backY, pointerView });
            }
            SetCursorPos(userCursor.X, userCursor.Y);
            var (afterDispose, _, _) = await ReadScreenAsync();
            Add("zoom.dispose_restores_1x", Math.Abs(afterDispose - 1) < 0.01, new { level = afterDispose });

            // A magnifying process that dies must not leave the screen zoomed.
            using (var probe = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--magnifier-crash-probe") { UseShellExecute = false, CreateNoWindow = true })!)
            {
                await Task.Delay(1200);
                var (whileRunning, _, _) = await ReadScreenAsync();
                probe.Kill(); await probe.WaitForExitAsync();
                await Task.Delay(700);
                var (afterKill, _, _) = await ReadScreenAsync();
                Add("zoom.killed_process_restores_1x", Math.Abs(afterKill - 1) < 0.01, new { whileRunning, afterKill });
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "magnifier.json"), JsonSerializer.Serialize(new { syntheticDataOnly = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); app.Shutdown(failed ? 1 : 0);
        }
        app.Startup += (_, _) => window.Show();
    }

    private static void PressKeys(params ushort[] keys) => SendInput((uint)keys.Length, keys.Select(k => new KeyboardInput { Type = 1, Vk = k }).ToArray(), Marshal.SizeOf<KeyboardInput>());
    private static void ReleaseKeys(params ushort[] keys) => SendInput((uint)keys.Length, keys.Select(k => new KeyboardInput { Type = 1, Vk = k, Flags = 2 }).ToArray(), Marshal.SizeOf<KeyboardInput>());
    private static void SendWheel(int delta)
    {
        // INPUT with MOUSEINPUT on x64: mouseData at offset 16, dwFlags at 20 (MOUSEEVENTF_WHEEL).
        var input = new byte[40];
        BitConverter.GetBytes(0u).CopyTo(input, 0);
        BitConverter.GetBytes(delta).CopyTo(input, 16);
        BitConverter.GetBytes(0x0800u).CopyTo(input, 20);
        SendRaw(1, input, 40);
    }
    [DllImport("user32.dll", EntryPoint = "SendInput")] private static extern uint SendRaw(uint count, byte[] input, int size);
    private static void SendMiddleClick()
    {
        foreach (uint flags in new[] { 0x0020u, 0x0040u }) // MOUSEEVENTF_MIDDLEDOWN, MIDDLEUP
        {
            var input = new byte[40];
            BitConverter.GetBytes(flags).CopyTo(input, 20);
            SendRaw(1, input, 40);
        }
    }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);

    // Only one thread per process may drive full-screen magnification, so the actual screen
    // transform is read by a short-lived separate process.
    private static async Task<(float Level, int X, int Y)> ReadScreenAsync()
    {
        using var reader = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--magnifier-read") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        string line = (await reader.StandardOutput.ReadToEndAsync()).Trim();
        await reader.WaitForExitAsync();
        var parts = line.Split(' ');
        return parts.Length == 3 ? (float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1]), int.Parse(parts[2])) : (0, 0, 0);
    }

    internal static int ReadProbe()
    {
        if (!MagInitialize() || !MagGetFullscreenTransform(out float level, out int x, out int y)) return 1;
        Console.WriteLine(level.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + x + " " + y);
        MagUninitialize();
        return 0;
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
