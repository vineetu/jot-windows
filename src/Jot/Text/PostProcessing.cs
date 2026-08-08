using System.Text;

namespace Jot.Text;

// Port of jot-shared `Sources/JotTextPipeline/PostProcessing.swift`. Behaviour is locked by the
// shared golden fixtures replayed in Jot.Tests (`Fixtures/text/post_processing.json`).

/// <summary>
/// The final whitespace / punctuation tidy over an otherwise-cleaned transcript. Four rules:
/// trim the ends, collapse interior whitespace runs (tabs and the stray single newlines the decoder
/// emits around punctuation) to one space, keep <c>\n\n</c> paragraph boundaries intact, and drop
/// the stray space the decoder sometimes writes before sentence punctuation (" ." → ".").
///
/// IT TRIMS TRAILING WHITESPACE, so it can never be the last stage of the cleanup chain: the
/// pipeline's output ends in one space on purpose (that space is what makes a pasted dictation join
/// the next one), and running this after the trailing space is added would silently delete it.
/// <see cref="TextPipeline.Clean"/> restores the space afterwards.
///
/// The rules are Latin-script-safe, not English-specific — but they are NOT script-agnostic, so the
/// CALLER decides who gets them. See <see cref="TextPipeline"/> for that gate.
/// </summary>
public static class PostProcessing
{
    public static string Apply(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        string working = text.Trim();

        // Paragraph boundaries arrive as "\n\n" from upstream segmentation. Clean INSIDE each one
        // so the whitespace collapse below cannot eat the boundary itself.
        string[] paragraphs = working.Split("\n\n");
        for (int i = 0; i < paragraphs.Length; i++) paragraphs[i] = CleanParagraph(paragraphs[i]);
        return string.Join("\n\n", paragraphs);
    }

    private static string CleanParagraph(string paragraph)
    {
        string working = CollapseInternalWhitespace(paragraph.Trim());

        // " ." / " ," — the engine emits these when a pause lands before the mark.
        foreach (string mark in Punctuation) working = working.Replace(" " + mark, mark);

        return working;
    }

    private static readonly string[] Punctuation = [",", ".", ";", ":", "!", "?"];

    private static string CollapseInternalWhitespace(string input)
    {
        var output = new StringBuilder(input.Length);
        bool lastWasWhitespace = false;
        foreach (char c in input)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasWhitespace) output.Append(' ');
                lastWasWhitespace = true;
            }
            else
            {
                output.Append(c);
                lastWasWhitespace = false;
            }
        }
        return output.ToString();
    }
}
