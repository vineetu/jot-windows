using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Jot.Services.Ai;

/// <summary>
/// Raw-HTTP AI client for the four supported providers (OpenAI, Anthropic, Gemini, Ollama).
/// Uses a single shared <see cref="HttpClient"/> with a 30s timeout and <c>System.Text.Json</c>
/// only — no SDKs, no extra packages. See <see cref="IAiClient"/> for the contract.
/// </summary>
public sealed class AiClient : IAiClient
{
    // One shared client for the process lifetime (avoids socket exhaustion from per-call clients).
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Fixed editing instruction. Deliberately strict: fix only, never add/remove/rephrase meaning.
    private const string SystemPrompt =
        "You are a transcription editor. Fix punctuation, capitalization, and remove filler words " +
        "(um, uh, like) from the user's dictated text. Do not add, remove, or rephrase content. " +
        "Return only the corrected text with no preamble.";

    // ---- public API ----

    public async Task<AiResult> TestConnectionAsync(AiConfig config, CancellationToken ct = default)
    {
        string provider = (config.Provider ?? "").Trim();

        if (provider.Length == 0 || provider.Equals("None", StringComparison.OrdinalIgnoreCase))
            return new AiResult(false, "Choose an AI provider first.");

        if (AiDefaults.NeedsKey(provider) && string.IsNullOrWhiteSpace(config.ApiKey))
            return new AiResult(false, $"Enter an API key for {provider}.");

        try
        {
            await RunTestAsync(provider, config, ct).ConfigureAwait(false);
            return new AiResult(true, $"Connected to {provider} successfully.");
        }
        catch (AiHttpException ex)
        {
            return new AiResult(false, DescribeHttp(provider, ex));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AiResult(false, $"Connection to {provider} timed out.");
        }
        catch (HttpRequestException ex)
        {
            return new AiResult(false, $"Couldn't reach {provider}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new AiResult(false, $"{provider} test failed: {ex.Message}");
        }
    }

    public async Task<string> CleanupAsync(string transcript, AiConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return transcript;

        try
        {
            string cleaned = await ChatAsync(SystemPrompt, transcript, config, ct).ConfigureAwait(false);
            // If the model returned nothing usable, keep the user's original words.
            return string.IsNullOrWhiteSpace(cleaned) ? transcript : cleaned.Trim();
        }
        catch
        {
            // Cleanup must never lose the transcript — swallow everything and return the original.
            return transcript;
        }
    }

    // ---- provider dispatch ----

    private static Task<string> ChatAsync(string system, string user, AiConfig c, CancellationToken ct)
        => (c.Provider ?? "").Trim().ToLowerInvariant() switch
        {
            "openai" => OpenAiChatAsync(system, user, c, ct),
            "anthropic" => AnthropicChatAsync(system, user, c, ct),
            "gemini" => GeminiChatAsync(system, user, c, ct),
            "ollama" => OllamaChatAsync(system, user, c, ct),
            _ => throw new NotSupportedException($"AI provider '{c.Provider}' is not supported."),
        };

    private static Task RunTestAsync(string provider, AiConfig c, CancellationToken ct)
        => provider.ToLowerInvariant() switch
        {
            // GET /models is a cheap credentialed probe.
            "openai" => GetAsync(
                BaseUrlOf(c) + "/models",
                req => req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + c.ApiKey), ct),
            // A 1-token messages call is the smallest valid request.
            "anthropic" => DiscardAsync(AnthropicChatAsync("Reply with OK.", "Hi", c, ct, maxTokens: 1)),
            "gemini" => DiscardAsync(GeminiChatAsync("Reply with OK.", "Hi", c, ct)),
            // Listing local models needs no key.
            "ollama" => GetAsync(BaseUrlOf(c) + "/api/tags", _ => { }, ct),
            _ => throw new NotSupportedException($"AI provider '{provider}' is not supported."),
        };

    // ---- OpenAI (chat/completions, Bearer) ----

    private static async Task<string> OpenAiChatAsync(string system, string user, AiConfig c, CancellationToken ct)
    {
        string url = BaseUrlOf(c) + "/chat/completions";
        var body = new
        {
            model = Model(c),
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        };

        using JsonDocument doc = await PostJsonAsync(url, body,
            req => req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + c.ApiKey), ct)
            .ConfigureAwait(false);

        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    // ---- Anthropic (messages, x-api-key + anthropic-version) ----

