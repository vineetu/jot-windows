using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jot.Transcription;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// Which engine handles which language. Pure routing, checked with fakes so it runs everywhere and
/// so a failure points at the decision rather than at a model.
///
/// The case that matters most is "auto": Granite is English-only and cannot detect a language, so
/// auto-detect MUST go to the multilingual engine. Routing it to English would not fail loudly — it
/// would transliterate whatever the user said into English words.
/// </summary>
public class LanguageRoutedTranscriberTests
{
    private sealed class FakeEngine(string name, bool installed) : ITranscriber, IStreamingTranscriber
    {
        public string Name { get; } = name;
        public bool IsModelInstalled { get; } = installed;
        public int WarmUps { get; private set; }
        public List<string> Calls { get; } = [];

        public Task<string> TranscribeAsync(float[] samples, int sampleRate,
                                            CancellationToken ct = default)
        {
            Calls.Add("transcribe");
            return Task.FromResult(Name);
        }

        public void WarmUp() => WarmUps++;

        public IStreamingSession OpenStream() => new FakeSession(Name);

        private sealed class FakeSession(string name) : IStreamingSession
        {
            public string Accept(float[] newSamples) => name;
            public string Finish() => name;
        }
    }

    private static (LanguageRoutedTranscriber Router, FakeEngine English, FakeEngine Other) Build(
        bool englishInstalled = true)
    {
        var english = new FakeEngine("english", englishInstalled);
        var other = new FakeEngine("other", installed: true);
        return (new LanguageRoutedTranscriber(english, other), english, other);
    }

    private static string Run(LanguageRoutedTranscriber r, string language)
    {
        r.SetLanguage(language);
        return r.TranscribeAsync(new float[16], 16_000).GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData("en")]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("English")]      // the legacy display name still stored by older installs
    public void English_in_any_stored_form_goes_to_the_english_engine(string language)
    {
        (LanguageRoutedTranscriber r, _, _) = Build();
        Assert.Equal("english", Run(r, language));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr")]
    [InlineData("German")]
    [InlineData("zh-CN")]
    public void Other_languages_go_to_the_multilingual_engine(string language)
    {
        (LanguageRoutedTranscriber r, _, _) = Build();
        Assert.Equal("other", Run(r, language));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-language")]
    public void Autodetect_and_unknown_values_go_to_the_multilingual_engine(string language)
    {
        // Granite cannot detect a language. Anything that is not definitely English must not
        // reach it.
        (LanguageRoutedTranscriber r, _, _) = Build();
        Assert.Equal("other", Run(r, language));
    }

    [Fact]
    public void English_falls_back_when_the_english_engine_is_not_installed()
    {
        (LanguageRoutedTranscriber r, _, _) = Build(englishInstalled: false);
        Assert.Equal("other", Run(r, "en-US"));
    }

    [Fact]
    public void A_language_change_re_routes_without_rebuilding_the_transcriber()
    {
        // The reason routing is per-utterance rather than decided once in the factory: Settings can
        // change the language at any time and the DI graph is not rebuilt.
        (LanguageRoutedTranscriber r, _, _) = Build();
        Assert.Equal("english", Run(r, "en-US"));
        Assert.Equal("other", Run(r, "de-DE"));
        Assert.Equal("english", Run(r, "en-US"));
    }

    [Fact]
    public void IsModelInstalled_reports_the_engine_that_would_actually_run()
    {
        var english = new FakeEngine("english", installed: false);
        var other = new FakeEngine("other", installed: true);
        var r = new LanguageRoutedTranscriber(english, other);

        r.SetLanguage("en-US");
        Assert.True(r.IsModelInstalled);   // falls back to the installed multilingual engine
        r.SetLanguage("de-DE");
        Assert.True(r.IsModelInstalled);
    }

    [Fact]
    public void WarmUp_only_warms_the_engine_the_current_language_uses()
    {
        (LanguageRoutedTranscriber r, FakeEngine english, FakeEngine other) = Build();

        r.SetLanguage("en-US");
        r.WarmUp();
        Assert.Equal(1, english.WarmUps);
        Assert.Equal(0, other.WarmUps);

        r.SetLanguage("de-DE");
        r.WarmUp();
        Assert.Equal(1, english.WarmUps);
        Assert.Equal(1, other.WarmUps);
    }

    [Fact]
    public void Streaming_opens_on_the_routed_engine()
    {
        (LanguageRoutedTranscriber r, _, _) = Build();

        r.SetLanguage("en-US");
        Assert.Equal("english", r.OpenStream().Finish());
        r.SetLanguage("fr-FR");
        Assert.Equal("other", r.OpenStream().Finish());
    }

    [Fact]
    public async Task Route_changes_are_logged_once_not_per_utterance()
    {
        var lines = new List<string>();
        var r = new LanguageRoutedTranscriber(new FakeEngine("english", true),
                                              new FakeEngine("other", true), lines.Add);
        r.SetLanguage("en-US");
        await r.TranscribeAsync(new float[16], 16_000);
        await r.TranscribeAsync(new float[16], 16_000);
        await r.TranscribeAsync(new float[16], 16_000);
        Assert.Single(lines);

        r.SetLanguage("de-DE");
        await r.TranscribeAsync(new float[16], 16_000);
        Assert.Equal(2, lines.Count);
        Assert.Contains("ggml", lines[1]);
    }
}
