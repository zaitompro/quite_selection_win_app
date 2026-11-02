using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using QuietSelection.App.Platform;
using QuietSelection.App.Selection;

namespace QuietSelection.App.Workers;

internal static class WorkerHost
{
    internal static int RunSelection(string name)
    {
        // Keep the creating MTA apartment alive. Do not cache elements, ranges or text.
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(5_000);
            while (pipe.IsConnected) SelectionRoundTrip(pipe);
            return 0;
        }
        catch (EndOfStreamException) { return 0; }
        catch { return 1; }
    }

    private static void SelectionRoundTrip(Stream pipe)
    {
        var request = WorkerProtocol.ReadAsync<WorkerRequest>(pipe, CancellationToken.None).GetAwaiter().GetResult();
        WorkerResponse response;
        try
        {
            if (request.Kind is "selection" or "point")
            {
                var selection = request.Kind == "point" && request.Region is { } point
                    ? SelectionReader.ReadAtPoint(point.X, point.Y, request.OwnerProcess)
                    : SelectionReader.Read((nint)request.SourceWindow);
                response = new(selection.Error, selection.Text, ProcessId: Environment.ProcessId, Diagnostic: selection.Diagnostic);
            }
            else response = new(ErrorCode.WorkerFailure);
        }
        catch (QuietException ex) { response = new(ex.Code, ProcessId: Environment.ProcessId); }
        catch { response = new(ErrorCode.ProviderFailure, ProcessId: Environment.ProcessId); }
        WorkerProtocol.WriteAsync(pipe, response, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Resident helper for WinRT/SAPI synthesis. Serves requests until the pipe closes.</summary>
    internal static int RunNativeSpeech(string name)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(5_000);
            while (pipe.IsConnected)
            {
                var request = WorkerProtocol.ReadAsync<WorkerRequest>(pipe, CancellationToken.None).GetAwaiter().GetResult();
                WorkerResponse response;
                try { response = request.Kind == "synthesize-wave" ? Execute(request) : new(ErrorCode.WorkerFailure); }
                catch (QuietException ex) { response = new(ex.Code); }
                catch { response = new(ErrorCode.SpeechFailure); }
                WorkerProtocol.WriteAsync(pipe, response, CancellationToken.None).GetAwaiter().GetResult();
            }
            return 0;
        }
        catch (EndOfStreamException) { return 0; }
        catch { return 1; }
    }

    internal static int Run(string name)
    {
        // The worker owns no UI. The process Main thread is MTA and exits after one request.
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(5_000);
            var request = WorkerProtocol.ReadAsync<WorkerRequest>(pipe, CancellationToken.None).GetAwaiter().GetResult();
            WorkerResponse response;
            try { response = Execute(request); }
            catch (QuietException ex) { response = new(ex.Code); }
            catch { response = new(ErrorCode.WorkerFailure); }
            WorkerProtocol.WriteAsync(pipe, response, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch { return 1; }
    }

    private static WorkerResponse Execute(WorkerRequest request)
    {
        switch (request.Kind)
        {
            case "selection":
                var selection = SelectionReader.Read((nint)request.SourceWindow);
                if (selection.Error != ErrorCode.None) throw new QuietException(selection.Error);
                return new(Text: selection.Text, Diagnostic: selection.Diagnostic);
            case "voices":
                NativeMethods.Check(NativeMethods.qs_voices(out var voicesPointer), ErrorCode.SpeechFailure);
                var voices = NativeMethods.TakeString(voicesPointer).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Split('\t')).Where(x => x.Length == 3).Select(x => new VoiceInfo(x[0], x[1], x[2])).ToArray();
                return new(Voices: voices.Concat(Speech.Downloads.PiperModels.AvailableVoices()).Concat(Speech.Downloads.SileroModels.AvailableVoices()).Concat(Speech.Downloads.AdditionalModels.AvailableVoices()).ToArray());
            case "ocr-languages":
                NativeMethods.Check(NativeMethods.qs_ocr_languages(out var languagesPointer, out var dimension), ErrorCode.OcrUnavailable);
                return new(Languages: NativeMethods.TakeString(languagesPointer).Split('\n', StringSplitOptions.RemoveEmptyEntries), MaximumDimension: (int)dimension);
            case "speak":
            case "synthesize-test":
            case "synthesize-wave":
                if (request.Text.Length > TextLimits.Maximum) throw new QuietException(ErrorCode.TooLong);
                if (request.Speed is < 0 or > 100) throw new QuietException(ErrorCode.InvalidSettings);
                nint audio; uint length;
                int speechResult = request.VoiceId.StartsWith("sapi5:", StringComparison.Ordinal)
                    ? NativeMethods.qs_sapi_synthesize(request.VoiceId[6..], request.Text, request.Speed, out audio, out length)
                    : NativeMethods.qs_synthesize(request.VoiceId, request.Text, LanguagePlanner.SpeakingRate(request.Speed), out audio, out length);
                if (speechResult == NativeMethods.ErrorNotFoundHResult) throw new QuietException(ErrorCode.VoiceMissing);
                NativeMethods.Check(speechResult, ErrorCode.SpeechFailure);
                try
                {
                    if (request.Kind == "synthesize-wave")
                    {
                        if (length is <= 44 or > WorkerProtocol.MaximumWaveBytes) throw new QuietException(ErrorCode.SpeechFailure);
                        var bytes = new byte[length];
                        System.Runtime.InteropServices.Marshal.Copy(audio, bytes, 0, bytes.Length);
                        return new(AudioBytes: length, AudioData: bytes);
                    }
                    if (request.Kind == "speak" && !NativeMethods.PlaySound(audio, 0, 0x0004 | 0x0002))
                        throw new QuietException(ErrorCode.SpeechFailure);
                    using var process = System.Diagnostics.Process.GetCurrentProcess();
                    return new(AudioBytes: length, PeakWorkingSetBytes: process.PeakWorkingSet64, PrivateBytes: process.PrivateMemorySize64);
                }
                finally { NativeMethods.qs_free(audio); }
            case "ocr":
                if (!NativeMethods.IsPackaged)
                    throw new QuietException(ErrorCode.PackageIdentityRequired);
                var region = request.Region ?? throw new QuietException(ErrorCode.CaptureFailed);
                NativeMethods.Check(NativeMethods.qs_ocr_languages(out var availableLanguages, out var maximumDimension), ErrorCode.OcrUnavailable);
                NativeMethods.TakeString(availableLanguages);
                region.Validate((int)maximumDimension);
                if (request.SourceWindow == 0 || NativeMethods.GetForegroundWindow() != (nint)request.SourceWindow)
                    throw new QuietException(ErrorCode.SourceChanged);
                // What the user circled on a magnified screen, not the unmagnified desktop pixels.
                using (var captured = ScreenCapture.CaptureDesktopRegion(region, (int)maximumDimension))
                {
                    if (NativeMethods.GetForegroundWindow() != (nint)request.SourceWindow)
                        throw new QuietException(ErrorCode.SourceChanged);
                    return Recognize(captured, request.Language);
                }
            case "ocr-point":
                // Fallback for text under the pointer that UI Automation does not expose. Only the
                // area around the pointer is captured; only the paragraph under it is kept.
                if (!NativeMethods.IsPackaged)
                    throw new QuietException(ErrorCode.PackageIdentityRequired);
                var pointer = request.Region ?? throw new QuietException(ErrorCode.CaptureFailed);
                using (var around = ScreenCapture.CaptureAroundPointer(pointer.X, pointer.Y, 1200, 360, out var inImage))
                    return OcrPipeline.RecognizeAt(around, request.Language, inImage);
            case "capture-probe":
                // Checks only: two brightness numbers of a synthetic striped window, no image is returned.
                if (request.Region is not { } probeRegion) return new(Text: "ERR no region");
                Magnifier.MagnifierNative.MagInitialize();
                Magnifier.MagnifierNative.MagGetFullscreenTransform(out float probeLevel, out int probeX, out int probeY);
                Magnifier.MagnifierNative.MagUninitialize();
                string probeState = $"level={probeLevel} offset={probeX},{probeY} region={probeRegion} screen={Magnifier.MagnifierNative.GetSystemMetrics(0)}x{Magnifier.MagnifierNative.GetSystemMetrics(1)}";
                Bitmap probe;
                try { probe = ScreenCapture.CaptureDesktopRegion(probeRegion, 8192); }
                catch (QuietException) { return new(Text: "ERR " + probeState); }
                using (probe)
                {
                    var (leftMean, rightMean) = ScreenCapture.Fingerprint(probe);
                    return new(Text: leftMean.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " " + rightMean.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                }
            case "ocr-fixture":
                using (var bitmap = DrawFixture(request.Text, request.Fixture))
                    return Recognize(bitmap, request.Language);
            default: throw new QuietException(ErrorCode.WorkerFailure);
        }
    }

    // Synthetic OCR images that resemble hard screen captures. Text comes from the checks only.
    private static Bitmap DrawFixture(string text, string? kind)
    {
        bool japanese = text.Any(c => c is >= '\u3040' and <= '\u9fff');
        string family = japanese ? "Yu Gothic UI" : "Segoe UI";
        (float size, Color back, Color fore) = kind switch
        {
            "small" => (8.5f, Color.White, Color.Black),            // About 11 px, a dense UI font.
            "dark" => (11f, Color.FromArgb(32, 32, 32), Color.FromArgb(230, 230, 230)),
            "low-contrast" => (11f, Color.FromArgb(243, 243, 243), Color.FromArgb(140, 140, 140)),
            "tight" => (16f, Color.White, Color.Black),
            _ => (32f, Color.White, Color.Black)
        };
        using var font = new Font(family, size);
        SizeF measured;
        using (var probe = new Bitmap(1, 1)) using (var g = Graphics.FromImage(probe)) measured = g.MeasureString(text, font);
        int margin = kind == "tight" ? 0 : kind is null ? 40 : 8;
        int width = (int)Math.Ceiling(measured.Width) + 2 * margin, height = (int)Math.Ceiling(measured.Height) + 2 * margin;
        if (kind is null) { width = Math.Max(width, 1200); height = Math.Max(height, 180); }
        var bitmap = new Bitmap(Math.Max(8, width), Math.Max(8, height), PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(back);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit; // As on screen.
        using var brush = new SolidBrush(fore);
        graphics.DrawString(text, font, brush, margin - (kind == "tight" ? 2 : 0), margin - (kind == "tight" ? 3 : 0));
        return bitmap;
    }

    private static WorkerResponse Recognize(Bitmap bitmap, string language) => OcrPipeline.Recognize(bitmap, language);
}
