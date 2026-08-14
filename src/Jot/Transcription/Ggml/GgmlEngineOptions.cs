using Jot.Services.Abstractions;

namespace Jot.Transcription.Ggml;

/// <summary>
/// Hidden / env-var knobs for the ggml engine. Default is OFF — shipping users stay on ONNX
/// until the owner flips this. There is no Settings toggle on this branch.
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

    public static bool IsEnabled(JotSettings? s, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        string? engine = env(EngineEnvVar);
        if (string.Equals(engine, "ggml", StringComparison.OrdinalIgnoreCase)) return true;
        // Explicit ort wins over a leftover settings.json flag — emergency back-out.
        if (string.Equals(engine, "ort", StringComparison.OrdinalIgnoreCase)) return false;
        return s?.UseGgmlEngine == true;
    }

    public static GgmlEngineOptions Resolve(
        JotSettings s,
        EngineChoice choice,
        Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        return new GgmlEngineOptions
        {
            Enabled = IsEnabled(s, env),
            AttContextRight = ResolveLookahead(s.GgmlAttContextRight, env),
            Backend = ResolveBackend(choice, env),
            NativeDir = BlankToNull(env(GgmlNativeLocator.EnvVar)),
        };
    }

    internal static int ResolveLookahead(int settingValue, Func<string, string?> env)
    {
        if (int.TryParse(env(LookaheadEnvVar), out int fromEnv) && IsLegal(fromEnv))
            return fromEnv;
        return IsLegal(settingValue) ? settingValue : DefaultLookahead;
    }

    internal static NativeMethods.BackendRequest ResolveBackend(EngineChoice choice, Func<string, string?> env)
    {
        string? raw = env(BackendEnvVar);
        if (string.Equals(raw, "cpu", StringComparison.OrdinalIgnoreCase))
            return NativeMethods.BackendRequest.Cpu;
        if (string.Equals(raw, "vulkan", StringComparison.OrdinalIgnoreCase))
            return NativeMethods.BackendRequest.Vulkan;
        if (string.Equals(raw, "auto", StringComparison.OrdinalIgnoreCase))
            return NativeMethods.BackendRequest.Auto;

        // Follow EngineSelector's existing GPU/CPU split. Auto-without-proof is Int4Cpu → CPU.
        return choice is EngineChoice.Fp16Dml or EngineChoice.Int4DmlEncoder
            ? NativeMethods.BackendRequest.Vulkan
            : NativeMethods.BackendRequest.Cpu;
    }

    private static bool IsLegal(int r)
    {
        foreach (int legal in LegalLookaheads)
            if (legal == r) return true;
        return false;
    }

    private static string? BlankToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
