using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jot.Services;

/// <summary>
/// Thin client for the feedback service at <c>https://jot-donations.ideaflow.page/feedback</c> — the
/// same endpoint and wire format the Mac/iOS apps use: POST <c>{platform, version, message}</c>, response
/// <c>{status:"ok", id}</c> or <c>{status:"error", error}</c>. User-initiated only (the user types a
/// message and presses Send), so it doesn't violate the no-telemetry pledge. No email client involved.
/// </summary>
public sealed class FeedbackClient
{
    private const string Endpoint = "https://jot-donations.ideaflow.page/feedback";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Submit one message. Returns the server-assigned id, or throws
    /// <see cref="FeedbackException"/> with a user-presentable message on rejection / transport error.</summary>
    public async Task<int> SendAsync(string message, CancellationToken ct = default)
    {
        string version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
        var payload = new FeedbackRequest("windows", version, message);
        string json = JsonSerializer.Serialize(payload);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = content };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await Http.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);

        FeedbackResponse? decoded = null;
        try { decoded = JsonSerializer.Deserialize<FeedbackResponse>(body); } catch { /* fall through */ }

        if (decoded is not null)
        {
            if (decoded.Status == "ok" && decoded.Id is int id) return id;
            // Prefer the server's own message (e.g. "Rate limit exceeded. Please try again later.").
            throw new FeedbackException(decoded.Error ?? "The server rejected the feedback.");
        }

        if (!response.IsSuccessStatusCode)
            throw new FeedbackException($"Server returned HTTP {(int)response.StatusCode}.");
        throw new FeedbackException("The server returned an unexpected response.");
    }

    private sealed record FeedbackRequest(
        [property: JsonPropertyName("platform")] string Platform,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("message")] string Message);

    private sealed class FeedbackResponse
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("id")] public int? Id { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }
}

/// <summary>User-presentable feedback failure (rate limit / validation / transport).</summary>
public sealed class FeedbackException(string message) : Exception(message);
