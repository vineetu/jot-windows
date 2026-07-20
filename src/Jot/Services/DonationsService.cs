using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jot.Services;

/// <summary>
/// Read-only client for the donations summary endpoint at
/// <c>https://jot-donations.ideaflow.page/summary</c> — the same public, stateless, no-auth,
/// no-device-identifier endpoint the Mac/iOS apps use. The server learns of donations via an
/// Every.org webhook; the client only ever GETs the totals. User-initiated (opening the donate
/// popup), so it doesn't violate the "no telemetry / no automatic network calls" pledge.
/// </summary>
public sealed class DonationsService
{
    private const string SummaryUrl = "https://jot-donations.ideaflow.page/summary";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Fetch the latest totals, or null on any failure (offline / bad status / parse error).</summary>
    public async Task<DonationsSummary?> FetchSummaryAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await Http.GetStringAsync(SummaryUrl, ct);
            return JsonSerializer.Deserialize<DonationsSummary>(json);
        }
        catch
        {
            return null; // caller shows an offline/retry state
        }
    }
}

public sealed class DonationsSummary
{
    [JsonPropertyName("total_donations")] public int TotalDonations { get; set; }
    [JsonPropertyName("total_raised_usd")] public double TotalRaisedUsd { get; set; }
    [JsonPropertyName("per_charity")] public List<DonationCharity> PerCharity { get; set; } = new();
    [JsonPropertyName("last_updated")] public DateTime LastUpdated { get; set; }
}

public sealed class DonationCharity
{
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("fundraiser_url")] public string? FundraiserUrl { get; set; }
    [JsonPropertyName("logo_url")] public string? LogoUrl { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("total_raised_usd")] public double TotalRaisedUsd { get; set; }

    // Display helpers for the donations popup.
    [JsonIgnore] public bool HasLogo => !string.IsNullOrWhiteSpace(LogoUrl);
    [JsonIgnore] public bool HasAmount => TotalRaisedUsd > 0;

    /// <summary>"$40 raised · 3 donations" for this charity, or "" if nothing raised yet. Invariant
    /// culture so a non-US locale doesn't mangle the "$" amount.</summary>
    [JsonIgnore]
    public string AmountDisplay => TotalRaisedUsd > 0
        ? $"${TotalRaisedUsd.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} raised · " +
          $"{Count} {(Count == 1 ? "donation" : "donations")}"
        : "";
}
