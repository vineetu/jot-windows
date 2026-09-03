using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jot.Transcription;
using Jot.Transcription.Granite;
using Jot.Transcription.Onnx;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;
using Xunit.Abstractions;

namespace Jot.Tests;

/// <summary>
/// The Granite English engine: detokenization and CTC collapse as pure facts that always run, plus
/// the end-to-end transcript behind a real skip when the 536 MB checkpoint is absent.
/// </summary>
public class GraniteTranscriberTests(ITestOutputHelper output)
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "granite");

    private static string ProbeWav =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "probe.wav");

    private sealed record DecodeCase(int[] Ids, string Text);

    private static IEnumerable<DecodeCase> DecodeCases()
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(FixtureDir, "decode_cases.json")));
        foreach (JsonElement c in doc.RootElement.EnumerateArray())
        {
            yield return new DecodeCase(
                c.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).ToArray(),
                c.GetProperty("text").GetString()!);
        }
    }

    private static GraniteTokens Tokens()
    {
        // The shipping vocab lives next to the graph, so the pure-logic facts read it from the
        // staged model when present and fall back to the fixture-free path otherwise.
        GraniteModel? m = GraniteAssets.Model;
        Assert.NotNull(m);
        return GraniteTokens.Load(m!.Vocab);
    }

    // ------------------------------------------------------------------ detokenization

    [ModelFact("granite")]
    public void Decode_reproduces_the_reference_tokenizer_on_every_golden_case()
    {
        GraniteTokens tokens = Tokens();
        foreach (DecodeCase c in DecodeCases())
        {
            string got = tokens.Decode(c.Ids);
            output.WriteLine($"{c.Ids.Length,3} ids -> \"{got}\"");
            Assert.Equal(c.Text, got);
        }
    }

    [Fact]
    public void Decode_joins_bytes_across_pieces_before_interpreting_utf8()
    {
        // 'é' is two UTF-8 bytes (0xC3 0xA9) and BPE routinely splits them across pieces. Decoding
        // piece-by-piece yields two replacement characters; decoding the joined byte run yields 'é'.
        // In the byte-level alphabet 0xC3 and 0xA9 are 'Ã' and '©'.
        var tokens = GraniteTokens.FromPieces(["<|blank|>", "Ã", "©"]);
        Assert.Equal("é", tokens.Decode([1, 2]));
    }

    [Fact]
    public void Decode_maps_the_byte_level_space_marker_back_to_a_space()
    {
        // U+0120 is byte 0x20 in the GPT-2 alphabet.
        var tokens = GraniteTokens.FromPieces(["<|blank|>", "hi", "Ġthere"]);
        Assert.Equal("hi there", tokens.Decode([1, 2]));
    }

    [Fact]
    public void Decode_skips_the_blank_and_added_tokens_rather_than_spelling_them_out()
    {
        // Every character of "<|blank|>" is a legal byte-level character, so without the special
        // check the blank would decode to its own literal spelling inside the user's transcript.
        var tokens = GraniteTokens.FromPieces(["<|blank|>", "ok"]);
        Assert.Equal("ok", tokens.Decode([0, 1, 0]));
    }

    [Fact]
    public void Decode_ignores_ids_outside_the_vocabulary()
    {
        var tokens = GraniteTokens.FromPieces(["<|blank|>", "ok"]);
        Assert.Equal("ok", tokens.Decode([1, 99, -3]));
    }

    // ------------------------------------------------------------------ CTC collapse

    private static Tensor<float> LogitsFor(int vocab, params int[] argmaxPerFrame)
    {
        var t = new DenseTensor<float>([1, argmaxPerFrame.Length, vocab]);
        for (int f = 0; f < argmaxPerFrame.Length; f++) t[0, f, argmaxPerFrame[f]] = 1.0f;
        return t;
    }

    [Fact]
    public void CollapseGreedy_drops_repeats_and_blanks()
    {
        List<int> ids = GraniteTranscriber.CollapseGreedy(LogitsFor(8, 3, 3, 0, 0, 5, 5, 5, 0, 2));
        Assert.Equal([3, 5, 2], ids);
    }

    [Fact]
    public void CollapseGreedy_keeps_a_doubled_token_that_a_blank_separates()
    {
        // The whole reason repeat-collapse must run BEFORE the blank filter. Filtering blanks first
        // turns [7, blank, 7] into [7, 7] into [7] — silently dropping a letter from words like
        // "bookkeeper". This fact fails loudly if that order is ever swapped.
        List<int> ids = GraniteTranscriber.CollapseGreedy(LogitsFor(8, 7, 0, 7));
        Assert.Equal([7, 7], ids);
    }

    [Fact]
    public void CollapseGreedy_returns_nothing_for_all_blank_frames()
        => Assert.Empty(GraniteTranscriber.CollapseGreedy(LogitsFor(8, 0, 0, 0, 0)));

    // ------------------------------------------------------------------ end to end

    [ModelFact("granite")]
    public void Transcribes_probe_wav_to_the_reference_transcript()
    {
        GraniteModel model = GraniteAssets.Model!;
        using var engine = new GraniteTranscriber(model, new OnnxSessionFactory());
        Assert.True(engine.IsModelInstalled);

        float[] samples = WavAudio.ReadMono16k(ProbeWav);
        string text = engine.TranscribeAsync(samples, WavAudio.SampleRate).GetAwaiter().GetResult();
        output.WriteLine($"transcript: {text}");

        // Byte-exact against the Python reference RUN ON THE SAME int8 GRAPH. That distinction is
        // the point: on this clip fp32 decodes the proper noun as "nimotron" and the shipping int8
        // graph as "nemotron", so pinning to the fp32 text would fail a correct port. (Which of the
        // two is closer to the truth here is luck, not a quality claim — quantization quality is
        // measured by WER over a corpus, not by one word on one clip.)
        Assert.Equal("the quick brown fox jumps over the lazy dog nemotron streaming on the gpu", text);
    }

    [ModelFact("granite")]
    public void Output_is_lowercase_and_unpunctuated_so_the_punctuation_stage_is_required()
    {
        // Documents the contract PunctCapSeg exists to satisfy. If a future checkpoint starts
        // emitting casing, this fails and the punctuation stage needs re-evaluating rather than
        // silently double-casing the user's text.
        GraniteModel model = GraniteAssets.Model!;
        using var engine = new GraniteTranscriber(model, new OnnxSessionFactory());
        string text = engine.TranscribeAsync(WavAudio.ReadMono16k(ProbeWav), WavAudio.SampleRate)
                            .GetAwaiter().GetResult();

        Assert.DoesNotContain(text, c => char.IsUpper(c));
        Assert.DoesNotContain(text, c => ".,?!;:".Contains(c));
    }

    [ModelFact("granite")]
    public void Streaming_finish_equals_the_batch_transcript()
    {
        // The session revises rather than only grows, so the guarantee worth testing is not that
        // partials are monotonic — it is that Finish() lands on exactly the batch result no matter
        // how the audio was chunked or how many partial passes the throttle allowed.
        GraniteModel model = GraniteAssets.Model!;
        using var engine = new GraniteTranscriber(model, new OnnxSessionFactory());

        float[] samples = WavAudio.ReadMono16k(ProbeWav);
        string batch = engine.TranscribeAsync(samples, WavAudio.SampleRate).GetAwaiter().GetResult();

        GraniteTranscriber.Session session = engine.OpenStream();
        const int Chunk = 16_000 / 2;   // 500 ms, the cadence LiveTranscription feeds at
        for (int at = 0; at < samples.Length; at += Chunk)
            session.Accept(samples[at..Math.Min(at + Chunk, samples.Length)]);
        string streamed = session.Finish();

        output.WriteLine($"batch    : {batch}");
        output.WriteLine($"streamed : {streamed}");
        Assert.Equal(batch, streamed);
    }

    [ModelFact("granite")]
    public void Empty_and_sub_frame_audio_transcribe_to_nothing_without_throwing()
    {
        GraniteModel model = GraniteAssets.Model!;
        using var engine = new GraniteTranscriber(model, new OnnxSessionFactory());
        Assert.Equal("", engine.TranscribeAsync([], WavAudio.SampleRate).GetAwaiter().GetResult());
        Assert.Equal("", engine.TranscribeAsync(new float[80], WavAudio.SampleRate)
                              .GetAwaiter().GetResult());
    }

    // ------------------------------------------------------------------ the revision contract

    [ModelFact("granite")]
    public void The_streaming_session_declares_that_it_revises_text()
    {
        // Load-bearing, not cosmetic. The CLI's finals-only protocol commits partial text and cannot
        // retract it; on a revising engine that duplicates and drops words. StreamMode reads THIS
        // flag to decide whether to commit partials, so a session that lied here would silently
        // corrupt every English stream.
        GraniteModel model = GraniteAssets.Model!;
        using var engine = new GraniteTranscriber(model, new OnnxSessionFactory());
        Assert.True(engine.OpenStream().RevisesText);
    }

    [Fact]
    public void Append_only_is_the_interface_default_so_existing_engines_keep_their_guarantee()
    {
        // Through the interface: RevisesText is a default interface member, so an engine that never
        // mentions it — every engine that existed before Granite — keeps reporting append-only.
        IStreamingSession session = new AppendOnlySession();
        Assert.False(session.RevisesText);
    }

    private sealed class AppendOnlySession : IStreamingSession
    {
        public string Accept(float[] newSamples) => "";
        public string Finish() => "";
    }

    [Fact]
    public void Rejects_audio_that_is_not_16_kHz()
    {
        using var engine = new GraniteTranscriber(new GraniteModel(directory: "nonexistent"),
                                                  new OnnxSessionFactory());
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            engine.TranscribeAsync(new float[1000], 44_100).GetAwaiter().GetResult());
        Assert.Contains("16000", ex.Message);
    }

    [Fact]
    public void A_missing_model_reads_as_not_installed_rather_than_throwing()
    {
        var model = new GraniteModel(directory: Path.Combine(Path.GetTempPath(), "no-granite-here"));
        Assert.False(model.IsInstalled);
        using var engine = new GraniteTranscriber(model, new OnnxSessionFactory());
        Assert.False(engine.IsModelInstalled);
        engine.WarmUp();   // must be a no-op, not a crash
    }
}
