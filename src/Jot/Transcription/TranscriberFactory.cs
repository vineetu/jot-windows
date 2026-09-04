using Jot.Services.Abstractions;
using Jot.Text;
using Jot.Transcription.Ggml;
using Jot.Transcription.Granite;
using Jot.Transcription.Onnx;

namespace Jot.Transcription;

/// <summary>
/// Builds the transcriber and applies its language. Both live here because the app's DI registration
/// and the <c>jot</c> CLI must construct the SAME engine from the SAME inputs — a second copy of the
/// selection or of the downcast below would drift, and a drifted CLI silently transcribes with the
/// wrong engine or the wrong language.
///
/// The split is by language, not by device, and the English route has three shapes depending on
/// what is downloaded:
///   * Granite + punct_cap_seg_en  -> Granite Speech 5.0 TurboCTC on ONNX Runtime (CPU). Punctuation
///     is mandatory here; that head emits no marks or casing at all.
///   * punct_cap_seg_en alone      -> the ggml engine with English punctuation RESTORED over its own.
///     A quality setting (default on), not a repair, and a pass-through when switched off.
///   * neither                     -> ggml untouched.
/// Anything not English, and "auto", is ggml in every case — the punctuation model is English-only.
/// The models are REQUIRED parameters rather than optional ones so a new call site cannot silently
/// lose the English engine by forgetting to pass it.
/// </summary>
public static class TranscriberFactory
{
    /// <param name="log">Per-launch engine line. The app passes JotLog.Info; the CLI writes no app state,
    /// so it passes null.</param>
    public static ITranscriber Create(
        JotSettings s,
        NemotronGgufModel gguf,
        GraniteModel granite,
        PunctCapSegModel punct,
        Action<string>? log = null,
        Func<string, string?>? env = null,
        Func<bool>? restoreEnglishPunctuation = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var ggmlOpts = GgmlEngineOptions.Resolve(s, env);

        // JOT_ENGINE=ort used to force the ONNX Nemotron path. That engine is gone (K3 accepted:
        // Q8_0 beats int4 at 4+ threads; the 1–2 thread tail is 1–2 core machines, recoverable
        // from git). A leftover env var must not silently select a missing engine.
        string ortNote = GgmlEngineOptions.IsOrtRequested(env)
            ? ", JOT_ENGINE=ort ignored"
            : "";
        log?.Invoke(
            $"engine: ggml (device={s.TranscriptionDevice}, backend={ggmlOpts.Backend}, " +
            $"r={ggmlOpts.AttContextRight}, gguf={gguf.IsInstalled}, natives={GgmlNativeLocator.IsPresent(env)}" +
            $"{ortNote}) + granite (english={granite.IsInstalled}, punct={punct.IsInstalled})");

        var multilingual = new GgmlNemotronTranscriber(gguf, ggmlOpts);
        var sessions = new OnnxSessionFactory();

        ITranscriber english;
        if (granite.IsInstalled && punct.IsInstalled)
        {
            // Granite needs punctuation unconditionally: its CTC head emits no casing or marks at
            // all, so half of the pair is worse than neither.
            english = new PunctuatingTranscriber(new GraniteTranscriber(granite, sessions),
                                                 new PunctCapSeg(punct, sessions));
        }
        else if (punct.IsInstalled)
        {
            // No Granite, but the punctuation model is here: restore English on the MULTILINGUAL
            // engine instead. ggml already punctuates, so this is a quality upgrade rather than a
            // repair, and it is a setting — read per utterance, since Settings changes it without
            // rebuilding DI — which is why the app passes a LIVE accessor rather than letting this
            // close over `s`, a snapshot taken once at construction. The CLI passes nothing and gets
            // the snapshot, which is correct for a one-shot process. ownsInner is false because
            // `multilingual` is also the non-English route; disposing it here would tear down an
            // engine still in use.
            english = new PunctuatingTranscriber(
                multilingual, new PunctCapSeg(punct, sessions),
                enabled: restoreEnglishPunctuation ?? (() => s.RestoreEnglishPunctuation),
                ownsInner: false);

        }
        else
        {
            // Nothing to add. Reporting not-installed routes English to ggml untouched, which is
            // exactly today's behaviour.
            english = new UninstalledTranscriber();
        }

        Func<bool> restoring = restoreEnglishPunctuation ?? (() => s.RestoreEnglishPunctuation);
        var routed = new LanguageRoutedTranscriber(
            english, multilingual, log,
            englishLabel: () => granite.IsInstalled ? "granite (English)"
                              : restoring() ? "ggml + punctuation (English)"
                                            : "ggml (English, punctuation off)");
        routed.SetLanguage(s.Language);
        return routed;
    }

    /// <summary>Applies the stored language (locale code, or a legacy display name) to the engine.
    /// Called at startup and on change; takes effect on the NEXT dictation (sessions snapshot it).</summary>
    public static void ApplyLanguage(ITranscriber transcriber, string language)
    {
        switch (transcriber)
        {
            // The router owns the choice of engine, so it must see the change first — it forwards
            // to the multilingual engine itself.
            case LanguageRoutedTranscriber routed:
                routed.SetLanguage(language);
                break;
            case GgmlNemotronTranscriber g:
                g.SetLanguage(language); // BCP-47 / "auto"; mapping onto C NULL happens at stream_begin
                break;
        }
    }

    /// <summary>The ggml engine inside whatever <see cref="Create"/> returned, or null when ggml is not
    /// it. Callers needing the CONCRETE engine must come through here: English routing wrapped ggml in
    /// <see cref="LanguageRoutedTranscriber"/>, and a direct type test then silently matches nothing —
    /// which is how the leftover-ONNX cleanup stopped deleting ~2 GB on every upgraded install.
    /// A new wrapper must be handled here AND in <see cref="ApplyLanguage"/>.</summary>
    public static GgmlNemotronTranscriber? GgmlEngineOf(ITranscriber transcriber) => transcriber switch
    {
        LanguageRoutedTranscriber routed => GgmlEngineOf(routed.Multilingual),
        GgmlNemotronTranscriber g => g,
        _ => null,
    };

    /// <summary>
    /// Stands in for the English engine when its assets are incomplete. It reports
    /// not-installed, which is exactly what <see cref="LanguageRoutedTranscriber"/> uses to route
    /// English to ggml instead — so a missing download degrades to the multilingual engine rather
    /// than throwing at the moment the user speaks.
    /// </summary>
    private sealed class UninstalledTranscriber : ITranscriber
    {
        public bool IsModelInstalled => false;

        public Task<string> TranscribeAsync(float[] samples, int sampleRate,
                                            CancellationToken ct = default)
            => throw new InvalidOperationException(
                "The English engine is not installed; the router should not have selected it.");
    }
}
