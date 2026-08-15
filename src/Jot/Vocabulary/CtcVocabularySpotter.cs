using System.IO;
using Jot.Services;
using Jot.Services.Abstractions;
using Jot.Transcription.Ctc;
using Jot.Transcription.Onnx;

namespace Jot.Vocabulary;

/// <summary>
/// Seam 1, for real: the post-stop CTC keyword spotter. Samples in, "these terms were said, here" out.
///
/// Pipeline, in this order and for these reasons:
///   1. TRIM leading/trailing silence. Correctness first, latency second — <c>per_feature</c>
///      normalization is utterance-GLOBAL, so silence changes every frame's normalized value. Measured:
///      a 48 s clip decodes; the same clip padded to 50 s of the same recording returns nothing at all.
///   2. Bail above <see cref="MaxSpeechSeconds"/>. Chosen from the measured latency table, not taste.
///   3. Mel → single ONNX graph → [frames, 1025] log-probs (already log-softmaxed).
///   4. <see cref="CtcWordSpotter"/> DP over every term and alias in one pass.
///   5. Shift every detection back by the leading-trim offset. The DP works in TRIMMED time; the gate
///      places detections by proportion of the FULL recording, so skipping this puts every correction
///      early in exact proportion to the silence removed.
///
/// Threading: called from the stop path, never from <c>LiveTranscription</c>'s poll thread (that would
/// add straight into the realtime budget and degrade live captions to batch by construction). Guarded
/// with a lock anyway because Settings can call <see cref="Unload"/> from the UI thread mid-pass.
/// </summary>
public sealed class CtcVocabularySpotter : IVocabularySpotter, IDisposable
{
    /// <summary>
    /// Hard cutoff on TRIMMED speech: above this the pass is skipped entirely and logged.
    ///
    /// Sized against the CPU floor, because that is what a machine without a usable D3D12 GPU gets.
    /// MEASURED end-to-end (trim + mel + graph + DP) by <c>CtcSpotterTests.M2_LatencyCpuVersusDirectMl</c>
    /// on this machine, p50 of 5 runs after warm-up:
    ///
    ///           trimmed speech   CPU EP     DirectML
    ///           6.4 s             181 ms      129 ms
    ///          38.0 s            1493 ms      801 ms
    ///          58.9 s            2421 ms     1038 ms
    ///          89.5 s            4177 ms     1453 ms
    ///
    /// So CPU is super-linear and crosses <c>RecorderController.SlowStopMs = 5000</c> just past 120 s,
    /// while DirectML is close to flat past 40 s (the graph stops being the cost; the managed mel
    /// front-end becomes it). 120 s is therefore the CPU-safe bound, and the same bound is kept on the
    /// GPU path deliberately — a cutoff that moved with the tier would make the feature's behaviour
    /// depend on the user's hardware in a way nothing in the UI could explain.
    /// </summary>
    public const double MaxSpeechSeconds = 120.0;

    /// <summary>Below this there is no speech to spot in and the front-end's (n-1) statistics are
    /// meaningless. Cheaper to skip than to reason about.</summary>
    public const double MinSpeechSeconds = 0.25;

    private readonly CtcModel _model;
    private readonly OnnxSessionFactory _factory;
    private readonly ISettingsStore? _settings;
    private readonly float _minScore;

    /// <summary>Guards the ONNX session and a spotting pass — held for 1–2 s by <see cref="Warm"/>
    /// and for the whole of <see cref="SpotCore"/>.</summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// Guards the two SMALL text artifacts (tokens + tokenizer, 262 KB, ~ms to load) and nothing
    /// else. A second lock, not hygiene:
    ///
    /// <see cref="CheckTerm"/> is called from the UI thread on every keystroke in the term box AND
    /// once per row when the Vocabulary page builds. When it shared <see cref="_gate"/> it used
    /// <c>TryEnter</c> to avoid freezing on a running pass — and returned <c>Unknown</c> (⇒ no
    /// warning) whenever it lost the race. Startup <see cref="Warm"/> holds <see cref="_gate"/> for
    /// 2–3 s loading the DirectML session, which is EXACTLY when the page's view-model is built, so
    /// the saved rows came out silently warning-free while the add-form — typed seconds later, after
    /// Warm finished — warned correctly about the identical term. Two answers, one instant.
    ///
    /// Splitting the artifacts off removes the contention instead of papering over it: validation
    /// now waits only on a 262 KB read it may need itself, never on a 132 MB session or a pass.
    ///
    /// LOCK ORDER — <see cref="_gate"/> then <see cref="_textGate"/>, never the reverse.
    /// <see cref="CheckTerm"/> and <see cref="TryLoadTokenizer"/> take <see cref="_textGate"/> alone;
    /// the only nesting is <see cref="TryLoad"/>/<see cref="Free"/> under <see cref="_gate"/>.
    /// </summary>
    private readonly Lock _textGate = new();

