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
            response = request.Kind == "selection"
                ? new(Text: SelectionReader.Read((nint)request.SourceWindow), ProcessId: Environment.ProcessId)
                : new(ErrorCode.WorkerFailure);
        }
        catch (QuietException ex) { response = new(ex.Code, ProcessId: Environment.ProcessId); }
        catch { response = new(ErrorCode.ProviderFailure, ProcessId: Environment.ProcessId); }
        WorkerProtocol.WriteAsync(pipe, response, CancellationToken.None).GetAwaiter().GetResult();
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
            case "selection": return new(Text: SelectionReader.Read((nint)request.SourceWindow));
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
                if (speechResult == unchecked((int)0x80070490)) throw new QuietException(ErrorCode.VoiceMissing);
                NativeMethods.Check(speechResult, ErrorCode.SpeechFailure);
                try
                {
                    if (request.Kind == "synthesize-wave")
                    {
                        if (length is <= 44 or > 1_000_000) throw new QuietException(ErrorCode.SpeechFailure);
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
                uint packageLength = 0;
                if (NativeMethods.GetCurrentPackageFullName(ref packageLength, 0) == 15700)
                    throw new QuietException(ErrorCode.PackageIdentityRequired);
                var region = request.Region ?? throw new QuietException(ErrorCode.CaptureFailed);
                NativeMethods.Check(NativeMethods.qs_ocr_languages(out var availableLanguages, out var maximumDimension), ErrorCode.OcrUnavailable);
                NativeMethods.TakeString(availableLanguages);
                region.Validate((int)maximumDimension);
                if (request.SourceWindow == 0 || NativeMethods.GetForegroundWindow() != (nint)request.SourceWindow)
                    throw new QuietException(ErrorCode.SourceChanged);
                using (var captured = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(captured))
                        graphics.CopyFromScreen(region.X, region.Y, 0, 0, captured.Size, CopyPixelOperation.SourceCopy);
                    if (NativeMethods.GetForegroundWindow() != (nint)request.SourceWindow)
                        throw new QuietException(ErrorCode.SourceChanged);
                    return Recognize(captured, request.Language);
                }
            case "ocr-fixture":
                using (var bitmap = new Bitmap(1200, 180, PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    using (var font = new Font("Yu Gothic", 32))
                    {
                        graphics.Clear(Color.White);
                        graphics.DrawString(request.Text, font, Brushes.Black, 20, 40);
                    }
                    return Recognize(bitmap, request.Language);
                }
            default: throw new QuietException(ErrorCode.WorkerFailure);
        }
    }

    private static WorkerResponse Recognize(Bitmap bitmap, string language)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            NativeMethods.Check(NativeMethods.qs_recognize(data.Scan0, bitmap.Width, bitmap.Height, language, out var text), ErrorCode.OcrFailure);
            var result = NativeMethods.TakeString(text);
            if (string.IsNullOrWhiteSpace(result)) throw new QuietException(ErrorCode.OcrEmpty);
            if (result.Length > TextLimits.Maximum) throw new QuietException(ErrorCode.TooLong);
            return new(Text: result);
        }
        finally { bitmap.UnlockBits(data); }
    }
}
