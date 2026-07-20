using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Jot.Services.Ai;

/// <summary>A parsed PFB session: the raw JWT, its expiry, and (optionally) the signed-in subject.</summary>
public sealed record PfbSession(string Token, DateTimeOffset Expiry, string? Subject)
{
    /// <summary>Valid only with a safety cushion, so a call in flight can't die mid-request.</summary>
    public bool IsValid => Expiry > DateTimeOffset.UtcNow.AddSeconds(60);
    public TimeSpan Remaining => Expiry - DateTimeOffset.UtcNow;
    /// <summary>True within the last hour before expiry — used to show an orange "expiring soon" hint.</summary>
    public bool ExpiringSoon => IsValid && Remaining <= TimeSpan.FromMinutes(60);
}

/// <summary>Outcome of a sign-in / install attempt, with UI-safe text.</summary>
public sealed record PfbSignInResult(bool Ok, string Message);

/// <summary>
/// Sign-in and token lifecycle for the PFB AI Gateway (mirrors macOS <c>Flavor1Session</c>): runs the
/// <c>gimme-ai-creds</c> CLI (interactive Okta browser login), captures the JWT off stdout, parses its
/// <c>exp</c>, and stores it encrypted (DPAPI via <see cref="AiCredentials"/> under "PFB") so it flows
/// into <c>AiConfig.ApiKey</c> like any other provider key.
///
/// There is deliberately NO silent or automatic sign-in — a browser Okta step is unavoidable, so
/// <see cref="SignInAsync"/> must only be invoked from an explicit user gesture (the Sign-in button).
/// Tokens live ~24h; on HTTP 401 the app drops the token and returns to the Sign-in button (a fresh
/// token requires the browser again — never auto-retry).
/// </summary>
public sealed class PfbAuth
{
    /// <summary>Provider key under which the JWT is stored in <see cref="AiCredentials"/>.</summary>
    public const string Provider = "PFB";

    // Public CDN download of the Windows credential CLI. Sony-flavor only (the Public/Store binary
    // ships no PlayStation URLs); PFB isn't selectable in the Public flavor so this is inert there.
#if SONY
    public const string CliDownloadUrl =
        "https://download.ai.studios.playstation.com/ai-gateway-cli/binaries/gimme-ai-creds-windows-amd64.exe";
#else
    public const string CliDownloadUrl = "";
#endif

    private readonly AiCredentials _credentials;

    public PfbAuth(AiCredentials credentials) => _credentials = credentials;

    /// <summary>Fixed install path in %LOCALAPPDATA%; existence is checked before every sign-in.</summary>
    public static string CliPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gimme-ai-creds.exe");

    public static bool CliInstalled => File.Exists(CliPath);

    /// <summary>The current stored session, or <c>null</c> when none is stored or it has expired.</summary>
    public PfbSession? Current
    {
        get
        {
            string? token = _credentials.GetKey(Provider);
            if (string.IsNullOrWhiteSpace(token)) return null;
            return TryParse(token, out PfbSession? s) && s!.IsValid ? s : null;
        }
    }

    public bool IsSignedIn => Current is not null;

    /// <summary>Clear the stored JWT (Disconnect button, and on 401 from the gateway).</summary>
    public void Disconnect() => _credentials.SetKey(Provider, "");

