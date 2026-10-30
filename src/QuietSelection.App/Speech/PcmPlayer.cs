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
/// <see cref="PlayAsync"/> copies the chunk into a native buffer and returns while up to about
/// one second of audio is queued, so a streaming engine (Pocket sends 80 ms blocks) can run ahead.
/// The device starts once a short pre-roll is buffered; if it ever runs dry it pauses until the
/// pre-roll is rebuilt, turning many audible clicks into one short pause.
/// The caller may clear its array as soon as PlayAsync returns. Call <see cref="DrainAsync"/>
/// before disposing to let the queued audio finish.
/// </summary>
internal sealed class PcmPlayer : IDisposable
{
    // Seconds of audio. After each underrun the required buffer doubles (0.6, 1.2, 2.4, 3.0),
    // so an engine slightly slower than real time pauses a few times instead of clicking constantly.
    private const double PreRoll = 0.4, FirstRebuffer = 0.6, MaximumRebuffer = 3.0;
    private double rebufferPreRoll = FirstRebuffer;
    private double QueueAhead => Math.Max(1.0, rebufferPreRoll + 0.4);
    private const uint CallbackEvent = 0x00050000, HeaderDone = 1;
    private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));
    private readonly AutoResetEvent bufferDone = new(false);
    private readonly Queue<(nint Header, nint Data, int Length)> queued = new();
    private nint device;
    private int rate;
    private long queuedBytes;
    private bool running;      // Device output is not paused.
    private bool rebuffering;  // It ran dry once; wait for the longer pre-roll.
    internal int Underruns { get; private set; }
    private double QueuedSeconds => rate == 0 ? 0 : queuedBytes / (rate * 2.0);

    internal Task PlayAsync(PcmChunk chunk, CancellationToken token, Action? submitted = null) =>
        Task.Run(() => Play(chunk, token, submitted), CancellationToken.None);

    internal Task DrainAsync(CancellationToken token) => Task.Run(() => { Start(); WaitUntil(0, token); }, CancellationToken.None);

    private void Play(PcmChunk chunk, CancellationToken token, Action? submitted)
    {
        chunk.Validate(); token.ThrowIfCancellationRequested();
        if (device == 0 || rate != chunk.SampleRate)
        {
            Start(); WaitUntil(0, token); // Finish the previous rate before reopening the device.
            Close();
            var format = new WaveFormat { Tag = 1, Channels = 1, SampleRate = (uint)chunk.SampleRate,
                AverageBytes = (uint)chunk.SampleRate * 2, Alignment = 2, Bits = 16 };
            Check(waveOutOpen(out device, uint.MaxValue, ref format, (nuint)bufferDone.SafeWaitHandle.DangerousGetHandle(), 0, CallbackEvent));
            waveOutPause(device); running = false; rebuffering = false; // Output starts after the pre-roll.
            rate = chunk.SampleRate;
        }
        Reap();
        if (running && queued.Count == 0)
        {
            // The device played everything before this chunk arrived: an underrun.
            waveOutPause(device); running = false; Underruns++;
            if (rebuffering || Underruns > 1) rebufferPreRoll = Math.Min(MaximumRebuffer, rebufferPreRoll * 2);
            rebuffering = true;
        }
        nint data = Marshal.AllocHGlobal(chunk.Data.Length), header = Marshal.AllocHGlobal(HeaderSize);
        bool queuedHere = false;
        try
        {
            Marshal.Copy(chunk.Data, 0, data, chunk.Data.Length);
            Marshal.StructureToPtr(new WaveHeader { Data = data, Length = (uint)chunk.Data.Length }, header, false);
            Check(waveOutPrepareHeader(device, header, (uint)HeaderSize));
            queued.Enqueue((header, data, chunk.Data.Length)); queuedHere = true; queuedBytes += chunk.Data.Length;
            Check(waveOutWrite(device, header, (uint)HeaderSize));
            submitted?.Invoke();
            if (!running && QueuedSeconds >= (rebuffering ? rebufferPreRoll : PreRoll)) Start();
        }
        catch
        {
            if (!queuedHere) Free(header, data, chunk.Data.Length);
            throw;
        }
        if (QueuedSeconds > QueueAhead) { Start(); WaitUntil(QueueAhead, token); }
    }

    private void Start()
    {
        if (running || device == 0 || queued.Count == 0) return;
        waveOutRestart(device); running = true; rebuffering = false;
    }

    // Releases buffers the device has finished.
    private void Reap()
    {
        while (queued.TryPeek(out var item) && (Marshal.ReadInt32(item.Header, FlagsOffset) & HeaderDone) != 0)
        {
            queued.Dequeue(); queuedBytes -= item.Length;
            waveOutUnprepareHeader(device, item.Header, (uint)HeaderSize);
            Free(item.Header, item.Data, item.Length);
        }
    }

    /// <summary>Waits until at most <paramref name="seconds"/> of audio remain queued.</summary>
    private void WaitUntil(double seconds, CancellationToken token)
    {
        while (true)
        {
            Reap();
            if (queued.Count == 0 || QueuedSeconds <= seconds) return;
            if (token.IsCancellationRequested) { Stop(); token.ThrowIfCancellationRequested(); }
            // The event wakes us when a buffer finishes; the timeout covers a missed signal.
            WaitHandle.WaitAny([bufferDone, token.WaitHandle], 100);
        }
    }

    // Stops output at once and releases every queued buffer.
    private void Stop()
    {
        if (device != 0) waveOutReset(device);
        running = false; queuedBytes = 0;
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
    [DllImport("winmm.dll")] private static extern uint waveOutPause(nint device);
    [DllImport("winmm.dll")] private static extern uint waveOutRestart(nint device);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(nint device);
}