    private CtcEncoder? _encoder;
    private CtcTokens? _tokens;
    private CtcTokenizer? _tokenizer;
    private CtcWordSpotter? _dp;
    private bool _loadFailed;
    private bool _disposed;

    // Encoding a term is pure and the list barely changes between dictations, so cache it. Keyed on the
    // exact surface string, because casing IS the point of a vocabulary term.
    private readonly Dictionary<string, int[]> _encodeCache = new(StringComparer.Ordinal);

    public CtcVocabularySpotter(
        CtcModel model,
        OnnxSessionFactory factory,
        ISettingsStore? settings = null,
        float minScore = CtcWordSpotter.DefaultMinScore)
    {
        _model = model;
        _factory = factory;
        _settings = settings;
        _minScore = minScore;
    }

    /// <summary>
    /// Model present on disk. Deliberately a file check, not a session check: a missing model must read
    /// as "not ready" instantly and WITHOUT throwing or loading 132 MB, because <c>VocabularyRunner</c>
    /// asks this on every dictation and the contract when it is false is "no spotter, no corrections,
    /// no error".
    /// </summary>
    public bool IsReady
    {
        get
        {
            if (_disposed || _loadFailed) return false;
            try { return _model.IsInstalled; }
            catch { return false; }   // a data folder that vanished mid-session is not a crash
        }
    }

