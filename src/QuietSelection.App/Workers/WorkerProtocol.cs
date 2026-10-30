using System.Text.Json;

namespace QuietSelection.App.Workers;

internal sealed record WorkerRequest(string Kind, long SourceWindow = 0, string Text = "", string VoiceId = "",
    int Speed = 50, string Language = "ru", PixelRegion? Region = null, string[]? VoiceIds = null, string? Fixture = null);
internal sealed record WorkerResponse(ErrorCode Error = ErrorCode.None, string Text = "", VoiceInfo[]? Voices = null,
    string[]? Languages = null, long AudioBytes = 0, int MaximumDimension = 0,
    long PeakWorkingSetBytes = 0, long PrivateBytes = 0, long PeakPrivateBytes = 0,
    long LoadWorkingSetBytes = 0, long LoadPrivateBytes = 0, double LoadMilliseconds = 0,
    double FirstAudioMilliseconds = 0, double SynthesisMilliseconds = 0, double AudioSeconds = 0,
    int Chunks = 0, int ProcessedCharacters = 0, long WorkingSetBytes = 0,
    bool CacheHit = false, int ProcessId = 0, int RetainedModels = 0,
    double ClientFirstAudioMilliseconds = 0, double ClientElapsedMilliseconds = 0,
    double PlaybackUnderrunMilliseconds = 0, double MaxPlaybackUnderrunMilliseconds = 0,
    byte[]? AudioData = null);

internal static class WorkerProtocol
{
    // A WAV response is base64 in JSON (about 4/3 of its size) plus a small envelope.
    internal const int MaximumWaveBytes = 6_000_000;
    private const int MaximumMessageBytes = 8_500_000;
    internal static async Task WriteAsync<T>(Stream stream, T message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        try
        {
            if (bytes.Length > MaximumMessageBytes) throw new QuietException(ErrorCode.TooLong);
            await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token);
            await stream.WriteAsync(bytes, token);
            await stream.FlushAsync(token);
        }
        finally { Array.Clear(bytes); }
    }
    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes, token);
        var length = BitConverter.ToInt32(lengthBytes);
        if (length is <= 0 or > MaximumMessageBytes) throw new QuietException(ErrorCode.WorkerFailure);
        var bytes = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(bytes, token);
            return JsonSerializer.Deserialize<T>(bytes) ?? throw new QuietException(ErrorCode.WorkerFailure);
        }
        finally { Array.Clear(bytes); }
    }
}
