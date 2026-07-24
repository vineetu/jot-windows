using Jot.Services.Download;

namespace Jot.Transcription;

/// <summary>
/// A downloadable on-device model: presence check + the manifest describing what to fetch + the fetch
/// itself. Lets one observable download surface (<see cref="Jot.Services.ModelDownload"/>) drive any
/// model (int4 CPU, fp16 GPU) without knowing which one it holds.
/// </summary>
public interface IModelInstaller
{
    bool IsInstalled { get; }
    AssetManifest Manifest { get; }
    Task EnsureInstalledAsync(IProgress<double>? progress = null, CancellationToken ct = default);
}
