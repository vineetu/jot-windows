using System.IO;
using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// The term list: the sanitize choke point, duplicate/alias-ownership rules, the feed-time
/// `enrichedAliases` fix, and the on-disk layout D7 depends on.
///
/// The load-bearing cases are (a) <see cref="VocabularyStore.SanitizeTerm"/> — iOS shipped a bug
/// where a typed term containing <c>:</c> or a leading <c>#</c> corrupted the interchange format,
/// and separately where Settings' free-text editing bypassed the sanitizer the add-dialog used —
/// and (b) the file landing under <c>Vocabulary\</c>, which is the folder "Erase all data" and a
/// data-folder move both act on.
/// </summary>
public class VocabularyStoreTests
{
    private static string MakeTempRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "jot-vocabstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    // MARK: - Sanitize (the choke point)

    [Theory]
    [InlineData("  Nemotron  ", "Nemotron")]
    [InlineData("Nemo:tron", "Nemotron")]          // colon separates term from aliases in the file format
    [InlineData("Nemo,tron", "Nemotron")]
    [InlineData("Nemo;tron", "Nemotron")]
    [InlineData("#Nemotron", "Nemotron")]          // leading # is a comment marker in the file format
    [InlineData("Claude    Code", "Claude Code")]  // whitespace runs collapse
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void SanitizeTerm_StripsWhatCorruptsTheFileFormat(string? raw, string expected)
        => Assert.Equal(expected, VocabularyStore.SanitizeTerm(raw));

    [Fact]
    public void SanitizeTerm_PreservesCase()
        => Assert.Equal("UJET", VocabularyStore.SanitizeTerm(" UJET "));

    // MARK: - Add / merge / aliases

    [Fact]
    public void Add_SanitizesAndKeepsAliases()
    {
        var store = new VocabularyStore(null);
        VocabularyTerm? t = store.Add(" Nemo:tron ", ["nemo tron", " neutron "]);
        Assert.NotNull(t);
        Assert.Equal("Nemotron", t!.Text);
        Assert.Equal(["nemo tron", "neutron"], t.Aliases);
    }

    [Fact]
    public void Add_ExistingTerm_MergesInsteadOfCreatingASecondRow()
    {
        var store = new VocabularyStore(null);
        store.Add("Nemotron", ["nemo tron"]);
        VocabularyTerm? again = store.Add("nemotron", ["neutron", "nemo tron"]);

        Assert.Single(store.Terms);
        Assert.Equal(["nemo tron", "neutron"], again!.Aliases);
    }

    [Fact]
    public void Add_DoesNotAcceptAnAliasIdenticalToItsOwnTerm()
    {
        var store = new VocabularyStore(null);
        VocabularyTerm? t = store.Add("Nemotron", ["nemotron"]);
        Assert.Empty(t!.Aliases);
    }

    /// <summary>An alias owned by two terms makes the gate's plausibility lever ambiguous, so the
    /// duplicate never persists — last write wins and the alias moves.</summary>
    [Fact]
    public void Add_SameAliasOnASecondTerm_MovesIt()
    {
        var store = new VocabularyStore(null);
        VocabularyTerm first = store.Add("Nemotron", ["neutron"])!;
        VocabularyTerm second = store.Add("Neutronix", ["neutron"])!;

        Assert.Empty(first.Aliases);
        Assert.Equal(["neutron"], second.Aliases);
    }

    [Fact]
    public void AddMapping_CreatesTheTermAndRecordsTheHeardForm()
    {
        var store = new VocabularyStore(null);
        VocabularyTerm? t = store.AddMapping("nemo tron", "Nemotron");
        Assert.Equal("Nemotron", t!.Text);
        Assert.Equal(["nemo tron"], t.Aliases);
    }

    [Fact]
    public void Find_MatchesTermOrAlias()
    {
        var store = new VocabularyStore(null);
        store.Add("Nemotron", ["nemo tron"]);
        Assert.NotNull(store.Find("NEMOTRON"));
        Assert.NotNull(store.Find("Nemo Tron"));
        Assert.Null(store.Find("something else"));
    }

