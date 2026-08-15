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

        env ??= Environment.GetEnvironmentVariable;
        bool assets = gguf.IsInstalled && GgmlNativeLocator.IsPresent(env);
        var ggmlOpts = GgmlEngineOptions.Resolve(s, choice, env, assets);

        ITranscriber onnx = CreateOnnx(choice, int4, fp16, factory);
        ITranscriber ggml = new GgmlNemotronTranscriber(gguf, ggmlOpts);

        // Forced ONNX (emergency back-out): do not wrap, do not probe Vulkan.
        if (GgmlEngineOptions.IsOrtForced(env))
        {
            log?.Invoke($"engine: {choice} (JOT_ENGINE=ort, device={s.TranscriptionDevice}, " +
                $"verdict={s.GpuProbeVerdict ?? "none"}, keyMatch={keyMatches}, fp16={fp16.IsInstalled})");
            (ggml as IDisposable)?.Dispose();
            return onnx;
        }

        // Forced ggml (legacy flag / JOT_ENGINE=ggml) with no ONNX to fall back to: return it bare
        // so missing-asset tests still see GgmlNemotronTranscriber.
        if (ggmlOpts.Enabled && !int4.IsInstalled && !fp16.IsInstalled)
        {
            log?.Invoke(
                $"engine: ggml (device={s.TranscriptionDevice}, backend={ggmlOpts.Backend}, " +
                $"r={ggmlOpts.AttContextRight}, gguf={gguf.IsInstalled}, natives={GgmlNativeLocator.IsPresent(env)})");
            (onnx as IDisposable)?.Dispose();
            return ggml;
        }

        // Default: pick ggml when assets appear (including mid-launch after a download), ONNX otherwise.
        // A ggml load failure falls through to ONNX with a log, never a crash.
        log?.Invoke(
            $"engine: selecting (ggmlAssets={assets}, device={s.TranscriptionDevice}, " +
            $"backend={ggmlOpts.Backend}, r={ggmlOpts.AttContextRight}, onnx={choice})");
        return new SelectingTranscriber(ggml, onnx, () => GgmlEngineOptions.IsOrtForced(env));
    }

    private static ITranscriber CreateOnnx(
        EngineChoice choice, NemotronModel int4, NemotronFp16Model fp16, OnnxSessionFactory factory)
        => choice switch
        {
            EngineChoice.Fp16Dml => new NemotronFp16Transcriber(fp16, factory, ComputeBackend.DirectML),
            EngineChoice.Int4DmlEncoder => new NemotronTranscriber(int4, factory, ComputeBackend.DirectML),
            _ => new NemotronTranscriber(int4, factory, ComputeBackend.Cpu),
        };

    /// <summary>Applies the stored language (locale code, or a legacy display name) to the engine.
    /// Called at startup and on change; takes effect on the NEXT dictation (sessions snapshot it).</summary>
    public static void ApplyLanguage(ITranscriber transcriber, string language)
    {
        if (transcriber is SelectingTranscriber sel)
        {
            sel.SetLanguage(language);
            return;
        }
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
