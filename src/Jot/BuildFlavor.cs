namespace Jot;

/// <summary>
/// Compile-time build flavor (<c>-p:Flavor=Sony</c> defines <c>SONY</c>). Public (default, Store):
/// bring-your-own cloud providers, no PFB, gateway hostnames not compiled in. Sony (internal): PFB is
/// the only online AI, external providers removed. Compile-time on purpose — a Sony build cannot reach
/// an external provider even if <c>settings.json</c> is hand-edited.
/// </summary>
public static class BuildFlavor
{
#if SONY
    public const bool IsSony = true;
    public const string Name = "Sony";
    public const bool HasPfb = true;

    public static readonly string[] AiProviders = ["None", "PFB"];

    public const string AiInfoTitle = "Powered by the PFB AI Gateway";
    public const string AiInfoMessage =
        "Optional transcript rewrite runs through the Sony PFB AI Gateway. Sign in with " +
        "your Sony account below. Requires the Sony network or VPN.";
#else
    public const bool IsSony = false;
    public const string Name = "Public";
    public const bool HasPfb = false;

    public static readonly string[] AiProviders = ["None", "OpenAI", "Anthropic", "Gemini", "Ollama"];

    public const string AiInfoTitle = "Bring your own provider";
    public const string AiInfoMessage =
        "Choose a provider below for optional AI rewrite. Ollama runs fully " +
        "on-device; the rest need an API key.";
#endif
}
