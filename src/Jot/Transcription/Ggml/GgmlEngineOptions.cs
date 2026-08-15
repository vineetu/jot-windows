using Jot.Services.Abstractions;

namespace Jot.Transcription.Ggml;

/// <summary>
/// Env / settings knobs for the ggml engine. Default is ON when the Q8_0 GGUF and official
/// natives are present. <c>JOT_ENGINE=ort</c> is the emergency back-out onto ONNX.
/// <c>UseGgmlEngine</c> remains a legacy force-on (no Settings toggle).
/// </summary>
internal sealed class GgmlEngineOptions
{
    public const string EngineEnvVar = "JOT_ENGINE";
    public const string LookaheadEnvVar = "JOT_GGML_R";
    public const string BackendEnvVar = "JOT_GGML_BACKEND";

    /// <summary>Nemotron 3.5 trained R menu. Anything else is treated as the live default (3).</summary>
    public static readonly int[] LegalLookaheads = [0, 3, 6, 13];

    public const int DefaultLookahead = 3;

    public bool Enabled { get; init; }
    public int AttContextRight { get; init; } = DefaultLookahead;
    public NativeMethods.BackendRequest Backend { get; init; } = NativeMethods.BackendRequest.Cpu;
    public string? NativeDir { get; init; }

    public static bool IsOrtForced(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        return string.Equals(env(EngineEnvVar), "ort", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEnabled(JotSettings? s, Func<string, string?>? env = null, bool assetsPresent = false)
    {
        env ??= Environment.GetEnvironmentVariable;
        string? engine = env(EngineEnvVar);
        if (string.Equals(engine, "ggml", StringComparison.OrdinalIgnoreCase)) return true;
        // Explicit ort wins over a leftover settings.json flag — emergency back-out.
        if (string.Equals(engine, "ort", StringComparison.OrdinalIgnoreCase)) return false;
        if (s?.UseGgmlEngine == true) return true;
        // Shipping default: ggml when the GGUF and official natives are both on disk.
        return assetsPresent;
    }

    public static GgmlEngineOptions Resolve(
        JotSettings s,
        EngineChoice choice,
        Func<string, string?>? env = null,
        bool assetsPresent = false)
    {
        env ??= Environment.GetEnvironmentVariable;
        return new GgmlEngineOptions
        {
            Enabled = IsEnabled(s, env, assetsPresent),
            AttContextRight = ResolveLookahead(s.GgmlAttContextRight, env),
            Backend = ResolveBackend(choice, env, s.TranscriptionDevice),
            NativeDir = BlankToNull(env(GgmlNativeLocator.EnvVar)),
        };
    }

    internal static int ResolveLookahead(int settingValue, Func<string, string?> env)
    {
        if (int.TryParse(env(LookaheadEnvVar), out int fromEnv) && IsLegal(fromEnv))
            return fromEnv;
        return IsLegal(settingValue) ? settingValue : DefaultLookahead;
    }

    internal static NativeMethods.BackendRequest ResolveBackend(
        EngineChoice choice, Func<string, string?> env, string? device = null)
    {
        string? raw = env(BackendEnvVar);
        if (string.Equals(raw, "cpu", StringComparison.OrdinalIgnoreCase))
            return NativeMethods.BackendRequest.Cpu;
        if (string.Equals(raw, "vulkan", StringComparison.OrdinalIgnoreCase))
            return NativeMethods.BackendRequest.Vulkan;
        if (string.Equals(raw, "auto", StringComparison.OrdinalIgnoreCase))
            return NativeMethods.BackendRequest.Auto;

        // Explicit CPU stays on ggml-CPU. Auto and any GPU pick try Vulkan; load retries CPU
        // if the ICD is missing (measured: no crash, TRANSCRIBE_ERR_BACKEND).
        if (string.Equals(device, TranscriptionDevices.Cpu, StringComparison.OrdinalIgnoreCase))
            return NativeMethods.BackendRequest.Cpu;

        if (choice is EngineChoice.Fp16Dml or EngineChoice.Int4DmlEncoder)
            return NativeMethods.BackendRequest.Vulkan;

        // Auto (EngineSelector still returns Int4Cpu when there is no leftover fp16 verdict)
        // is the shipping default and must try Vulkan, not inherit the ONNX CPU fallback.
        if (string.Equals(device, TranscriptionDevices.Auto, StringComparison.OrdinalIgnoreCase) ||
            (device is not null && device.Contains("GPU", StringComparison.OrdinalIgnoreCase)))
            return NativeMethods.BackendRequest.Vulkan;

        return NativeMethods.BackendRequest.Cpu;
    }

    private static bool IsLegal(int r)
    {
        foreach (int legal in LegalLookaheads)
            if (legal == r) return true;
        return false;
    }

    private static string? BlankToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
