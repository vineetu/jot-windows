using Jot.Services.Abstractions;
using Jot.Transcription.Ggml;
using Jot.Transcription.Nemotron;
using Jot.Transcription.Onnx;

namespace Jot.Transcription;

/// <summary>
/// Builds the transcriber and applies its language. Both live here because the app's DI registration and
/// the `jot` CLI must construct the SAME engine from the SAME inputs — a second copy of the selection or
/// of the two downcasts below would drift, and a drifted CLI silently transcribes with the wrong engine
/// or the wrong language.
/// </summary>
public static class TranscriberFactory
{
    /// <param name="log">Per-launch engine line. The app passes JotLog.Info; the CLI writes no app state,
    /// so it passes null.</param>
    public static ITranscriber Create(
        JotSettings s,
        NemotronModel int4,
        NemotronFp16Model fp16,
        OnnxSessionFactory factory,
        Action<string>? log = null)
        => Create(s, int4, fp16, new NemotronGgufModel(settings: null), factory, log);

    /// <param name="log">Per-launch engine line. The app passes JotLog.Info; the CLI writes no app state,
    /// so it passes null.</param>
    public static ITranscriber Create(
        JotSettings s,
        NemotronModel int4,
        NemotronFp16Model fp16,
        NemotronGgufModel gguf,
        OnnxSessionFactory factory,
        Action<string>? log = null,
        Func<string, string?>? env = null)
    {
        var adapter = Platform.GpuInfo.TryGetPrimaryAdapter(); // ~1 ms enumeration, no D3D device
        bool keyMatches = adapter is not null && s.GpuProbeKey == adapter.CacheKey;
        EngineChoice choice = EngineSelector.Select(
            s.TranscriptionDevice, fp16.IsInstalled, s.GpuProbeVerdict, keyMatches);

        var ggml = GgmlEngineOptions.Resolve(s, choice, env);
        if (ggml.Enabled)
        {
            log?.Invoke(
                $"engine: ggml (device={s.TranscriptionDevice}, backend={ggml.Backend}, " +
                $"r={ggml.AttContextRight}, gguf={gguf.IsInstalled}, natives={GgmlNativeLocator.IsPresent()})");
            return new GgmlNemotronTranscriber(gguf, ggml);
        }

        log?.Invoke($"engine: {choice} (device={s.TranscriptionDevice}, " +
            $"verdict={s.GpuProbeVerdict ?? "none"}, keyMatch={keyMatches}, fp16={fp16.IsInstalled})");
        return choice switch
        {
            EngineChoice.Fp16Dml => new NemotronFp16Transcriber(fp16, factory, ComputeBackend.DirectML),
            EngineChoice.Int4DmlEncoder => new NemotronTranscriber(int4, factory, ComputeBackend.DirectML),
            _ => new NemotronTranscriber(int4, factory, ComputeBackend.Cpu),
        };
    }

    /// <summary>Applies the stored language (locale code, or a legacy display name) to the engine.
    /// Called at startup and on change; takes effect on the NEXT dictation (sessions snapshot it).</summary>
    public static void ApplyLanguage(ITranscriber transcriber, string language)
    {
        if (transcriber is GgmlNemotronTranscriber g)
        {
            g.SetLanguage(language); // BCP-47 / "auto"; mapping onto C NULL happens at stream_begin
            return;
        }
        NemotronLocales.TryGetSlot(language, out long slot); // unknown → en-US, never a wrong guess
        if (transcriber is NemotronTranscriber n)
            n.SetLanguageId(slot);
        else if (transcriber is NemotronFp16Transcriber f)
            f.SetLanguageSlot(slot);   // same slot space in both exports (languages.json)
    }
}