    private static async Task<string> AnthropicChatAsync(
        string system, string user, AiConfig c, CancellationToken ct, int maxTokens = 1024)
    {
        string url = BaseUrlOf(c) + "/messages";
        var body = new
        {
            model = Model(c),
            max_tokens = maxTokens,
            system,
            messages = new[] { new { role = "user", content = user } },
        };

        using JsonDocument doc = await PostJsonAsync(url, body, req =>
        {
            req.Headers.TryAddWithoutValidation("x-api-key", c.ApiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }, ct).ConfigureAwait(false);

        // Response content is an array of blocks; pull the first text block.
        if (doc.RootElement.TryGetProperty("content", out JsonElement content)
            && content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out JsonElement type) && type.GetString() == "text"
                    && block.TryGetProperty("text", out JsonElement text))
                    return text.GetString() ?? "";
            }
        }
        return "";
    }

    // ---- Gemini (generateContent, key in query string) ----

    private static async Task<string> GeminiChatAsync(string system, string user, AiConfig c, CancellationToken ct)
    {
        string baseUrl = BaseUrlOf(c);
        string model = Model(c);
        string url = $"{baseUrl}/models/{model}:generateContent?key={Uri.EscapeDataString(c.ApiKey ?? "")}";
        var body = new
        {
            system_instruction = new { parts = new[] { new { text = system } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = user } } } },
        };

        using JsonDocument doc = await PostJsonAsync(url, body, _ => { }, ct).ConfigureAwait(false);

        if (doc.RootElement.TryGetProperty("candidates", out JsonElement candidates)
            && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0
            && candidates[0].TryGetProperty("content", out JsonElement content)
            && content.TryGetProperty("parts", out JsonElement parts)
            && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement part in parts.EnumerateArray())
                if (part.TryGetProperty("text", out JsonElement text))
                    return text.GetString() ?? "";
        }
        return "";
    }

    // ---- Ollama (api/chat, no key) ----

    private static async Task<string> OllamaChatAsync(string system, string user, AiConfig c, CancellationToken ct)
    {
        string url = BaseUrlOf(c) + "/api/chat";
        var body = new
        {
            model = Model(c), // defaults to llama3.2 via AiDefaults
            stream = false,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        };

        using JsonDocument doc = await PostJsonAsync(url, body, _ => { }, ct).ConfigureAwait(false);

        if (doc.RootElement.TryGetProperty("message", out JsonElement message)
            && message.TryGetProperty("content", out JsonElement content))
            return content.GetString() ?? "";
        return "";
    }

    // ---- HTTP plumbing ----

    private static async Task<JsonDocument> PostJsonAsync(
        string url, object body, Action<HttpRequestMessage> configure, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        configure(req);

        using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AiHttpException((int)resp.StatusCode, text);

        return JsonDocument.Parse(text);
    }

    private static async Task GetAsync(string url, Action<HttpRequestMessage> configure, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        configure(req);

        using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new AiHttpException((int)resp.StatusCode, text);
        }
    }

    private static async Task DiscardAsync(Task<string> task) => await task.ConfigureAwait(false);

    // ---- helpers ----

    // Base URL / model: use the user's override when set, else the provider default (single source
    // of truth in AiDefaults, shared with the Settings UI).
    private static string BaseUrlOf(AiConfig c)
        => (string.IsNullOrWhiteSpace(c.BaseUrl) ? AiDefaults.BaseUrl(c.Provider) : c.BaseUrl!.Trim()).TrimEnd('/');

    private static string Model(AiConfig c)
        => string.IsNullOrWhiteSpace(c.Model) ? AiDefaults.Model(c.Provider) : c.Model!.Trim();

    private static string DescribeHttp(string provider, AiHttpException ex)
    {
        string detail = ExtractError(ex.Body);
        string suffix = detail.Length > 0 ? $" — {detail}" : "";
        return ex.StatusCode switch
        {
            401 or 403 => $"{provider} rejected the API key (HTTP {ex.StatusCode}){suffix}",
            404 => $"{provider} endpoint or model not found (HTTP 404){suffix}",
            429 => $"{provider} is rate limiting requests (HTTP 429){suffix}",
            >= 500 => $"{provider} server error (HTTP {ex.StatusCode}){suffix}",
            _ => $"{provider} returned HTTP {ex.StatusCode}{suffix}",
        };
    }

    /// <summary>Best-effort extraction of a provider's error message from a JSON body.</summary>
    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("error", out JsonElement err))
            {
                if (err.ValueKind == JsonValueKind.String) return err.GetString() ?? "";
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out JsonElement m))
                    return m.GetString() ?? "";
            }
            if (root.TryGetProperty("message", out JsonElement msg) && msg.ValueKind == JsonValueKind.String)
                return msg.GetString() ?? "";
        }
        catch { /* not JSON — fall through to a trimmed raw snippet */ }

        string trimmed = body.Trim();
        return trimmed.Length > 200 ? trimmed[..200] : trimmed;
    }

    /// <summary>Carries a non-2xx status code plus the raw response body for friendly messaging.</summary>
    private sealed class AiHttpException(int statusCode, string body) : Exception
    {
        public int StatusCode { get; } = statusCode;
        public string Body { get; } = body;
    }
}
