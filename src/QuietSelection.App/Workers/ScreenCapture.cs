using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using static QuietSelection.App.Magnifier.MagnifierNative;

namespace QuietSelection.App.Workers;

/// <summary>
/// Captures a desktop region as the user saw it. Under full-screen magnification (ours or Windows
/// Magnifier) the screen shows a zoomed view, and a plain screen copy at desktop coordinates would
/// return a different area. The current transform is read and the matching on-screen rectangle is
/// captured instead, at the magnified resolution (sharper text for OCR), reduced only to fit the
/// recognizer's limit. Runs in a short-lived worker process: only one thread per process may hold
/// the Magnification API, and the app's magnifier owns it there.
/// </summary>
internal static class ScreenCapture
{
    internal static Bitmap CaptureDesktopRegion(PixelRegion region, int maximumDimension)
    {
        float level = 1; int offsetX = 0, offsetY = 0;
        if (MagInitialize())
        {
            try { if (!MagGetFullscreenTransform(out level, out offsetX, out offsetY) || level < 1) { level = 1; offsetX = offsetY = 0; } }
            finally { MagUninitialize(); }
        }
        int screenWidth = GetSystemMetrics(0), screenHeight = GetSystemMetrics(1);
        double left = (region.X - offsetX) * (double)level, top = (region.Y - offsetY) * (double)level;
        double right = left + region.Width * (double)level, bottom = top + region.Height * (double)level;
        int x = (int)Math.Floor(Math.Max(0, left)), y = (int)Math.Floor(Math.Max(0, top));
        int width = (int)Math.Ceiling(Math.Min(screenWidth, right)) - x, height = (int)Math.Ceiling(Math.Min(screenHeight, bottom)) - y;
        if (width < 8 || height < 8) throw new QuietException(ErrorCode.CaptureFailed); // Region is not on screen.
        var captured = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(captured))
            graphics.CopyFromScreen(x, y, 0, 0, captured.Size, CopyPixelOperation.SourceCopy);
        if (width <= maximumDimension && height <= maximumDimension) return captured;
        // Fit the recognizer's limit, keeping the aspect ratio.
        double factor = Math.Min((double)maximumDimension / width, (double)maximumDimension / height);
        var fitted = new Bitmap(Math.Max(8, (int)(width * factor)), Math.Max(8, (int)(height * factor)), PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(fitted))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(captured, 0, 0, fitted.Width, fitted.Height);
        }
        captured.Dispose();
        return fitted;
    }

    /// <summary>Mean brightness of the left and right thirds; used by checks with a synthetic striped window.</summary>
    internal static (double Left, double Right) Fingerprint(Bitmap bitmap)
    {
        double Mean(int from, int to)
        {
            double sum = 0; int count = 0;
            for (int y = 0; y < bitmap.Height; y += 4)
                for (int x = from; x < to; x += 4, count++)
                {
                    var c = bitmap.GetPixel(x, y);
                    sum += (c.R * 0.2126 + c.G * 0.7152 + c.B * 0.0722) / 255;
                }
            return count == 0 ? 0 : sum / count;
        }
        return (Mean(0, bitmap.Width / 3), Mean(bitmap.Width * 2 / 3, bitmap.Width));
    }
}
