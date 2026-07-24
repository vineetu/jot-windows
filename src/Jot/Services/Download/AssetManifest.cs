namespace Jot.Services.Download;

/// <summary>One downloadable file: exact size (byte-true progress + completeness guard) and SHA-256
/// (integrity — a corrupted or tampered file must never be promoted to a "ready" model).</summary>
public sealed record DownloadAsset(string Name, long Bytes, string? Sha256 = null);

/// <summary>
/// A model's complete download recipe — base URL plus every file with its exact size and hash. Both
/// model installers (int4 CPU + fp16 GPU) hand a manifest to <see cref="AssetDownloader"/>, so there is
/// exactly one downloader implementation and N cheap manifests, never a second copy of the retry/resume
/// logic to drift.
/// </summary>
public sealed record AssetManifest(string BaseUrl, IReadOnlyList<DownloadAsset> Assets)
{
    /// <summary>Total download size in bytes — lets the UI show "X MB of Y MB".</summary>
    public long TotalBytes { get; } = Assets.Sum(a => a.Bytes);

    /// <summary>
    /// A non-technical status line for a [0,1] progress fraction, e.g. "Downloading… 340 MB of 754 MB (45%)".
    /// The MB counter keeps moving even when the rounded percent looks stuck, so a slow download reads as alive.
    /// </summary>
    public string DescribeProgress(double fraction)
    {
        double totalMb = TotalBytes / (1024.0 * 1024.0);
        return $"Downloading… {fraction * totalMb:0} MB of {totalMb:0} MB ({fraction * 100:0}%)";
    }
}
