using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jot.Delivery;
using Jot.Models;
using Jot.Recording;
using Jot.Services;
using Jot.Services.Abstractions;
using Jot.Transcription;
using Jot.Transcription.Ctc;
using Jot.Transcription.Ggml;
using Jot.Transcription.Onnx;
using Jot.Vocabulary;
using Xunit;
using Xunit.Abstractions;

namespace Jot.Tests;

/// <summary>
/// THE end-to-end proof, and the only test in the suite where nothing about vocabulary is a double:
/// real audio, the real Nemotron engine the app selects on this machine, the real
/// <see cref="CtcVocabularySpotter"/> over the real 132 MB checkpoint, the real
/// <see cref="VocabularyGate"/>, and the real <c>RecorderController.StopAndDeliverAsync</c> delivery
/// path (state machine, deadline, store, paste).
///
/// Everything else that touches this feature is unit-scale: <c>CtcSpotterTests</c> proves the spotter
/// finds terms, <c>VocabGoldenFixtureTests</c> proves the gate matches Mac/iOS, and
/// <c>RecorderVocabularyD6Tests</c> proves delivery survives a broken spotter -- but all of them with a
/// HAND-WRITTEN transcript and a HAND-WRITTEN detection. None answer the only question that matters:
/// does a dictation containing "Nemotron" actually come out of the app spelled "Nemotron"? That needs
/// the engine's REAL mis-spelling, which nobody can predict, so the off-pass PRINTS it rather than
/// asserting a guess.
///
/// Skipped, honestly, on any machine without the models. See <see cref="ModelFactAttribute"/>.
/// Shares <c>GgmlNative</c> with the binding/soak tests — one Vulkan device per testhost.
/// </summary>
[Collection("GgmlNative")]
public class RecorderVocabularyEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private const string Clip = "tts-terms.wav";
    private const int Rate = 16_000;

    /// <summary>The five terms the owner will seed, with the "sounds like" aliases a user would type
    /// after hearing the engine's own output once.</summary>
    private static (string Text, string[] Aliases)[] Planted =>
    [
        ("Nemotron",    []),
        ("Parakeet",    []),
        ("Sriram",      ["Sri Ram"]),
        ("Okta",        ["Octa"]),
        ("Claude Code", ["Claud code"]),
    ];

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "jot-e2e-" + Guid.NewGuid().ToString("N"));

    private bool _logInitialized;

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ } }

    // MARK: - Fakes for the parts NOT under test (no WASAPI device, no synthetic keystrokes)

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public JotSettings Current { get; } = new();
        public void Save() { }
        public void Reset() { }
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class FakeRecordingStore : IRecordingStore
    {
        public ObservableCollection<RecordingItem> Items { get; } = [];
        public void Add(RecordingItem item) => Items.Add(item);
        public void Delete(RecordingItem item) => Items.Remove(item);
        public void Rename(RecordingItem item, string title) => item.Title = title;
        public IReadOnlyList<string> AllTags() => [];
    }

    private sealed class SilentSounds : ISoundService
    {
        public int Errors;
        public void PlayStart() { }
        public void PlayStop() { }
        public void PlayCancel() { }
        public void PlaySuccess() { }
        public void PlayError() => Errors++;
        public void Preview() { }
    }

    // MARK: - Wrappers that break a REAL spotter (D6, with real audio rather than mocks)

    /// <summary>Real spotter, then throw. Deliberately a LATE failure: the real pass has already run,
    /// so this is the "session blew up after doing work" shape, not a trivially-empty stub.</summary>
    private sealed class FailingSpotter(IVocabularySpotter inner) : IVocabularySpotter
    {
        public bool IsReady => inner.IsReady;
        public IReadOnlyList<VocabularyGate.Detection> Spot(
            float[] s, int rate, IReadOnlyList<VocabularyTerm> terms, CancellationToken ct)
        {
            _ = inner.Spot(s, rate, terms, ct);
            throw new InvalidOperationException("onnx session blew up mid-pass");
        }
    }

    /// <summary>Real spotter, but slower than the deadline. Throws nothing, so only the deadline can
    /// save the dictation.</summary>
    private sealed class SlowSpotter(IVocabularySpotter inner, int delayMs) : IVocabularySpotter
    {
        public bool IsReady => inner.IsReady;
        public IReadOnlyList<VocabularyGate.Detection> Spot(
            float[] s, int rate, IReadOnlyList<VocabularyTerm> terms, CancellationToken ct)
        {
            Thread.Sleep(delayMs);
            return inner.Spot(s, rate, terms, ct);
        }
    }

    /// <summary>Captures the gate's own APPLY/BLOCK trace. Without this a "not corrected" result is
    /// unattributable: nobody can tell whether the spotter missed the word or the gate protected it, and
    /// those are opposite bugs with opposite fixes.</summary>
    private sealed class CapturingSink : IDiagnosticsSink
    {
        public readonly List<string> Lines = [];
        public void Record(DiagnosticsCategory category, string message,
                           IReadOnlyDictionary<string, string> metadata) =>
            Lines.Add($"[{category}] {message}  {{{string.Join(", ", metadata.Select(kv => $"{kv.Key}={kv.Value}"))}}}");
    }

    // MARK: - Harness

    private sealed record Harness(
        RecorderController Controller,
        FakeSettingsStore Settings,
        FakeRecordingStore Store,
        SilentSounds Sounds,
        List<string> Pastes,
        List<string> Ready,
        List<(string Title, string Message)> Failures,
        List<VocabularyCorrection> Corrections,
        CtcVocabularySpotter RealSpotter,
        ITranscriber Engine,
        CapturingSink Diagnostics,
        VocabularyStore Terms);

    /// <summary>Exactly the engine the shipping app constructs: ggml via
    /// <see cref="TranscriberFactory"/>. Anything else and the transcript under test is not the
    /// transcript the owner will see.</summary>
    private ITranscriber BuildAppEngine(JotSettings s)
    {
        // Granite/punct pointed at nothing on purpose: this suite is about the VOCABULARY gate on
        // the ggml transcript, so it must keep exercising ggml regardless of whether the English
        // engine happens to be installed on the machine running it.
        ITranscriber t = TranscriberFactory.Create(
            s, new NemotronGgufModel(),
            new Jot.Transcription.Granite.GraniteModel(directory: @"C:\nope\granite"),
            new Jot.Text.PunctCapSegModel(directory: @"C:\nope\punct"),
            msg => output.WriteLine(msg));
        output.WriteLine($"engine: ggml installed={t.IsModelInstalled} device={s.TranscriptionDevice}");
        return t;
    }

    private Harness Build(bool vocabularyOn, Func<CtcVocabularySpotter, IVocabularySpotter>? wrap = null,
                          int deadlineMs = 4_000)
    {
        // The D6 paths below log stack traces on purpose. Point JotLog at the temp root so they never
        // land in the real user's jot.log, which is the file Send-feedback attaches.
        if (!_logInitialized) { JotLog.Initialize(() => _root); _logInitialized = true; }

        var settings = new FakeSettingsStore();
        settings.Current.DataDirectory = _root;   // stats.json / logs stay off the real user data
        settings.Current.AutoPaste = true;        // exercise the paste leg, not just the store
        settings.Current.Language = "en-US";
        settings.Current.VocabularyEnabled = vocabularyOn;
        // Copied from the REAL settings.json so the engine choice matches the app's. These three are the
        // only inputs EngineSelector reads, and a fresh JotSettings has none of them.
        settings.Current.TranscriptionDevice = LiveSettings.TranscriptionDevice ?? TranscriptionDevices.Auto;
        settings.Current.GpuProbeVerdict = LiveSettings.GpuProbeVerdict;
        settings.Current.GpuProbeKey = LiveSettings.GpuProbeKey;

        var terms = new VocabularyStore(null);    // in-memory: never writes the owner's vocabulary.json
        foreach ((string text, string[] aliases) in Planted) terms.Add(text, aliases);

        // The REAL spotter over the REAL installed checkpoint, resolved exactly as the app resolves it
        // (no directory override). A wrong path would silently degrade to "not ready" and this whole
        // file would pass while proving nothing, so it is asserted rather than assumed.
        var real = new CtcVocabularySpotter(new CtcModel(), new OnnxSessionFactory(), settings);
        Assert.True(real.IsReady, $"spotter not ready -- model missing at {new CtcModel().Directory}");

        var sink = new CapturingSink();
        var runner = new VocabularyRunner(
            settings, terms, new CorrectionStore(null), new CorrectionProvenance(null),
            wrap is null ? real : wrap(real), EmbeddedCommonWordsProvider.Shared, sink);

        var store = new FakeRecordingStore();
        var sounds = new SilentSounds();
        ITranscriber engine = BuildAppEngine(settings.Current);
        var controller = new RecorderController(
            new AudioRecorder(), engine, settings, store, sounds, new UsageStats(settings), runner)
        {
            VocabularyDeadlineMs = deadlineMs,
        };

        var pastes = new List<string>();
        var ready = new List<string>();
        var failures = new List<(string, string)>();
        var corrections = new List<VocabularyCorrection>();
        controller.PasteOverride = (text, _, _) => { pastes.Add(text); return TextInjector.PasteResult.Pasted; };
        controller.TranscriptReady += t => ready.Add(t);
        controller.Failed += (title, message) => failures.Add((title, message));
        controller.CorrectionsReady += c => corrections.AddRange(c);

        return new Harness(controller, settings, store, sounds, pastes, ready, failures, corrections,
                           real, engine, sink, terms);
    }

    /// <summary>The ONE seam standing in for the mic: the WAV's samples plus the transcript the REAL
    /// engine produces from them. Mirrors <c>CaptureAndTranscribeAsync</c>'s batch leg exactly, minus
    /// WASAPI.</summary>
    private void AttachCapture(Harness h, float[] samples)
    {
        h.Controller.CaptureOverride = async () =>
        {
            var sw = Stopwatch.StartNew();
            string text = (await h.Engine.TranscribeAsync(samples, Rate)).Trim();
            sw.Stop();
            output.WriteLine($"nemotron batch decode: {sw.ElapsedMilliseconds} ms for " +
                             $"{samples.Length / (double)Rate:F2} s of audio");
            return (new RecordingResult(samples, Rate, Path.Combine(_root, "e2e.wav"),
                                        TimeSpan.FromSeconds(samples.Length / (double)Rate)), text);
        };
    }

    private static void Release(Harness h)
    {
        h.RealSpotter.Dispose();
        (h.Engine as IDisposable)?.Dispose();
    }

    private static float[] LoadClip() =>
        WavAudio.ReadMono16k(Path.Combine(ModelGate.ResolvedAudioDir!, Clip));

    // MARK: - THE proof

    /// <summary>
    /// Vocabulary OFF, then ON, over the same clip and the same engine. The off-pass is PRINTED rather
    /// than asserted against a guess: nobody knows what an RNNT writes for "Nemotron" until it writes
    /// it, and a test asserting "nemo tron" would be testing its author's imagination.
    ///
    /// A term that fails to correct is a FINDING, not a threshold to tune. The assertion names it.
    /// </summary>
    [ModelFact("installed-ctc", "ggml-natives", "ggml-model", "audio:tts-terms.wav")]
    public async Task E2E_VocabularyOffThenOn_CorrectsThePlantedTerms()
    {
        float[] samples = LoadClip();
        output.WriteLine($"clip: {Clip}, {samples.Length / (double)Rate:F2} s");

        // ---- Pass 1: vocabulary OFF. Ground truth of what the engine really writes.
        Harness off = Build(vocabularyOn: false);
        AttachCapture(off, samples);
        await off.Controller.StopAndDeliverAsync();

        string rawText = Assert.Single(off.Store.Items).Transcript;
        output.WriteLine("");
        output.WriteLine("=== VOCAB OFF (raw) ===");
        output.WriteLine(rawText);
        output.WriteLine("");
        Assert.Empty(off.Failures);
        Assert.Equal(RecorderState.Idle, off.Controller.State);
        Assert.Equal(rawText, Assert.Single(off.Pastes));   // the paste leg agrees with the store
        Assert.Empty(off.Corrections);                      // off means OFF: no gate, no chip
        Release(off);

        // ---- Pass 2: vocabulary ON, same clip, same engine.
        Harness on = Build(vocabularyOn: true);
        AttachCapture(on, samples);
        // The app warms the session at startup (WireVocabularySpotterLifecycle), so a cold 1-2 s load
        // inside the stop is not what this test measures. The cold case is its own test below.
        on.RealSpotter.Warm();

        var sw = Stopwatch.StartNew();
        await on.Controller.StopAndDeliverAsync();
        sw.Stop();

        string gated = Assert.Single(on.Store.Items).Transcript;
        output.WriteLine("=== VOCAB ON (gated) ===");
        output.WriteLine(gated);
        output.WriteLine("");
        output.WriteLine($"stop-to-delivered (warm spotter): {sw.ElapsedMilliseconds} ms");
        output.WriteLine("corrections reported to the pill: " +
            (on.Corrections.Count == 0 ? "(none)"
             : string.Join(", ", on.Corrections.Select(c => $"{c.OriginalWord} -> {c.Term}"))));
        output.WriteLine("");

        Assert.Empty(on.Failures);
        Assert.Equal(RecorderState.Idle, on.Controller.State);
        Assert.Equal(gated, Assert.Single(on.Pastes));

        // RAW spotter output, printed before the verdicts. A "not corrected" term is unattributable
        // without it: spotter-missed and gate-blocked are opposite bugs with opposite fixes.
        List<VocabularyGate.Detection> detections =
            [.. on.RealSpotter.Spot(samples, Rate, [.. on.Terms.Terms], default).OrderBy(d => d.StartTime)];
        output.WriteLine("=== raw spotter detections ===");
        foreach (VocabularyGate.Detection d in detections)
        {
            output.WriteLine($"  {d.Term,-12} score={d.Score,8:F3}  {d.StartTime,6:F2}s-{d.EndTime:F2}s");
        }
        output.WriteLine("");
        output.WriteLine("=== gate decision trace ===");
        foreach (string line in on.Diagnostics.Lines) output.WriteLine("  " + line);
        output.WriteLine("");

        // Per-term verdict table -- the thing the report quotes.
        var missing = new List<string>();
        output.WriteLine("=== per term ===");
        foreach ((string term, _) in Planted)
        {
            bool inRaw = rawText.Contains(term, StringComparison.Ordinal);
            bool inGated = gated.Contains(term, StringComparison.Ordinal);
            string verdict = inGated ? (inRaw ? "already correct" : "CORRECTED") : "NOT CORRECTED";
            output.WriteLine($"  {term,-12} raw={inRaw,-5} gated={inGated,-5}  {verdict}");
            if (!inGated) missing.Add(term);
        }
        // DEFECT CHECK, stated separately from recall because it is the worse of the two failure
        // shapes: a missed term leaves the transcript as the engine wrote it, but a duplicated tail
        // CORRUPTS text that was already fine -- and it is pasted before anyone can review it.
        //
        // VocabularyGate.Apply (the rescore path) has AbsorbTrailingDuplicates for exactly this, and
        // its doc comment names this exact string. ApplyFromDetections -- the ONLY path Nemotron on
        // Windows runs -- had neither that guard nor AlignmentWindow, so a multi-word term placed on
        // one whitespace word left the term's tail duplicated behind it ("Claude Code code"). Fixed
        // 2026-07-25 as a DELIBERATE divergence from jot-shared; this stays as its regression.
        var duplicated = new List<string>();
        foreach ((string term, _) in Planted)
        {
            string[] w = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (w.Length < 2) continue;
            string tail = string.Join(' ', w[1..]);
            if (gated.Contains($"{term} {tail}", StringComparison.OrdinalIgnoreCase))
                duplicated.Add($"{term} {tail}");
        }

        // A term that did not come out right is a FINDING, and the two shapes are NOT the same bug:
        //
        //   * the spotter never heard it — RECALL. Nothing downstream can rescue that, so it fails.
        //
        //   * the spotter heard it but no transcript word is a plausible host — the plausibility
        //     brake doing its job. Real case, this clip: "Parakeet" scored BEST of all five (-0.313)
        //     while the engine wrote "Herrakit", which is 0.62 away against a 0.45 ceiling. That
        //     ceiling is the over-correction guard ("every name became Jamy") and is pinned by the
        //     shared golden fixtures, so v1.3 does not widen it and the correction genuinely cannot
        //     happen. What v1.3 DOES owe the user is the two things that were missing: ATTRIBUTION —
        //     a log line naming the term, its score and the nearest word it was measured against, so
        //     "I added the term and nothing happened" is answerable — and a remedy that actually
        //     works. Both are asserted below. A silently dropped detection is still a failure.
        var unheard = new List<string>();
        var unexplained = new List<string>();
        foreach (string term in missing)
        {
            if (!detections.Any(d => d.Term == term)) { unheard.Add(term); continue; }

            // The other REFUSAL that is attribution rather than a silent drop: a multi-word term whose
            // only plausible host is one of its own words. Publishing there would insert the term's
            // missing words into text the engine got right ("Claw code" → "Claw Claude Code"), so the
            // gate refuses and says so. No alias remedy applies — the engine's spelling of the OTHER
            // words is what is missing — so this branch ends here.
            string? partial = on.Diagnostics.Lines.FirstOrDefault(
                l => l.Contains("partial-term-skipped", StringComparison.Ordinal)
                     && l.Contains($"→ {term}", StringComparison.Ordinal));
            if (partial is not null)
            {
                output.WriteLine($"  {term,-12} heard, but its only host is one of its own words — logged: {partial}");
                continue;
            }

            // Gate BLOCK is attribution, not a silent drop. ggml writes "Sri Ram"; the spotter
            // hears Sriram; the brake refuses Ram→Sriram and says so. Same contract as
            // spot-unplaced: the log names the term so "I added it and nothing happened" is
            // answerable. ONNX used to miss this path (this test skipped after int4/fp16 left).
            string? blocked = on.Diagnostics.Lines.FirstOrDefault(
                l => l.Contains("decision=BLOCK", StringComparison.Ordinal)
                     && l.Contains($"→ {term}", StringComparison.Ordinal));
            if (blocked is not null)
            {
                output.WriteLine($"  {term,-12} heard, gate refused — logged: {blocked}");
                continue;
            }

            string? log = on.Diagnostics.Lines.FirstOrDefault(
                l => l.Contains($"spot-unplaced {term}", StringComparison.Ordinal));
            if (log is null)
            {
                unexplained.Add($"{term} (heard, dropped, NOTHING logged)");
                continue;
            }
            output.WriteLine($"  {term,-12} heard but unplaceable — logged: {log}");

            // The remedy the log points at, proven rather than asserted in prose: the user reads the
            // engine's own spelling out of that line and adds it as a "sounds like" alias. Re-gated
            // over the SAME raw text and the SAME detection, the term must then come out right.
            string nearest = log[(log.IndexOf("nearest=", StringComparison.Ordinal) + 8)..];
            nearest = nearest[..nearest.IndexOfAny([',', '}'])].Trim();
            VocabularyGate.Detection det = detections.First(d => d.Term == term) with { Aliases = [nearest] };
            string repaired = VocabularyGate.ApplyFromDetections(
                rawText, [det], samples.Length / (double)Rate, EmbeddedCommonWordsProvider.Shared).Text;
            output.WriteLine($"  {term,-12} with alias \"{nearest}\" → {(repaired.Contains(term, StringComparison.Ordinal) ? "CORRECTS" : "STILL NOT CORRECTED")}");
            if (!repaired.Contains(term, StringComparison.Ordinal))
                unexplained.Add($"{term} (heard, logged, but the logged nearest word \"{nearest}\" is not a working alias either)");
        }
        Release(on);

        var defects = new List<string>();
        if (unheard.Count > 0)
            defects.Add("terms spoken but the spotter never heard them: " + string.Join(", ", unheard));
        if (unexplained.Count > 0)
            defects.Add("terms silently dropped — no usable attribution: " + string.Join(", ", unexplained));
        if (duplicated.Count > 0)
            defects.Add("multi-word terms delivered with a DUPLICATED tail: " + string.Join(", ", duplicated));

        Assert.True(defects.Count == 0,
            string.Join("\n", defects) + $"\nraw:   {rawText}\ngated: {gated}");
    }

    /// <summary>
    /// The cold path, stated separately because it is what a user gets if <c>Warm</c> ever stops being
    /// called: the 132 MB session load lands INSIDE the stop. Prints the wall time; asserts only the
    /// invariant, that a transcript is delivered either way.
    /// </summary>
    [ModelFact("installed-ctc", "ggml-natives", "ggml-model", "audio:tts-terms.wav")]
    public async Task E2E_ColdSpotter_StillDeliversWithinTheDeadlineOrDegrades()
    {
        Harness h = Build(vocabularyOn: true);
        AttachCapture(h, LoadClip());

        var sw = Stopwatch.StartNew();
        await h.Controller.StopAndDeliverAsync();
        sw.Stop();

        string text = Assert.Single(h.Store.Items).Transcript;
        output.WriteLine($"COLD stop-to-delivered: {sw.ElapsedMilliseconds} ms " +
                         $"(vocabulary deadline {h.Controller.VocabularyDeadlineMs} ms)");
        output.WriteLine($"corrections applied cold: {h.Corrections.Count}");
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Empty(h.Failures);
        Assert.Equal(RecorderState.Idle, h.Controller.State);
        Release(h);
    }

    // MARK: - D6 with real audio

    /// <summary>D6: a spotter that throws after doing the real work still delivers the real transcript,
    /// byte-for-byte identical to the vocabulary-off run.</summary>
    [ModelFact("installed-ctc", "ggml-natives", "ggml-model", "audio:tts-terms.wav")]
    public async Task E2E_FailingSpotter_StillDeliversTheRawTranscript()
    {
        float[] samples = LoadClip();

        Harness reference = Build(vocabularyOn: false);
        AttachCapture(reference, samples);
        await reference.Controller.StopAndDeliverAsync();
        string expected = Assert.Single(reference.Store.Items).Transcript;
        Release(reference);

        Harness h = Build(vocabularyOn: true, wrap: real => new FailingSpotter(real));
        AttachCapture(h, samples);
        await h.Controller.StopAndDeliverAsync();

        Assert.Equal(expected, Assert.Single(h.Store.Items).Transcript);
        Assert.Equal(expected, Assert.Single(h.Pastes));
        Assert.Equal(expected, Assert.Single(h.Ready));
        Assert.Empty(h.Failures);
        Assert.Equal(0, h.Sounds.Errors);
        Assert.Equal(RecorderState.Idle, h.Controller.State);
        output.WriteLine("throwing spotter -> ungated transcript delivered intact:");
        output.WriteLine(expected);
        Release(h);
    }

    /// <summary>D6: a spotter slower than the deadline throws nothing, so only the deadline saves the
    /// dictation -- and the state machine must still come home to Idle.</summary>
    [ModelFact("installed-ctc", "ggml-natives", "ggml-model", "audio:tts-terms.wav")]
    public async Task E2E_SlowSpotter_HitsTheDeadlineAndStillDelivers()
    {
        float[] samples = LoadClip();

        Harness reference = Build(vocabularyOn: false);
        AttachCapture(reference, samples);
        await reference.Controller.StopAndDeliverAsync();
        string expected = Assert.Single(reference.Store.Items).Transcript;
        Release(reference);

        // 300 ms budget against 2 s of deliberate lateness: the deadline fires long before the pass ends.
        Harness h = Build(vocabularyOn: true, wrap: real => new SlowSpotter(real, 2_000), deadlineMs: 300);
        AttachCapture(h, samples);
        var sw = Stopwatch.StartNew();
        await h.Controller.StopAndDeliverAsync();
        sw.Stop();

        Assert.Equal(expected, Assert.Single(h.Store.Items).Transcript);
        Assert.Equal(expected, Assert.Single(h.Pastes));
        Assert.Empty(h.Corrections);          // nothing applied -- the pass never came back
        Assert.Empty(h.Failures);
        Assert.Equal(RecorderState.Idle, h.Controller.State);   // NOT wedged in Transcribing
        output.WriteLine($"slow spotter -> delivered ungated after {sw.ElapsedMilliseconds} ms");
        Release(h);
    }

    // MARK: - the live machine's settings, read once

    /// <summary>The real settings.json, so the engine under test is the owner's engine. Read-only, and
    /// falls back to the defaults when it is absent.</summary>
    private static class LiveSettings
    {
        private static readonly JotSettings S = Read();

        public static string? TranscriptionDevice => S.TranscriptionDevice;
        public static string? GpuProbeVerdict => S.GpuProbeVerdict;
        public static string? GpuProbeKey => S.GpuProbeKey;

        private static JotSettings Read()
        {
            try
            {
                string p = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Jot", "settings.json");
                if (!File.Exists(p)) return new JotSettings();
                return System.Text.Json.JsonSerializer.Deserialize<JotSettings>(File.ReadAllText(p))
                       ?? new JotSettings();
            }
            catch { return new JotSettings(); }
        }
    }
}
