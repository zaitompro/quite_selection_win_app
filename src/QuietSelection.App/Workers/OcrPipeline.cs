using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using QuietSelection.App.Platform;

namespace QuietSelection.App.Workers;

/// <summary>
/// Prepares a screen capture for Windows OCR, which misses small UI text, light text on dark
/// backgrounds, low contrast and glyphs touching the image edge. The capture is converted to
/// dark-on-light grayscale with stretched contrast, enlarged when small and padded with margin.
/// If the chosen language finds nothing, the other installed ru/en/ja recognizers are tried.
/// Images stay in memory and are cleared when released.
/// </summary>
internal static partial class OcrPipeline
{
    // Windows OCR separates Japanese characters with spaces; speech would pause at each one.
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=[\u3000-\u30FF\u3400-\u9FFF\uFF00-\uFFEF])[ \t]+(?=[\u3000-\u30FF\u3400-\u9FFF\uFF00-\uFFEF])")]
    private static partial System.Text.RegularExpressions.Regex SpacesBetweenCjk();

    // Windows OCR is reliable from roughly 20 px line height; small UI text is about 11–15 px.
    private const int SmallHeight = 160, MediumHeight = 420;

    internal static WorkerResponse Recognize(Bitmap capture, string language)
    {
        NativeMethods.Check(NativeMethods.qs_ocr_languages(out var listPointer, out uint maximumDimension), ErrorCode.OcrUnavailable);
        var installed = NativeMethods.TakeString(listPointer).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var languages = new List<string> { language };
        foreach (var other in new[] { "ru", "en", "ja" })
            if (other != language.Split('-')[0] && installed.FirstOrDefault(x => x.Split('-')[0] == other) is { } tag) languages.Add(tag);

        int scale = capture.Height < SmallHeight ? 3 : capture.Height < MediumHeight ? 2 : 1;
        while (scale > 1 && (capture.Width * scale + 64 > maximumDimension || capture.Height * scale + 64 > maximumDimension
            || (long)capture.Width * capture.Height * scale * scale > 40_000_000)) scale--;

        using var prepared = Prepare(capture, scale);
        // Prepared image first; the untouched capture second, in case the enhancement hurt a clean image.
        foreach (var candidate in languages)
        {
            string text = Run(prepared, candidate);
            if (text.Length == 0 && candidate == language) text = Run(capture, candidate);
            if (text.Length > 0)
            {
                if (text.Length > TextLimits.Maximum) throw new QuietException(ErrorCode.TooLong);
                return new(Text: text);
            }
        }
        throw new QuietException(ErrorCode.OcrEmpty);
    }

    /// <summary>
    /// Recognizes the capture around the pointer and keeps only the paragraph under it: the line
    /// containing (or nearest to) the pointer plus neighbouring lines of the same column with
    /// line-sized gaps.
    /// </summary>
    internal static WorkerResponse RecognizeAt(Bitmap capture, string language, System.Drawing.Point pointer)
    {
        NativeMethods.Check(NativeMethods.qs_ocr_languages(out var listPointer, out uint maximumDimension), ErrorCode.OcrUnavailable);
        var installed = NativeMethods.TakeString(listPointer).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var languages = new List<string> { language };
        foreach (var other in new[] { "ru", "en", "ja" })
            if (other != language.Split('-')[0] && installed.FirstOrDefault(x => x.Split('-')[0] == other) is { } tag) languages.Add(tag);
        int scale = capture.Height < SmallHeight ? 3 : capture.Height < MediumHeight ? 2 : 1;
        while (scale > 1 && (capture.Width * scale + 64 > maximumDimension || capture.Height * scale + 64 > maximumDimension)) scale--;
        int margin = Margin(capture.Height, scale);
        using var prepared = Prepare(capture, scale);
        foreach (var candidate in languages)
        {
            var lines = RunLines(prepared, candidate)
                .Select(x => x with { Left = (x.Left - margin) / scale, Top = (x.Top - margin) / scale, Right = (x.Right - margin) / scale, Bottom = (x.Bottom - margin) / scale })
                .OrderBy(x => x.Top).ToList();
            if (lines.Count == 0) continue;
            var text = OcrLayout.PickParagraph(lines, pointer.X, pointer.Y);
            if (text.Length == 0) throw new QuietException(ErrorCode.OcrEmpty);
            if (text.Length > TextLimits.Maximum) throw new QuietException(ErrorCode.TooLong);
            return new(Text: SpacesBetweenCjk().Replace(text, "").Trim());
        }
        throw new QuietException(ErrorCode.OcrEmpty);
    }

    private static List<OcrLine> RunLines(Bitmap bitmap, string language)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int result = NativeMethods.qs_recognize_lines(data.Scan0, bitmap.Width, bitmap.Height, language, out var text);
            if (result == NativeMethods.ErrorNotFoundHResult) return [];
            NativeMethods.Check(result, ErrorCode.OcrFailure);
            var lines = new List<OcrLine>();
            foreach (var row in NativeMethods.TakeString(text).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int tab = row.IndexOf('\t');
                if (tab < 0) continue;
                var box = row[..tab].Split(' ');
                if (box.Length != 4 || !box.All(v => int.TryParse(v, out _))) continue;
                lines.Add(new(int.Parse(box[0]), int.Parse(box[1]), int.Parse(box[2]), int.Parse(box[3]), row[(tab + 1)..]));
            }
            return lines;
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static int Margin(int height, int scale) => Math.Max(24, height * scale / 6);

    private static string Run(Bitmap bitmap, string language)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int result = NativeMethods.qs_recognize(data.Scan0, bitmap.Width, bitmap.Height, language, out var text);
            if (result == NativeMethods.ErrorNotFoundHResult) return ""; // Recognizer for this language is not installed.
            NativeMethods.Check(result, ErrorCode.OcrFailure);
            return SpacesBetweenCjk().Replace(NativeMethods.TakeString(text), "").Trim();
        }
        finally { bitmap.UnlockBits(data); }
    }

    /// <summary>Dark text on a light background, contrast stretched, enlarged and padded.</summary>
    internal static Bitmap Prepare(Bitmap capture, int scale)
    {
        int width = capture.Width, height = capture.Height;
        var pixels = new byte[width * height * 4];
        var gray = new byte[width * height];
        try
        {
            var data = capture.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * width * 4, width * 4);
            }
            finally { capture.UnlockBits(data); }

            var histogram = new int[256];
            for (int i = 0; i < gray.Length; i++)
            {
                int b = pixels[i * 4], g = pixels[i * 4 + 1], r = pixels[i * 4 + 2];
                byte value = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                gray[i] = value; histogram[value]++;
            }
            // The most common shade is the background; a dark one means light text, so invert.
            int background = Array.IndexOf(histogram, histogram.Max());
            bool invert = background < 128;
            // Stretch between the 1st and 99th percentiles so faint text gains contrast.
            int low = Percentile(histogram, gray.Length, 0.01), high = Percentile(histogram, gray.Length, 0.99);
            if (high - low < 16) { low = 0; high = 255; }
            for (int i = 0; i < gray.Length; i++)
            {
                int value = Math.Clamp((gray[i] - low) * 255 / (high - low), 0, 255);
                if (invert) value = 255 - value;
                pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = (byte)value;
                pixels[i * 4 + 3] = 255;
            }

            using var normalized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var target = normalized.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < height; y++) Marshal.Copy(pixels, y * width * 4, target.Scan0 + y * target.Stride, width * 4);
            }
            finally { normalized.UnlockBits(target); }

            // Margin around the text: OCR misses lines that touch the image border.
            int margin = Margin(height, scale);
            var result = new Bitmap(width * scale + 2 * margin, height * scale + 2 * margin, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(result);
            graphics.Clear(Color.White);
            graphics.InterpolationMode = scale > 1 ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(normalized, new Rectangle(margin, margin, width * scale, height * scale), 0, 0, width, height, GraphicsUnit.Pixel);
            return result;
        }
        finally { Array.Clear(pixels); Array.Clear(gray); }
    }

    private static int Percentile(int[] histogram, int total, double fraction)
    {
        long limit = (long)(total * fraction), sum = 0;
        for (int i = 0; i < histogram.Length; i++) { sum += histogram[i]; if (sum > limit) return i; }
        return 255;
    }
}
