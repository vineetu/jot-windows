using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jot.Text;

/// <summary>
/// The id -> piece table read straight out of a SentencePiece <c>.model</c> file.
///
/// WHY THIS EXISTS. Reconstruction needs the raw piece string, U+2581 word-start marker and all,
/// because casing is applied per character and the marker occupies a character slot.
/// <c>Microsoft.ML.Tokenizers.SentencePieceTokenizer</c> exposes no public id->piece accessor
/// (<c>TryMapIdToToken</c> is not public on it), and its <c>Decode</c> deliberately consumes the
/// marker and re-spaces the text — exactly the information we need to keep.
///
/// So the split is: the LIBRARY still does encoding, because a hand-rolled Unigram/BPE encoder that
/// disagrees with the reference by one merge is a silent, untraceable quality loss (this repo has
/// been bitten by precisely that in the CTC spotter). This class only does the trivially verifiable
/// half — a flat table lookup — and its output is checked against a dumped reference piece list.
///
/// The file is a protobuf <c>ModelProto</c>; the only field read here is the repeated
/// <c>pieces</c> (field 1), and within each entry the <c>piece</c> string (field 1). Everything else
/// (scores, types, trainer spec) is skipped by wire type.
/// </summary>
internal sealed class SentencePieceVocab
{
    private readonly string[] _pieces;

    private SentencePieceVocab(string[] pieces) => _pieces = pieces;

    public int Count => _pieces.Length;

    /// <summary>The piece for an id, or empty when the id is outside the vocabulary.</summary>
    public string Piece(int id) => id >= 0 && id < _pieces.Length ? _pieces[id] : string.Empty;

    public static SentencePieceVocab Load(string modelPath)
    {
        byte[] proto = File.ReadAllBytes(modelPath);
        var pieces = new List<string>(32_000);
        int at = 0;

        while (at < proto.Length)
        {
            if (!TryReadVarint(proto, ref at, out ulong key)) break;
            int field = (int)(key >> 3);
            int wire = (int)(key & 0x7);

            if (field == 1 && wire == 2)   // repeated SentencePiece pieces = 1
            {
                if (!TryReadVarint(proto, ref at, out ulong len)) break;
                int end = at + (int)len;
                if (end > proto.Length) break;
                pieces.Add(ReadPiece(proto, at, end));
                at = end;
            }
            else if (!SkipField(proto, ref at, wire))
            {
                break;
            }
        }

        if (pieces.Count == 0)
            throw new InvalidDataException($"No SentencePiece pieces found in {modelPath}.");
        return new SentencePieceVocab(pieces.ToArray());
    }

    /// <summary>Reads the <c>piece</c> string (field 1) out of one SentencePiece sub-message.</summary>
    private static string ReadPiece(byte[] buf, int at, int end)
    {
        while (at < end)
        {
            if (!TryReadVarint(buf, ref at, out ulong key)) break;
            int field = (int)(key >> 3);
            int wire = (int)(key & 0x7);

            if (field == 1 && wire == 2)
            {
                if (!TryReadVarint(buf, ref at, out ulong len)) break;
                if (at + (int)len > end) break;
                return Encoding.UTF8.GetString(buf, at, (int)len);
            }
            if (!SkipField(buf, ref at, wire)) break;
        }
        return string.Empty;
    }

    private static bool SkipField(byte[] buf, ref int at, int wire)
    {
        switch (wire)
        {
            case 0: return TryReadVarint(buf, ref at, out _);
            case 1: at += 8; return at <= buf.Length;
            case 2:
                if (!TryReadVarint(buf, ref at, out ulong len)) return false;
                at += (int)len;
                return at <= buf.Length;
            case 5: at += 4; return at <= buf.Length;
            default: return false;   // groups (3/4) do not appear in this schema
        }
    }

    private static bool TryReadVarint(byte[] buf, ref int at, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (at < buf.Length && shift <= 63)
        {
            byte b = buf[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }
}
