using System.Diagnostics;
using QuietSelection.App.Workers;

namespace QuietSelection.App.Translation;

internal sealed record TranslationRequest(IReadOnlyList<TranslationSegment> Segments, string Target);
internal sealed record TranslationResponse(bool Success, string Text, string Failure);

internal sealed class TranslationClient(string modelRoot)
{
    internal string LastFailure { get; private set; } = "";
    internal long PeakWorkingSetBytes { get; private set; }
    internal static string DefaultModelRoot => Platform.AppPaths.Models("argos-v1");

    internal async Task<string> TranslateAsync(IReadOnlyList<TranslationSegment> segments, string target, CancellationToken token)
    {
        if (segments.All(x => x.Source == target)) return string.Concat(segments.Select(x => x.Text));
        if (segments.Any(x => !TranslationPlanner.IsSupported(x.Source, target))) throw new QuietException(ErrorCode.UnsupportedPair);
        var runtime = Path.Combine(AppContext.BaseDirectory, "translation-runtime", "python.exe");
        if (!File.Exists(runtime) || !Directory.Exists(modelRoot)) throw new QuietException(ErrorCode.ModelMissing);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var process = new Process { StartInfo = new(runtime)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(runtime)!
        } };
        foreach (var argument in new[] { "-I", "-B", Path.Combine(AppContext.BaseDirectory, "translation", "worker.py"), modelRoot })
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["OMP_NUM_THREADS"] = "2";
        process.StartInfo.Environment["OPENBLAS_NUM_THREADS"] = "1";
        process.Start();
        WorkerJob job;
        try { job = new WorkerJob(process.Handle, 1536UL * 1024 * 1024); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        using var lifetime = job;
        // Drain directly to a discard sink. Do not buffer/log dependency diagnostics.
        var diagnostics = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            await WorkerProtocol.WriteAsync(process.StandardInput.BaseStream, new TranslationRequest(segments, target), deadline.Token);
            process.StandardInput.Close();
            var response = await WorkerProtocol.ReadAsync<TranslationResponse>(process.StandardOutput.BaseStream, deadline.Token);
            token.ThrowIfCancellationRequested();
            if (!response.Success || string.IsNullOrWhiteSpace(response.Text))
            { LastFailure = response.Failure is "MEMORY_LIMIT" or "RUNTIME_ERROR" ? response.Failure : "ENGINE_FAILURE"; throw new QuietException(ErrorCode.TranslationFailure); }
            if (response.Text.Length > TextLimits.Maximum) throw new QuietException(ErrorCode.TooLong);
            return response.Text;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new QuietException(ErrorCode.TranslationTimeout); }
        catch (EndOfStreamException) { throw new QuietException(ErrorCode.TranslationFailure); }
        finally
        {
            if (!process.HasExited) { process.Refresh(); PeakWorkingSetBytes = Math.Max(PeakWorkingSetBytes, process.PeakWorkingSet64); }
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await diagnostics;
        }
    }

    internal async Task ProbeAsync(CancellationToken token)
    {
        foreach (var sample in new[] { ("en", "ru", "This is a translation test."),
            ("ja", "ru", "これは翻訳のテストです。"), ("ja", "en", "これは翻訳のテストです。") })
        {
            string result = await TranslateAsync([new(sample.Item3, sample.Item1)], sample.Item2, token);
            bool expectedScript = sample.Item2 == "ru" ? result.Any(c => c is >= '\u0400' and <= '\u04ff') : result.Any(char.IsAsciiLetter);
            if (!expectedScript) throw new QuietException(ErrorCode.TranslationFailure);
        }
    }
}
