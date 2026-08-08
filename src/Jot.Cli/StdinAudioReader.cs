using System.Buffers.Binary;

namespace Jot.Cli;

internal enum PcmEncoding
{
    S16le,
    F32le,
}

/// <summary>A malformed input the caller must report and exit 1 on. Carried as an exception because the
/// decoder is pure — it never writes to stderr and never exits.</summary>
internal sealed class PcmDecodeException(string message) : Exception(message);

/// <summary>
/// Incremental PCM decoder for the stream-mode stdin contract: raw 16 kHz mono PCM, s16le or f32le, with
/// a leading RIFF/WAV header autodetected and skipped. <c>--encoding</c>, NOT the WAV <c>fmt</c> chunk,
/// governs decoding.
///
/// Bytes in, samples out — the actual stdin handle stays in the run loop so the header sniff is testable
/// without a real pipe. A trailing partial frame stays buffered for the next feed and is dropped at EOF.
/// </summary>
internal sealed class StdinAudioReader(PcmEncoding encoding)
{
    /// <summary>A WAV whose chunk list never yields a data chunk would otherwise buffer forever.</summary>
    private const int NoDataChunkBound = 1_048_576;

    private static readonly byte[] Riff = "RIFF"u8.ToArray();
    private static readonly byte[] Wave = "WAVE"u8.ToArray();
    private static readonly byte[] DataId = "data"u8.ToArray();

    private byte[] _pending = new byte[64 * 1024];
    private int _length;
    private bool _headerHandled;

    public static bool TryParseEncoding(string raw, out PcmEncoding parsed)
    {
        switch (raw)
        {
            case "s16le": parsed = PcmEncoding.S16le; return true;
            case "f32le": parsed = PcmEncoding.F32le; return true;
            default: parsed = PcmEncoding.S16le; return false;
        }
    }

    public int BytesPerFrame => encoding == PcmEncoding.S16le ? 2 : 4;

    /// <summary>Feeds <paramref name="count"/> bytes read from the producer; returns whatever complete
    /// frames that made available (often none while the header is still arriving).</summary>
    public float[] Feed(byte[] data, int count)
    {
        Append(data, count);

        if (!_headerHandled)
        {
            // The 12-byte RIFF/WAVE preamble is the minimum needed to sniff.
            if (_length < 12) return [];
            if (Matches(0, Riff))
            {
                // RIFF is a container family — WebP and AVI are RIFF too. Decoding container bytes as
                // PCM would be the forbidden silently-deaf outcome, so only WAVE gets the chunk walk.
                if (!Matches(8, Wave))
                {
                    throw new PcmDecodeException(
                        "input is a RIFF container but not WAV — pipe raw PCM or a WAV file");
                }
                int? offset = DataOffset();
                if (offset is null)
                {
                    if (_length > NoDataChunkBound)
                        throw new PcmDecodeException("WAV header with no data chunk in first 1 MiB");
                    return [];
                }
                Consume(offset.Value);
            }
            _headerHandled = true;
        }

        return ConsumeFrames();
    }

    /// <summary>EOF. Two unfinished-header cases: a stream that started with RIFF but never reached its
    /// data chunk is a truncated WAV and fails loudly; under 12 bytes of raw PCM is flushed as frames
    /// (an earlier gate on "header handled" dropped both silently with exit 0).</summary>
    public float[] Flush()
    {
        if (!_headerHandled && Matches(0, Riff))
            throw new PcmDecodeException("truncated WAV (EOF before the data chunk)");
        return ConsumeFrames();
    }

    private float[] ConsumeFrames()
    {
        int stride = BytesPerFrame;
        int frames = _length / stride;
        if (frames == 0) return [];

        var samples = new float[frames];
        ReadOnlySpan<byte> src = _pending.AsSpan(0, frames * stride);
        if (encoding == PcmEncoding.S16le)
        {
            for (int i = 0; i < frames; i++)
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(src.Slice(i * 2, 2)) / 32768f;
        }
        else
        {
            for (int i = 0; i < frames; i++)
                samples[i] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(i * 4, 4));
        }
        Consume(frames * stride);
        return samples;
    }

    /// <summary>Offset of the first audio byte (past the data chunk header), or null while the chunk
    /// list is still incomplete.</summary>
    private int? DataOffset()
    {
        long offset = 12; // "RIFF" + size + "WAVE"
        while (offset + 8 <= _length)
        {
            if (Matches((int)offset, DataId)) return (int)offset + 8;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(_pending.AsSpan((int)offset + 4, 4));
            // Chunks are word-aligned: an odd size carries a pad byte.
            offset += 8L + size + (size & 1);
            // A bogus size that overshoots the buffer is indistinguishable from "not buffered yet"; the
            // 1 MiB bound is what ends it.
            if (offset > int.MaxValue) return null;
        }
        return null;
    }

    private bool Matches(int offset, byte[] token) =>
        _length >= offset + token.Length && _pending.AsSpan(offset, token.Length).SequenceEqual(token);

    private void Append(byte[] data, int count)
    {
        if (count <= 0) return;
        if (_length + count > _pending.Length)
            Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _length + count));
        Buffer.BlockCopy(data, 0, _pending, _length, count);
        _length += count;
    }

    private void Consume(int count)
    {
        if (count <= 0) return;
        _length -= count;
        if (_length > 0) Buffer.BlockCopy(_pending, count, _pending, 0, _length);
    }
}
