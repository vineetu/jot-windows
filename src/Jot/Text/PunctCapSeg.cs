using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Jot.Transcription.Onnx;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Jot.Text;

/// <summary>
/// Restores punctuation, capitalization and sentence boundaries on lowercase unpunctuated text —
/// the second half of Jot's English path, since Granite Speech 5.0's CTC head emits neither.
///
/// A port of <c>punctuators.PunctCapSegModelONNX</c> + <c>PunctCapSegResultCollector</c>. The model
/// predicts FOUR streams per SentencePiece token: punctuation before it, punctuation after it, a
/// per-character capitalization mask, and whether a sentence ends there. Reconstruction walks the
/// pieces character by character because casing is per-character, not per-token — that is what lets
/// it produce "GPU" and "p.m." rather than "Gpu" and "Pm".
///
/// Known weaknesses, measured rather than assumed: it over-capitalizes some common nouns ("brown
/// Fox") and splits long digit runs oddly ("555. 0134"). Both are the upstream model's behaviour,
/// reproduced faithfully here; fixing them means a different model, not a different port.
/// </summary>
public sealed class PunctCapSeg : IDisposable
{
    /// <summary>The graph's positional limit. Longer input is windowed.</summary>
    private const int MaxLength = 256;

    /// <summary>Tokens of context shared between adjacent windows (punctuators' infer default).</summary>
    private const int Overlap = 16;

    private const int BosId = 1;
    private const int EosId = 2;

    /// <summary>SentencePiece's unknown id. Anything the lowercase-English vocabulary cannot spell
    /// lands here — see <see cref="Segment"/> for why that must not reach the user as text.</summary>
    private const int UnkId = 0;

    /// <summary>Widest per-character capitalization mask the graph emits.</summary>
    private const int CapSlots = 16;

    private const string Null = "<NULL>";
    private const string Acronym = "<ACRONYM>";

    /// <summary>
    /// The English checkpoint's label sets, from its config.yaml. Hardcoded rather than parsed
    /// because the graph and these lists are one artifact — a label list that disagreed with the
    /// graph would silently emit the wrong punctuation mark, not fail.
    /// </summary>
    private static readonly string[] PreLabels = [Null, "¿"];
    private static readonly string[] PostLabels = [Null, Acronym, ".", ",", "?"];

    private readonly PunctCapSegModel _model;
    private readonly OnnxSessionFactory _sessions;
    private readonly object _loadGate = new();

    private InferenceSession? _session;
    private SentencePieceTokenizer? _tokenizer;
    private SentencePieceVocab? _vocab;

    public PunctCapSeg(PunctCapSegModel model, OnnxSessionFactory sessions)
    {
        _model = model;
        _sessions = sessions;
    }

    public bool IsModelInstalled => _model.IsInstalled;

    /// <summary>
    /// Punctuated, cased text with sentences joined by single spaces. Whitespace-only input comes
    /// back unchanged, and so does anything the tokenizer reduces to no tokens.
    /// </summary>
    public string Apply(string text)
    {
        IReadOnlyList<string> sentences = Segment(text);
        return sentences.Count == 0 ? text : string.Join(" ", sentences);
    }

    /// <summary>The restored text split into sentences, in order.</summary>
    public IReadOnlyList<string> Segment(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        EnsureLoaded();

        string prepared = StripForModel(text);
        if (prepared.Length == 0) return [];

        IReadOnlyList<EncodedToken> tokens = _tokenizer!.EncodeToTokens(
            prepared, out string? normalized,
            addBeginningOfSentence: false, addEndOfSentence: false);
        if (tokens.Count == 0) return [];

        string source = normalized ?? prepared;
        var ids = new int[tokens.Count];
        var surfaces = new string[tokens.Count];
        var unknown = new bool[tokens.Count];
        for (int i = 0; i < tokens.Count; i++)
        {
            ids[i] = tokens[i].Id;
            unknown[i] = tokens[i].Id == UnkId;
            // An unknown token keeps the ORIGINAL characters. Reconstruct emits the vocabulary's
            // piece for every id, so without this an id the vocabulary cannot spell prints as the
            // literal text "<unk>" — measured on real dictation: "a spike of 75%" was delivered to
            // the user as "a spike of 75<unk>". The vocabulary has no '%', and no ' ", curly quote,
            // en dash, ellipsis, degree sign, '@', '#', '=', '/', '~' or emoji either. Punctuating
            // text must never be able to DELETE any of it.
            surfaces[i] = unknown[i] ? Slice(source, tokens[i].Offset) : _vocab!.Piece(tokens[i].Id);
        }

        var pieces = new List<string>(tokens.Count);
        var pre = new List<string?>(tokens.Count);
        var post = new List<string?>(tokens.Count);
        var caps = new List<bool[]>(tokens.Count);
        var sbd = new List<bool>(tokens.Count);
        var raw = new List<bool>(tokens.Count);

        foreach (Window w in Windows(tokens.Count))
        {
            Predict(ids, surfaces, unknown, w, pieces, pre, post, caps, sbd, raw);
        }
        return Reconstruct(pieces, pre, post, caps, sbd, raw);
    }

