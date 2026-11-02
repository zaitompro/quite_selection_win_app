using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace QuietSelection.App.WindowTheme;

/// <summary>
/// Dark-window checks on own synthetic windows only: a light WPF window, a classic dialog with a
/// tab control (like file properties) and a remembered window type. Screen pixels are sampled
/// in memory to measure brightness and discarded.
/// </summary>
internal static class WindowThemeChecks
{
    internal static void Run(Application app, string[] args)
    {
        string directory = Path.GetFullPath(args[Array.IndexOf(args, "--report-directory") + 1]);
        Directory.CreateDirectory(directory);
        app.Startup += async (_, _) =>
        {
            var checks = new List<object>(); bool failed = false;
            void Add(string id, bool passed, object? data = null) { checks.Add(new { id, passed, data }); failed |= !passed; }
            try
            {
                using var client = new WindowThemeClient();
                var light = LightWindow("QuietSelection — проверка тёмного окна", 200);
                light.Show(); light.Activate();
                await Task.Delay(1500); // First frames of a new window are not final.
                nint handle = new WindowInteropHelper(light).Handle;
                double before = Brightness(handle);
                Add("detect.light_window", WindowThemeHost.IsLight(handle), new { before });

                await client.SendAsync(new("theme-configure"));
                var on = await client.SendAsync(new("theme-toggle", (long)handle));
                await Task.Delay(600);
                double darkened = Brightness(handle);
                Add("toggle.darkens_window", on.Success && on.Applied && darkened < 0.35, new { before, darkened, on.Rule });

                // Clicks pass through: the window under the overlay is still hit.
                var center = Center(handle);
                nint hit = WindowFromPoint(new() { X = center.X, Y = center.Y });
                Add("overlay.click_through", GetAncestor(hit, 2) == handle, new { hit = (long)hit, handle = (long)handle });

                // Cost with one overlay at steady state.
                var helper = Process.GetProcessById(client.ProcessId!.Value);
                var cpuStart = helper.TotalProcessorTime; var clock = Stopwatch.StartNew();
                await Task.Delay(3000);
                helper.Refresh();
                double cpu = (helper.TotalProcessorTime - cpuStart).TotalMilliseconds / clock.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
                checks.Add(new { id = "overlay.cpu_percent_of_all_cores", passed = true, cpu = Math.Round(cpu, 2), workingSetMiB = helper.WorkingSet64 / 1048576 });

                var off = await client.SendAsync(new("theme-toggle", (long)handle));
                await Task.Delay(900);
                double restored = Brightness(handle);
                Add("toggle.restores_window", off.Success && !off.Applied && restored > 0.6, new { restored });

                // Automatic: a classic dialog with tabs, like file properties.
                await client.SendAsync(new("theme-configure", Properties: true));
                var controls = new CommonControls { Size = 8, Classes = 0x8 }; InitCommonControlsEx(ref controls);
                nint sheet = CreateWindowEx(0x8, "#32770", "Свойства: проверка", 0x00CF0000, 300, 200, 520, 420, 0, 0, 0, 0); // WS_EX_TOPMOST
                CreateWindowEx(0, "SysTabControl32", null, 0x50000000, 12, 12, 480, 340, sheet, 0, 0, 0);
                ShowWindow(sheet, 5);
                await Task.Delay(1500);
                var status = await client.SendAsync(new("theme-status"));
                double sheetBrightness = Brightness(sheet);
                Add("auto.property_sheet_darkened", status.Overlays == 1 && sheetBrightness < 0.35, new { status.Overlays, sheetBrightness });
                DestroyWindow(sheet);
                await Task.Delay(300);

                // Remembered type: a second window of the same kind darkens by itself.
                string rule = WindowThemeHost.RuleFor(handle);
                await client.SendAsync(new("theme-configure", Rules: [rule]));
                var second = LightWindow("QuietSelection — второе окно", 260);
                second.Show();
                await Task.Delay(2500);
                nint secondHandle = new WindowInteropHelper(second).Handle;
                double secondBrightness = Brightness(secondHandle);
                Add("auto.remembered_type_darkened", secondBrightness < 0.35 && !rule.Contains("-4", StringComparison.Ordinal), new { rule, secondBrightness });

                // A crashed helper takes its overlays with it.
                Process.GetProcessById(client.ProcessId!.Value).Kill();
                await Task.Delay(800);
                double afterCrash = Brightness(secondHandle);
                Add("crash.overlays_disappear", afterCrash > 0.6, new { afterCrash });
                second.Close(); light.Close();
            }
            catch (Exception ex) { checks.Add(new { id = "exception", passed = false, error = ex.ToString() }); failed = true; }
            await File.WriteAllTextAsync(Path.Combine(directory, "window-theme.json"), JsonSerializer.Serialize(new { syntheticDataOnly = true, checks },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            app.Shutdown(failed ? 1 : 0);
        };
    }

    // A light window even when Windows uses the dark theme.
    private static Window LightWindow(string title, int top)
    {
        var window = new Window
        {
            Title = title, Width = 560, Height = 300, Left = 240, Top = top, Topmost = true, ShowActivated = true,
            Background = Brushes.White, Foreground = Brushes.Black, FontSize = 20,
            Content = new TextBlock { Text = "Синтетический светлый текст для проверки.", Margin = new(24), Foreground = Brushes.Black }
        };
        Views.SystemTheme.Preview(window, dark: false);
        return window;
    }

    private static (int X, int Y) Center(nint window)
    {
        WindowThemeNative.VisibleBounds(window, out var r);
        return ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
    }

    /// <summary>Mean brightness (0..1) of the window's central area as seen on screen.</summary>
    private static double Brightness(nint window)
    {
        WindowThemeNative.VisibleBounds(window, out var r);
        int width = Math.Max(8, r.Width / 2), height = Math.Max(8, r.Height / 2);
        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap)) g.CopyFromScreen(r.Left + r.Width / 4, r.Top + r.Height / 4, 0, 0, bitmap.Size);
        double sum = 0; int count = 0;
        for (int y = 0; y < height; y += 6)
            for (int x = 0; x < width; x += 6, count++)
            {
                var c = bitmap.GetPixel(x, y);
                sum += (c.R * 0.2126 + c.G * 0.7152 + c.B * 0.0722) / 255;
            }
        return sum / count;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CommonControls { public int Size, Classes; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string? title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("comctl32.dll")] private static extern bool InitCommonControlsEx(ref CommonControls controls);
}
