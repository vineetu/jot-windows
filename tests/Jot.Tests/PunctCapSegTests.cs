using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jot.Text;
using Jot.Transcription.Onnx;
using Xunit;
using Xunit.Abstractions;

namespace Jot.Tests;

/// <summary>
/// The punctuation / true-casing / segmentation stage, checked against the Python
/// <c>punctuators</c> reference on the same inputs.
///
/// The port is split deliberately — the library encodes, and <c>SentencePieceVocab</c> supplies the
/// id->piece table — so both halves are pinned separately here. If the encoder ever drifts, the
/// ids fact fails; if the proto reader drifts, the pieces fact fails; and if the reconstruction
/// logic drifts, the sentences fact fails. One combined test would leave you guessing which.
/// </summary>
public class PunctCapSegTests(ITestOutputHelper output)
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "granite");

    private sealed record PunctCase(string Text, int[] Ids, string[] Pieces, string[] Sentences);

    private static List<PunctCase> Cases()
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(FixtureDir, "punct_cases.json")));
        var cases = new List<PunctCase>();
        foreach (JsonElement c in doc.RootElement.EnumerateArray())
        {
            cases.Add(new PunctCase(
                c.GetProperty("text").GetString()!,
                c.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).ToArray(),
                c.GetProperty("pieces").EnumerateArray().Select(e => e.GetString()!).ToArray(),
                c.GetProperty("sentences").EnumerateArray().Select(e => e.GetString()!).ToArray()));
        }
        return cases;
    }

    // ------------------------------------------------------------------ the id->piece table

    [ModelFact("punct")]
    public void Vocab_reads_the_same_pieces_the_reference_tokenizer_reports()
    {
        var vocab = SentencePieceVocab.Load(PunctAssets.Model!.Tokenizer);
        output.WriteLine($"vocab size: {vocab.Count}");
        Assert.Equal(32_000, vocab.Count);

        // The four reserved ids, which also prove the table is not shifted by one.
        Assert.Equal("<unk>", vocab.Piece(0));
        Assert.Equal("<s>", vocab.Piece(1));
        Assert.Equal("</s>", vocab.Piece(2));
        Assert.Equal("<pad>", vocab.Piece(3));

        foreach (PunctCase c in Cases())
        {
            for (int i = 0; i < c.Ids.Length; i++)
                Assert.Equal(c.Pieces[i], vocab.Piece(c.Ids[i]));
        }
    }

    [ModelFact("punct")]
    public void Vocab_returns_empty_for_ids_outside_the_table()
    {
        var vocab = SentencePieceVocab.Load(PunctAssets.Model!.Tokenizer);
        Assert.Equal("", vocab.Piece(-1));
        Assert.Equal("", vocab.Piece(vocab.Count));
        Assert.Equal("", vocab.Piece(int.MaxValue));
    }

    // ------------------------------------------------------------------ end to end

    [ModelFact("punct")]
    public void Restores_the_same_sentences_as_the_python_reference()
    {
        using var stage = new PunctCapSeg(PunctAssets.Model!, new OnnxSessionFactory());
        foreach (PunctCase c in Cases())
        {
            IReadOnlyList<string> got = stage.Segment(c.Text);
            output.WriteLine($"IN : {c.Text}");
            output.WriteLine($"OUT: {string.Join(" | ", got)}");
            Assert.Equal(c.Sentences, got);
        }
    }

    [ModelFact("punct")]
    public void Apply_joins_the_sentences_into_one_string()
    {
        using var stage = new PunctCapSeg(PunctAssets.Model!, new OnnxSessionFactory());
        Assert.Equal("Hello, world.", stage.Apply("hello world"));
        Assert.Equal("What time is it?", stage.Apply("what time is it"));
    }

    [ModelFact("punct")]
    public void Capitalization_is_per_character_not_per_token()
    {
        // The whole reason reconstruction walks characters. A per-token implementation produces
        // "Gpu" and "Nasa"; this asserts the acronyms survive intact.
        using var stage = new PunctCapSeg(PunctAssets.Model!, new OnnxSessionFactory());
        Assert.Contains("GPU", stage.Apply("the quick brown fox jumps over the lazy dog " +
                                           "nemotron streaming on the gpu"));
        Assert.Contains("NASA", stage.Apply("meet me at the nasa hq tomorrow"));
    }

    [ModelFact("punct")]
    public void Long_input_is_windowed_and_still_produces_continuous_text()
    {
        // Past the 256-token limit the input is split, so this guards the seam: every word must
        // survive exactly once, with no duplication from the overlap and no gap from the trim.
        using var stage = new PunctCapSeg(PunctAssets.Model!, new OnnxSessionFactory());

        const string Sentence = "the meeting is scheduled for tomorrow morning at nine ";
        string longText = string.Concat(Enumerable.Repeat(Sentence, 40)).Trim();
        int wordsIn = longText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        string got = stage.Apply(longText);
        int wordsOut = got.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        output.WriteLine($"words in {wordsIn}, out {wordsOut}, {got.Length} chars");

        Assert.Equal(wordsIn, wordsOut);
        Assert.Contains("meeting", got);
    }

    // ------------------------------------------------------------------ degenerate input

    [ModelFact("punct")]
    public void Blank_input_comes_back_untouched()
    {
        using var stage = new PunctCapSeg(PunctAssets.Model!, new OnnxSessionFactory());
        Assert.Equal("", stage.Apply(""));
        Assert.Equal("   ", stage.Apply("   "));
        Assert.Empty(stage.Segment(""));
    }

    [Fact]
    public void A_missing_model_reads_as_not_installed_rather_than_throwing()
    {
        var model = new PunctCapSegModel(
            directory: Path.Combine(Path.GetTempPath(), "no-punct-here"));
        Assert.False(model.IsInstalled);
        using var stage = new PunctCapSeg(model, new OnnxSessionFactory());
        Assert.False(stage.IsModelInstalled);
    }
}
