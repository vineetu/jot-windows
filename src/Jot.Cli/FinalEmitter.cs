namespace Jot.Cli;

/// <summary>
/// Turns the engine's cumulative partial hypothesis into committed NDJSON finals.
///
/// It rides on an append-only hypothesis: the partial only ever grows and committed text is never
/// revised. On this engine that is structural (tokens append, fed chunks are monotone, detokenize is a
/// pure prefix function), so the revision branch below is defense — warned ONCE, recovered
/// conservatively, and covered by synthetic tests rather than by live measurement. Already-printed text
/// cannot be retracted in a finals-only protocol.
///
/// Locked because the Ctrl-C handler's finalize runs on a different thread than the read loop's emits.
/// </summary>
internal sealed class FinalEmitter
{
    /// <summary>Spaceless-script backstop: a pending run this long commits all but the holdback.</summary>
    private const int SpacelessCommitThreshold = 48;

    private const int SpacelessHoldback = 16;

    // Deliberately NOT the ASCII set: ASCII commas and periods appear inside numbers ("3,000") and
    // committing there would split them.
    private static readonly char[] CjkSentencePunctuation = ['。', '！', '？', '；', '，', '、'];

    private const string RevisionWarning =
        "jot: warning: engine revised committed text (append-only assumption violated — see design R11); " +
        "recovered conservatively, adjacent words may repeat or drop";

    private readonly object _lock = new();
    private readonly Func<string, string>? _correct;
    private readonly Action<string> _emit;
    private readonly Action<string> _warn;

    /// <summary>Languages written without inter-word spaces: whitespace boundaries never arrive, so
    /// without a fallback a zh stream emits nothing until EOF — batch in disguise.</summary>
    private readonly bool _spacelessScript;

    /// <summary>The prefix of the cumulative hypothesis already written out.</summary>
    private string _emitted = "";

    private bool _warnedRevision;

    /// <param name="correct">Per-segment vocabulary correction, applied to the committed text only.</param>
    /// <param name="emit">Where a committed segment goes. Injected so tests capture without stdout.</param>
    public FinalEmitter(
        Func<string, string>? correct,
        Action<string> emit,
        bool spacelessScript,
        Action<string>? warn = null)
    {
        _correct = correct;
        _emit = emit;
        _spacelessScript = spacelessScript;
        _warn = warn ?? Console.Error.WriteLine;
    }

    /// <summary>Feeds a new cumulative hypothesis. Returns whether anything was committed.</summary>
    public bool AcceptPartial(string partial)
    {
        lock (_lock)
        {
            if (!partial.StartsWith(_emitted, StringComparison.Ordinal))
            {
                // Mid-stream revision: adopt the new hypothesis wholesale. The divergent tail is dropped
                // rather than emitted as garbled overlap.
                WarnRevisionOnce();
                _emitted = partial;
                return false;
            }

            string pending = partial[_emitted.Length..];

            // Hold back the trailing in-progress token: commit only through the last whitespace so a
            // word is never split across two finals.
            int boundary = LastWhitespace(pending);
            if (boundary >= 0)
            {
                bool committed = Commit(pending[..boundary]);
                _emitted = partial[..(_emitted.Length + boundary + 1)];
                return committed;
            }

            if (!_spacelessScript) return false;

            int cut = pending.LastIndexOfAny(CjkSentencePunctuation);
            if (cut >= 0)
            {
                bool committed = Commit(pending[..(cut + 1)]);
                _emitted = partial[..(_emitted.Length + cut + 1)];
                return committed;
            }

            if (pending.Length >= SpacelessCommitThreshold)
            {
                int at = pending.Length - SpacelessHoldback;
                // An unpaired surrogate would not survive JSON encoding.
                if (char.IsLowSurrogate(pending[at])) at--;
                bool committed = Commit(pending[..at]);
                _emitted = partial[..(_emitted.Length + at)];
                return committed;
            }
            return false;
        }
    }

    /// <summary>
    /// EOF / Ctrl-C / session roll: <c>Finish()</c> returned the whole-session transcript — emit
    /// whatever it carries beyond the committed prefix.
    ///
    /// The committed prefix always ends at a whitespace boundary and the engine's final text does not
    /// carry that trailing whitespace, so the comparison runs right-trimmed; without that, every clean
    /// session ends in a spurious revision warning.
    ///
    /// If the final text diverges INSIDE committed text (re-casing, re-punctuating), the tail beyond the
    /// committed length is still emitted, snapped back to the previous word boundary — bounded
    /// duplication of at most one word beats silently losing the last utterance, which is the whole
    /// point of finalization.
    /// </summary>
    public void FinishSession(string finalText)
    {
        lock (_lock)
        {
            string committedPrefix = _emitted.TrimEnd();

            if (finalText.StartsWith(committedPrefix, StringComparison.Ordinal))
            {
                Commit(finalText[committedPrefix.Length..]);
            }
            else
            {
                WarnRevisionOnce();
                if (finalText.Length <= committedPrefix.Length)
                {
                    _emitted = finalText;
                    return;
                }
                int start = committedPrefix.Length;
                while (start > 0 && !char.IsWhiteSpace(finalText[start - 1])) start--;
                Commit(finalText[start..]);
            }
            _emitted = finalText;
        }
    }

    /// <summary>A rolled session restarts its hypothesis from empty. The revision warning stays one per
    /// process — it reports an engine invariant, not a session.</summary>
    public void ResetSession()
    {
        lock (_lock) _emitted = "";
    }

    private bool Commit(string segment)
    {
        string trimmed = segment.Trim();
        if (trimmed.Length == 0) return false;
        _emit(_correct is null ? trimmed : _correct(trimmed));
        return true;
    }

    private void WarnRevisionOnce()
    {
        if (_warnedRevision) return;
        _warnedRevision = true;
        _warn(RevisionWarning);
    }

    private static int LastWhitespace(string s)
    {
        for (int i = s.Length - 1; i >= 0; i--)
            if (char.IsWhiteSpace(s[i])) return i;
        return -1;
    }
}
