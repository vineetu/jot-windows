using System;
using System.Collections.Generic;
using System.Linq;
using Jot.Transcription.Nemotron;
using Jot.ViewModels;
using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// E6's outcome, pinned. Every number in <see cref="VocabularyLimits"/> cost 9500 clips of real
/// speech (docs/plans/vocabulary-brake-per-language.md); these tests are what stops one being changed
/// on a hunch, and what makes the two "we say English-only" copy surfaces provably wrong if the set
/// ever moves again.
/// </summary>
public class VocabularyLimitsTests
{
    /// <summary>The shipped answer, language by language. Written out rather than derived, because a
    /// test that recomputes the table from the table proves nothing.</summary>
    [Theory]
    // Inside budget at the English setting.
    [InlineData("cs-CZ", VocabularyLimits.NoLimit)]
    [InlineData("da-DK", VocabularyLimits.NoLimit)]
    [InlineData("es-ES", VocabularyLimits.NoLimit)]
    [InlineData("es-US", VocabularyLimits.NoLimit)]
    [InlineData("fi-FI", VocabularyLimits.NoLimit)]
    [InlineData("fr-CA", VocabularyLimits.NoLimit)]
    [InlineData("hu-HU", VocabularyLimits.NoLimit)]
    [InlineData("it-IT", VocabularyLimits.NoLimit)]
    [InlineData("nl-NL", VocabularyLimits.NoLimit)]
    [InlineData("pt-PT", VocabularyLimits.NoLimit)]
    [InlineData("ro-RO", VocabularyLimits.NoLimit)]
    [InlineData("sk-SK", VocabularyLimits.NoLimit)]
    [InlineData("sv-SE", VocabularyLimits.NoLimit)]
    // Tightened, each to the loosest distance that met the budget.
    [InlineData("bg-BG", 0.25)]
    [InlineData("de-DE", 0.15)]
    [InlineData("el-GR", 0.20)]
    [InlineData("pl-PL", 0.20)]
    [InlineData("ru-RU", 0.20)]
    [InlineData("uk-UA", 0.20)]
    public void EachLanguageRunsAtItsMeasuredDistance(string locale, double expected) =>
        Assert.Equal(expected, VocabularyLimits.TextualMaxDistance(locale));

    /// <summary>English is the acoustic language, but the corrector still serves it while the 132 MB
    /// checkpoint is missing — at E5's measured setting, which three documents quote.</summary>
    [Fact]
    public void EnglishKeepsTheSettingE5Measured() =>
        Assert.Equal(VocabularyLimits.NoLimit, VocabularyLimits.TextualMaxDistance("en-US"));

    /// <summary>Slovenian: 1.72 adversarial false applies per 1000 words, and the only setting that
    /// gets it under budget leaves 16 % recall. It is Off, and Off has to mean the runner does
    /// nothing — not "runs a bit".</summary>
    [Fact]
    public void SlovenianIsCutEvenThoughItHasAFrequencyList()
    {
        Assert.NotNull(EmbeddedCommonWordsProvider.ResourceFor("sl-SI"));
        Assert.False(VocabularyLimits.TextualShips("sl-SI"));
        Assert.Equal(VocabularyRunner.VocabularyMode.Off, VocabularyRunner.ModeFor("sl-SI"));
    }

    /// <summary>
    /// The 19-not-20 finding. `common-words-sr.txt` ships and Serbian is not one of Nemotron's 40
    /// locales, so `Normalize` folds any "sr-RS" a settings file could contain to en-US and the list is
    /// unreachable — which is why E6 measured 19 languages, not the 20 the ship note claimed.
    ///
    /// If Nemotron ever gains Serbian this test fails, and that is the point: the language would
    /// silently start running an UNMEASURED corrector on the strength of a list nobody has ever
    /// scored.
    /// </summary>
    [Fact]
    public void SerbianCannotBeSelectedAtAll()
    {
        Assert.DoesNotContain(NemotronLocales.All, l => l.Code.StartsWith("sr", StringComparison.OrdinalIgnoreCase));
        // A stored "sr-RS" therefore transcribes as English — Normalize folds every unknown value to
        // the default locale — while the vocabulary table, which fails closed instead, serves it not
        // at all. Two different answers to "what language is this?", both deliberate: one must pick a
        // model, the other must refuse to guess. The list is what is unreachable either way.
        Assert.Equal("en-US", NemotronLocales.Normalize("sr-RS"));
        Assert.False(VocabularyLimits.TextualShips("sr-RS"));
        Assert.DoesNotContain("sr", VocabularyLimits.Languages);
    }

    /// <summary>Every language in the table must be selectable AND have a frequency list — the two
    /// things that make its measured number mean anything.</summary>
    [Fact]
    public void EveryShippedLanguageIsReachableAndHasABrake()
    {
        var selectable = NemotronLocales.All
            .Select(l => l.Code.Split('-')[0].ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string lang in VocabularyLimits.Languages)
        {
            Assert.Contains(lang, selectable);
            Assert.NotNull(EmbeddedCommonWordsProvider.ResourceFor(lang));
        }
    }

