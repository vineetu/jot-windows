using Jot.Services.Abstractions;
using Jot.Transcription.Nemotron;

namespace Jot.Vocabulary;

/// <summary>
/// Whether the acoustic spotter could EVER find a term — D12's product requirement, made visible.
/// A term the checkpoint cannot represent does not fail loudly; it silently never matches, forever.
/// </summary>
public enum TermSpottability
{
    /// <summary>No model on disk yet, so there is no honest answer. The UI must NOT warn on this —
    /// warning here would flag every term as broken before the download finishes.</summary>
    Unknown,

    /// <summary>Every piece is in the head's id space; the DP can match it.</summary>
    Ok,

    /// <summary>Under <see cref="VocabularyTermRules.MinTermLength"/> characters. Measured tokenizer
    /// defect: a single-character input encodes to an EMPTY id list — no exception, no <c>&lt;unk&gt;</c>,
    /// just a term that can never match.</summary>
    TooShort,

    /// <summary>The checkpoint's 1024-piece English BPE has no piece for something in this term. It has
    /// no digits, no hyphen, no accented Latin and no CJK — so <c>Wi-Fi</c>, <c>café</c>, <c>3.14</c> and
    /// <c>Zürich</c> can never be spotted, at any threshold, with any tuning.</summary>
    Unsupported,
}

/// <summary>Term rules that need no model, kept out of the spotter so the UI can apply them while the
/// download is still running.</summary>
public static class VocabularyTermRules
{
    /// <summary>Shortest term the spotter can represent. See <see cref="TermSpottability.TooShort"/>.</summary>
    public const int MinTermLength = 2;

    /// <summary><see cref="TermSpottability.TooShort"/>, or <see cref="TermSpottability.Unknown"/> when
    /// the length rule has nothing to say and only the model can answer.</summary>
    public static TermSpottability CheckLength(string? term) =>
        VocabularyStore.SanitizeTerm(term).Length < MinTermLength
            ? TermSpottability.TooShort
            : TermSpottability.Unknown;
}

/// <summary>
/// Seam 1 · where acoustic detections come from. The shipping implementation is
/// <see cref="CtcVocabularySpotter"/>; <see cref="NoVocabularySpotter"/> remains as the null object for
/// tests and for builds with the feature compiled out.
/// </summary>
public interface IVocabularySpotter
{
    /// <summary>False while the spotter's model is absent, downloading, or failed to prepare. The
    /// contract when false is: no spotter, no corrections, NO error — the dictation is untouched.</summary>
    bool IsReady { get; }

    /// <summary>Spot the terms actually spoken, with the audio time range of each. Runs post-stop on
    /// the full <c>RecordingResult.Samples</c> buffer — never the WAV, which is quantized to 16-bit
    /// and is therefore not the audio the transcriber saw.</summary>
    IReadOnlyList<VocabularyGate.Detection> Spot(
        float[] samples, int sampleRate, IReadOnlyList<VocabularyTerm> terms, CancellationToken ct);

    /// <summary>
    /// THE term-validation entry point for the UI (D12). Never throws, never blocks on a download;
    /// returns <see cref="TermSpottability.Unknown"/> whenever it cannot tell.
    ///
    /// A DEFAULT implementation on purpose: the model-free half of the rule is the honest answer for any
    /// spotter that has no checkpoint, so a test double or a future engine gets it right by doing nothing.
    /// </summary>
    TermSpottability CheckTerm(string? term) => VocabularyTermRules.CheckLength(term);
}

/// <summary>The null object: no model, no detections, and only the model-free half of term validation.</summary>
public sealed class NoVocabularySpotter : IVocabularySpotter
{
    public static readonly NoVocabularySpotter Instance = new();
    public bool IsReady => false;

    public IReadOnlyList<VocabularyGate.Detection> Spot(
        float[] samples, int sampleRate, IReadOnlyList<VocabularyTerm> terms, CancellationToken ct) => [];

    public TermSpottability CheckTerm(string? term) => VocabularyTermRules.CheckLength(term);
}