    // MARK: - enrichedAliases (feed time only)

    /// <summary>
    /// Mac's `design.md §6 [ADD]` and iOS's `correction-review-implementation.md §11` filed the same
    /// bug: "Ramaa Nathan" heard MERGED as "Ramanathan" was replaced by the SHORTER term "Ramaa",
    /// because the multi-word term was never plausible against the merged word and so never competed.
    /// The space-stripped form is added at FEED time only — the stored term is untouched.
    /// </summary>
    [Fact]
    public void FeedAliases_AddsTheMergedFormForAMultiWordTerm()
    {
        var term = new VocabularyTerm { Text = "Ramaa Nathan", Aliases = ["ramanadan"] };
        Assert.Equal(["ramanadan", "RamaaNathan"], VocabularyStore.FeedAliases(term));
        Assert.Equal(["ramanadan"], term.Aliases);   // the user's list is NOT mutated
    }

    [Fact]
    public void FeedAliases_LeavesASingleWordTermAlone()
    {
        var term = new VocabularyTerm { Text = "Nemotron", Aliases = ["neutron"] };
        Assert.Equal(["neutron"], VocabularyStore.FeedAliases(term));
    }

    [Fact]
    public void FeedAliases_DoesNotDuplicateAnAliasTheUserAlreadyTyped()
    {
        var term = new VocabularyTerm { Text = "Claude Code", Aliases = ["claudecode"] };
        Assert.Equal(["claudecode"], VocabularyStore.FeedAliases(term));
    }

    // MARK: - Persistence + D7 layout

    [Fact]
    public void Terms_RoundTripThroughVocabularyJsonUnderTheVocabularyFolder()
    {
        string root = MakeTempRoot();
        try
        {
            var store = new VocabularyStore(root);
            store.Add("Nemotron", ["nemo tron", "neutron"]);
            store.Add("UJET", ["you jet"]);

            // The path is the contract: JotDataPurge and DataFolderMigrator both act on this folder.
            string file = Path.Combine(root, "Vocabulary", "vocabulary.json");
            Assert.True(File.Exists(file), $"expected {file}");

            var reloaded = new VocabularyStore(root);
            Assert.Equal(["Nemotron", "UJET"], reloaded.Terms.Select(t => t.Text));
            Assert.Equal(["nemo tron", "neutron"], reloaded.Terms[0].Aliases);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>A null root is the in-memory / test mode: no directory is ever created.</summary>
    [Fact]
    public void NullRoot_NeverTouchesDisk()
    {
        var store = new VocabularyStore(null);
        store.Add("Nemotron");
        Assert.Single(store.Terms);
    }

    [Fact]
    public void Remove_Persists()
    {
        string root = MakeTempRoot();
        try
        {
            var store = new VocabularyStore(root);
            VocabularyTerm t = store.Add("Nemotron")!;
            store.Add("UJET");
            store.Remove(t);

            Assert.Equal(["UJET"], new VocabularyStore(root).Terms.Select(x => x.Text));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    // MARK: - The interchange ("simple") format

    [Fact]
    public void VocabularyFile_ParsesTheSharedSimpleFormat()
    {
        IReadOnlyList<VocabularyTerm> terms = VocabularyFile.Parse(
            "# a comment\nUJET: you jet, ew jet\n\nOsiris\nD'Andre: dandre, dahndray\n");

        Assert.Equal(["UJET", "Osiris", "D'Andre"], terms.Select(t => t.Text));
        Assert.Equal(["you jet", "ew jet"], terms[0].Aliases);
        Assert.Empty(terms[1].Aliases);
    }

    [Fact]
    public void VocabularyFile_SerializeRoundTrips()
    {
        const string body = "UJET: you jet, ew jet\nOsiris\n";
        Assert.Equal(body, VocabularyFile.Serialize([.. VocabularyFile.Parse(body)]));
    }

    [Fact]
    public void VocabularyFile_SerializeIsEmptyForNoTerms()
        => Assert.Equal("", VocabularyFile.Serialize([]));
}
