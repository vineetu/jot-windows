namespace Jot.Transcription;

/// <summary>Which engine the app should construct at startup.</summary>
public enum EngineChoice
{
    /// <summary>fp16 model, all graphs on DirectML — the GPU tier.</summary>
    Fp16Dml,
    /// <summary>int4 model on CPU — the default tier, correct everywhere.</summary>
    Int4Cpu,
    /// <summary>int4 with the encoder on DirectML — legacy behavior for an explicit "GPU" pick when the
    /// fp16 model isn't installed (int4 decoder/joint have no DML kernels; factory falls back safely).</summary>
    Int4DmlEncoder,
}

/// <summary>Canonical TranscriptionDevice setting values (shown verbatim in the Settings picker).</summary>
public static class TranscriptionDevices
{
    public const string Auto = "Auto";
    public const string Cpu = "CPU";
    public const string Gpu = "GPU (DirectML)";
}

/// <summary>
/// The one engine-selection rule, kept pure so the full matrix is unit-testable. "Auto" (the default)
/// routes to the GPU tier only when everything is proven: fp16 model on disk AND a cached probe verdict
/// of "GPU" that was measured on the CURRENT adapter+driver (key match). Anything less — no verdict,
/// stale key after a GPU/driver change, model missing — lands on int4/CPU, which is known-good
/// everywhere. Explicit picks are honored exactly as before Auto existed.
/// </summary>
public static class EngineSelector
{
    public static EngineChoice Select(
        string? device, bool fp16Installed, string? cachedVerdict, bool verdictKeyMatches)
    {
        // Explicit GPU: user's call — fp16 when present, else the legacy int4 DML-encoder arrangement.
        bool wantsGpu = device is not null && device.Contains("GPU", StringComparison.OrdinalIgnoreCase);
        if (wantsGpu) return fp16Installed ? EngineChoice.Fp16Dml : EngineChoice.Int4DmlEncoder;

        if (string.Equals(device, TranscriptionDevices.Auto, StringComparison.OrdinalIgnoreCase)
            && fp16Installed && cachedVerdict == "GPU" && verdictKeyMatches)
            return EngineChoice.Fp16Dml;

        return EngineChoice.Int4Cpu; // explicit CPU, Auto-without-proof, or anything unrecognized
    }
}
