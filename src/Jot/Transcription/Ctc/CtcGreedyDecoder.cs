using System;
using System.Collections.Generic;

namespace Jot.Transcription.Ctc;

/// <summary>One emitted CTC token and the encoder frame it was emitted on.</summary>
internal readonly record struct CtcToken(int Id, int Frame);

/// <summary>
/// Plain greedy CTC collapse, matching sherpa-onnx's offline greedy decoder exactly:
/// argmax per frame (FIRST index wins a tie, like std::max_element), emit when the id is neither
/// blank nor a repeat of the previous frame's argmax — and note that <c>prev</c> is updated on
/// BLANK frames too, which is what lets a genuine doubled token ("bookkeeper") survive.
/// </summary>
internal static class CtcGreedyDecoder
{
    /// <param name="logProbs">Flat [frames, vocab] row-major.</param>
    public static List<CtcToken> Decode(ReadOnlySpan<float> logProbs, int frames, int vocab, int blankId)
    {
        var outp = new List<CtcToken>();
        int prev = -1;
        for (int t = 0; t < frames; t++)
        {
            ReadOnlySpan<float> row = logProbs.Slice(t * vocab, vocab);
            int best = 0;
            float bestVal = row[0];
            for (int i = 1; i < vocab; i++) if (row[i] > bestVal) { bestVal = row[i]; best = i; }

            if (best != blankId && best != prev) outp.Add(new CtcToken(best, t));
            prev = best;
        }
        return outp;
    }
}