    /// <summary>
    /// Lowercases and removes sentence punctuation, so the model sees the shape it was trained on.
    ///
    /// LOWERCASE because the vocabulary is lowercase-only (spe_32k_lc_en) — every uppercase
    /// character otherwise encodes to &lt;unk&gt; and "Zürich" comes back mangled. Casing is what
    /// this model PREDICTS; supplying it is what breaks it.
    ///
    /// STRIPPED because the model adds its own marks on top of any it is given. Jot for iOS
    /// measured the result of not doing this at 6.8 → 27.1 marks per 100 words ("there??",
    /// "Yeah,,", "log. File.."). Granite emits punctuation on ~4% of clips — mostly ITN artifacts —
    /// so this is a live case, not a hypothetical one.
    ///
    /// Two things are deliberately kept:
    ///   * APOSTROPHES. The label set is <c>. , ?</c> plus <c>&lt;ACRONYM&gt;</c> with no
    ///     apostrophe, so a contraction removed here can never be rebuilt.
    ///   * DOTS AND COMMAS BETWEEN NON-SPACES — "3.1", "5,000", "1.0.3", "example.com". These are
    ///     inside a token, not ending a sentence; stripping them yields "3 1" and "example com",
    ///     which the model then re-punctuates as prose. iOS guards this with a digits-only rule and
    ///     measured 17/420 real transcripts corrupted without it; the wider "not whitespace either
    ///     side" test used here is a deliberate superset that also saves domains and version
    ///     strings, and still strips every sentence-final mark, which is followed by a space.
    /// </summary>
    internal static string StripForModel(string text)
    {
        string lower = text.ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        for (int i = 0; i < lower.Length; i++)
        {
            char ch = lower[i];
            if (ch is '.' or ',')
            {
                bool inside = i > 0 && !char.IsWhiteSpace(lower[i - 1])
                              && i + 1 < lower.Length && !char.IsWhiteSpace(lower[i + 1]);
                sb.Append(inside ? ch : ' ');
                continue;
            }
            sb.Append("?!;:\"()[]".Contains(ch) ? ' ' : ch);
        }
        return string.Join(' ', sb.ToString()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The token's own characters, taken back out of the text the tokenizer normalized.</summary>
    private static string Slice(string source, Range offset)
    {
        (int start, int length) = offset.GetOffsetAndLength(source.Length);
        return source.Substring(start, length);
    }

    /// <summary>A slice of the id stream plus the sub-slice of it that survives overlap trimming.</summary>
    private readonly record struct Window(int Start, int Stop, int KeepFrom, int KeepTo);

    /// <summary>
    /// Splits the ids into model-sized windows. Every window after the first starts
    /// <see cref="Overlap"/> tokens early so its opening tokens have left context, and then half
    /// that overlap is dropped from each seam — the same asymmetric trim punctuators uses, so a
    /// token is never predicted twice and never predicted with no context.
    /// </summary>
    private static IEnumerable<Window> Windows(int count)
    {
        int budget = MaxLength - 2;   // BOS + EOS
        int start = 0, index = 0;
        while (start < count)
        {
            int from = start - (index == 0 ? 0 : Overlap);
            int stop = Math.Min(from + budget, count);
            bool first = index == 0;
            bool last = stop >= count;
            yield return new Window(from, stop,
                                    from + (first ? 0 : Overlap / 2),
                                    stop - (last ? 0 : Overlap / 2));
            start = from + budget;
            index++;
        }
    }

    private void Predict(IReadOnlyList<int> ids, IReadOnlyList<string> surfaces,
                         IReadOnlyList<bool> unknown, Window w, List<string> pieces,
                         List<string?> pre, List<string?> post, List<bool[]> caps,
                         List<bool> sbd, List<bool> raw)
    {
        int span = w.Stop - w.Start;
        var input = new DenseTensor<long>([1, span + 2]);
        input[0, 0] = BosId;
        for (int i = 0; i < span; i++) input[0, i + 1] = ids[w.Start + i];
        input[0, span + 1] = EosId;

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            _session!.Run([NamedOnnxValue.CreateFromTensor("input_ids", input)]);

        Tensor<long>? preOut = null, postOut = null;
        Tensor<bool>? capOut = null, segOut = null;
        foreach (DisposableNamedOnnxValue o in outputs)
        {
            switch (o.Name)
            {
                case "pre_preds": preOut = o.AsTensor<long>(); break;
                case "post_preds": postOut = o.AsTensor<long>(); break;
                case "cap_preds": capOut = o.AsTensor<bool>(); break;
                case "seg_preds": segOut = o.AsTensor<bool>(); break;
            }
        }
        if (preOut is null || postOut is null || capOut is null || segOut is null)
            throw new InvalidDataException("punct_cap_seg_en produced an unexpected output set.");

        // Position 0 is BOS and the last is EOS; both carry predictions that belong to no token.
        for (int i = w.KeepFrom; i < w.KeepTo; i++)
        {
            int at = i - w.Start + 1;
            pieces.Add(surfaces[i]);
            raw.Add(unknown[i]);
            pre.Add(Label(PreLabels, preOut[0, at]));
            post.Add(Label(PostLabels, postOut[0, at]));

            var mask = new bool[CapSlots];
            for (int c = 0; c < CapSlots; c++) mask[c] = capOut[0, at, c];
            caps.Add(mask);
            sbd.Add(segOut[0, at]);
        }
    }

    private static string? Label(string[] labels, long index)
    {
        if (index < 0 || index >= labels.Length) return null;
        string label = labels[index];
        return label == Null ? null : label;
    }

    /// <summary>
    /// Walks pieces character by character, applying the four prediction streams. SentencePiece's
    /// U+2581 marks a word start, so it becomes a space and is not itself emitted — but it still
    /// occupies index 0 of the capitalization mask, which is why the character loop starts at the
    /// same offset the mask is indexed by.
    /// </summary>
    private static List<string> Reconstruct(List<string> pieces, List<string?> pre, List<string?> post,
                                            List<bool[]> caps, List<bool> sbd, List<bool> raw)
    {
        var sentences = new List<string>();
        var current = new StringBuilder();

        for (int t = 0; t < pieces.Count; t++)
        {
            string piece = pieces[t];
            if (piece.Length == 0) continue;

            bool wordStart = piece[0] == '▁';
            if (wordStart && current.Length > 0) current.Append(' ');
            int charStart = wordStart ? 1 : 0;

            for (int c = charStart; c < piece.Length; c++)
            {
                char ch = piece[c];
                if (c == charStart && pre[t] is { } preMark) current.Append(preMark);
                // Casing is NOT applied to a token the model could not read: its per-character mask
                // describes an <unk>, not these characters. Surrounding punctuation still is — the
                // neighbouring context was read normally.
                if (!raw[t] && c < CapSlots && caps[t][c]) ch = char.ToUpperInvariant(ch);
                current.Append(ch);

                bool lastChar = c == piece.Length - 1;
                if (post[t] == Acronym) current.Append('.');
                else if (lastChar && post[t] is { } postMark) current.Append(postMark);

                if (lastChar && sbd[t])
                {
                    sentences.Add(current.ToString());
                    current.Clear();
                }
            }
        }
        if (current.Length > 0) sentences.Add(current.ToString());
        return sentences;
    }

    private void EnsureLoaded()
    {
        if (_session is not null) return;
        lock (_loadGate)
        {
            if (_session is not null) return;
            if (!_model.IsInstalled)
                throw new InvalidOperationException(
                    $"Punctuation model is not installed at {_model.Directory}.");

            using FileStream fs = File.OpenRead(_model.Tokenizer);
            // BOS/EOS are added explicitly per window (the graph wants them INSIDE the id sequence
            // it scores), so the tokenizer must not add its own.
            _tokenizer = SentencePieceTokenizer.Create(fs, addBeginningOfSentence: false,
                                                           addEndOfSentence: false);
            // Encoding comes from the library; the id->piece table is read from the same proto
            // because the library exposes no public accessor for it. See SentencePieceVocab.
            _vocab = SentencePieceVocab.Load(_model.Tokenizer);
            _session = _sessions.Create(_model.Graph, ComputeBackend.Cpu);
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
