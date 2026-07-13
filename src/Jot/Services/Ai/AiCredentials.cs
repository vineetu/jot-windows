namespace Jot.Services.Ai;

/// <summary>
/// Holds the AI provider API key in memory for the current session only (never written to disk), so
/// both Settings (which captures it) and the dictation pipeline (which uses it for optional cleanup)
/// can share it. A production build would back this with the Windows Credential Locker / DPAPI.
/// </summary>
public sealed class AiCredentials
{
    public string? ApiKey { get; set; }
}