/// <summary>
/// The ONE orchestration seam between the recording pipeline and the vocabulary core: decide whether
/// vocabulary may run at all, spot, gate, record provenance, and build the ask deck. Having exactly
/// one class means there is exactly one place to test the invariants.
///
/// This class does NOT own the never-break-a-dictation guarantee (D6) — the deadline and the
/// fall-back-to-ungated-text live at the call site in <c>RecorderController</c>, because a hang here
/// throws nothing and only the caller can abandon it.
/// </summary>
public sealed class VocabularyRunner
{
    /// <summary>What one gated dictation produced. <see cref="Deck"/> has no consumer in v1.3 — the
    /// ask card is the next slice — but it is computed here so the filter below is the shipped path
    /// and not something the card has to remember.</summary>
    public sealed record Outcome(
        string Text,
        IReadOnlyList<VocabularyGate.Proposal> Proposals,
        IReadOnlyList<AskPolicy.Selection> Deck)
    {
        public static Outcome Unchanged(string text) => new(text, [], []);

        /// <summary>The pill-chip payload: APPLIED corrections only, de-duped by
        /// <c>(originalWord, term)</c> so a term applied three times reads as one (ux §3.3).
        /// Blocked near-misses are deliberately absent — the paste already happened, so the pill
        /// cannot help with them; they live on the review surface (ux §3.5).</summary>
        public IReadOnlyList<VocabularyCorrection> Corrections =>
            Proposals.Where(p => p.Outcome == "applied")
                     .Select(p => new VocabularyCorrection(p.OriginalWord, p.Term))
                     .Distinct()
                     .ToList();
    }

    private readonly ISettingsStore _settings;
    private readonly VocabularyStore _terms;
    private readonly CorrectionStore _corrections;
    private readonly CorrectionProvenance _provenance;
    private readonly IVocabularySpotter _spotter;
    private readonly ITextVocabularySpotter? _corrector;
    private readonly ICommonWordsProvider _commonWords;
    private readonly IDiagnosticsSink _diagnostics;

    public VocabularyRunner(
        ISettingsStore settings,
        VocabularyStore terms,
        CorrectionStore corrections,
        CorrectionProvenance provenance,
        IVocabularySpotter? spotter = null,
        ICommonWordsProvider? commonWords = null,
        IDiagnosticsSink? diagnostics = null,
        ITextVocabularySpotter? corrector = null)
    {
        _settings = settings;
        _terms = terms;
        _corrections = corrections;
        _provenance = provenance;
        _spotter = spotter ?? NoVocabularySpotter.Instance;
        _corrector = corrector;
        _commonWords = commonWords ?? EmbeddedCommonWordsProvider.Shared;
        _diagnostics = diagnostics ?? JotLogDiagnosticsSink.Instance;
    }

    // MARK: - Gating

    /// <summary>What a language can have. Auto-detect is <see cref="Off"/> in both: the transcribers
    /// throw the model's <c>&lt;xx-YY&gt;</c> locale token away, so there is no per-recording resolved
    /// language to gate on.</summary>
    public enum VocabularyMode
    {
        /// <summary>No common-word list ships for this language, so the gate's over-correction brake
        /// would be absent. Partial enablement is worse than off — the shipped Spanish incident was
        /// vocab ["Lisa"] clobbering "lista", and the brake is what stops it.</summary>
        Off,

        /// <summary>Spelling-only: <see cref="VocabularyCorrector"/> matches terms against the finished
        /// transcript. No model, no download, no licence.</summary>
        Textual,

        /// <summary>Sound-alike: the CTC spotter hears the term in the audio. English only — that is
        /// what the checkpoint is trained on, and running it on other audio is actively harmful.</summary>
        Acoustic,
    }

