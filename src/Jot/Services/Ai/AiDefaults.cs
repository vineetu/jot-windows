namespace Jot.Services.Ai;

/// <summary>
/// Per-provider defaults (endpoint, curated model list, key page). Single source of truth:
/// <see cref="AiClient"/> falls back to these, and Settings shows them.
/// </summary>
public static class AiDefaults
{
    private static string Key(string? provider) => (provider ?? "").Trim().ToLowerInvariant();

    /// <summary>Curated model IDs for the dropdown. First = default.</summary>
    public static string[] Models(string? provider) => Key(provider) switch
    {
        "openai" => ["gpt-5.6-terra", "gpt-5.6-luna"],
        "anthropic" => ["claude-haiku-4-5", "claude-sonnet-5"],
        "gemini" => ["gemini-3.1-flash-lite", "gemini-3.5-flash"],
        "ollama" => ["gemma4:e4b", "gemma4:e2b"],
        "pfb" => PfbGateway.ModelIds,   // gateway models; exact IDs sent in `model`
        _ => [],
    };

    public static string Model(string? provider)
    {
        string[] models = Models(provider);
        return models.Length > 0 ? models[0] : "";
    }

    public static string BaseUrl(string? provider) => Key(provider) switch
    {
        "openai" => "https://api.openai.com/v1",
        "anthropic" => "https://api.anthropic.com/v1",
        "gemini" => "https://generativelanguage.googleapis.com/v1beta",
        "ollama" => "http://localhost:11434",
        "pfb" => PfbGateway.ProdBaseUrl,   // Sony-internal; reachable only on the Sony network/VPN
        _ => "",
    };

    /// <summary>API-key page for the provider; shown as a link under the key box.</summary>
    public static string ApiKeyUrl(string? provider) => Key(provider) switch
    {
        "openai" => "https://platform.openai.com/api-keys",
        "anthropic" => "https://console.anthropic.com/settings/keys",
        "gemini" => "https://aistudio.google.com/apikey",
        _ => "", // Ollama runs locally — no key page
    };

    /// <summary>Cloud providers need a pasted API key; Ollama runs locally, and PFB authenticates via
    /// a JWT from its own sign-in flow (see <see cref="PfbAuth"/>) — neither shows the API-key box.</summary>
    public static bool NeedsKey(string? provider) => Key(provider) is "openai" or "anthropic" or "gemini";
}
