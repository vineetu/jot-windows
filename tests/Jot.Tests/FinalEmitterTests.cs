using Jot.Cli;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// The stream protocol is frozen across platforms, so these vectors are the contract: what gets
/// committed, when, and what a finals-only consumer sees when the engine misbehaves. The revision branch
/// is unreachable on this engine (append-only tokens, monotone chunks, prefix-pure detokenize) — it is
/// covered here synthetically because it can never be provoked live.
/// </summary>
public class FinalEmitterTests
{
    private sealed class Capture
    {
        public List<string> Emitted { get; } = [];
        public List<string> Warnings { get; } = [];
    }

    private static (FinalEmitter Emitter, Capture Log) Make(
        bool spaceless = false, Func<string, string>? correct = null)
    {
        var log = new Capture();
        var emitter = new FinalEmitter(correct, log.Emitted.Add, spaceless, log.Warnings.Add);
        return (emitter, log);
    }

    [Fact]
    public void HoldsBackTheInProgressTokenUntilAWhitespaceBoundary()
    {
        (FinalEmitter e, Capture log) = Make();

        Assert.True(e.AcceptPartial("hello wor"));
        Assert.Equal(["hello"], log.Emitted);   // "wor" is still in progress

        Assert.True(e.AcceptPartial("hello world tod"));
        Assert.Equal(["hello", "world"], log.Emitted);
    }

    [Fact]
    public void NothingCommitsWhileTheFirstWordIsStillGrowing()
    {
        (FinalEmitter e, Capture log) = Make();
        Assert.False(e.AcceptPartial("hel"));
        Assert.False(e.AcceptPartial("hell"));
        Assert.False(e.AcceptPartial("hello"));
        Assert.Empty(log.Emitted);
    }

    [Fact]
    public void EverythingNewUpToTheBoundaryIsOneSegment()
    {
        // A partial that jumps several words forward commits them as a single final, not one per word.
        (FinalEmitter e, Capture log) = Make();
        e.AcceptPartial("the quick brown fox ju");
        Assert.Equal(["the quick brown fox"], log.Emitted);
    }

    [Fact]
    public void MultiplePartialsCommitEachNewWholeWordExactlyOnce()
    {
        (FinalEmitter e, Capture log) = Make();
        foreach (string p in new[]
        {
            "the ", "the quick ", "the quick brown ", "the quick brown fo", "the quick brown fox ",
        })
        {
            e.AcceptPartial(p);
        }
        Assert.Equal(["the", "quick", "brown", "fox"], log.Emitted);
    }

    [Fact]
    public void SpacelessScriptCommitsAtCjkSentencePunctuation()
    {
        (FinalEmitter e, Capture log) = Make(spaceless: true);

        Assert.True(e.AcceptPartial("你好，"));
        Assert.Equal(["你好，"], log.Emitted);

        Assert.True(e.AcceptPartial("你好，世界。"));
        Assert.Equal(["你好，", "世界。"], log.Emitted);

        // No boundary yet in the new pending run.
        Assert.False(e.AcceptPartial("你好，世界。今天"));
        Assert.Equal(2, log.Emitted.Count);
    }

    [Fact]
    public void AsciiPunctuationIsNotACommitBoundary()
    {
        // The ASCII set is deliberately excluded: "3,000" must not split.
        (FinalEmitter e, Capture log) = Make(spaceless: true);
        Assert.False(e.AcceptPartial("费用3,000元."));
        Assert.Empty(log.Emitted);
    }

    [Fact]
    public void SpacelessLengthBackstopCommitsAllButSixteen()
    {
        (FinalEmitter e, Capture log) = Make(spaceless: true);

        Assert.False(e.AcceptPartial(new string('中', 47)));
        Assert.Empty(log.Emitted);

        Assert.True(e.AcceptPartial(new string('中', 50)));
        Assert.Equal(new string('中', 34), Assert.Single(log.Emitted));
    }

    [Fact]
    public void SpacedScriptNeverUsesTheSpacelessFallbacks()
    {
        (FinalEmitter e, Capture log) = Make(spaceless: false);
        Assert.False(e.AcceptPartial(new string('x', 60) + "。"));
        Assert.Empty(log.Emitted);
    }

