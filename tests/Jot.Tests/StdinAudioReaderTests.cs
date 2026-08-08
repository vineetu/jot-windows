using System.IO;
using Jot.Cli;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// The stdin contract has to survive a producer that hands over bytes in arbitrary slices, so every rule
/// here is about partial arrival: a header split across reads, a frame split across reads, and the two
/// EOF cases an earlier "did we handle a header" gate swallowed silently (truncated WAV, sub-12-byte raw
/// PCM). Feeding a wrong container as PCM would be the forbidden silently-deaf outcome, so RIFF-not-WAVE
/// is a hard failure.
/// </summary>
public class StdinAudioReaderTests
{
    private static byte[] S16(params short[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(bytes, i * 2);
        return bytes;
    }

    private static byte[] F32(params float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(bytes, i * 4);
        return bytes;
    }

    private static byte[] Wav(byte[] payload, params (string Id, int Size)[] extraChunks)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(0);                       // the walk never reads the RIFF size
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);                // PCM
        w.Write((short)1);                // mono
        w.Write(16_000);
        w.Write(32_000);
        w.Write((short)2);
        w.Write((short)16);
        foreach ((string id, int size) in extraChunks)
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes(id));
            w.Write(size);
            w.Write(new byte[size]);
            if ((size & 1) == 1) w.Write((byte)0);   // word-alignment pad
        }
        w.Write("data"u8.ToArray());
        w.Write(payload.Length);
        w.Write(payload);
        return ms.ToArray();
    }

    private static float[] Feed(StdinAudioReader reader, byte[] bytes) => reader.Feed(bytes, bytes.Length);

    [Fact]
    public void RawS16leDecodesToNormalizedFloats()
    {
        var reader = new StdinAudioReader(PcmEncoding.S16le);
        float[] samples = Feed(reader, S16(0, 16384, -32768, 32767, 1, -1));
        Assert.Equal([0f, 0.5f, -1f, 32767f / 32768f, 1f / 32768f, -1f / 32768f], samples);
    }

    [Fact]
    public void RawF32lePassesSamplesThrough()
    {
        var reader = new StdinAudioReader(PcmEncoding.F32le);
        Assert.Equal([0.25f, -0.5f, 1f], Feed(reader, F32(0.25f, -0.5f, 1f)));
    }

    [Fact]
    public void WavHeaderIsSkipped()
    {
        var reader = new StdinAudioReader(PcmEncoding.S16le);
        Assert.Equal([0.5f, -1f], Feed(reader, Wav(S16(16384, -32768))));
    }

    [Fact]
    public void WavHeaderSplitAcrossTwoFeedsIsStillSkipped()
    {
        byte[] file = Wav(S16(16384, -32768));
        var reader = new StdinAudioReader(PcmEncoding.S16le);

        Assert.Empty(reader.Feed(file, 20));   // mid-header: nothing decodable yet
        Assert.Equal([0.5f, -1f], reader.Feed(file[20..], file.Length - 20));
    }

    [Fact]
    public void WavHeaderSplitInsideTheTwelveBytePreambleIsStillSkipped()
    {
        byte[] file = Wav(S16(16384));
        var reader = new StdinAudioReader(PcmEncoding.S16le);

        Assert.Empty(reader.Feed(file, 5));
        Assert.Equal([0.5f], reader.Feed(file[5..], file.Length - 5));
    }

    [Fact]
    public void OddSizedChunksCarryAPadByte()
    {
        // Without the word-alignment pad the walk lands one byte short and never finds "data".
        var reader = new StdinAudioReader(PcmEncoding.S16le);
        Assert.Equal([0.5f, -1f], Feed(reader, Wav(S16(16384, -32768), ("LIST", 3))));
    }

    [Fact]
    public void RiffThatIsNotWaveFailsLoudly()
    {
        var reader = new StdinAudioReader(PcmEncoding.S16le);
        byte[] webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, .. "VP8 "u8];

        var ex = Assert.Throws<PcmDecodeException>(() => { Feed(reader, webp); });
        Assert.Equal("input is a RIFF container but not WAV — pipe raw PCM or a WAV file", ex.Message);
    }

    [Fact]
    public void WavThatEndsBeforeItsDataChunkFailsLoudly()
    {
        var reader = new StdinAudioReader(PcmEncoding.S16le);
        byte[] file = Wav(S16(16384));
        Assert.Empty(reader.Feed(file, 24));   // "RIFF…WAVEfmt " and half the fmt body

        var ex = Assert.Throws<PcmDecodeException>(() => { reader.Flush(); });
        Assert.Equal("truncated WAV (EOF before the data chunk)", ex.Message);
    }

    [Fact]
    public void WavWithNoDataChunkIsBoundedAtOneMiB()
    {
        var reader = new StdinAudioReader(PcmEncoding.S16le);
        // A chunk that claims far more bytes than will ever arrive: the walk can never advance.
        byte[] header = [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, .. "JUNK"u8, 0x80, 0x96, 0x98, 0x00];
        Assert.Empty(Feed(reader, header));

        var ex = Assert.Throws<PcmDecodeException>(() => { Feed(reader, new byte[1_048_577]); });
        Assert.Equal("WAV header with no data chunk in first 1 MiB", ex.Message);
    }

    [Fact]
    public void RawPcmShorterThanTheSniffWindowIsFlushedAtEof()
    {
        // Under 12 bytes the header sniff never completes; the bytes are still audio.
        var reader = new StdinAudioReader(PcmEncoding.S16le);
        Assert.Empty(Feed(reader, S16(16384, -32768)));
        Assert.Equal([0.5f, -1f], reader.Flush());
    }

    [Fact]
    public void TrailingPartialFrameIsHeldThenDropped()
    {
        var reader = new StdinAudioReader(PcmEncoding.S16le);
        byte[] oddTail = [.. S16(0, 16384, -32768, 32767, 1, -1), 0x7f];

        Assert.Equal(6, Feed(reader, oddTail).Length);
        Assert.Empty(reader.Flush());
    }

    [Fact]
    public void FramesSplitAcrossFeedsRejoin()
    {
        var reader = new StdinAudioReader(PcmEncoding.S16le);
        byte[] first = [.. S16(0, 16384, -32768, 32767, 1, -1), 0x00];   // 13 bytes: 6 frames + half of one
        Assert.Equal(6, Feed(reader, first).Length);
        Assert.Equal([0.5f], Feed(reader, [0x40]));
    }

    [Fact]
    public void EncodingParsingAcceptsOnlyTheTwoSupportedForms()
    {
        Assert.True(StdinAudioReader.TryParseEncoding("s16le", out PcmEncoding s16));
        Assert.Equal(PcmEncoding.S16le, s16);
        Assert.True(StdinAudioReader.TryParseEncoding("f32le", out PcmEncoding f32));
        Assert.Equal(PcmEncoding.F32le, f32);
        Assert.False(StdinAudioReader.TryParseEncoding("mp3", out _));
        Assert.False(StdinAudioReader.TryParseEncoding("S16LE", out _));
    }
}
