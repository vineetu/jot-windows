namespace Jot.Cli;

/// <summary>One parse. <see cref="Error"/> non-null means a usage error (exit 2) and every other field
/// is meaningless — carried rather than thrown so the rules are testable without a process exit.</summary>
internal sealed class ParsedArgs
{
    public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);
    public List<string> Positionals { get; } = [];
    public string? Error { get; set; }
}

/// <summary>
/// The Mac CLI's parser, rule for rule — one left-to-right pass. A scan-and-remove parser let a missing
/// option value swallow the next token (<c>--language --raw a.wav b.wav</c> transcribed the wrong file
/// and exited 0), so: an option's value must exist and must not begin with "-", an unknown "-" token is a
/// usage error, and <c>--</c> ends option parsing.
/// </summary>
internal static class CliArgParser
{
    /// <summary>Tokens before any <c>--</c>. Mode/help/version detection must look at THIS and not the
    /// whole list, or <c>jot transcribe -- -h</c> prints usage instead of transcribing the file "-h".</summary>
    public static IReadOnlyList<string> PreEndOfOptions(IReadOnlyList<string> tokens)
    {
        var head = new List<string>(tokens.Count);
        foreach (string t in tokens)
        {
            if (t == "--") break;
            head.Add(t);
        }
        return head;
    }

    public static ParsedArgs Parse(
        IReadOnlyList<string> tokens,
        IReadOnlySet<string> flagNames,
        IReadOnlyDictionary<string, string> optionAliases)
    {
        var parsed = new ParsedArgs();
        bool positionalOnly = false;
        for (int i = 0; i < tokens.Count; i++)
        {
            string tok = tokens[i];
            if (positionalOnly)
            {
                parsed.Positionals.Add(tok);
            }
            else if (tok == "--")
            {
                positionalOnly = true;
            }
            else if (flagNames.Contains(tok))
            {
                parsed.Flags.Add(tok);
            }
            else if (optionAliases.TryGetValue(tok, out string? canonical))
            {
                if (i + 1 >= tokens.Count || tokens[i + 1].StartsWith('-'))
                {
                    parsed.Error = $"missing value for {tok}";
                    return parsed;
                }
                parsed.Options[canonical] = tokens[i + 1];
                i++;
            }
            else if (tok.StartsWith('-'))
            {
                parsed.Error = $"unknown option '{tok}'";
                return parsed;
            }
            else
            {
                parsed.Positionals.Add(tok);
            }
        }
        return parsed;
    }
}
