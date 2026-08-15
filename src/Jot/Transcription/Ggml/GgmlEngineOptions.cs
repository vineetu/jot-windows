using Jot.Services.Abstractions;

namespace Jot.Transcription.Ggml;

/// <summary>
/// Env / settings knobs for the ggml engine — the only RNNT. <c>JOT_ENGINE=ort</c> is ignored
/// (the ONNX Nemotron path was removed). <c>JOT_ENGINE=ggml</c> is a no-op. Backend is Auto/Vulkan
/// unless the user picked CPU or set <c>JOT_GGML_BACKEND</c>.
/// </summary>
internal sealed class GgmlEngineOptions
{
    public const string EngineEnvVar = "JOT_ENGINE";
    public const string LookaheadEnvVar = "JOT_GGML_R";
    public const string BackendEnvVar = "JOT_GGML_BACKEND";

    /// <summary>Nemotron 3.5 trained R menu. Anything else is treated as the live default (3).</summary>
    public static readonly int[] LegalLookaheads = [0, 3, 6, 13];

    public const int DefaultLookahead = 3;

    public int AttContextRight { get; init; } = DefaultLookahead;
    public NativeMethods.BackendRequest Backend { get; init; } = NativeMethods.BackendRequest.Cpu;
    public string? NativeDir { get; init; }

    /// <summary>True when a leftover <c>JOT_ENGINE=ort</c> is set. The factory logs and ignores it.</summary>
    public static bool IsOrtRequested(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        return string.Equals(env(EngineEnvVar), "ort", StringComparison.OrdinalIgnoreCase);
    }

    public static GgmlEngineOptions Resolve(JotSettings s, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        return new GgmlEngineOptions
        {
            AttContextRight = ResolveLookahead(s.GgmlAttContextRight, env),
            Backend = ResolveBackend(env, s.TranscriptionDevice),
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
        Func<string, string?> env, string? device = null)
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
