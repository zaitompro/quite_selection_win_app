using System.Runtime.InteropServices;

namespace QuietSelection.App.Speech;

internal sealed record PcmChunk(byte[] Data, int SampleRate)
{
    internal void Validate()
    {
        if (Data.Length is <= 0 or > 262144 || Data.Length % 2 != 0 || SampleRate is < 8000 or > 96000)
            throw new QuietException(ErrorCode.SpeechFailure);
    }
}

/// <summary>
/// One device owner for the reading operation. Audio never leaves memory.
/// <see cref="PlayAsync"/> copies the chunk into a native buffer and returns once the previous
/// buffer has finished, so the next chunk is already queued and playback has no gap.
/// The caller may clear its array as soon as PlayAsync returns. Call <see cref="DrainAsync"/>
/// before disposing to let the last buffer finish.
/// </summary>
internal sealed class PcmPlayer : IDisposable
{
    private const int QueuedBuffers = 2;
    private const uint CallbackEvent = 0x00050000, HeaderDone = 1;
    private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));
    private readonly AutoResetEvent bufferDone = new(false);
    private readonly Queue<(nint Header, nint Data, int Length)> queued = new();
    private nint device;
    private int rate;

    internal Task PlayAsync(PcmChunk chunk, CancellationToken token, Action? submitted = null) =>
        Task.Run(() => Play(chunk, token, submitted), CancellationToken.None);

    internal Task DrainAsync(CancellationToken token) => Task.Run(() => WaitUntil(0, token), CancellationToken.None);

    private void Play(PcmChunk chunk, CancellationToken token, Action? submitted)
    {
        chunk.Validate(); token.ThrowIfCancellationRequested();
        if (device == 0 || rate != chunk.SampleRate)
        {
            WaitUntil(0, token); // Finish the previous rate before reopening the device.
            Close();
            var format = new WaveFormat { Tag = 1, Channels = 1, SampleRate = (uint)chunk.SampleRate,
                AverageBytes = (uint)chunk.SampleRate * 2, Alignment = 2, Bits = 16 };
            Check(waveOutOpen(out device, uint.MaxValue, ref format, (nuint)bufferDone.SafeWaitHandle.DangerousGetHandle(), 0, CallbackEvent));
            rate = chunk.SampleRate;
        }
        nint data = Marshal.AllocHGlobal(chunk.Data.Length), header = Marshal.AllocHGlobal(HeaderSize);
        bool queuedHere = false;
        try
        {
            Marshal.Copy(chunk.Data, 0, data, chunk.Data.Length);
            Marshal.StructureToPtr(new WaveHeader { Data = data, Length = (uint)chunk.Data.Length }, header, false);
            Check(waveOutPrepareHeader(device, header, (uint)HeaderSize));
            queued.Enqueue((header, data, chunk.Data.Length)); queuedHere = true;
            Check(waveOutWrite(device, header, (uint)HeaderSize));
            submitted?.Invoke();
        }
        catch
        {
            if (!queuedHere) Free(header, data, chunk.Data.Length);
            throw;
        }
        WaitUntil(QueuedBuffers - 1, token);
    }

    /// <summary>Releases finished buffers until at most <paramref name="pending"/> remain queued.</summary>
    private void WaitUntil(int pending, CancellationToken token)
    {
        while (true)
        {
            while (queued.TryPeek(out var item) && (Marshal.ReadInt32(item.Header, FlagsOffset) & HeaderDone) != 0)
            {
                queued.Dequeue();
                waveOutUnprepareHeader(device, item.Header, (uint)HeaderSize);
                Free(item.Header, item.Data, item.Length);
            }
            if (queued.Count <= pending) return;
            if (token.IsCancellationRequested) { Stop(); token.ThrowIfCancellationRequested(); }
            // The event wakes us when a buffer finishes; the timeout covers a missed signal.
            WaitHandle.WaitAny([bufferDone, token.WaitHandle], 100);
        }
    }

    // Stops output at once and releases every queued buffer.
    private void Stop()
    {
        if (device != 0) waveOutReset(device);
        while (queued.TryDequeue(out var item))
        {
            if (device != 0) waveOutUnprepareHeader(device, item.Header, (uint)HeaderSize);
            Free(item.Header, item.Data, item.Length);
        }
    }

    private void Close()
    {
        Stop();
        if (device != 0) { waveOutClose(device); device = 0; }
    }

    private static void Free(nint header, nint data, int length)
    {
        unsafe { new Span<byte>((void*)data, length).Clear(); } // No audio remains in freed memory.
        Marshal.FreeHGlobal(data); Marshal.FreeHGlobal(header);
    }

    public void Dispose() { Close(); bufferDone.Dispose(); }

    private static void Check(uint result) { if (result != 0) throw new QuietException(ErrorCode.SpeechFailure); }
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat { internal ushort Tag, Channels; internal uint SampleRate, AverageBytes; internal ushort Alignment, Bits, ExtraSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader { internal nint Data; internal uint Length, Recorded; internal nuint User; internal uint Flags, Loops; internal nint Next; internal nuint Reserved; }
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out nint device, uint id, ref WaveFormat format, nuint callback, nuint instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(nint device);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(nint device);
}