    /// <inheritdoc/>
    public TermSpottability CheckTerm(string? term)
    {
        TermSpottability basic = VocabularyTermRules.CheckLength(term);
        if (basic != TermSpottability.Unknown) return basic;

        // Everything past the length rule needs the checkpoint's own BPE. With no model there is no
        // honest answer, and "Unknown" is what stops the UI warning about a term that is probably fine.
        //
        // Only the TOKENIZER is loaded (252 KB, ~ms), never the 132 MB session — a term box must not
        // stall for a second and a half on the first keystroke.
        //
        // A blocking Enter on _textGate, NOT a TryEnter: the wait is bounded by that same 262 KB read
        // and by nothing else (see _textGate), whereas TryEnter answered "Unknown" — silently, and
        // indistinguishably from "no model" — whenever startup's Warm happened to be running. That is
        // the bug where a saved `Wi-Fi` row showed no warning while the add form warned about `Wi-Fi`
        // in the same instant.
        if (!IsReady || !TryLoadTokenizer()) return TermSpottability.Unknown;
        lock (_textGate)
        {
            // Re-tested inside the lock: Unload/Dispose can free the artifacts between the load above
            // and here, and a null deref on the UI thread would take the window down.
            if (_tokenizer is null || _tokens is null) return TermSpottability.Unknown;

            // ASKS THE QUESTION THE ENGINE ASKS. The badge means "the spotter can look for this", and
            // since the casing fix the spotter looks for every form in CtcSpotForms.Expand — so testing
            // only the typed spelling would put the UI and the DP on different definitions of Ok, which
            // is the same class of quiet drift the whole vocabulary subsystem keeps producing.
            string clean = VocabularyStore.SanitizeTerm(term);
            foreach (string form in CtcSpotForms.Expand(clean))
                if (_tokenizer.IsSpottable(form, _tokens)) return TermSpottability.Ok;
            return TermSpottability.Unsupported;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<VocabularyGate.Detection> Spot(
        float[] samples, int sampleRate, IReadOnlyList<VocabularyTerm> terms, CancellationToken ct)
    {
        // An empty list is also the "vocabulary just got switched off" signal from the runner's point of
        // view, and holding 283 MB for a feature with nothing to look for is the leak D10 called out.
        if (terms.Count == 0) { Unload(); return []; }
        if (!IsReady || samples.Length == 0 || sampleRate <= 0) return [];

        try
        {
            return SpotCore(samples, sampleRate, terms, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // D6 is enforced at the call site too, but a spotter that throws here would also poison the
            // next dictation via a half-built session, so it is caught and the session dropped.
            JotLog.Error("vocabulary spotter failed — no corrections this dictation", ex);
            Unload();
            return [];
        }
    }

    private IReadOnlyList<VocabularyGate.Detection> SpotCore(
        float[] samples, int sampleRate, IReadOnlyList<VocabularyTerm> terms, CancellationToken ct)
    {
        (float[] speech, double leadSeconds) = SilenceTrim.Trim(samples, sampleRate);
        double seconds = speech.Length / (double)sampleRate;

        if (seconds < MinSpeechSeconds) return [];
        if (seconds > MaxSpeechSeconds)
        {
            JotLog.Info($"vocabulary: skipping spotter — {seconds:0.0}s of speech exceeds the " +
                        $"{MaxSpeechSeconds:0}s cutoff");
            return [];
        }
        if (sampleRate != CtcMelFrontend.SampleRate)
        {
            JotLog.Warn($"vocabulary: skipping spotter — {sampleRate} Hz audio, model needs " +
                        $"{CtcMelFrontend.SampleRate} Hz");
            return [];
        }

        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!TryLoad()) return [];

            IReadOnlyList<CtcSpotQuery> queries = BuildQueries(terms);
            if (queries.Count == 0) return [];

            ct.ThrowIfCancellationRequested();

            float[] features = new CtcMelFrontend().ComputeFeatureMajor(speech, out int frames);
            if (frames < CtcEncoder.SubsamplingFactor) return [];

            float[] logProbs = _encoder!.Run(features, frames, out int outFrames, out int vocab);
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<VocabularyGate.Detection> raw = _dp!.Spot(logProbs, outFrames, vocab, queries);
            if (leadSeconds <= 0 || raw.Count == 0) return raw;

            // Back into FULL-recording time. See the class comment: the gate maps a detection's midpoint
            // to a fraction of the whole recording, so an unshifted detection lands early by exactly the
            // amount of silence we removed.
            var shifted = new List<VocabularyGate.Detection>(raw.Count);
            foreach (VocabularyGate.Detection d in raw)
                shifted.Add(d with { StartTime = d.StartTime + leadSeconds, EndTime = d.EndTime + leadSeconds });
            return shifted;
        }
    }

    // MARK: - Queries

    /// <summary>
    /// Ceiling on the queries ONE term may contribute, casing variants included.
    ///
    /// A budget, not a taste: every query is another O(frames x tokens) lane in the DP, and the list cap
    /// is 200 terms. MEASURED by <c>CtcCasingTests.English_CasingVariantsCostIsBounded</c> at 60 s of
    /// audio — 200 plain queries 20.5 ms, 600 casing-expanded queries 73.7 ms — so ~0.12 ms per query
    /// per minute of speech. At this ceiling the worst case a user can construct is 200 x 12 = 2400
    /// queries ≈ 290 ms, which is inside the post-stop pass and nowhere near <c>SlowStopMs</c>.
    ///
    /// The as-typed forms are NEVER dropped to make room (see <see cref="BuildQueries"/>), so this can
    /// only ever cut a casing variant — it cannot regress a term that used to be found.
    /// </summary>
    private const int MaxQueriesPerTerm = 12;

    /// <summary>
    /// One query per surface form: the term's own spelling, each alias, and the CASING VARIANTS of both.
    ///
    /// ALIASES are not belt-and-braces — they are the documented Mac/iOS bug. "Sri Ram" spoken tokenizes
    /// nothing like "Sriram" written, so searching only the canonical spelling misses the exact case the
    /// user added the term for.
    ///
    /// CASING is the same class of bug found one layer down, and it was live in the shipped English
    /// path (see <see cref="CtcSpotForms"/>). parakeet-tdt_ctc-110m is a punctuation-and-capitalisation
    /// model, so `Migration` and `migration` are DIFFERENT id sequences and the DP searches for exactly
    /// what it is handed. MEASURED on 65 words of real speech, recall at the shipped −3.0 threshold:
    ///
    ///     typed as the model emitted it   65/65   median −0.05
    ///     all lowercase                   63/65   median −0.05
    ///     Title Case                      44/65   median −2.26     <- 32 points of silent loss
    ///     ALL UPPERCASE                    0/65   median −8.06     <- total, silent loss
    ///     best of all four                65/65   median −0.05
    ///
    /// Nothing warned, nothing logged, and the shipped tests never saw it because every one of them
    /// typed its terms the way the model happens to render a proper noun.
    ///
    /// Two invariants this must keep:
    ///   * ALL forms report back under <c>term.Text</c> — the spelling the user typed is what the gate
    ///     pastes, so a variant reporting its own casing would put "OKTA" in the sentence.
    ///   * As-typed forms are emitted FIRST and are exempt from <see cref="MaxQueriesPerTerm"/>, so the
    ///     expansion is strictly additive to what shipped.
    /// The DP suppresses overlapping hits per TERM, so several forms matching the same audio still yield
    /// one detection — at the best of their scores, which is the whole point.
    /// </summary>
    private IReadOnlyList<CtcSpotQuery> BuildQueries(IReadOnlyList<VocabularyTerm> terms)
    {
        var queries = new List<CtcSpotQuery>(terms.Count * 4);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (VocabularyTerm term in terms)
        {
            string text = VocabularyStore.SanitizeTerm(term.Text);
            if (text.Length == 0) continue;

            IReadOnlyList<string> aliases = VocabularyStore.FeedAliases(term);
            string[] surfaces = [text, .. aliases.Select(VocabularyStore.SanitizeTerm)];

            seen.Clear();
            int added = 0;

            // Pass 1 — exactly what shipped, so the fix cannot take a detection away.
            foreach (string form in surfaces)
                if (TryAdd(form)) added++;

            // Pass 2 — the casing variants, term's own first, alias variants after. Budgeted.
            foreach (string surface in surfaces)
            {
                if (added >= MaxQueriesPerTerm) break;
                foreach (string form in CtcSpotForms.Expand(surface))
                {
                    if (added >= MaxQueriesPerTerm) break;
                    if (TryAdd(form)) added++;
                }
            }

            bool TryAdd(string form)
            {
                if (form.Length < VocabularyTermRules.MinTermLength || !seen.Add(form)) return false;
                int[] ids = Encode(form);
                if (ids.Length == 0) return false;
                queries.Add(new CtcSpotQuery(term.Text, aliases, ids));
                return true;
            }
        }
        return queries;
    }

    private int[] Encode(string form)
    {
        if (_encodeCache.TryGetValue(form, out int[]? cached)) return cached;
        int[] ids = [.. _tokenizer!.Encode(form)];
        // Drop unmatchable forms once, here, rather than letting the DP reject them every dictation.
        if (!_tokenizer.IsSpottable(form, _tokens!)) ids = [];
        _encodeCache[form] = ids;
        return ids;
    }

    // MARK: - Session lifecycle

    /// <summary>
    /// The CHEAP half — the two small text artifacts (10 KB + 252 KB). Split out from the session load so
    /// term validation can answer in milliseconds on the UI thread without pulling in 132 MB of weights.
    /// </summary>
    private bool TryLoadTokenizer()
    {
        lock (_textGate)
        {
            if (_disposed || _loadFailed) return false;
            if (_tokenizer is not null) return true;
            if (!_model.IsInstalled) return false;   // download may simply not have finished; not sticky

            try
            {
                _tokens = CtcTokens.Load(_model.Tokens);
                _tokenizer = CtcTokenizer.Load(_model.Tokenizer);
                return true;
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                JotLog.Error($"vocabulary: CTC tokenizer unreadable (model at {_model.Directory})", ex);
                FreeText();
                return false;
            }
        }
    }

    /// <summary>
    /// Creates the ONNX session on first use and keeps it. Measured load: 1.0 s on CPU, 1.9 s on
    /// DirectML — which is why it is never paid twice, and why <see cref="Warm"/> exists to keep it out
    /// of the first stop.
    /// </summary>
    private bool TryLoad()
    {
        if (!TryLoadTokenizer()) return false;
        if (_encoder is not null) return true;

        try
        {
            _encoder = new CtcEncoder(_model.Graph, _factory, Backend());
            _dp = new CtcWordSpotter(_tokens!.BlankId, CtcEncoder.FrameSeconds, _minScore);
            JotLog.Info($"vocabulary: CTC spotter ready (blank={_tokens.BlankId}, backend={Backend()})");
            return true;
        }
        catch (Exception ex)
        {
            // Latched: a corrupt or half-downloaded model would otherwise retry the 132 MB load on every
            // dictation. Re-enabling vocabulary (which calls Unload) clears it.
            _loadFailed = true;
            JotLog.Error($"vocabulary: CTC spotter unavailable (model at {_model.Directory})", ex);
            Free();
            return false;
        }
    }

    /// <summary>
    /// Always CPU. Measured decision, not a default:
    ///
    /// DirectML next to a live Vulkan ggml engine delivered 15.7 s against
    /// <c>VocabularyDeadlineMs = 4000</c> and silently dropped corrections (named kill).
    /// CPU EP measured 2421 ms at 59 s of speech — under budget. The ONNX Nemotron path
    /// that used to restore DirectML via <c>JOT_ENGINE=ort</c> is gone, so the other
    /// branch can never be taken.
    /// </summary>
    private static ComputeBackend Backend() => ComputeBackend.Cpu;

    /// <summary>
    /// Build the session ahead of the first dictation. Call when vocabulary is switched on and at
    /// startup when it is already on: the 1 s load landing inside a stop blows the deadline on its own.
    /// Silent no-op when the model is absent.
    /// </summary>
    public void Warm()
    {
        lock (_gate) { _loadFailed = false; TryLoad(); }
    }

    /// <summary>
    /// Release the ONNX session. A real reclaim, not hygiene: MEASURED at +283 MB working set / 276 MB
    /// returned on the CPU EP (the M0 spike), and +116 MB / 84 MB returned on DirectML, where most of the
    /// weights live in GPU memory instead. Nothing else in this app disposes an ONNX session, so without
    /// this "turn vocabulary off" holds it all until restart.
    /// </summary>
    public void Unload()
    {
        lock (_gate)
        {
            Free();
            _loadFailed = false;   // an explicit off→on cycle is the user's retry for a bad model
        }
    }

    /// <summary>Everything. Callers must hold <see cref="_gate"/> — it touches the session — and this
    /// takes <see cref="_textGate"/> in the one permitted order.</summary>
    private void Free()
    {
        _encoder?.Dispose();
        _encoder = null;
        _dp = null;
        _encodeCache.Clear();
        FreeText();
    }

    /// <summary>Just the two text artifacts. The one thing <see cref="TryLoadTokenizer"/> may release
    /// on its own error path, which can run WITHOUT <see cref="_gate"/> (from <see cref="CheckTerm"/>)
    /// and so must never touch the session. Any session already built outlives it until the next
    /// <see cref="Unload"/>; <c>_loadFailed</c> latches, so nothing uses it in the meantime.</summary>
    private void FreeText()
    {
        lock (_textGate)
        {
            _tokens = null;
            _tokenizer = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Free();
        }
    }
}

/// <summary>
/// Leading/trailing silence removal. Not a VAD — a VAD's job is to find speech boundaries inside audio,
/// and this only has to stop the utterance-global feature normalization from being computed against a
/// long quiet head or tail (see <see cref="CtcVocabularySpotter"/>).
/// </summary>
internal static class SilenceTrim
{
    private const double WindowSeconds = 0.02;

