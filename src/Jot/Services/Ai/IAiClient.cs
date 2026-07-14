namespace Jot.Services.Ai;

/// <summary>
/// Immutable snapshot of the user's AI settings, passed in at call time. Mirrors the Settings
/// surface (provider picker + base URL + model + key). There is deliberately no stored API key —
/// <see cref="ApiKey"/> is supplied by the caller per request (kept in-memory only; see
/// <c>SettingsViewModel.AiApiKey</c>).
/// </summary>
/// <param name="Provider">One of None | OpenAI | Anthropic | Gemini | Ollama (case-insensitive).</param>
/// <param name="BaseUrl">Optional endpoint override; each provider falls back to its public default.</param>
/// <param name="Model">Optional model name; each provider falls back to a sensible default.</param>
/// <param name="ApiKey">Provider API key. Not required for Ollama.</param>
public sealed record AiConfig(string Provider, string? BaseUrl, string? Model, string? ApiKey);

/// <summary>Outcome of a reachability check: <see cref="Ok"/> plus a user-facing <see cref="Message"/>.</summary>
/// <param name="Ok">True when the endpoint/credentials were reachable and accepted.</param>
/// <param name="Message">Friendly success or failure text, safe to show in the UI.</param>
public sealed record AiResult(bool Ok, string Message);

/// <summary>
/// A minimal chat client over the configured AI provider. Two jobs only: verify the connection,
/// and clean up a raw dictation transcript. Implementations use a single shared HttpClient and
/// never throw out of <see cref="CleanupAsync"/> — cleanup must never lose the user's words.
/// </summary>
public interface IAiClient
{
    /// <summary>Verifies the endpoint/credentials are reachable. Surfaces errors as a friendly result.</summary>
    Task<AiResult> TestConnectionAsync(AiConfig config, CancellationToken ct = default);

    /// <summary>
    /// Returns a cleaned copy of <paramref name="transcript"/> (filler removed, punctuation/casing
    /// fixed) without changing meaning or adding content. On any error — network, bad key, non-200,
    /// unsupported provider — returns the original transcript unchanged. Never throws.
    /// </summary>
    Task<string> CleanupAsync(string transcript, AiConfig config, CancellationToken ct = default);

    /// <summary>
    /// Rewrites <paramref name="original"/> per <paramref name="instruction"/> (a prompt body or a
    /// spoken instruction) and returns only the rewritten text. An empty instruction means "improve
    /// clarity and flow, preserving meaning/tone/length". Unlike cleanup, this SURFACES errors — the
    /// user is actively waiting on the result — so it throws on failure.
    /// </summary>
    Task<string> RewriteAsync(string original, string instruction, AiConfig config, CancellationToken ct = default);

    /// <summary>
    /// One-shot chat: sends <paramref name="systemPrompt"/> + <paramref name="userMessage"/> to the
    /// configured provider and returns the reply text. Throws on failure. Used by Ask Jot.
    /// </summary>
    Task<string> AskAsync(string systemPrompt, string userMessage, AiConfig config, CancellationToken ct = default);
}
