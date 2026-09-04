using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Jot.Text;
using Jot.Transcription.Onnx;
using Jot.Transcription;
using Jot.Transcription.Granite;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// Routing rules for restoring English punctuation over the MULTILINGUAL engine — the shape a user
/// gets with the punctuation model downloaded and Granite not.
///
/// These run with no models on disk: the decorator's behaviour is decided by its inputs, and the
/// cases that matter (a shared engine being disposed twice, a non-English utterance reaching an
/// English-only model, the toggle silently doing nothing) are all reachable without one.
/// </summary>
public class EnglishPunctuationRoutingTests
{
    /// <summary>A real stage over a directory with no model in it — the decorator must behave
    /// correctly against the genuine object, not a null that happens to short-circuit.</summary>
    private static PunctCapSeg Uninstalled() =>
        new(new PunctCapSegModel(directory: Path.Combine(Path.GetTempPath(), "jot-no-punct-model")),
            new OnnxSessionFactory());

    private sealed class FakeEngine : ITranscriber, IStreamingTranscriber, IDisposable
    {
        public int Disposals;
        public string Text = "hello world";
        public bool IsModelInstalled => true;
        public Task<string> TranscribeAsync(float[] s, int r, CancellationToken ct = default)
            => Task.FromResult(Text);
        public IStreamingSession OpenStream() => new Session(this);
        public void Dispose() => Disposals++;

        private sealed class Session(FakeEngine owner) : IStreamingSession
        {
            public string Accept(float[] samples) => owner.Text;
            public string Finish() => owner.Text;
        }
    }

    /// <summary>
    /// The ggml engine is BOTH the multilingual route and the thing punctuated for English, so the
    /// router holds it twice. Disposing it through the decorator too would tear down an engine the
    /// other route is still using — a crash on the next non-English dictation.
    /// </summary>
    [Fact]
    public void The_shared_engine_is_disposed_exactly_once()
    {
        var engine = new FakeEngine();
        var english = new PunctuatingTranscriber(engine, punctuation: Uninstalled(),
                                                 enabled: () => false, ownsInner: false);
        using (var routed = new LanguageRoutedTranscriber(english, engine)) { }
        Assert.Equal(1, engine.Disposals);
    }

    /// <summary>Off must be a true pass-through, not a re-punctuation that happens to agree.</summary>
    [Fact]
    public async Task Disabled_returns_the_engine_text_untouched()
    {
        var engine = new FakeEngine { Text = "Okay, this is Nemotron's own punctuation." };
        var t = new PunctuatingTranscriber(engine, punctuation: Uninstalled(),
                                           enabled: () => false, ownsInner: false);
        Assert.Equal(engine.Text, await t.TranscribeAsync([], 16_000));
    }

    /// <summary>
    /// A disabled decorator must NOT claim to revise: the CLI's finals-only protocol stops
    /// committing partials when it sees that flag, so a pass-through claiming revision would cost
    /// ggml its incremental finals for nothing.
    /// </summary>
    [Fact]
    public void Disabled_does_not_claim_to_revise_text()
    {
        var engine = new FakeEngine();
        var t = new PunctuatingTranscriber(engine, punctuation: Uninstalled(),
                                           enabled: () => false, ownsInner: false);
        Assert.False(t.OpenStream().RevisesText);
    }

    /// <summary>
    /// The punctuation model is English-only. Any other language, and "auto", must reach the
    /// multilingual engine WITHOUT it — re-punctuating German as English is worse than leaving it.
    /// </summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("auto")]
    public void Non_english_never_reaches_the_english_decorator(string language)
    {
        var engine = new FakeEngine();
        var english = new PunctuatingTranscriber(engine, punctuation: Uninstalled(),
                                                 enabled: () => true, ownsInner: false);
        var routed = new LanguageRoutedTranscriber(english, engine);
        routed.SetLanguage(language);
        Assert.NotSame(english, routed.Selected());
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    public void English_selects_the_punctuating_route(string language)
    {
        var engine = new FakeEngine();
        var english = new PunctuatingTranscriber(engine, punctuation: Uninstalled(),
                                                 enabled: () => true, ownsInner: false);
        var routed = new LanguageRoutedTranscriber(english, engine);
        routed.SetLanguage(language);
        Assert.Same(english, routed.Selected());
    }
}