    /// <summary>
    /// D5, re-derived per language. English gets the acoustic spotter; a language the corrector was
    /// MEASURED safe in gets the corrector; the rest stay off.
    ///
    /// MEASURED (1041 FLEURS clips, docs/plans/vocabulary-corrector-vs-spotter.md): the textual path
    /// recovers 34–37 % of the terms the engine got wrong at 0.27 false applies per 1000 words on a
    /// realistic 25-term list. Residual stacking (corrector fills terms the spotter NEVER heard) is
    /// deliberately NOT English policy — it bought +5.8 points of recall for +6 false applies
    /// (against the spotter's ZERO), all six "the term was never spoken" near-neighbour names.
    /// A later arm lets the corrector place only terms the spotter HEARD and the gate then lost;
    /// that is a merge, not a third Mode, and it is not this table.
    ///
    /// MEASURED AGAIN, per language (9500 more clips, docs/plans/vocabulary-brake-per-language.md),
    /// because all of the above was English and the corrector's only safety net outside its own
    /// threshold is a frequency list whose coverage varies from 88.8 % of types to 56.9 %. Eighteen of
    /// the nineteen reachable languages clear a 1.0-false-applies-per-1000-words budget, six of them
    /// only after tightening; Slovenian does not clear it at any setting worth shipping and is Off.
    ///
    /// TWO conditions, not one, and they are different claims: a list must EXIST (no list ⇒ no brake ⇒
    /// the shipped `lista → Lisa` incident with the safety net removed), and the measurement must say
    /// the list is strong enough to be worth having.
    /// </summary>
    public static VocabularyMode ModeFor(string? language)
    {
        string locale = NemotronLocales.Normalize(language);
        if (locale.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return VocabularyMode.Acoustic;
        return EmbeddedCommonWordsProvider.ResourceFor(locale) is not null
               && VocabularyLimits.TextualShips(locale)
            ? VocabularyMode.Textual
            : VocabularyMode.Off;
    }

    /// <summary>Whether the ACOUSTIC spotter may run — still the English-only question, and still what
    /// the 132 MB download offer and the sound-alike copy key on.</summary>
    public static bool LanguageSupported(string? language) =>
        ModeFor(language) == VocabularyMode.Acoustic;

    /// <summary>What this runner can actually deliver: the language's mode, narrowed by the detection
    /// sources it was given. A runner built without a corrector cannot serve a textual-only language,
    /// and saying so here is what keeps <see cref="ShouldRun"/> honest.</summary>
    public VocabularyMode Mode
    {
        get
        {
            VocabularyMode mode = ModeFor(_settings.Current.Language);
            return mode == VocabularyMode.Textual && _corrector is null ? VocabularyMode.Off : mode;
        }
    }

    /// <summary>Master toggle ON, at least one term, and a language something can run on. Model
    /// readiness is deliberately NOT here: a term list with no model yet must still save and still
    /// short-circuit silently, which <see cref="Run"/> does.</summary>
    public bool ShouldRun =>
        _settings.Current.VocabularyEnabled
        && _terms.Terms.Count > 0
        && Mode != VocabularyMode.Off;

    // MARK: - The run

    /// <summary>
    /// Spot → gate → record. Returns the input unchanged for every "not applicable" case, so the
    /// caller can assign the result unconditionally.
    ///
    /// Called AFTER <c>TextPipeline.Clean</c> and BEFORE <c>_store.Add</c>: nothing mutates the
    /// transcript between those two points, so the proposals' <c>PublishedStart</c>/
    /// <c>PublishedLength</c> are exact against the string that is both saved and pasted.
    /// </summary>
    public Outcome Run(string text, float[] samples, int sampleRate, TimeSpan duration, CancellationToken ct = default)
    {
        if (!ShouldRun || string.IsNullOrWhiteSpace(text)) return Outcome.Unchanged(text);

        List<VocabularyTerm> terms = [.. _terms.Terms];

        // ONE detector per dictation. In English the spotter is that detector — it recovered 45.8 %
        // of missed terms to the corrector's 37.9 %, with FEWER false applies — and residual
        // stacking (corrector fills terms the spotter NEVER heard) traded +5.8 pp for +6 false
        // applies on focused-25 (52.9 / 0.27 vs the spotter's 47.1 / 0.00). Those six FPs were
        // all "the term was never spoken" (Terry→Jerry, Maria→Marie, …). Do not reopen that arm.
        //
        // The corrector MAY still run in English, but only as a placer: terms the spotter heard
        // and the gate then lost (spot-unplaced or decision=BLOCK). MEASURED on the same 1041
        // clips (heard-unplaced, 2026-08-14): one-pass-keep is 49.4 / 0.00 on focused-25
        // (+2 recoveries, both near-concat names) and does not add a false apply on all-155
        // either (0.71, same as spotter-alone). Second-pass and one-pass-drop both pick up the
        // two David→Dravid FPs the keep merge structurally leaves claimed. Residual stays 0.27.
        //
        // The no-model case is unchanged: English with the checkpoint absent falls back to
        // spelling matches through the same gate. Still silent, still no error.
        IReadOnlyList<VocabularyGate.Detection> detections =
            Mode == VocabularyMode.Acoustic && _spotter.IsReady
                ? _spotter.Spot(samples, sampleRate, terms, ct)
                : _corrector?.Spot(text, terms, duration.TotalSeconds, _settings.Current.Language, ct) ?? [];
        if (detections.Count == 0) return Outcome.Unchanged(text);

        ct.ThrowIfCancellationRequested();

        IReadOnlyList<OverrideEntry> overrides = _corrections.Snapshot();
        string? resource = EmbeddedCommonWordsProvider.ResourceFor(
            NemotronLocales.Normalize(_settings.Current.Language));
        IReadOnlyList<VocabularyGate.Detection> enriched = Enrich(detections, terms);

        // Classification pass: which heard terms did the gate lose? No logs — the publish
        // pass below is the one the review surface and the user-facing trace see.
        if (Mode == VocabularyMode.Acoustic && _spotter.IsReady && _corrector is not null)
        {
            VocabularyGate.Result probe = VocabularyGate.ApplyFromDetections(
                text, enriched, duration.TotalSeconds, _commonWords, resource, overrides);
            IReadOnlyList<VocabularyTerm> prize = UnplacedHeard(enriched, probe, text, terms);
            if (prize.Count > 0)
            {
                IReadOnlyList<VocabularyGate.Detection> textual = _corrector.Spot(
                    text, prize, duration.TotalSeconds, _settings.Current.Language, ct);
                if (textual.Count > 0)
                    enriched = [.. enriched, .. Enrich(textual, terms)];
            }
        }

        VocabularyGate.Result result = VocabularyGate.ApplyFromDetections(
            text,
            enriched,
            duration.TotalSeconds,
            _commonWords,
            resource,
            overrides,
            _diagnostics);

        // Stash against the GATE-OUTPUT text — the string those offsets index into. Commit happens
        // once the recording has an id (see Commit), so an aborted delivery leaves nothing behind.
        _provenance.Record(result.Proposals, result.Text);

        IReadOnlyList<AskPolicy.Selection> deck = SelectAsks(
            _provenance.PendingRecords,
            overrides,
            _corrections.KeyboardSuppressedPairs(),
            _corrections.MergeAskedPairs());

        return new Outcome(result.Text, result.Proposals, deck);
    }

    /// <summary>
    /// THE ask-deck filter, and it is load-bearing (D8). <c>AskPolicy.WorthAsking</c> ends
    /// <c>r.Outcome == "applied" || Prior(r) &gt; 0</c>, while <c>VocabularyGate.Decide</c>'s override
    /// branch requires <c>!isCommon</c>. So the moment anything teaches a common-word pair — the
    /// review surface's KEPT pick, or the right-click "Add to Vocabulary" gesture, both +1 — that
    /// pair carries <c>Prior &gt; 0</c> forever while <c>Decide</c> keeps blocking it, and it would be
    /// asked on EVERY later dictation, indefinitely. That is the exact nagging the owner ruled out.
    ///
    /// This is app-side composition, NOT a policy divergence: <c>AskPolicy</c> ships unchanged. If a
    /// blocked pair should ever get a card, it needs its own KEPT card copy and its own suppression
    /// path — escalate, do not quietly widen this.
    /// </summary>
    public static IReadOnlyList<AskPolicy.Selection> SelectAsks(
        IReadOnlyList<CorrectionRecord> records,
        IReadOnlyList<OverrideEntry> overrides,
        IReadOnlySet<string> keyboardSuppressed,
        IReadOnlySet<string> mergeAsked) =>
        AskPolicy.Select(
            records.Where(r => r.Outcome == "applied").ToList(),
            overrides, keyboardSuppressed, mergeAsked);

    // MARK: - Provenance lifecycle

    /// <summary>Clear the pending slot at the START of every dictation, so a rewrite or an aborted
    /// run can never have its proposals committed under the NEXT transcript's id.</summary>
    public void ClearPending() => Guarded(_provenance.ClearPending);

    /// <summary>Persist this dictation's proposals under the saved recording's id — immediately
    /// after <c>_store.Add</c>, which is where the id first exists.</summary>
    public void Commit(Guid recordingId) => Guarded(() => _provenance.Commit(recordingId));

    /// <summary>
    /// Bank the ask card's explicit answers, AFTER <see cref="Commit"/> (the verdict ledger is keyed
    /// by the recording id, which does not exist before it). Three effects per answer:
    ///
    /// (1) the per-occurrence verdict, so the review surface shows the row already resolved and the
    /// owner is never asked the same thing twice across the two surfaces (ux §4.5);
    /// (2) the learning delta, clamped to ±1 per mapping per transcript by the provenance ledger;
    /// (3) <b>keyboard suppression</b> — see the comment on <see cref="SuppressFurtherAsks"/>.
    /// </summary>
    public void ApplyAskAnswers(
        Guid recordingId,
        IReadOnlyList<AskAnswer> answers,
        IReadOnlyList<AskDeck.AskSplice>? splices = null) => Guarded(() =>
    {
        // Report OUR OWN edits before anything reads the payload. The reconcile's blind diff is for
        // EXTERNAL edits; on a revert ("Nemotron" → "neumotron") it is genuinely ambiguous and maps
        // the anchor into the middle of the new word, which would hide the row it belongs to.
        // Applied in order, each against the text as it stood right after it, which is exactly the
        // sequence NoteSelfEdit's fingerprint check expects.
        foreach (AskDeck.AskSplice s in splices ?? [])
            _provenance.NoteSelfEdit(recordingId, s.RecordKey, s.Start, s.OldLength, s.NewLength, s.TextAfter);

        foreach (AskAnswer a in answers)
        {
            foreach (CorrectionProvenance.MappingDelta d in
                     _provenance.SetVerdict(recordingId, a.Record, a.Verdict))
            {
                _corrections.Adjust(d.OriginalWord, d.Term, d.Delta);
            }
            SuppressFurtherAsks(a.Record);
        }
    });

    /// <summary>
    /// "Once the user answers, don't ask again" — the owner's stated requirement, and the one thing
    /// on this path that nothing else writes.
    ///
    /// <c>AskPolicy.WorthAsking</c>'s first line excludes <c>keyboardSuppressed</c> pairs, and the
    /// doc bills that as the live guarantee. It is NOT live by itself here: the only writers of that
    /// set are <c>NoteBlockedKeep</c> (blocked pairs only — our deck has none, D8) and
    /// <c>SuppressBlock</c> ("Stop asking", which v1.3 ships no UI for). An APPLIED pair answered
    /// "use the term" gains <c>Prior &gt; 0</c> and stays <c>Outcome == "applied"</c>, so it would be
    /// asked on EVERY later dictation forever. Routing an answered pair through
    /// <see cref="CorrectionStore.SuppressBlock"/> closes that: it is the only writer of the set the
    /// keyboard reads, the gate deliberately ignores it, and transcript review is unaffected — so
    /// the pair stops being asked without becoming invisible.
    /// </summary>
    private void SuppressFurtherAsks(CorrectionRecord record) =>
        _corrections.SuppressBlock(record.OriginalWord, record.Term);

    // D6 reaches these too: the bookkeeping around a dictation must never be able to lose one.
    private static void Guarded(Action action)
    {
        try { action(); }
        catch (Exception ex) { Services.JotLog.Error("vocabulary provenance failed", ex); }
    }

    /// <summary>
    /// Terms the spotter heard and the gate then lost — a kept (BLOCK) proposal, or no proposal
    /// and the engine did not already write the term. Identity no-ops stay out: there is nothing
    /// to place. Unheard terms stay out: that is the residual set whose 6 focused-25 false
    /// applies (0.27 / 1000) sank every previous stack.
    /// </summary>
    internal static IReadOnlyList<VocabularyTerm> UnplacedHeard(
        IReadOnlyList<VocabularyGate.Detection> acoustic,
        VocabularyGate.Result gated,
        string originalText,
        IReadOnlyList<VocabularyTerm> terms)
    {
        var heard = new HashSet<string>(StringComparer.Ordinal);
        foreach (VocabularyGate.Detection d in acoustic)
            heard.Add(CorrectionKey.Normalize(d.Term));

        var applied = new HashSet<string>(StringComparer.Ordinal);
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (VocabularyGate.Proposal p in gated.Proposals)
        {
            string key = CorrectionKey.Normalize(p.Term);
            if (p.Outcome == "applied") applied.Add(key);
            else blocked.Add(key);
        }

        var prize = new List<VocabularyTerm>();
        foreach (VocabularyTerm t in terms)
        {
            string key = CorrectionKey.Normalize(t.Text);
            if (!heard.Contains(key) || applied.Contains(key)) continue;
            if (blocked.Contains(key) || !AlreadySpelled(originalText, t.Text))
                prize.Add(t);
        }
        return prize;
    }

    private static bool AlreadySpelled(string text, string term)
    {
        IReadOnlyList<(int Start, int End)> spans = VocabularyGate.WordSpans(text);
        if (spans.Count == 0) return false;
        string[] words = new string[spans.Count];
        for (int i = 0; i < spans.Count; i++)
            words[i] = CorrectionKey.Normalize(text[spans[i].Start..spans[i].End]);
        string[] want = term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(CorrectionKey.Normalize).Where(w => w.Length > 0).ToArray();
        if (want.Length == 0) return false;
        for (int i = 0; i + want.Length <= words.Length; i++)
        {
            bool all = true;
            for (int k = 0; k < want.Length && all; k++) all = words[i + k] == want[k];
            if (all) return true;
        }
        return false;
    }

    // Feed-time alias enrichment (see VocabularyStore.FeedAliases) plus the user's own aliases, which
    // the spotter has no reason to carry back. Detections for terms no longer in the list pass
    // through untouched rather than being dropped — the gate still guards them.
    // Internal, not private: the CLI runs the same gate call and skipping this silently changes the
    // gate's plausibility inputs.
    internal static IReadOnlyList<VocabularyGate.Detection> Enrich(
        IReadOnlyList<VocabularyGate.Detection> detections, IReadOnlyList<VocabularyTerm> terms)
    {
        var byText = new Dictionary<string, VocabularyTerm>(StringComparer.Ordinal);
        foreach (VocabularyTerm t in terms) byText[CorrectionKey.Normalize(t.Text)] = t;

        var result = new List<VocabularyGate.Detection>(detections.Count);
        foreach (VocabularyGate.Detection d in detections)
        {
            if (!byText.TryGetValue(CorrectionKey.Normalize(d.Term), out VocabularyTerm? term))
            {
                result.Add(d);
                continue;
            }
            var aliases = new List<string>(d.Aliases);
            foreach (string a in VocabularyStore.FeedAliases(term))
            {
                if (!aliases.Any(x => CorrectionKey.Normalize(x) == CorrectionKey.Normalize(a)))
                    aliases.Add(a);
            }
            result.Add(d with { Aliases = aliases });
        }
        return result;
    }
}
