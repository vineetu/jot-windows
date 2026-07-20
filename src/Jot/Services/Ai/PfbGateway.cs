using System.Text;
using System.Text.Json;

namespace Jot.Services.Ai;

/// <summary>
/// Facts and request-shaping for the PFB (Sony/PlayStation) AI Gateway — the single source of
/// truth for the gateway endpoint, the four model IDs, and the per-model body quirks the gateway
/// enforces. Distilled from the shipping macOS <c>Flavor1Client</c>.
///
/// The host <c>ai-gateway.dspprod.bis.sie.sony.com</c> is INTERNAL to the Sony network/VPN, so
/// none of this can be exercised off-network — it is built to the gateway docs (2026-07-18) and
/// verified by a colleague on the Sony network (see <c>store-assets</c>/the integration guide).
///
/// The gateway's common route is OpenAI Chat-Completions shaped and routes to OpenAI / Anthropic /
/// Bedrock by the <c>model</c> field, so Claude-via-PFB still returns OpenAI-style
/// <c>choices[].message.content</c>. The per-model quirks below are NOT optional — sending the
/// wrong one returns HTTP 400 every time (the #1 cause of "it doesn't work"):
///   • GPT-5.x  → add <c>reasoning_effort</c>, OMIT <c>temperature</c>.
///   • Claude   → send <c>temperature</c>, NO <c>reasoning_effort</c>.
///   • BOTH     → NO token-limit field (the model runs uncapped; per the gateway docs, omit
///     <c>max_tokens</c> / <c>max_completion_tokens</c> entirely).
/// </summary>
public static class PfbGateway
{
    // The Sony-internal gateway hostnames are compiled in only for the Sony flavor, so the public
    // Microsoft Store binary carries no internal Sony URLs. In the Public flavor PFB is never
    // selectable (see BuildFlavor.AiProviders), so these empty defaults are inert.
#if SONY
    /// <summary>Production gateway (common, OpenAI-compatible route). Sony-internal.</summary>
    public const string ProdBaseUrl = "https://ai-gateway.dspprod.bis.sie.sony.com/pfb/common/v1";
    /// <summary>Non-prod gateway, for testing with non-prod keys.</summary>
    public const string NonProdBaseUrl = "https://ai-gateway.dspnonprod.bis.sie.sony.com/pfb/common/v1";
#else
    public const string ProdBaseUrl = "";
    public const string NonProdBaseUrl = "";
#endif

    /// <summary>The four models the gateway exposes. <c>Id</c> is the exact string sent in <c>model</c>.</summary>
    public static readonly (string Label, string Id)[] Catalog =
    [
        ("Claude Haiku 4.5", "global.anthropic.claude-haiku-4-5-20251001-v1:0"),
        ("Claude Sonnet 5",  "global.anthropic.claude-sonnet-5"),
        ("GPT-5.6 Luna",     "gpt-5.6-luna"),
        ("GPT-5.6 Terra",    "gpt-5.6-terra"),
    ];

    /// <summary>The model IDs, verbatim — used both in the Settings dropdown and the request body.</summary>
    public static string[] ModelIds => Catalog.Select(m => m.Id).ToArray();

    /// <summary>Default model (Claude Haiku 4.5 — the cheapest standard tier).</summary>
    public const string DefaultModel = "global.anthropic.claude-haiku-4-5-20251001-v1:0";

    /// <summary>GPT-5.x family — the branch that adds reasoning_effort and omits temperature.</summary>
    public static bool IsGpt5(string model) => model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);

    /// <summary>Friendly label for a model ID, falling back to the raw ID.</summary>
    public static string Label(string id)
    {
        foreach ((string label, string mid) in Catalog)
            if (string.Equals(mid, id, StringComparison.OrdinalIgnoreCase)) return label;
        return id;
    }

    /// <summary>
    /// Serializes a <c>chat/completions</c> body with the correct per-model fields. Uses a plain
    /// dictionary (not an anonymous type) precisely so reasoning_effort / temperature can be
    /// conditionally present — the gateway 400s on the wrong combination. No token-limit field is
    /// ever sent (the model runs uncapped).
    /// </summary>
    public static string SerializeBody(string model, string system, string user, double temperature, bool stream)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
            ["stream"] = stream,
        };

        // NO max_tokens / max_completion_tokens on either family — the gateway runs the model
        // uncapped and its own docs omit the field.
        if (IsGpt5(model))
        {
            // The 5.5/5.6 fast tier uses "none"; the older 5.0 family uses "minimal". Both our GPT
            // models are 5.6 → "none". Sending "minimal" to a 5.6 model 400s (and vice-versa).
            body["reasoning_effort"] = model.StartsWith("gpt-5.0", StringComparison.OrdinalIgnoreCase)
                ? "minimal" : "none";
            // temperature is deliberately OMITTED — any non-default value 400s on GPT-5.x.
        }
        else
        {
            body["temperature"] = temperature;   // Claude: temperature allowed, no reasoning_effort.
        }

        return JsonSerializer.Serialize(body);
    }

    /// <summary>
    /// Pulls the reply text out of a non-streaming response: <c>choices[0].message.content</c>,
    /// which is normally a string but is occasionally an array of <c>{text}</c> parts (join them).
    /// </summary>
    public static string ExtractContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out JsonElement choices)
            || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return "";
        if (!choices[0].TryGetProperty("message", out JsonElement message)
            || !message.TryGetProperty("content", out JsonElement content))
            return "";

        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (JsonElement part in content.EnumerateArray())
                if (part.TryGetProperty("text", out JsonElement t)) sb.Append(t.GetString());
            return sb.ToString();
        }
        return "";
    }
}
