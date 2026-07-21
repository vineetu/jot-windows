using Jot.Text;
using Xunit;

namespace Jot.Tests;

public sealed class TextPipelineTests
{
    // A non-{en,es,de,fr,it,pt} language with no <unk> must pass through byte-identical — even with the
    // Nemotron scrubber enabled (isNemotron:true exercises the scrubber's rule-0 fast path, the production path).
    [Theory]
    [InlineData("مرحبا بالعالم", "Arabic")]
    [InlineData("こんにちは 世界", "Japanese")]
    [InlineData("안녕하세요  두  칸", "Korean")]
    [InlineData("um hello world", "None")]      // unmapped name → "" iso → filler never runs
    public void NoOp_language_without_unk_is_byte_identical(string text, string lang) =>
        Assert.Equal(text, TextPipeline.Clean(text, lang, isNemotron: true));

    // The one intended exception: the <unk> scrubber is language-agnostic, so a genuine artifact is fixed
    // even for a no-op language.
    [Theory]
    [InlineData("25<unk>", "Arabic", "25%")]
    [InlineData("hola<unk>mundo", "Korean", "hola mundo")]
    public void NoOp_language_still_fixes_unk(string text, string lang, string expected) =>
        Assert.Equal(expected, TextPipeline.Clean(text, lang, isNemotron: true));

    [Fact]
    public void English_all_filler_collapses_to_empty()
    {
        // Cleanup runs before the recorder's whitespace gate, so all-filler → "" → NothingTranscribed.
        Assert.Equal("", TextPipeline.Clean("Um. Uh.", "English", isNemotron: true));
    }

    [Fact]
    public void English_filler_and_unk_both_handled()
    {
        // scrubber "<unk>" → space, then filler strips "um", then (stub) number pass is a no-op.
        Assert.Equal("Hello world ", TextPipeline.Clean("um hello <unk> world", "English", isNemotron: true));
    }

    [Fact]
    public void Idempotent_over_a_mixed_corpus()
    {
        (string text, string lang)[] corpus =
        [
            ("um hello world", "English"),
            ("Ähm, hallo", "German"),
            ("¿Verdad, eh?", "Spanish"),
            ("25<unk> off", "English"),
            ("こんにちは 世界", "Japanese"),
            ("Hello.\n\num world.", "English"),
        ];
        foreach ((string text, string lang) in corpus)
        {
            string once = TextPipeline.Clean(text, lang, isNemotron: true);
            string twice = TextPipeline.Clean(once, lang, isNemotron: true);
            Assert.Equal(once, twice);
        }
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.Equal("", TextPipeline.Clean("", "English", isNemotron: true));
    }

    // End-to-end English: scrubber → filler(+recap) → NumberNormalizer, all in order, one trailing space.
    [Theory]
    [InlineData("I read fifteen pages", "I read 15 pages ")]
    [InlineData("fifty dollars", "$50 ")]
    [InlineData("um twenty-five percent", "25% ")]           // filler stripped + recap + percent
    [InlineData("in twenty twenty-six", "in 2026 ")]
    public void English_number_normalization_runs_after_filler(string input, string expected) =>
        Assert.Equal(expected, TextPipeline.Clean(input, "English", isNemotron: true));

    [Fact]
    public void Numbers_are_not_touched_for_non_english_languages()
    {
        // The English-only gate: Spanish gets filler cleaning but NOT number normalization.
        Assert.Equal("quince páginas ", TextPipeline.Clean("quince páginas", "Spanish", isNemotron: true));
    }
}
