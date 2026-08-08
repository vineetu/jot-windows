using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jot.Cli;

/// <summary>
/// One JSON object per line on stdout, flushed per line — the machine streaming contract. The field set
/// is additive-only: a consumer parses <c>{"type":"final","text":"…"}</c> and must tolerate new fields,
/// never removed ones.
/// </summary>
internal static class Ndjson
{
    private sealed record Line(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string Text);

    // Relaxed escaping so non-ASCII stays raw UTF-8. The default encoder \u-escapes every non-ASCII
    // character, which is valid JSON but not the same bytes the Mac CLI emits for CJK/accented text.
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static TextWriter? _out;

    /// <summary>Replaces Console.Out: its default writer emits CRLF and the console codepage, and the
    /// protocol is LF-terminated UTF-8 with no BOM.</summary>
    public static void Install()
    {
        var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = false,
            NewLine = "\n",
        };
        Console.SetOut(writer);
        _out = writer;
    }

    public static string Serialize(string text) =>
        JsonSerializer.Serialize(new Line("final", text), Options);

    public static void EmitFinal(string text)
    {
        TextWriter w = _out ?? Console.Out;
        w.Write(Serialize(text));
        // Written as a literal, not WriteLine: an un-Installed writer would terminate with CRLF.
        w.Write('\n');
        w.Flush();
    }
}