    [Fact]
    public void RevisionWarnsOnceAndAdoptsTheNewHypothesisWholesale()
    {
        (FinalEmitter e, Capture log) = Make();
        e.AcceptPartial("hello world ");
        Assert.Equal(["hello world"], log.Emitted);

        // The divergent tail is dropped, not emitted as garbled overlap.
        Assert.False(e.AcceptPartial("goodbye world "));
        Assert.Equal(["hello world"], log.Emitted);
        Assert.Single(log.Warnings);

        Assert.False(e.AcceptPartial("something else "));
        Assert.Single(log.Warnings);

        // Committing resumes from the adopted hypothesis.
        Assert.True(e.AcceptPartial("something else again "));
        Assert.Equal(["hello world", "again"], log.Emitted);
    }

    [Fact]
    public void FinishFlushesTheTailWithoutASpuriousRevisionWarning()
    {
        // The committed prefix ends in whitespace and the engine's final text does not carry it — the
        // comparison must be right-trimmed or every clean session ends in a warning.
        (FinalEmitter e, Capture log) = Make();
        e.AcceptPartial("hello world tail");
        Assert.Equal(["hello world"], log.Emitted);

        e.FinishSession("hello world tail");
        Assert.Equal(["hello world", "tail"], log.Emitted);
        Assert.Empty(log.Warnings);
    }

    [Fact]
    public void FinishEmitsNothingWhenTheEngineAddsNothing()
    {
        (FinalEmitter e, Capture log) = Make();
        e.AcceptPartial("hello world ");
        e.FinishSession("hello world");
        Assert.Equal(["hello world"], log.Emitted);
        Assert.Empty(log.Warnings);
    }

    [Fact]
    public void FinishDivergenceSnapsBackToOneWordOfDuplication()
    {
        (FinalEmitter e, Capture log) = Make();
        e.AcceptPartial("hello world ");
        Assert.Equal(["hello world"], log.Emitted);

        // Re-cased inside committed text: the tail is still flushed, snapped back to the previous word
        // boundary, so at most "world" repeats.
        e.FinishSession("Hello world, again");
        Assert.Equal(["hello world", "world, again"], log.Emitted);
        Assert.Single(log.Warnings);
    }

    [Fact]
    public void FinishThatShrinksBelowTheCommittedPrefixEmitsNothing()
    {
        (FinalEmitter e, Capture log) = Make();
        e.AcceptPartial("hello world ");
        e.FinishSession("bye");
        Assert.Equal(["hello world"], log.Emitted);
        Assert.Single(log.Warnings);
    }

    [Fact]
    public void ResetSessionStartsTheNextHypothesisFromEmptyAndKeepsTheWarningOnce()
    {
        (FinalEmitter e, Capture log) = Make();
        e.AcceptPartial("hello world ");
        e.FinishSession("hello world");
        e.ResetSession();

        // A fresh session's first partial is not a prefix of the old one; without the reset this would
        // read as a revision.
        Assert.True(e.AcceptPartial("second session "));
        Assert.Equal(["hello world", "second session"], log.Emitted);
        Assert.Empty(log.Warnings);
    }

    [Fact]
    public void VocabularyCorrectionRunsPerCommittedSegment()
    {
        (FinalEmitter e, Capture log) = Make(correct: s => s.Replace("kubernetis", "Kubernetes"));
        e.AcceptPartial("deploy kubernetis now ");
        Assert.Equal(["deploy Kubernetes now"], log.Emitted);
    }

    [Fact]
    public void NdjsonFramingIsOneCompactObjectWithRawUnicode()
    {
        Assert.Equal("""{"type":"final","text":"hello"}""", Ndjson.Serialize("hello"));
        Assert.Equal("""{"type":"final","text":"你好"}""", Ndjson.Serialize("你好"));
        Assert.Equal("""{"type":"final","text":"say \"hi\""}""", Ndjson.Serialize("say \"hi\""));
    }
}
