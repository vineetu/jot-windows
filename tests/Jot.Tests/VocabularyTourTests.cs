using System.Linq;
using Jot.Controls;
using Jot.Services;
using Jot.Services.Abstractions;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// The two vocabulary tours. They exist because the feature's flagship gesture (select a word in a
/// transcript → right-click → "Add to Vocabulary…") is otherwise completely invisible, and because
/// "experimental, English only" has to be said somewhere a user actually reads.
///
/// The shared shape (2–4 cards, non-empty live bodies, resolvable id) is already covered for every tour
/// by <see cref="TourCatalogTests"/>; these pin the parts specific to these two.
/// </summary>
public class VocabularyTourTests
{
    [Fact]
    public void BothTours_AreRegistered_AndReachableByName()
    {
        Assert.Contains(TourCatalog.Vocabulary, TourCatalog.All);
        Assert.Contains(TourCatalog.AddToVocabulary, TourCatalog.All);
        Assert.Same(TourCatalog.Vocabulary, TourCatalog.ById("vocabulary"));
        Assert.Same(TourCatalog.AddToVocabulary, TourCatalog.ById("ADD-TO-VOCABULARY"));
    }

    [Fact]
    public void VocabularyTour_StatesBothHonestLimits()
    {
        string body = string.Concat(TourCatalog.Vocabulary.Cards.Select(c => c.Body(new JotSettings())))
                    + TourCatalog.Vocabulary.Cards[2].Title;
        Assert.Contains("English", body);
        Assert.Contains("Experimental", body);
    }

    [Fact]
    public void VocabularyTour_QuotesTheRealDownloadSize()
    {
        // Never a hardcoded number: the tour, the consent prompt and the Settings row all read the
        // manifest, so re-cutting the model release can't leave one of them lying.
        string body = string.Concat(TourCatalog.Vocabulary.Cards.Select(c => c.Body(new JotSettings())));
        Assert.Contains($"{CtcModelDownload.SizeMb} MB", body);
    }

    [Fact]
    public void AddToVocabularyTour_TeachesTheRightClickGesture()
    {
        string body = string.Concat(
            TourCatalog.AddToVocabulary.Cards.Select(c => c.Title + " " + c.Body(new JotSettings())));
        Assert.Contains("Right-click", body);
        // The menu item's literal label, so the tour and the transcript pane can't drift apart.
        Assert.Contains("Add to Vocabulary", body);
    }

    /// <summary>
    /// Deliberate: NEITHER tour auto-fires. The feature ships default-off inside Advanced features, so an
    /// automatic tour would land on people who never enabled it. Both are Help-hub + --tour only, which
    /// means they must never acquire a trigger rule in <see cref="TourTriggers"/> without this changing.
    /// </summary>
    [Fact]
    public void NeitherTour_IsWiredToAnAutomaticTrigger()
    {
        var s = new JotSettings();
        // The only settings-driven auto-trigger is the rewrite tour's AI flip; nothing about vocabulary
        // participates, and turning vocabulary on must not mark either tour as "shown".
        s.VocabularyEnabled = true;
        Assert.False(TourCatalog.WasShown(s, TourCatalog.Vocabulary.Id));
        Assert.False(TourCatalog.WasShown(s, TourCatalog.AddToVocabulary.Id));
        Assert.False(TourTriggers.IsAiJustConfigured(s.AiProvider, s.AiProvider));
    }

    [Fact]
    public void ShowingATour_RecordsItOnce_InTheSharedList()
    {
        var s = new JotSettings();
        TourCatalog.MarkShown(s, TourCatalog.Vocabulary.Id);
        TourCatalog.MarkShown(s, TourCatalog.AddToVocabulary.Id);
        TourCatalog.MarkShown(s, TourCatalog.Vocabulary.Id);   // re-opened from Help — no duplicate
        Assert.Equal(2, s.ShownTours.Count);
        Assert.False(s.FirstRunTipsDone);                      // and never the wizard-tied flag
    }
}
