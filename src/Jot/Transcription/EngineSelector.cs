namespace Jot.Transcription;

/// <summary>Canonical TranscriptionDevice setting values (shown verbatim in the Settings picker).</summary>
public static class TranscriptionDevices
{
    public const string Auto = "Auto";
    public const string Cpu = "CPU";
    /// <summary>Leftover label from the ONNX era. <c>StartupMigration.MigrateGpuDeviceLabel</c>
    /// rewrites it to <see cref="GpuVulkan"/> so the picker still matches.</summary>
    public const string Gpu = "GPU (DirectML)";
    /// <summary>Settings label for the ggml Vulkan backend. Contains "GPU" so a leftover
    /// "GPU (DirectML)" value is still recognized as a GPU pick by <c>GgmlEngineOptions</c>.</summary>
    public const string GpuVulkan = "GPU (Vulkan)";
}
