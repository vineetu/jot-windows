using Microsoft.ML.OnnxRuntime;

namespace Jot.Transcription.Onnx;

/// <summary>Where an ONNX model runs. CPU is the universal, driver-free floor.</summary>
public enum ComputeBackend
{
    /// <summary>In-box CPU execution provider. Works on every x64 Windows machine, no drivers.</summary>
    Cpu,

    /// <summary>DirectML (any Direct3D 12 GPU — integrated or discrete). Falls back to CPU on failure.</summary>
    DirectML,
}

/// <summary>
/// The single seam for creating ONNX Runtime <see cref="InferenceSession"/>s (CPU + DirectML EPs),
/// isolated so a future Windows ML / NPU backend only changes <see cref="Create"/>.
/// </summary>
public sealed class OnnxSessionFactory
{
    /// <summary>Raised when a requested backend could not be created and we fell back to CPU.</summary>
    public event Action<string>? BackendFallback;

    public InferenceSession Create(string modelPath, ComputeBackend backend)
    {
        if (backend == ComputeBackend.DirectML)
        {
            try
            {
                return new InferenceSession(modelPath, DirectMlOptions());
            }
            catch (Exception ex)
            {
                // DirectML can fail on old drivers / headless sessions — degrade, never crash.
                BackendFallback?.Invoke($"DirectML unavailable ({ex.Message}); using CPU.");
            }
        }

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

    private static SessionOptions DirectMlOptions()
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // The DirectML EP requires sequential execution and no arena memory pattern.
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = false,
            // See CpuOptions: keep ORT quiet below ERROR so a missing/invalid stderr handle can't fault
            // the native logger while the FP16 encoder's graph is optimised. (Python: log_severity_level=3.)
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        options.AppendExecutionProvider_DML(0); // adapter 0 (default GPU)
        return options;
    }
}