    /// <summary>Silence is relative, not absolute: a quiet recording's speech would fall under any fixed
    /// dBFS floor. -40 dB below the loudest window is well under speech and well above room tone.</summary>
    private const double RelativeFloor = 0.01;      // -40 dB relative to peak window RMS

    /// <summary>Absolute floor so a recording of pure silence trims to nothing instead of amplifying its
    /// own noise into "speech".</summary>
    private const double AbsoluteFloor = 1e-4;      // ≈ -80 dBFS

    /// <summary>Keep this much either side. Speech onsets ramp, and the front-end's 25 ms window plus the
    /// 8x subsampling means a hard cut can clip the first token's evidence.</summary>
    private const double PadSeconds = 0.2;

    /// <returns>The trimmed samples and how many seconds were removed from the FRONT — the offset every
    /// detection time has to be shifted by.</returns>
    public static (float[] Speech, double LeadSeconds) Trim(float[] samples, int sampleRate)
    {
        if (samples.Length == 0 || sampleRate <= 0) return (samples, 0);

        int win = Math.Max(1, (int)(WindowSeconds * sampleRate));
        int windows = samples.Length / win;
        if (windows < 3) return (samples, 0);

        var rms = new double[windows];
        double peak = 0;
        for (int w = 0; w < windows; w++)
        {
            double sum = 0;
            int start = w * win;
            for (int i = start; i < start + win; i++) sum += (double)samples[i] * samples[i];
            rms[w] = Math.Sqrt(sum / win);
            peak = Math.Max(peak, rms[w]);
        }

        double threshold = Math.Max(peak * RelativeFloor, AbsoluteFloor);
        int first = -1, last = -1;
        for (int w = 0; w < windows; w++)
            if (rms[w] >= threshold) { if (first < 0) first = w; last = w; }

        if (first < 0) return ([], 0);   // nothing above the floor anywhere

        int pad = (int)(PadSeconds * sampleRate);
        int from = Math.Max(0, first * win - pad);
        int to = Math.Min(samples.Length, (last + 1) * win + pad);
        if (to - from >= samples.Length) return (samples, 0);

        return (samples[from..to], from / (double)sampleRate);
    }
}
