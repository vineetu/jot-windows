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
/// Jot now has TWO engines and the split is by language, not by device:
///   * English  -> Granite Speech 5.0 TurboCTC on ONNX Runtime (CPU), punctuated by punct_cap_seg_en.
///   * anything else, and "auto" -> Nemotron 3.5 via ggml, which is the multilingual one.
/// The models are REQUIRED parameters rather than optional ones so a new call site cannot silently
/// lose the English engine by forgetting to pass it; when the Granite assets are missing the router
/// falls back to ggml on its own.
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
        Func<string, string?>? env = null)
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
        ITranscriber english = new GraniteTranscriber(granite, sessions);

        // Punctuation is not optional for this engine — Granite's CTC head emits no casing or marks
        // at all — so without the punctuation model the English engine is NOT offered and English
        // stays on ggml, which punctuates itself. Half of the pair is worse than neither.
        if (punct.IsInstalled)
            english = new PunctuatingTranscriber(english, new PunctCapSeg(punct, sessions));
        else
            english = new UninstalledTranscriber();

        var routed = new LanguageRoutedTranscriber(english, multilingual, log);
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
