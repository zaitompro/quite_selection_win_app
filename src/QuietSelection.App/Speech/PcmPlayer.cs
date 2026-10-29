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

/// <summary>One device owner for the reading operation. Audio never leaves memory.</summary>
internal sealed class PcmPlayer : IDisposable
{
    private nint device;
    private int rate;
    internal Task PlayAsync(PcmChunk chunk, CancellationToken token, Action? submitted = null) => Task.Run(() => Play(chunk, token, submitted), CancellationToken.None);

    private void Play(PcmChunk chunk, CancellationToken token, Action? submitted)
    {
        chunk.Validate(); token.ThrowIfCancellationRequested();
        if (device == 0 || rate != chunk.SampleRate)
        {
            Dispose();
            var format = new WaveFormat { Tag = 1, Channels = 1, SampleRate = (uint)chunk.SampleRate,
                AverageBytes = (uint)chunk.SampleRate * 2, Alignment = 2, Bits = 16 };
            Check(waveOutOpen(out device, uint.MaxValue, ref format, 0, 0, 0));
            rate = chunk.SampleRate;
        }
        var pinned = GCHandle.Alloc(chunk.Data, GCHandleType.Pinned);
        int size = Marshal.SizeOf<WaveHeader>();
        nint header = Marshal.AllocHGlobal(size);
        bool prepared = false;
        try
        {
            Marshal.StructureToPtr(new WaveHeader { Data = pinned.AddrOfPinnedObject(), Length = (uint)chunk.Data.Length }, header, false);
            Check(waveOutPrepareHeader(device, header, (uint)size)); prepared = true;
            token.ThrowIfCancellationRequested();
            Check(waveOutWrite(device, header, (uint)size));
            submitted?.Invoke();
            int flags = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));
            while ((Marshal.ReadInt32(header, flags) & 1) == 0)
            {
                token.ThrowIfCancellationRequested();
                Thread.Sleep(5);
            }
        }
        finally
        {
            if (prepared)
            {
                waveOutReset(device);
                waveOutUnprepareHeader(device, header, (uint)size);
            }
            Marshal.FreeHGlobal(header);
            pinned.Free();
        }
    }

    public void Dispose()
    {
        if (device != 0) { waveOutReset(device); waveOutClose(device); device = 0; }
    }
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
