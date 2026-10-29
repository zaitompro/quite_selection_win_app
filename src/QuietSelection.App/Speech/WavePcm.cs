using System.Buffers.Binary;

namespace QuietSelection.App.Speech;

internal static class WavePcm
{
    internal static IEnumerable<PcmChunk> Read(byte[] wav)
    {
        if (wav.Length < 44 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new QuietException(ErrorCode.SpeechFailure);
        int rate = 0, dataStart = 0, dataLength = 0;
        for (int offset = 12; offset <= wav.Length - 8;)
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(offset + 4, 4));
            if (length > wav.Length - offset - 8) throw new QuietException(ErrorCode.SpeechFailure);
            int size = (int)length, body = offset + 8;
            if (wav.AsSpan(offset, 4).SequenceEqual("fmt "u8))
            {
                if (size < 16 || BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body)) != 1
                    || BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 2)) != 1
                    || BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 14)) != 16)
                    throw new QuietException(ErrorCode.SpeechFailure);
                rate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(body + 4)));
            }
            if (wav.AsSpan(offset, 4).SequenceEqual("data"u8)) { dataStart = body; dataLength = size; }
            offset = body + size + (size & 1);
        }
        if (dataLength <= 0 || dataLength % 2 != 0 || rate is < 8000 or > 96000)
            throw new QuietException(ErrorCode.SpeechFailure);
        for (int offset = 0; offset < dataLength; offset += 262144)
            yield return new(wav.AsSpan(dataStart + offset, Math.Min(262144, dataLength - offset)).ToArray(), rate);
    }
}
