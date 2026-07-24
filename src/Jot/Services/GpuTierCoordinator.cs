using Jot.Platform;
using Jot.Services.Abstractions;
using Jot.Transcription;
using Jot.Transcription.Nemotron;

namespace Jot.Services;

/// <summary>
/// Zero-touch GPU adoption: on "Auto", Jot upgrades itself to the fp16/DirectML tier with NO user
/// decisions — fetch the model silently when the machine looks capable, benchmark it, cache the verdict,
/// and switch engines at the next launch. A non-advanced user never has to figure anything out; the
/// Settings "GPU model" row is a status surface + manual escape hatch, not the primary path. This is the
/// ONE owner of GPU-verdict upkeep (fetch → probe → save) so the fetch and probe rules can't drift apart.
/// Runs entirely off the boot path; every skip is silent, logged, and rechecked next launch.
/// </summary>
public sealed class GpuTierCoordinator
{
    private readonly ISettingsStore _settings;
    private readonly NemotronFp16Model _fp16Model;
    private readonly GpuModelDownload _download;

    /// <summary>Raised (on the dispatcher) the one time a probe passes while this launch runs int4 —
    /// App shows the informational "restart for faster transcription" balloon.</summary>
    public event Action? UpgradeReady;

    public GpuTierCoordinator(ISettingsStore settings, NemotronFp16Model fp16Model, GpuModelDownload download)
    {
        _settings = settings;
        _fp16Model = fp16Model;
        _download = download;
    }

    /// <summary>Call once per launch from a background task.</summary>
    public async Task RunAsync()
    {
        var s = _settings.Current;
        // Explicit CPU/GPU = the user decided; Auto is the only mode that self-manages.
        if (!string.Equals(s.TranscriptionDevice, TranscriptionDevices.Auto, StringComparison.OrdinalIgnoreCase))
            return;

        var adapter = GpuInfo.TryGetPrimaryAdapter();
        if (adapter is null) return; // no DXGI identity → no honest way to cache a verdict

        if (!_fp16Model.IsInstalled)
        {
            if (!adapter.LooksCapable) return; // weak/software GPU: don't spend 1.3 GB to prove the obvious
            if (IsMeteredConnection())
            {
                JotLog.Info("gpu adopt: skipped fetch (metered connection); will recheck next launch");
                return;
            }
            JotLog.Info($"gpu adopt: fetching fp16 model in the background ({adapter.Description})");
            // Free-space precheck + retry/resume live in the downloader; failure text lands in the
            // Settings row's status and we simply try again next launch (the .part resumes).
            if (!await _download.EnsureAsync().ConfigureAwait(false))
            {
                JotLog.Info("gpu adopt: fetch incomplete — will resume next launch");
                return;
            }
        }

        // Verdict upkeep: earn one for the CURRENT adapter+driver (fresh model, GPU swap, driver update).
        if (s.GpuProbeVerdict is not null && s.GpuProbeKey == adapter.CacheKey) return;

        var r = GpuProbe.Run(_fp16Model); // ~10 s cold; we're on a background thread
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            s.GpuProbeKey = adapter.CacheKey;
            s.GpuProbeVerdict = r.GpuViable ? "GPU" : "CPU";
            s.GpuProbeAvgChunkMs = r.AvgChunkMs;
            s.GpuProbeReason = r.Reason;
            _settings.Save();
            JotLog.Info($"gpu probe: verdict={s.GpuProbeVerdict} ({r.Reason})");
            _download.Refresh(); // settings row: "Installed · in use" vs "didn't pass" text stays honest

            if (r.GpuViable && !s.GpuUpgradeBalloonShown)
            {
                s.GpuUpgradeBalloonShown = true;
                _settings.Save();
                UpgradeReady?.Invoke();
            }
            // Not viable → completely silent: files stay (a future driver may pass; explicit GPU still
            // honors them), next launch routes int4 exactly as this one did.
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
