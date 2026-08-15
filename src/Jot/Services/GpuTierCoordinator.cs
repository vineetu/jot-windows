using Jot.Platform;
using Jot.Services.Abstractions;
using Jot.Transcription;
using Jot.Transcription.Ggml;

namespace Jot.Services;

/// <summary>
/// Zero-touch GGUF adoption: fetch the Q8_0 model in the background when it is missing, probe
/// Vulkan, and after a successful ggml warm-up delete leftover int4/fp16 files. The first-run
/// wizard uses the same <see cref="ModelDownload"/>; this covers existing users who already
/// completed setup on ONNX. Does not fetch fp16 anymore (one-model payoff).
/// </summary>
public sealed class GpuTierCoordinator
{
    private readonly ISettingsStore _settings;
    private readonly NemotronGgufModel _gguf;
    private readonly ModelDownload _download;

    /// <summary>Raised (on the dispatcher) the one time a GGUF fetch + probe finishes while this
    /// launch started on ONNX — App kicks a ggml warm-up so the next dictation does not hitch.</summary>
    public event Action? UpgradeReady;

    public GpuTierCoordinator(
        ISettingsStore settings,
        NemotronGgufModel gguf,
        ModelDownload download)
    {
        _settings = settings;
        _gguf = gguf;
        _download = download;
    }

    /// <summary>Call once per launch from a background task.</summary>
    public async Task RunAsync()
    {
        bool fetchedThisLaunch = false;
        if (!_gguf.IsInstalled)
        {
            if (IsMeteredConnection())
            {
                JotLog.Info("ggml adopt: skipped fetch (metered connection); will recheck next launch");
                return;
            }
            JotLog.Info("ggml adopt: fetching Q8_0 GGUF in the background");
            if (!await _download.EnsureAsync().ConfigureAwait(false))
            {
                JotLog.Info("ggml adopt: fetch incomplete — will resume next launch");
                return;
            }
            fetchedThisLaunch = true;
        }

        var adapter = GpuInfo.TryGetPrimaryAdapter();
        var s = _settings.Current;
        bool needProbe = adapter is not null &&
            (s.GpuProbeVerdict is null || s.GpuProbeKey != adapter.CacheKey);

        ProbeResult? probe = null;
        if (needProbe && GgmlNativeLocator.IsPresent())
        {
            probe = GgmlProbe.Run(_gguf);
            JotLog.Info($"ggml probe: viable={probe.GpuViable} ({probe.Reason})");
        }

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (adapter is not null && probe is not null)
            {
                s.GpuProbeKey = adapter.CacheKey;
                s.GpuProbeVerdict = probe.GpuViable ? "GPU" : "CPU";
                s.GpuProbeAvgChunkMs = probe.AvgChunkMs;
                s.GpuProbeReason = probe.Reason;
                _settings.Save();
            }
            _download.Refresh();

            if (fetchedThisLaunch && _gguf.IsInstalled && !s.GpuUpgradeBalloonShown)
            {
                s.GpuUpgradeBalloonShown = true;
                _settings.Save();
                UpgradeReady?.Invoke();
            }
        });
    }

    /// <summary>True when Windows reports the internet connection as metered/capped. Unknown → treat as
    /// unmetered (the overwhelmingly common case; the download resumes across launches regardless).</summary>
    private static bool IsMeteredConnection()
    {
        try
        {
            var cost = Windows.Networking.Connectivity.NetworkInformation
                .GetInternetConnectionProfile()?.GetConnectionCost();
            return cost is not null
                && cost.NetworkCostType != Windows.Networking.Connectivity.NetworkCostType.Unrestricted;
        }
        catch
        {
            return false;
        }
    }
}
