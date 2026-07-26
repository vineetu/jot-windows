using Jot.Services.Abstractions;
using Jot.Services.Navigation;
using Jot.ViewModels;
using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// The VocabularyPage's logic: term validation copy, and the four states the design called out as
/// undesigned — duplicate term, term cap, an alias claimed by two terms, and renaming a term that
/// has correction history.
/// </summary>
public class VocabularyPageModelTests
{
    private sealed class FakeSettingsStore : ISettingsStore
    {
        public JotSettings Current { get; } = new();
        public void Save() { }
        public void Reset() { }
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class FakeNavigator : INavigator
    {
        public int BackCalls;
        public object? Parameter => null;
        public void Navigate(Type pageType, object? parameter = null) { }
        public void GoBack() => BackCalls++;
    }

    /// <summary>No model on disk, so the spotter can only answer the length rule — exactly the
    /// shipping v1.3 state.</summary>
    private sealed class NoModelSpotter : IVocabularySpotter
    {
        public bool IsReady => false;
        public IReadOnlyList<VocabularyGate.Detection> Spot(
            float[] samples, int sampleRate, IReadOnlyList<VocabularyTerm> terms, CancellationToken ct) => [];
        public TermSpottability CheckTerm(string? term) => VocabularyTermRules.CheckLength(term);
    }

    /// <summary>Stands in for a downloaded model: everything with a digit or an accent is
    /// unsupported, which is what the 1024-piece English BPE actually reports.</summary>
    private sealed class ModelSpotter : IVocabularySpotter
    {
        public bool IsReady => true;
        public IReadOnlyList<VocabularyGate.Detection> Spot(
            float[] samples, int sampleRate, IReadOnlyList<VocabularyTerm> terms, CancellationToken ct) => [];

        public TermSpottability CheckTerm(string? term)
        {
            if (VocabularyTermRules.CheckLength(term) == TermSpottability.TooShort) return TermSpottability.TooShort;
            string t = VocabularyStore.SanitizeTerm(term);
            return t.All(c => char.IsAsciiLetter(c) || c == ' ') ? TermSpottability.Ok : TermSpottability.Unsupported;
        }
    }

    /// <summary>A model that arrives DURING the session — the download finishing, or (before the lock
    /// split in <c>CtcVocabularySpotter</c>) startup's Warm letting go of the session lock. The
    /// ordering that produced the shipped bug: the saved <c>Wi-Fi</c> row showed no warning while the
    /// add form warned about <c>Wi-Fi</c> in the same instant.</summary>
    private sealed class LateModelSpotter : IVocabularySpotter
    {
        public bool Ready;
        public bool IsReady => Ready;
        public IReadOnlyList<VocabularyGate.Detection> Spot(
            float[] samples, int sampleRate, IReadOnlyList<VocabularyTerm> terms, CancellationToken ct) => [];

        public TermSpottability CheckTerm(string? term)
        {
            if (VocabularyTermRules.CheckLength(term) == TermSpottability.TooShort) return TermSpottability.TooShort;
            if (!Ready) return TermSpottability.Unknown;
            string t = VocabularyStore.SanitizeTerm(term);
            return t.All(c => char.IsAsciiLetter(c) || c == ' ') ? TermSpottability.Ok : TermSpottability.Unsupported;
        }
    }

    private static (VocabularyViewModel Vm, VocabularyStore Terms, CorrectionStore Corrections, FakeSettingsStore Settings)
        Build(IVocabularySpotter? spotter = null)
    {
        var settings = new FakeSettingsStore();
        settings.Current.Language = "en-US";
        var terms = new VocabularyStore(null);
        var corrections = new CorrectionStore(null);
        var vm = new VocabularyViewModel(terms, corrections, settings,
            spotter ?? new NoModelSpotter(), new FakeNavigator(), EmbeddedCommonWordsProvider.Shared);
        return (vm, terms, corrections, settings);
    }

    private static void Add(VocabularyViewModel vm, string term, params string[] aliases)
    {
        vm.NewTerm = term;
        foreach (string a in aliases) { vm.NewAlias = a; vm.AddAliasCommand.Execute(null); }
        vm.AddTermCommand.Execute(null);
    }

    // MARK: - Basic CRUD

