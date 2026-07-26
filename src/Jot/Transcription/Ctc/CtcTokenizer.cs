using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.ML.Tokenizers;

namespace Jot.Transcription.Ctc;

/// <summary>
/// Term -> CTC token ids, the direction the word spotter needs and the one nothing in this repo
/// could do before (we only had detokenization).
///
/// Backed by the checkpoint's own SentencePiece BPE model (<c>tokenizer.model</c>, 1024 pieces,
/// extracted from parakeet-tdt_ctc-110m.nemo). VERIFIED id-for-id against Python `sentencepiece`
/// on the same file — see CtcSpikeTests.C2_*. That verification is the point: a hand-rolled
/// longest-match encoder that disagrees by one merge round-trips perfectly and still gives the DP
/// a sequence the model never emits, i.e. silent zero recall with no exception and no log line.
///
/// IMPORTANT ARTIFACT NOTE: the sherpa-onnx release archive does NOT contain this file. It ships
/// only tokens.txt (pieces + ids, no merge scores), which is enough to DEcode and not enough to
/// encode. The .nemo checkpoint is where tokenizer.model comes from, so our own model release has
/// to carry it alongside the ONNX.
/// </summary>
internal sealed class CtcTokenizer
{
    private readonly SentencePieceTokenizer _sp;

    private CtcTokenizer(SentencePieceTokenizer sp) => _sp = sp;

    public static CtcTokenizer Load(string sentencePieceModelPath)
    {
        using FileStream fs = File.OpenRead(sentencePieceModelPath);
        // No BOS/EOS: the CTC head's vocabulary has neither, and emitting them would give the DP
        // ids that can never match a frame.
        return new CtcTokenizer(SentencePieceTokenizer.Create(fs, addBeginningOfSentence: false,
                                                                  addEndOfSentence: false));
    }

    /// <summary>Ids for a vocabulary term, in the CTC head's id space.</summary>
    public IReadOnlyList<int> Encode(string term) => _sp.EncodeToIds(term);

    /// <summary>
    /// True when every id the term encodes to is inside the CTC head's output range. An id outside
    /// it (or the unk id) is a term the spotter can never find, and the UI should say so rather
    /// than silently never matching.
    /// </summary>
    public bool IsSpottable(string term, CtcTokens tokens)
    {
        IReadOnlyList<int> ids = Encode(term);
        if (ids.Count == 0) return false;
        foreach (int id in ids)
            if (id < 0 || id >= tokens.Count || id == tokens.BlankId || tokens.Piece(id) == "<unk>")
                return false;
        return true;
    }
}
