using Jot.Transcription.Nemotron;

namespace Jot.Services;

/// <summary>
/// Observable download state for the OPTIONAL fp16 GPU model (~1.3 GB). A separate singleton from the
/// int4 <see cref="ModelDownload"/> so the required first-run download and the background GPU upgrade
/// each have their own progress/status surface (Settings shows both), while sharing one implementation.
/// </summary>
public sealed class GpuModelDownload : ModelDownload
{
    public const string GpuInstalledText = "Nemotron 3.5 FP16 · Installed";
    public const string GpuNotInstalledText = "Not installed (~1.3 GB)";

    public GpuModelDownload(NemotronFp16ModelInstaller installer)
        : base(installer, GpuInstalledText, GpuNotInstalledText) { }
}
