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

    [Fact]
    public void StripForModel_removes_sentence_marks_but_keeps_what_cannot_be_rebuilt()
    {
        // The model adds its own marks on top of any it is given -- iOS measured 6.8 -> 27.1 marks
        // per 100 words without this. Granite punctuates ~4% of clips, so it is a live case.
        Assert.Equal("hello world how are you", PunctCapSeg.StripForModel("Hello, world. How are you?"));

        // Apostrophes stay: the label set has none, so a contraction stripped here is gone forever.
        Assert.Equal("it's what i'd do", PunctCapSeg.StripForModel("It's what I'd do."));

        // Dots and commas INSIDE a token are not sentence marks. iOS guards digits only and
        // measured 17/420 transcripts corrupted without it; this also saves domains and versions.
        Assert.Equal("pi is 3.14 and 5,000 more", PunctCapSeg.StripForModel("Pi is 3.14 and 5,000 more."));
        Assert.Equal("see example.com or 1.0.3", PunctCapSeg.StripForModel("See example.com or 1.0.3."));
    }

    [ModelFact("punct")]
    public void Characters_the_vocabulary_cannot_spell_survive_verbatim()
    {
        // Reconstruction emits the vocabulary's piece for every id, so an id the vocabulary cannot
        // spell used to print as the literal text "<unk>". MEASURED on real dictation before the
        // fix: Granite heard "a spike of 75%" and the user was handed "a spike of 75<unk>".
        //
        // spe_32k_lc_en has no '%', no curly quote or apostrophe, no en dash, ellipsis, degree
        // sign, '@', '#', '=', '/', '~' and no emoji -- and, being LOWERCASE-only, no capital
        // letter either. Restoring punctuation must never be able to delete the text it punctuates.
        using var stage = new PunctCapSeg(PunctAssets.Model!, new OnnxSessionFactory());
        foreach (string fragment in new[]
                 { "75%", "25°", "a–b", "x=y", "50/50", "user@example.com", "#tag", "½" })
        {
            string got = stage.Apply("i saw " + fragment + " today");
            Assert.DoesNotContain("<unk>", got);
            // Case-insensitive: capitalising a character the model CAN read is its job, and it does
            // reasonably choose "X=y" here. The invariant under test is that nothing is DELETED.
            Assert.Contains(fragment, got, StringComparison.OrdinalIgnoreCase);
        }
    }

    [ModelFact("punct")]
    public void Uppercase_input_is_not_destroyed_by_the_lowercase_vocabulary()
    {
        // Every capital encodes to <unk> in a lowercase-only vocabulary, so text that arrives
        // already cased used to lose whole words ("Zurich" -> "<unk>urich"). The stage lowercases
        // first and lets the model predict casing, which is what it is for.
        using var stage = new PunctCapSeg(PunctAssets.Model!, new OnnxSessionFactory());
        string got = stage.Apply("I flew to Zurich on Monday");
        Assert.DoesNotContain("<unk>", got);
        Assert.Contains("urich", got);
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