    /// <summary>
    /// Runs the CLI, captures the JWT, validates + stores it. Only from a user gesture — the CLI can
    /// pop a browser. Wrapped with a ~125 s watchdog (the CLI's own browser-callback timeout is 120 s).
    /// </summary>
    public async Task<PfbSignInResult> SignInAsync(CancellationToken ct = default)
    {
        if (!CliInstalled)
            return new PfbSignInResult(false, "Sign-in helper isn't installed yet.");

        try
        {
            (int exit, string stdout, string stderr) = await RunCliAsync(ct).ConfigureAwait(false);
            if (exit != 0)
            {
                string detail = Snippet(stderr);
                return new PfbSignInResult(false,
                    detail.Length > 0 ? $"Sign-in failed — {detail}" : "Sign-in failed. Check the logs.");
            }

            string jwt = stdout.Trim();
            if (!TryParse(jwt, out PfbSession? session) || session is null)
                return new PfbSignInResult(false, "Sign-in returned an unreadable token.");
            if (!session.IsValid)
                return new PfbSignInResult(false, "Sign-in returned an already-expired token.");

            _credentials.SetKey(Provider, jwt);
            string who = string.IsNullOrEmpty(session.Subject) ? "" : $" as {session.Subject}";
            return new PfbSignInResult(true, $"Signed in{who}.");
        }
        catch (TimeoutException)
        {
            return new PfbSignInResult(false, "Unreachable. Check your connection / VPN and try again.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new PfbSignInResult(false, "Sign-in cancelled.");
        }
        catch (Exception ex)
        {
            return new PfbSignInResult(false, "Sign-in error: " + ex.Message);
        }
    }

    /// <summary>
    /// Downloads the sign-in helper CLI to <c>%LOCALAPPDATA%</c> (public CDN). Surfaced as the
    /// "Install sign-in helper" action when the CLI is missing.
    /// </summary>
    public async Task<PfbSignInResult> InstallCliAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            byte[] bytes = await http.GetByteArrayAsync(CliDownloadUrl, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(CliPath)!);
            await File.WriteAllBytesAsync(CliPath, bytes, ct).ConfigureAwait(false);
            return new PfbSignInResult(true, "Sign-in helper installed. You can sign in now.");
        }
        catch (Exception ex)
        {
            return new PfbSignInResult(false, "Couldn't download the sign-in helper: " + ex.Message);
        }
    }

    // gimme-ai-creds.exe --org pfb --quiet --no-clipboard
    //   --org pfb        the organization (required)
    //   --quiet          suppress the human banner so stdout is just the token
    //   --no-clipboard   don't touch the clipboard (we capture programmatically)
    private static async Task<(int exit, string stdout, string stderr)> RunCliAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CliPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--org");
        psi.ArgumentList.Add("pfb");
        psi.ArgumentList.Add("--quiet");
        psi.ArgumentList.Add("--no-clipboard");

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        Task<string> outTask = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> errTask = proc.StandardError.ReadToEndAsync(ct);

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(125));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, watchdog.Token);
        try
        {
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (watchdog.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            KillTree(proc);
            throw new TimeoutException("gimme-ai-creds timed out");
        }
        catch (OperationCanceledException)
        {
            KillTree(proc);
            throw;
        }

        string stdout = await outTask.ConfigureAwait(false);
        string stderr = await errTask.ConfigureAwait(false);
        return (proc.ExitCode, stdout, stderr);
    }

    private static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Parses a JWT's <c>exp</c> (Unix seconds) and optional <c>sub</c> from the payload segment.
    /// Signature is NOT verified — the gateway does that; we only need expiry + subject.
    /// </summary>
    public static bool TryParse(string jwt, out PfbSession? session)
    {
        session = null;
        try
        {
            string[] parts = jwt.Split('.');
            if (parts.Length < 2) return false;

            byte[] payload = Base64UrlDecode(parts[1]);
            using JsonDocument doc = JsonDocument.Parse(payload);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("exp", out JsonElement expEl)) return false;

            long exp = expEl.ValueKind == JsonValueKind.Number ? (long)expEl.GetDouble() : 0;
            if (exp <= 0) return false;
            string? sub = root.TryGetProperty("sub", out JsonElement subEl) ? subEl.GetString() : null;

            session = new PfbSession(jwt, DateTimeOffset.FromUnixTimeSeconds(exp), sub);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string s)
    {
        string b = s.Replace('-', '+').Replace('_', '/');
        b = (b.Length % 4) switch { 2 => b + "==", 3 => b + "=", _ => b };
        return Convert.FromBase64String(b);
    }

    private static string Snippet(string? s)
    {
        s = (s ?? "").Trim();
        return s.Length > 200 ? s[..200] : s;
    }
}
