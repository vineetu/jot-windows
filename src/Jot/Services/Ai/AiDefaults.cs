namespace Jot.Services.Ai;

/// <summary>
/// Per-provider defaults so the user doesn't have to hunt for an endpoint or model name — pick a
/// provider, pick a model from the curated list, and (Ollama aside) add a key. Advanced settings
/// expose free-text base-URL/model overrides for anything not in the list.
/// Single source of truth: <see cref="AiClient"/> falls back to these, and Settings shows them.
/// </summary>
public static class AiDefaults
{
    private static string Key(string? provider) => (provider ?? "").Trim().ToLowerInvariant();

    /// <summary>The curated model IDs offered in the (non-advanced) model dropdown. First = default.</summary>
    public static string[] Models(string? provider) => Key(provider) switch
    {
        "openai" => ["gpt-5.6-terra", "gpt-5.6-luna"],
        "anthropic" => ["claude-haiku-4-5", "claude-sonnet-5"],
        "gemini" => ["gemini-3.1-flash-lite", "gemini-3.5-flash"],
        "ollama" => ["gemma4:e4b", "gemma4:e2b"],
        _ => [],
    };

    /// <summary>The default model for a provider (the first curated option).</summary>
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
        _ => "",
    };

    /// <summary>Where the user gets an API key for this provider — surfaced as a link under the key box.</summary>
    public static string ApiKeyUrl(string? provider) => Key(provider) switch
    {
        "openai" => "https://platform.openai.com/api-keys",
        "anthropic" => "https://console.anthropic.com/settings/keys",
        "gemini" => "https://aistudio.google.com/apikey",
        _ => "", // Ollama runs locally — no key page
    };

    /// <summary>Cloud providers need an API key; Ollama runs locally and doesn't.</summary>
    public static bool NeedsKey(string? provider) => Key(provider) is "openai" or "anthropic" or "gemini";
}