    /// <summary>18 textual languages + English. The count the Store listing, the tour card and the
    /// feature doc all state; if it moves, those are wrong and this is where it is noticed.</summary>
    [Fact]
    public void TheShippedSetIsNineteenLanguages()
    {
        Assert.Equal(19, VocabularyLimits.Languages.Count);
        Assert.Equal(18, VocabularyLimits.Languages.Count(l => !l.Equals("en", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The blocked-language InfoBar has to give the RIGHT reason, and after E6 there are three. Telling
    /// a Slovenian user "Jot has no everyday-word list for Slovenian" would be false — there is one,
    /// 24 000 entries, and it is the measurement that says it cannot protect them. That is also the
    /// shape of false that gets "fixed" by someone shipping the list we already ship.
    ///
    /// Asserted here and not in the running app on purpose: WPF-UI's InfoBar exposes neither Title nor
    /// Message to UI Automation (checked against all 182 elements of the live Settings window), so the
    /// app can prove Slovenian is Off — it does, the badge reads "Experimental" and the description
    /// reads "Your current language isn't covered" — but not which sentence the bar is showing.
    /// </summary>
    [Fact]
    public void ACutLanguageIsNotToldItHasNoWordList()
    {
        string cut = SettingsViewModel.BlockedMessage("sl-SI");
        Assert.Contains("Slovenian", cut, StringComparison.Ordinal);
        Assert.DoesNotContain("no everyday-word list", cut, StringComparison.Ordinal);
        Assert.Contains("changing words you said correctly", cut, StringComparison.Ordinal);

        // A language nothing ships a list for still gets the original, and still-true, reason.
        string noList = SettingsViewModel.BlockedMessage("tr-TR");
        Assert.Contains("no everyday-word list for Turkish", noList, StringComparison.Ordinal);

        // And Auto detect is neither: there is no resolved language to have an opinion about.
        Assert.Contains("Auto detect", SettingsViewModel.BlockedMessage("auto"), StringComparison.Ordinal);
    }

    /// <summary>A language nobody measured gets the tightest shipped setting, not the loosest.
    /// Unreachable today; it is the direction of the default that matters.</summary>
    [Fact]
    public void AnUnmeasuredLanguageGetsTheTightestSetting() =>
        Assert.Equal(VocabularyLimits.Unmeasured, VocabularyLimits.TextualMaxDistance("mt-MT"));

    /// <summary>
    /// The table must FAIL CLOSED on anything it does not know, and the trap is that
    /// <c>NemotronLocales.Normalize</c> answers "en-US" for every value it fails to resolve — so a
    /// lookup that funnels through it hands an unknown language ENGLISH's uncapped setting. A bare
    /// subtag is the everyday shape of this ("de" is not a Nemotron code; "de-DE" is), and it must
    /// resolve to the language it names, not to English.
    /// </summary>
    [Theory]
    [InlineData("de", true, 0.15)]        // German's measured cap, not English's no-limit
    [InlineData("es", true, VocabularyLimits.NoLimit)]
    [InlineData("es-MX", true, VocabularyLimits.NoLimit)]   // an unlisted REGION resolves by subtag
    [InlineData("ru", true, 0.20)]
    [InlineData("xx", false, VocabularyLimits.Unmeasured)]
    [InlineData("hr", false, VocabularyLimits.Unmeasured)]  // no frequency list ships — never served
    [InlineData("sl", false, VocabularyLimits.Unmeasured)]  // measured unsafe — never served
    [InlineData(null, false, VocabularyLimits.Unmeasured)]  // "no language known" is not "English"
    public void AnUnrecognisedValueNeverInheritsEnglishsSetting(string? language, bool ships, double expected)
    {
        Assert.Equal(expected, VocabularyLimits.TextualMaxDistance(language));
        Assert.Equal(ships, VocabularyLimits.TextualShips(language));
    }

    /// <summary>The legacy display names older settings files still hold must keep resolving.</summary>
    [Theory]
    [InlineData("English", VocabularyLimits.NoLimit)]
    [InlineData("German", 0.15)]
    [InlineData("Croatian", VocabularyLimits.Unmeasured)]
    public void LegacyDisplayNamesStillResolve(string name, double expected) =>
        Assert.Equal(expected, VocabularyLimits.TextualMaxDistance(name));

    /// <summary>
    /// The distance is a real brake, not a stored number: at Greek's 0.20 the corrector refuses a
    /// one-edit near-miss on a six-character term (1/6 = 0.17 passes, 2/6 = 0.33 does not), while the
    /// English setting takes it. Both directions asserted, because a cap that never fires and a cap
    /// that blocks everything are the same bug from opposite sides.
    /// </summary>
    [Fact]
    public void TheDistanceActuallyGatesTheCorrector()
    {
        List<VocabularyTerm> terms = [new() { Text = "Nemotron" }];
        var corrector = new VocabularyCorrector();

        // "neumotrone": 2 edits against an 8-character term = 0.20 — inside the English setting.
        Assert.NotEmpty(corrector.Spot("the neumotrone model", terms, 2.0, VocabularyLimits.NoLimit));
        Assert.NotEmpty(corrector.Spot("the neumotrone model", terms, 2.0, 0.20));
        Assert.Empty(corrector.Spot("the neumotrone model", terms, 2.0, 0.15));

        // One edit is still reachable at the tightest shipped setting on a long term.
        Assert.NotEmpty(corrector.Spot("the nemotrone model", terms, 2.0, 0.15));
    }

    /// <summary>The Spanish incident's pair must still be PROPOSED in Spanish — the brake is what
    /// blocks it, and a distance that stopped the corrector proposing would hide the row instead of
    /// blocking it. es ships at NoLimit; this pins that the tightening pass did not quietly break the
    /// case the whole subsystem exists for.</summary>
    [Fact]
    public void SpanishStillSeesTheListaLisaPair()
    {
        List<VocabularyTerm> terms = [new() { Text = "Lisa" }];
        Assert.NotEmpty(new VocabularyCorrector().Spot(
            "hazme una lista de nombres", terms, 3.0, VocabularyLimits.TextualMaxDistance("es-ES")));
    }
}
