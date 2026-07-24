using System.Linq;
using Jot.Controls;
using Jot.Recording;
using Jot.Services.Abstractions;
using Xunit;

namespace Jot.Tests;

/// <summary>The contextual-tour registry and its persistence rules: every tour renders short, live-chord
/// copy through one window, and "seen" state is tracked once-ever per tour.</summary>
public class TourCatalogTests
{
    [Fact]
    public void EveryTour_Has2To4Cards_WithNonEmptyLiveBodies()
    {
        var s = new JotSettings();
        foreach (Tour tour in TourCatalog.All)
        {
            Assert.InRange(tour.Cards.Count, 2, 4);                 // the "less text" bar
            Assert.False(string.IsNullOrWhiteSpace(tour.Title));
            Assert.False(string.IsNullOrWhiteSpace(tour.Heading));
            foreach (TourCard c in tour.Cards)
            {
                Assert.False(string.IsNullOrWhiteSpace(c.Title));
                Assert.False(string.IsNullOrWhiteSpace(c.Body(s))); // bodies must render for any settings
            }
        }
    }

    [Fact]
    public void ChordCards_SpliceInTheLiveChord_NotAHardcodedString()
    {
        var s = new JotSettings { ToggleRecordingHotkey = "Ctrl+Alt+J", RewriteHotkey = "Ctrl+Alt+K" };
        // Shortcuts card 0 shows the toggle chord; card 1 shows the rewrite chord — both via HotkeyChord.Display.
        Assert.Contains(HotkeyChord.Display("Ctrl+Alt+J"), TourCatalog.Shortcuts.Cards[0].Body(s));
        Assert.Contains(HotkeyChord.Display("Ctrl+Alt+K"), TourCatalog.Shortcuts.Cards[1].Body(s));
        Assert.DoesNotContain("Ctrl + Shift + Space", TourCatalog.Shortcuts.Cards[0].Body(s));
    }

    [Fact]
    public void RewriteTour_UsesLiveVoiceChord()
    {
        var s = new JotSettings { RewriteWithVoiceHotkey = "Ctrl+Alt+M" };
        // Last rewrite card teaches "rewrite with voice" — must reflect that live binding.
        string body = string.Concat(TourCatalog.Rewrite.Cards.Select(c => c.Body(s)));
        Assert.Contains(HotkeyChord.Display("Ctrl+Alt+M"), body);
    }

    [Theory]
    [InlineData("getting-started")]
    [InlineData("shortcuts")]
    [InlineData("ai")]
    [InlineData("rewrite")]
    [InlineData("import")]
    [InlineData("feedback")]
    public void ById_ResolvesEveryName_CaseInsensitively(string id)
    {
        Assert.NotNull(TourCatalog.ById(id));
        Assert.NotNull(TourCatalog.ById(id.ToUpperInvariant()));
    }

    [Fact]
    public void ById_UnknownName_IsNull() => Assert.Null(TourCatalog.ById("nope"));

    [Fact]
    public void MarkShown_GettingStarted_SetsFirstRunFlag_NotTheList()
    {
        var s = new JotSettings();
        TourCatalog.MarkShown(s, TourCatalog.GettingStartedId);
        Assert.True(s.FirstRunTipsDone);
        Assert.Empty(s.ShownTours); // getting-started keeps its own wizard-tied lifecycle
    }

    [Fact]
    public void MarkShown_PerFeatureTour_RecordsIdOnce()
    {
        var s = new JotSettings();
        Assert.False(TourCatalog.WasShown(s, TourCatalog.Rewrite.Id));

        TourCatalog.MarkShown(s, TourCatalog.Rewrite.Id);
        TourCatalog.MarkShown(s, TourCatalog.Rewrite.Id); // idempotent — re-open never duplicates
        Assert.True(TourCatalog.WasShown(s, TourCatalog.Rewrite.Id));
        Assert.Single(s.ShownTours, t => t == TourCatalog.Rewrite.Id);
        Assert.False(s.FirstRunTipsDone);
    }
}

/// <summary>Pure trigger rules for the behavioural nudges — the once-ever + threshold logic that decides
/// whether the AI-setup nudge and the rewrite tour should fire.</summary>
public class TourTriggerTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("None", false)]
    [InlineData("none", false)]
    [InlineData("OpenAI", true)]
    [InlineData("Ollama", true)]
    public void IsAiConfigured_TreatsUnsetAndNoneAsNotConfigured(string? provider, bool expected)
        => Assert.Equal(expected, TourTriggers.IsAiConfigured(provider));

    [Fact]
    public void ShouldNudgeAiSetup_RealUserWithoutAi_NotYetNudged_Fires()
    {
        var s = new JotSettings { AiProvider = "None", AiSetupNudgeDone = false };
        Assert.True(TourTriggers.ShouldNudgeAiSetup(s, TourTriggers.AiNudgeMinDictations));
    }

    [Fact]
    public void ShouldNudgeAiSetup_TooFewDictations_DoesNotFire()
    {
        var s = new JotSettings { AiProvider = "None" };
        Assert.False(TourTriggers.ShouldNudgeAiSetup(s, TourTriggers.AiNudgeMinDictations - 1));
    }

    [Fact]
    public void ShouldNudgeAiSetup_AlreadyHasProvider_DoesNotFire()
    {
        var s = new JotSettings { AiProvider = "OpenAI" };
        Assert.False(TourTriggers.ShouldNudgeAiSetup(s, 999));
    }

    [Fact]
    public void ShouldNudgeAiSetup_AlreadyNudged_DoesNotFireAgain()
    {
        var s = new JotSettings { AiProvider = "None", AiSetupNudgeDone = true };
        Assert.False(TourTriggers.ShouldNudgeAiSetup(s, 999));
    }

    [Theory]
    [InlineData("None", "OpenAI", true)]   // the teachable moment
    [InlineData(null, "Ollama", true)]
    [InlineData("None", "None", false)]    // no change
    [InlineData("OpenAI", "Anthropic", false)] // provider swap — not a first-time setup
    [InlineData("OpenAI", "None", false)]  // turning AI off must never fire the tour
    public void IsAiJustConfigured_FiresOnlyOnUnsetToConfigured(string? before, string? after, bool expected)
        => Assert.Equal(expected, TourTriggers.IsAiJustConfigured(before, after));
}
