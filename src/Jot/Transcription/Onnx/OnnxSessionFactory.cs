using Microsoft.ML.OnnxRuntime;

namespace Jot.Transcription.Onnx;

/// <summary>Where an ONNX model runs. The spotter is always CPU — see <see cref="OnnxSessionFactory"/>.</summary>
public enum ComputeBackend
{
    /// <summary>In-box CPU execution provider. Works on every x64 Windows machine, no drivers.</summary>
    Cpu,

    /// <summary>
    /// Historical. DirectML next to a live Vulkan ggml engine was a named kill (15.7 s vs a
    /// 4000 ms vocabulary deadline). The factory treats this as CPU. Kept so leftover
    /// <c>--transcribe --dml</c> callers still compile.
    /// </summary>
    DirectML,
}

/// <summary>
/// The single seam for creating ONNX Runtime <see cref="InferenceSession"/>s.
/// CPU only: ORT remains solely for the Parakeet CTC 110M vocabulary spotter.
/// DirectML is not offered — Vulkan (ggml) + DirectML in one process dropped corrections.
/// </summary>
public sealed class OnnxSessionFactory
{
    /// <summary>Raised when a requested backend could not be created and we fell back to CPU.
    /// DirectML requests always take this path now (the EP is gone).</summary>
    public event Action<string>? BackendFallback;

    public InferenceSession Create(string modelPath, ComputeBackend backend)
    {
        if (backend == ComputeBackend.DirectML)
            BackendFallback?.Invoke("DirectML is no longer used; the CTC spotter runs on CPU.");

        return new InferenceSession(modelPath, CpuOptions());
    }

    private static SessionOptions CpuOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        // Silence ORT's WARNING-level chatter (e.g. "can't constant fold MatMul"). Besides being noise,
        // a GUI app has no console, so those native log writes can fault on an invalid stderr handle
        // during session creation. Matches the Python reference's log_severity_level=3.
        LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
    };
}
