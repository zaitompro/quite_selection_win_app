using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuietSelection.App.Views;

/// <summary>
/// The app mark drawn in code: lines of text with one selected line, on the brand blue.
/// The active variant adds a sound dot so the tray shows that reading is in progress.
/// </summary>
internal static class AppIcon
{
    internal static readonly Color Brand = Color.FromRgb(0x1B, 0x4B, 0x7E);
    private static readonly Color Highlight = Color.FromRgb(0xFF, 0xC8, 0x3D);
    private static readonly Color Active = Color.FromRgb(0x4C, 0xC2, 0xFF);

    internal static BitmapSource Render(int size, bool active = false)
    {
        var visual = new DrawingVisual();
        using (var g = visual.RenderOpen())
        {
            double s = size;
            g.DrawRoundedRectangle(new SolidColorBrush(Brand), null, new Rect(0, 0, s, s), s * 0.22, s * 0.22);
            var white = new SolidColorBrush(Colors.White);
            double left = s * 0.22, line = Math.Max(1, Math.Round(s * 0.09)), radius = line / 2;
            g.DrawRoundedRectangle(white, null, new Rect(left, s * 0.25, s * 0.56, line), radius, radius);
            // The selected line: a highlight block with a dark line on top.
            g.DrawRoundedRectangle(new SolidColorBrush(Highlight), null, new Rect(left - s * 0.04, s * 0.42, s * 0.52, line * 2), radius, radius);
            g.DrawRoundedRectangle(new SolidColorBrush(Brand), null, new Rect(left, s * 0.42 + line / 2, s * 0.44, line), radius, radius);
            g.DrawRoundedRectangle(white, null, new Rect(left, s * 0.66, s * 0.40, line), radius, radius);
            if (active)
            {
                double d = s * 0.36;
                g.DrawEllipse(new SolidColorBrush(Active), new Pen(new SolidColorBrush(Brand), Math.Max(1, s * 0.06)),
                    new Point(s - d / 2 - s * 0.02, s - d / 2 - s * 0.02), d / 2, d / 2);
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Title bar and taskbar icon (Windows scales it to the current DPI).</summary>
    internal static ImageSource Window { get; } = Render(48);

    /// <summary>A GDI icon for the notification area. The caller disposes it.</summary>
    internal static System.Drawing.Icon Tray(int size, bool active)
    {
        var source = Render(size, active);
        var pixels = new byte[size * size * 4];
        source.CopyPixels(pixels, size * 4, 0);
        using var bitmap = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, size, size), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
        finally { bitmap.UnlockBits(data); }
        nint handle = bitmap.GetHicon();
        try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    /// <summary>Writes the MSIX logos and a multi-size .ico (PNG entries) for the executable.</summary>
    internal static void Export(string directory)
    {
        Directory.CreateDirectory(directory);
        static byte[] Png(int size)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Render(size)));
            using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
        }
        foreach (var (name, size) in new[] { ("StoreLogo.png", 50), ("Square44x44Logo.png", 44), ("Square150x150Logo.png", 150) })
            File.WriteAllBytes(Path.Combine(directory, name), Png(size));
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];
        var images = sizes.Select(Png).ToArray();
        using var ico = new BinaryWriter(File.Create(Path.Combine(directory, "QuietSelection.ico")));
        ico.Write((ushort)0); ico.Write((ushort)1); ico.Write((ushort)sizes.Length); // ICONDIR
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            ico.Write((byte)0); ico.Write((byte)0); ico.Write((ushort)1); ico.Write((ushort)32);
            ico.Write(images[i].Length); ico.Write(offset); offset += images[i].Length;
        }
        foreach (var image in images) ico.Write(image);
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint handle);
}
