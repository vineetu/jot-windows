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
/// The single place ONNX Runtime <see cref="InferenceSession"/>s are created. Today it uses the
/// in-box CPU and DirectML execution providers shipped by <c>Microsoft.ML.OnnxRuntime.DirectML</c>.
///
/// This is the deliberate seam for the planned migration to <b>Windows ML</b>
/// (<c>Microsoft.Windows.AI.MachineLearning</c>): to add Copilot+ NPU coverage (Qualcomm QNN /
/// Intel OpenVINO / AMD VitisAI) we only change the body of <see cref="Create"/> to register the
/// certified EP catalog and pick a device via <c>GetEpDevices()</c>/<c>SetEpSelectionPolicy</c>.
/// Callers (<see cref="ParakeetTranscriber"/>) are unaffected because they still receive a plain
/// <see cref="InferenceSession"/>.
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
    };

    private static SessionOptions DirectMlOptions()
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // The DirectML EP requires sequential execution and no arena memory pattern.
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = false,
        };
        options.AppendExecutionProvider_DML(0); // adapter 0 (default GPU)
        return options;
    }
}
