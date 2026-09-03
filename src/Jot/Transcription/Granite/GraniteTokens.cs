using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Jot.Transcription.Granite;

/// <summary>
/// Detokenizer for Granite Speech 5.0's 16,384-entry CTC head.
///
/// The head is BYTE-LEVEL BPE (GPT-2 style), not SentencePiece — which is why this does not reuse
/// <see cref="Ctc.CtcTokens"/> or <c>Microsoft.ML.Tokenizers</c>' SentencePiece support. Pieces are
/// strings over a 256-character alphabet that stands in for the 256 byte values, so decoding is:
/// concatenate the pieces, map each character back to its byte, then interpret the whole byte run as
/// UTF-8. Doing the UTF-8 decode at the END is load-bearing: a multi-byte character (é, —, an emoji)
/// is routinely split across two BPE pieces, and decoding piece-by-piece turns it into replacement
/// characters.
///
/// Only the decode direction exists here. Encoding is what the vocabulary spotter needs, and the
/// spotter runs its own Parakeet CTC model with its own SentencePiece vocabulary.
/// </summary>
internal sealed class GraniteTokens
{
    /// <summary>CTC blank. Granite uses id 0 (the <c>&lt;|blank|&gt;</c> token, also pad_token_id).</summary>
    public const int BlankId = 0;

    private static readonly Dictionary<char, byte> ByteDecoder = BuildByteDecoder();

    private readonly string[] _pieces;

    private GraniteTokens(string[] pieces) => _pieces = pieces;

    public int Count => _pieces.Length;

    /// <summary>Loads the flat id-ordered piece list dumped from the checkpoint's tokenizer.json.</summary>
    public static GraniteTokens Load(string vocabJsonPath)
    {
        using FileStream fs = File.OpenRead(vocabJsonPath);
        string[]? pieces = JsonSerializer.Deserialize<string[]>(fs);
        if (pieces is null || pieces.Length == 0)
            throw new InvalidDataException($"Granite vocab at {vocabJsonPath} is empty or unreadable.");
        return new GraniteTokens(pieces);
    }

    /// <summary>Test seam: build directly from an id-ordered piece list.</summary>
    public static GraniteTokens FromPieces(string[] pieces) => new(pieces);

    /// <summary>
    /// Text for a CTC-collapsed id sequence. Ids outside the vocabulary and the blank are skipped
    /// rather than throwing: a decode is the last step of a dictation the user already spoke, and
    /// losing one token beats losing the utterance.
    /// </summary>
    public string Decode(IReadOnlyList<int> ids)
    {
        var bytes = new List<byte>(ids.Count * 4);
        foreach (int id in ids)
        {
            if (id == BlankId || id < 0 || id >= _pieces.Length) continue;
            string piece = _pieces[id];
            if (IsSpecial(piece)) continue;
            foreach (char c in piece)
            {
                if (ByteDecoder.TryGetValue(c, out byte b)) bytes.Add(b);
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>
    /// Added tokens are written in angle-pipe form (<c>&lt;|blank|&gt;</c>) and every character in
    /// that form is itself a valid byte-level character, so without this check they would decode to
    /// their own literal spelling and land in the user's transcript.
    /// </summary>
    private static bool IsSpecial(string piece) =>
        piece.Length > 2 && piece[0] == '<' && piece[1] == '|' && piece.EndsWith("|>", StringComparison.Ordinal);

    /// <summary>
    /// The GPT-2 byte alphabet, inverted. Printable ASCII and two Latin-1 runs stand for themselves;
    /// every remaining byte is mapped to U+0100 upward, in byte order. Notably this is what makes a
    /// space appear as U+0120 ('Ġ') inside pieces.
    /// </summary>
    private static Dictionary<char, byte> BuildByteDecoder()
    {
        var used = new List<int>();
        for (int b = '!'; b <= '~'; b++) used.Add(b);
        for (int b = 0xA1; b <= 0xAC; b++) used.Add(b);
        for (int b = 0xAE; b <= 0xFF; b++) used.Add(b);

        var map = new Dictionary<char, byte>(256);
        foreach (int b in used) map[(char)b] = (byte)b;

        int next = 0;
        for (int b = 0; b < 256; b++)
        {
            if (used.Contains(b)) continue;
            map[(char)(256 + next)] = (byte)b;
            next++;
        }
        return map;
    }
}
