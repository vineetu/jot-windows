namespace Jot.Vocabulary;

// Port of jot-shared `Sources/JotVocabCore/VocabTerm.swift` + `VocabularyFile.swift`
// (pinned commit 5326460). The Swift `id`/`text`/`aliases` shape is kept verbatim so the
// plain-text "simple format" round-trips between platforms.

/// <summary>
/// One user-curated term: the spelling Jot should prefer, plus optional "sounds like" aliases.
///
/// Aliases are not decoration — with no per-word confidence on the Windows engine, string
/// similarity against the term *or an alias* is the gate's only remaining plausibility lever
/// (<c>VocabularyGate.Decide</c> step (1)). An alias is the user telling us a pair is plausible.
/// </summary>
public sealed class VocabularyTerm
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public List<string> Aliases { get; set; } = [];

    /// <summary>Empty/whitespace-only rows are skipped on persist rather than written out.</summary>
    public bool IsBlank => string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// The vocabulary "simple format" parser/serializer — one term per line, optional aliases after a
/// colon (<c>UJET: you jet, ew jet</c>). <c>#</c> starts a comment.
///
/// This is NOT the persistence format (that is <c>vocabulary.json</c>, see
/// <see cref="VocabularyStore"/>); it is the cross-platform interchange shape, ported byte-for-byte
/// so a list authored on Mac/iOS pastes in here unchanged.
/// </summary>
public static class VocabularyFile
{
    public static IReadOnlyList<VocabularyTerm> Parse(string body)
    {
        var result = new List<VocabularyTerm>();
        foreach (string rawLine in body.Split('\n', '\r'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            // maxSplits 1: a colon inside the alias list belongs to the aliases, not to a new term.
            int colon = line.IndexOf(':');
            string text = (colon < 0 ? line : line[..colon]).Trim();
            if (text.Length == 0) continue;

            List<string> aliases = colon < 0
                ? []
                : line[(colon + 1)..].Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();

            result.Add(new VocabularyTerm { Text = text, Aliases = aliases });
        }
        return result;
    }

    public static string Serialize(IReadOnlyList<VocabularyTerm> terms)
    {
        var lines = new List<string>();
        foreach (VocabularyTerm t in terms)
        {
            if (t.IsBlank) continue;
            string text = t.Text.Trim();
            lines.Add(t.Aliases.Count == 0 ? text : $"{text}: {string.Join(", ", t.Aliases)}");
        }
        return lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
    }
}
