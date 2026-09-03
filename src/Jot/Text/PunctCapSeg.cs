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

        IReadOnlyList<int> ids = _tokenizer!.EncodeToIds(text);
        if (ids.Count == 0) return [];

        var pieces = new List<string>(ids.Count);
        var pre = new List<string?>(ids.Count);
        var post = new List<string?>(ids.Count);
        var caps = new List<bool[]>(ids.Count);
        var sbd = new List<bool>(ids.Count);

        foreach (Window w in Windows(ids.Count))
        {
            Predict(ids, w, pieces, pre, post, caps, sbd);
        }
        return Reconstruct(pieces, pre, post, caps, sbd);
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

    private void Predict(IReadOnlyList<int> ids, Window w, List<string> pieces, List<string?> pre,
                         List<string?> post, List<bool[]> caps, List<bool> sbd)
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
            pieces.Add(_vocab!.Piece(ids[i]));
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
                                            List<bool[]> caps, List<bool> sbd)
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
                if (c < CapSlots && caps[t][c]) ch = char.ToUpperInvariant(ch);
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
