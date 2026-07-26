using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jot.Transcription.Ctc;

/// <summary>
/// The CTC model's symbol table, read from the export's <c>tokens.txt</c> ("&lt;piece&gt; &lt;id&gt;"
/// per line, 1025 lines: 1024 BPE pieces + <c>&lt;blk&gt;</c>).
///
/// This is DEtokenization only — SentencePiece <c>U+2581</c> becomes a space, pieces concatenate.
/// The reverse direction (term -> ids, which the word spotter needs) is a separate and much harder
/// problem: this export ships NO SentencePiece model file, only the piece list. See CtcTokenizer notes.
/// </summary>
internal sealed class CtcTokens
{
    public const char SpacePiece = '▁';

    private readonly string[] _pieces;

    public int Count => _pieces.Length;

    /// <summary>Blank id. Not assumed to be 0 or last — read from the file, as sherpa-onnx does.</summary>
    public int BlankId { get; }

    private CtcTokens(string[] pieces, int blankId)
    {
        _pieces = pieces;
        BlankId = blankId;
    }

    public static CtcTokens Load(string tokensPath)
    {
        var map = new Dictionary<int, string>();
        int blank = -1;
        foreach (string raw in File.ReadLines(tokensPath, Encoding.UTF8))
        {
            if (raw.Length == 0) continue;
            int sp = raw.LastIndexOf(' ');
            if (sp <= 0 || !int.TryParse(raw[(sp + 1)..], out int id)) continue;
            string piece = raw[..sp];
            map[id] = piece;
            if (piece is "<blk>" or "<eps>" or "<blank>") blank = id;
        }
        if (map.Count == 0) throw new InvalidDataException($"no tokens parsed from {tokensPath}");
        if (blank < 0) throw new InvalidDataException($"no <blk>/<eps>/<blank> symbol in {tokensPath}");

        int max = 0;
        foreach (int k in map.Keys) max = Math.Max(max, k);
        var pieces = new string[max + 1];
        for (int i = 0; i <= max; i++) pieces[i] = map.TryGetValue(i, out string? p) ? p : string.Empty;
        return new CtcTokens(pieces, blank);
    }

    public string Piece(int id) => (uint)id < (uint)_pieces.Length ? _pieces[id] : string.Empty;

    public int IdOf(string piece)
    {
        for (int i = 0; i < _pieces.Length; i++) if (_pieces[i] == piece) return i;
        return -1;
    }

    /// <summary>
    /// Concatenate pieces and strip the ONE leading space SentencePiece puts on the first word —
    /// byte-for-byte what sherpa-onnx's Convert() does, so transcripts are directly comparable.
    /// </summary>
    public string Decode(IReadOnlyList<int> ids)
    {
        var sb = new StringBuilder();
        foreach (int id in ids) sb.Append(Piece(id).Replace(SpacePiece, ' '));
        if (sb.Length > 0 && sb[0] == ' ') sb.Remove(0, 1);
        return sb.ToString();
    }
}
