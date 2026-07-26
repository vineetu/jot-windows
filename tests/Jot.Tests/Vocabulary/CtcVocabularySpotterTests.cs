using System;
using System.IO;
using System.Linq;
using Jot.Transcription.Ctc;
using Jot.Transcription.Onnx;
using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// Everything about the shipping spotter that does NOT need 132 MB on disk: the no-model contract, term
/// validation, silence trimming, and the install recipe. CI must stay green with no download, so nothing
/// here touches an ONNX session.
/// </summary>
public class CtcVocabularySpotterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jot-ctc-spotter-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private CtcVocabularySpotter Spotter() =>
        new(new CtcModel(directory: Path.Combine(_root, "model")), new OnnxSessionFactory());

    // MARK: - The "no model" contract

    [Fact]
    public void IsReady_IsFalseWithNoModel_AndNeverThrows()
    {
        using var s = Spotter();
        Assert.False(s.IsReady);
    }

    [Fact]
    public void Spot_WithNoModel_ReturnsNothingAndDoesNotThrow()
    {
        using var s = Spotter();
        var terms = new[] { new VocabularyTerm { Text = "Nemotron" } };
        Assert.Empty(s.Spot(new float[16_000], 16_000, terms, default));
    }

    [Fact]
    public void Spot_WithNoTerms_ReturnsNothing()
    {
        using var s = Spotter();
        Assert.Empty(s.Spot(new float[16_000], 16_000, [], default));
    }

    [Fact]
    public void WarmAndUnload_AreSafeWithNoModel()
    {
        using var s = Spotter();
        s.Warm();
        s.Unload();
        s.Unload();
        Assert.False(s.IsReady);
    }

    [Fact]
    public void Disposed_ReportsNotReady()
    {
        var s = Spotter();
        s.Dispose();
        Assert.False(s.IsReady);
        Assert.Empty(s.Spot(new float[16_000], 16_000, [new VocabularyTerm { Text = "Jot" }], default));
    }

    // MARK: - Term validation (D12)

    [Theory]
    [InlineData("a")]
    [InlineData("I")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ShortTermsAreRejectedWithoutAModel(string? term)
    {
        // The tokenizer returns an EMPTY id list for a single character — measured, and silent. The UI
        // must be able to say so before the 132 MB download has finished.
        using var s = Spotter();
        Assert.Equal(TermSpottability.TooShort, s.CheckTerm(term));
        Assert.Equal(TermSpottability.TooShort, NoVocabularySpotter.Instance.CheckTerm(term));
        Assert.Equal(TermSpottability.TooShort, VocabularyTermRules.CheckLength(term));
    }

    [Fact]
    public void LongEnoughTermsAreUnknownUntilTheModelCanAnswer()
    {
        // NOT "Ok" and NOT "Unsupported": with no checkpoint there is no honest verdict, and a UI that
        // warned here would flag every term as broken during the download.
        using var s = Spotter();
        Assert.Equal(TermSpottability.Unknown, s.CheckTerm("Nemotron"));
        Assert.Equal(TermSpottability.Unknown, s.CheckTerm("Wi-Fi"));
    }

    [Fact]
    public void VocabularyStoreRefusesTermsTheSpotterCanNeverSee()
    {
        var store = new VocabularyStore(null);
        Assert.Null(store.Add("a"));
        Assert.Null(store.Add(" x "));
        Assert.Empty(store.Terms);

        Assert.NotNull(store.Add("Jot"));
        Assert.Single(store.Terms);

        // Editing down to one character is the same mistake through a different door.
        store.Update(store.Terms[0], "q", []);
        Assert.Equal("Jot", store.Terms[0].Text);
    }

    // MARK: - Silence trimming (D10d)

    [Fact]
    public void Trim_RemovesLeadingAndTrailingSilence_AndReportsTheOffset()
    {
        // Correctness, not latency: per_feature normalization is utterance-global, so a long quiet head
        // changes every frame the model sees. Measured consequence — a 48 s clip decodes and the same
        // clip at 50 s returns nothing.
        const int Sr = 16_000;
        float[] pcm = new float[Sr * 3];
        for (int i = Sr; i < Sr * 2; i++) pcm[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / Sr));

        (float[] speech, double lead) = SilenceTrim.Trim(pcm, Sr);

        Assert.InRange(lead, 0.75, 0.85);                              // 1 s of silence less 0.2 s of pad
        Assert.InRange(speech.Length / (double)Sr, 1.3, 1.5);
        Assert.True(speech.Length < pcm.Length);
    }

    [Fact]
    public void Trim_LeavesContinuousSpeechAlone()
    {
        const int Sr = 16_000;
        float[] pcm = new float[Sr];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / Sr));

        (float[] speech, double lead) = SilenceTrim.Trim(pcm, Sr);
        Assert.Equal(0, lead);
        Assert.Equal(pcm.Length, speech.Length);
    }

    [Fact]
    public void Trim_OfPureSilenceKeepsNothing()
    {
        // An absolute floor as well as a relative one, or a recording of room tone would normalize its
        // own noise up to "speech" and the spotter would run on nothing.
        (float[] speech, double lead) = SilenceTrim.Trim(new float[16_000 * 2], 16_000);
        Assert.Empty(speech);
        Assert.Equal(0, lead);
    }

    [Fact]
    public void Trim_IsRelativeSoAQuietRecordingSurvives()
    {
        const int Sr = 16_000;
        float[] pcm = new float[Sr * 2];
        for (int i = Sr / 2; i < Sr; i++) pcm[i] = (float)(0.004 * Math.Sin(2 * Math.PI * 440 * i / Sr));

        (float[] speech, _) = SilenceTrim.Trim(pcm, Sr);
        Assert.InRange(speech.Length / (double)Sr, 0.7, 1.1);
    }

    [Fact]
    public void Trim_HandlesTinyBuffers()
    {
        Assert.Equal(0, SilenceTrim.Trim([], 16_000).LeadSeconds);
        Assert.Empty(SilenceTrim.Trim([], 16_000).Speech);
        Assert.Equal(5, SilenceTrim.Trim(new float[5], 16_000).Speech.Length);   // fewer than 3 windows
    }

    // MARK: - Install recipe

    [Fact]
    public void ModelIsInstalledOnlyWhenAllThreeAssetsArePresent()
    {
        string dir = Path.Combine(_root, "m");
        Directory.CreateDirectory(dir);
        var model = new CtcModel(directory: dir);

        Assert.False(model.IsInstalled);
        File.WriteAllText(model.Graph, "x");
        Assert.False(model.IsInstalled);
        File.WriteAllText(model.Tokens, "x");
        // The tokenizer is the one the sherpa archive does NOT contain. A "model installed" that is
        // missing it would surface as a term list that silently never matches.
        Assert.False(model.IsInstalled);
        File.WriteAllText(model.Tokenizer, "x");
        Assert.True(model.IsInstalled);
    }

    [Fact]
    public void ManifestCarriesAllThreeAssetsWithRealHashes()
    {
        var manifest = new CtcModelInstaller(new CtcModel(directory: @"C:\x")).Manifest;
        var names = manifest.Assets.Select(a => a.Name).ToHashSet();

        Assert.Equal(3, names.Count);
        Assert.Contains(CtcModel.ModelFile, names);
        Assert.Contains(CtcModel.TokensFile, names);
        Assert.Contains(CtcModel.TokenizerFile, names);

        Assert.All(manifest.Assets, a =>
        {
            Assert.True(a.Bytes > 0, $"{a.Name}: size must be exact, not zero");
            Assert.NotNull(a.Sha256);
            Assert.Equal(64, a.Sha256!.Length);
            Assert.True(a.Sha256.All(Uri.IsHexDigit), $"{a.Name}: hash must be hex");
        });
    }

    [Fact]
    public void ManifestUrlFollowsTheReleaseConvention()
    {
        // BaseUrl + Name concatenation depends on the trailing slash, and a typo here is a silent 404
        // at download time rather than a build error.
        var manifest = new CtcModelInstaller(new CtcModel(directory: @"C:\x")).Manifest;
        Assert.StartsWith("https://github.com/vineetu/jot-windows/releases/download/", manifest.BaseUrl);
        Assert.Contains(CtcModelInstaller.ReleaseTag, manifest.BaseUrl);
        Assert.EndsWith("/", manifest.BaseUrl);
        Assert.InRange(manifest.TotalBytes / (1024.0 * 1024.0), 120, 140);   // the "~132 MB" in the copy
    }
}
