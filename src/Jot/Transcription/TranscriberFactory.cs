using Jot.Services.Abstractions;
using Jot.Transcription.Ggml;

namespace Jot.Transcription;

/// <summary>
/// Builds the transcriber and applies its language. Both live here because the app's DI registration and
/// the <c>jot</c> CLI must construct the SAME engine from the SAME inputs — a second copy of the
/// selection or of the downcast below would drift, and a drifted CLI silently transcribes with the
/// wrong engine or the wrong language.
/// </summary>
public static class TranscriberFactory
{
    /// <param name="log">Per-launch engine line. The app passes JotLog.Info; the CLI writes no app state,
    /// so it passes null.</param>
    public static ITranscriber Create(
        JotSettings s,
        NemotronGgufModel gguf,
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
            $"{ortNote})");
        return new GgmlNemotronTranscriber(gguf, ggmlOpts);
    }

    /// <summary>Applies the stored language (locale code, or a legacy display name) to the engine.
    /// Called at startup and on change; takes effect on the NEXT dictation (sessions snapshot it).</summary>
    public static void ApplyLanguage(ITranscriber transcriber, string language)
    {
        if (transcriber is GgmlNemotronTranscriber g)
            g.SetLanguage(language); // BCP-47 / "auto"; mapping onto C NULL happens at stream_begin
    }
}
