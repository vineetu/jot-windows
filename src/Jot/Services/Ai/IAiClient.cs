namespace Jot.Services.Ai;

/// <summary>
/// Immutable snapshot of the user's AI settings, passed per call. No stored API key —
/// <see cref="ApiKey"/> is supplied per request (in-memory only; see <c>SettingsViewModel.AiApiKey</c>).
/// </summary>
/// <param name="Provider">None | OpenAI | Anthropic | Gemini | Ollama (case-insensitive).</param>
/// <param name="BaseUrl">Optional endpoint override; falls back to the provider default.</param>
/// <param name="Model">Optional model name; falls back to the provider default.</param>
/// <param name="ApiKey">Provider API key. Not required for Ollama.</param>
public sealed record AiConfig(string Provider, string? BaseUrl, string? Model, string? ApiKey);

/// <summary>Outcome of a reachability check, with UI-safe <see cref="Message"/>.</summary>
public sealed record AiResult(bool Ok, string Message);

/// <summary>Minimal chat client over the configured AI provider; implementations share one HttpClient.</summary>
public interface IAiClient
{
    /// <summary>Verifies the endpoint/credentials are reachable; surfaces errors as a friendly result.</summary>
    Task<AiResult> TestConnectionAsync(AiConfig config, CancellationToken ct = default);

    /// <summary>
    /// Rewrites <paramref name="original"/> per <paramref name="instruction"/>, returning only the
    /// rewritten text. Empty instruction means "improve clarity/flow, preserving meaning/tone/length".
    /// Throws on failure — the user is waiting on the result.
    /// </summary>
    Task<string> RewriteAsync(string original, string instruction, AiConfig config, CancellationToken ct = default);

    /// <summary>One-shot chat used by Ask Jot; throws on failure.</summary>
    Task<string> AskAsync(string systemPrompt, string userMessage, AiConfig config, CancellationToken ct = default);
}
