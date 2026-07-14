namespace Jot.Services.Ai;

/// <summary>
/// Per-provider defaults so the user doesn't have to hunt for an endpoint or model name — pick a
/// provider and (Ollama aside) just add a key. Advanced settings expose the base-URL/model overrides.
/// Single source of truth: <see cref="AiClient"/> falls back to these, and Settings shows them.
/// </summary>
public static class AiDefaults
{
    private static string Key(string? provider) => (provider ?? "").Trim().ToLowerInvariant();

    public static string Model(string? provider) => Key(provider) switch
    {
        "openai" => "gpt-5.4-nano",
        "anthropic" => "claude-haiku-4-5",
        "gemini" => "gemini-3.1-flash-lite",
        "ollama" => "gemma4:e4b",
        _ => "",
    };

    public static string BaseUrl(string? provider) => Key(provider) switch
    {
        "openai" => "https://api.openai.com/v1",
        "anthropic" => "https://api.anthropic.com/v1",
        "gemini" => "https://generativelanguage.googleapis.com/v1beta",
        "ollama" => "http://localhost:11434",
        _ => "",
    };

    /// <summary>Cloud providers need an API key; Ollama runs locally and doesn't.</summary>
    public static bool NeedsKey(string? provider) => Key(provider) is "openai" or "anthropic" or "gemini";
}