    [Fact]
    public void AddingATerm_CreatesOneRowWithItsAliases()
    {
        (VocabularyViewModel vm, VocabularyStore terms, _, _) = Build();
        Add(vm, "Nemotron", "nemo tron", "neutron");

        VocabularyTermRow row = Assert.Single(vm.Rows);
        Assert.Equal("Nemotron", row.Text);
        Assert.Equal("also heard as: nemo tron · neutron", row.AliasSummary);
        Assert.Single(terms.Terms);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public void Search_FiltersOnTermsAndAliases()
    {
        (VocabularyViewModel vm, _, _, _) = Build();
        Add(vm, "Nemotron", "neutron");
        Add(vm, "Ideaflow");

        vm.SearchText = "neut";
        Assert.Equal("Nemotron", Assert.Single(vm.Rows).Text);

        vm.SearchText = "zzz";
        Assert.Empty(vm.Rows);
        Assert.True(vm.HasNoMatches);
    }

    [Fact]
    public void Delete_RemovesTheRowAndOffersAnUndoThatRestoresIt()
    {
        (VocabularyViewModel vm, VocabularyStore terms, _, _) = Build();
        Add(vm, "Nemotron", "neutron");

        vm.DeleteTermCommand.Execute(vm.Rows[0]);
        Assert.Empty(terms.Terms);
        Assert.True(vm.CanUndoDelete);
        Assert.Equal("Removed Nemotron.", vm.StatusMessage);

        vm.UndoDeleteCommand.Execute(null);
        Assert.Equal("Nemotron", Assert.Single(terms.Terms).Text);
    }

    // MARK: - §2.5b · the states nobody had designed

    [Fact]
    public void DuplicateTerm_MergesAliasesInsteadOfCreatingASecondRow()
    {
        (VocabularyViewModel vm, VocabularyStore terms, _, _) = Build();
        Add(vm, "Nemotron", "neutron");
        Add(vm, "nemotron", "nemo tron");

        Assert.Single(terms.Terms);
        Assert.Equal(["neutron", "nemo tron"], terms.Terms[0].Aliases);
        Assert.Equal("Already in your list — added 1 new spelling.", vm.StatusMessage);
    }

    [Fact]
    public void DuplicateTerm_WithNothingNew_SaysSo()
    {
        (VocabularyViewModel vm, _, _, _) = Build();
        Add(vm, "Nemotron", "neutron");
        Add(vm, "Nemotron", "neutron");

        Assert.Equal("Already in your list.", vm.StatusMessage);
    }

    [Fact]
    public void TermCap_DisablesAddAndExplainsWhy()
    {
        (VocabularyViewModel vm, VocabularyStore terms, _, _) = Build();
        for (int i = 0; i < VocabularyStore.MaxTerms; i++) terms.Add($"term{i:000}");
        vm.Refresh();

        Assert.True(vm.AtCap);
        Assert.False(vm.CanAdd);
        Assert.Equal($"You've reached {VocabularyStore.MaxTerms} terms. Remove one to add another.", vm.CapMessage);

        Add(vm, "Nemotron");
        Assert.Equal(VocabularyStore.MaxTerms, terms.Terms.Count);
    }

    /// <summary>An alias belonging to two terms makes the gate's only plausibility lever ambiguous,
    /// so the duplicate never persists — and the move has to be LOUD or the alias silently vanishes
    /// from the term the user put it on first.</summary>
    [Fact]
    public void AliasClaimedByTwoTerms_MovesToTheNewOwnerLoudly()
    {
        (VocabularyViewModel vm, VocabularyStore terms, _, _) = Build();
        Add(vm, "Nemotron", "neutron");
        Add(vm, "Neutronix", "neutron");

        Assert.Equal("\"neutron\" was listed under Nemotron — moved it here.", vm.StatusMessage);
        Assert.Empty(terms.Terms.First(t => t.Text == "Nemotron").Aliases);
        Assert.Equal(["neutron"], terms.Terms.First(t => t.Text == "Neutronix").Aliases);
    }

    /// <summary>Rename = delete + create in v1.3: every learned net is keyed on the OLD pair.
    /// Migrating them wrongly would silently transfer a learned override onto a different word.</summary>
    [Fact]
    public void RenamingATermWithHistory_WarnsBeforeSaving()
    {
        (VocabularyViewModel vm, _, CorrectionStore corrections, _) = Build();
        Add(vm, "Nemotron");
        corrections.Adjust("neumotron", "Nemotron", +1);

        vm.EditTermCommand.Execute(vm.Rows[0]);
        Assert.False(vm.ShowRenameWarning);      // nothing renamed yet

        vm.NewTerm = "NeMotron";
        Assert.True(vm.ShowRenameWarning);
    }

    [Fact]
    public void RenamingATermWithNoHistory_DoesNotWarn()
    {
        (VocabularyViewModel vm, _, _, _) = Build();
        Add(vm, "Nemotron");

        vm.EditTermCommand.Execute(vm.Rows[0]);
        vm.NewTerm = "NeMotron";
        Assert.False(vm.ShowRenameWarning);
    }

    // MARK: - §2.4 · inline warnings

    [Theory]
    [InlineData("ab", "Too short — terms under 3 characters are skipped to avoid false replacements.")]
    [InlineData("one two three four five", "Use a single word or short phrase (max 4 words).")]
    [InlineData("okay", "Common word — Jot won't swap this on its own. You can confirm it on the recording.")]
    [InlineData("UJET", "Add a sounds-like spelling — Jot rarely hears short acronyms correctly on its own.")]
    [InlineData("Nemotron", null)]
    public void WarningFor_MatchesTheDesignsTable(string term, string? expected)
    {
        (VocabularyViewModel vm, _, _, _) = Build();
        Assert.Equal(expected, vm.WarningFor(new VocabularyTerm { Text = term }));
    }

    /// <summary>D12 · a term the checkpoint can never encode must be TOLD, not silently ignored.</summary>
    [Fact]
    public void UnspottableTerm_IsWarnedAboutOnceTheModelCanAnswer()
    {
        (VocabularyViewModel vm, _, _, _) = Build(new ModelSpotter());
        Assert.Equal(VocabularyViewModel.UnsupportedWarning,
            vm.WarningFor(new VocabularyTerm { Text = "Wi-Fi" }));
        Assert.Equal(VocabularyViewModel.UnsupportedWarning,
            vm.WarningFor(new VocabularyTerm { Text = "Zürich" }));
        Assert.Null(vm.WarningFor(new VocabularyTerm { Text = "Nemotron" }));

        vm.NewTerm = "café";
        Assert.True(vm.ShowSpottabilityWarning);
        vm.NewTerm = "Nemotron";
        Assert.False(vm.ShowSpottabilityWarning);
    }

    /// <summary>With no model there is no honest answer, so the row must NOT claim the term is
    /// broken — that would flag every term until the download finishes.</summary>
    [Fact]
    public void UnspottableTerm_IsSilentWhileTheModelIsUnknown()
    {
        (VocabularyViewModel vm, _, _, _) = Build(new NoModelSpotter());
        Assert.Null(vm.WarningFor(new VocabularyTerm { Text = "Wi-Fi" }));
        vm.NewTerm = "café";
        Assert.False(vm.ShowSpottabilityWarning);
    }

    // MARK: - §2.4 · warnings must survive the ORDER the model becomes available in

    /// <summary>Ordering A — the spotter can already answer when the list is built. The saved row
    /// must carry the warning, not just the add form.</summary>
    [Fact]
    public void RowWarning_IsPresentWhenTheModelCouldAnswerBeforeTheListWasBuilt()
    {
        (VocabularyViewModel vm, _, _, _) = Build(new LateModelSpotter { Ready = true });
        Add(vm, "Wi-Fi");

        Assert.Equal(VocabularyViewModel.UnsupportedWarning, Assert.Single(vm.Rows).Warning);
        Assert.True(vm.Rows[0].HasWarning);
    }

    /// <summary>Ordering B — the model lands AFTER the rows exist. `Warning` is a snapshot taken in
    /// Refresh, so without Revalidate the row stays silent forever while the add form (recomputed on
    /// every keystroke) warns about the identical term. That contradiction, in one instant, was the
    /// reported defect.</summary>
    [Fact]
    public void RowWarning_CatchesUpWhenTheModelBecomesAvailableAfterwards()
    {
        var spotter = new LateModelSpotter { Ready = false };
        (VocabularyViewModel vm, _, _, _) = Build(spotter);
        Add(vm, "Wi-Fi");

        // Honest silence while nothing can answer — warning here would flag every term mid-download.
        Assert.Null(Assert.Single(vm.Rows).Warning);
        Assert.True(vm.HasModelNote);
        vm.NewTerm = "Wi-Fi";
        Assert.False(vm.ShowSpottabilityWarning);

        spotter.Ready = true;
        vm.Revalidate();

        Assert.Equal(VocabularyViewModel.UnsupportedWarning, Assert.Single(vm.Rows).Warning);
        Assert.False(vm.HasModelNote);
        // The row and the form now say the SAME thing, which is the whole point.
        Assert.True(vm.ShowSpottabilityWarning);
    }

    /// <summary>Revalidate on an already-correct list must not rebuild the rows — that would clear
    /// the ListView's selection out from under the user on every navigation.</summary>
    [Fact]
    public void Revalidate_DoesNotRebuildRowsWhenNothingChanged()
    {
        (VocabularyViewModel vm, _, _, _) = Build(new LateModelSpotter { Ready = true });
        Add(vm, "Wi-Fi");
        Add(vm, "Nemotron");

        VocabularyTermRow[] before = [.. vm.Rows];
        vm.Revalidate();

        Assert.Equal(before.Length, vm.Rows.Count);
        for (int i = 0; i < before.Length; i++) Assert.Same(before[i], vm.Rows[i]);
    }

    // MARK: - Bulk paste import

    [Fact]
    public void PastingAList_SwitchesToImportModeAndAddsThemAll()
    {
        (VocabularyViewModel vm, VocabularyStore terms, _, _) = Build();

        Assert.True(vm.TryBeginImport("Nemotron\nIdeaflow\nRamanathan"));
        Assert.True(vm.IsImportMode);
        Assert.Equal("Looks like a list — 3 terms found.", vm.ImportCountText);

        vm.AddTermCommand.Execute(null);
        Assert.Equal(3, terms.Terms.Count);
        Assert.Equal("Added 3 terms.", vm.StatusMessage);
        Assert.False(vm.IsImportMode);
    }

    [Fact]
    public void PastingASingleWord_IsNotAnImport()
    {
        (VocabularyViewModel vm, _, _, _) = Build();
        Assert.False(vm.TryBeginImport("Nemotron"));
        Assert.False(vm.IsImportMode);
    }

    [Fact]
    public void Import_ReportsWhatItSkippedAndWhy()
    {
        (VocabularyViewModel vm, VocabularyStore terms, _, _) = Build();
        Add(vm, "Nemotron");

        Assert.True(vm.TryBeginImport("Nemotron, Ideaflow, ab"));
        vm.AddTermCommand.Execute(null);

        Assert.Equal(2, terms.Terms.Count);
        Assert.Equal("Added 1 terms. Skipped 2 — already in your list, or under 3 characters.", vm.StatusMessage);
    }

    [Fact]
    public void Import_WithNothingNew_SaysNothingWasAdded()
    {
        (VocabularyViewModel vm, _, _, _) = Build();
        Add(vm, "Nemotron");
        Add(vm, "Ideaflow");

        Assert.True(vm.TryBeginImport("Nemotron, Ideaflow"));
        vm.AddTermCommand.Execute(null);

        Assert.Equal("Nothing added — those 2 terms are already in your list.", vm.StatusMessage);
    }

    [Fact]
    public void AddAsOneTerm_KeepsThePastedBlobAsASingleTerm()
    {
        (VocabularyViewModel vm, VocabularyStore terms, _, _) = Build();
        Assert.True(vm.TryBeginImport("Rock, Paper, Scissors"));

        vm.AddAsOneTermCommand.Execute(null);
        Assert.Equal("Rock Paper Scissors", Assert.Single(terms.Terms).Text);
    }

    // MARK: - Header honesty

    [Fact]
    public void Subtitle_SaysWhichMatchingTheLanguageActuallyGets()
    {
        // Was "· English only" for everything non-English. Spanish now gets the model-free corrector
        // (docs/plans/vocabulary-corrector-vs-spotter.md), so saying "English only" there would be
        // false; only a language with no frequency list is genuinely inactive.
        (VocabularyViewModel vm, _, _, FakeSettingsStore settings) = Build();
        Assert.DoesNotContain("·", vm.Subtitle, StringComparison.Ordinal);

        settings.Current.Language = "es-ES";
        Assert.Contains("· spelling matching only", vm.Subtitle, StringComparison.Ordinal);

        settings.Current.Language = "ja-JP";
        Assert.Contains("· not active in this language", vm.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelNote_ExplainsThatTermsAreStillSaved()
    {
        (VocabularyViewModel vm, _, _, FakeSettingsStore settings) = Build();
        Assert.True(vm.HasModelNote);
        Assert.Equal("Vocabulary model not downloaded — Jot is matching spellings only until it is.",
            vm.ModelNote);

        // Outside English the checkpoint is not what the terms are waiting on, so advertising it
        // would point the user at a 132 MB download that would do nothing for them.
        settings.Current.Language = "es-ES";
        Assert.False(vm.HasModelNote);
    }
}
