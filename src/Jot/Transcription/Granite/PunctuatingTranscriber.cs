using System;
using System.Threading;
using System.Threading.Tasks;
using Jot.Services;
using Jot.Text;

namespace Jot.Transcription.Granite;

/// <summary>
/// Adds punctuation, casing and sentence breaks to an engine that emits none. Wraps the Granite
/// English engine only — the ggml multilingual engine punctuates on its own and must not be run
/// through this.
///
/// It is a decorator rather than a step inside <see cref="GraniteTranscriber"/> so that the raw
/// model output stays independently testable, and a decorator rather than a stage in the caller's
/// pipeline so that EVERY entry point gets it: app dictation, streaming finalize, file import and
/// both CLI modes go through <c>ITranscriber</c>, and a caller that forgot to punctuate would ship
/// an unbroken lowercase run of words.
///
/// STREAMING PARTIALS ARE LEFT RAW ON PURPOSE. Punctuating them would cost a full second model pass
/// per caption tick for text the user watches for a moment and never keeps, and the sentence
/// segmentation of a half-finished utterance is wrong anyway — the mark that belongs at the end of a
/// clause cannot be known until the clause exists. <see cref="IStreamingSession.Finish"/> punctuates
/// once, so what actually gets pasted is always the punctuated form.
/// </summary>
public sealed class PunctuatingTranscriber : ITranscriber, IStreamingTranscriber, IDisposable
{
    private readonly ITranscriber _inner;
    private readonly PunctCapSeg _punctuation;

    public PunctuatingTranscriber(ITranscriber inner, PunctCapSeg punctuation)
    {
        _inner = inner;
        _punctuation = punctuation;
    }

    /// <summary>Both models must be present: punctuation is not optional polish for this engine,
    /// it is the difference between a sentence and a lowercase run-on.</summary>
    public bool IsModelInstalled => _inner.IsModelInstalled && _punctuation.IsModelInstalled;

    public async Task<string> TranscribeAsync(float[] samples, int sampleRate,
                                              CancellationToken ct = default)
        => Restore(await _inner.TranscribeAsync(samples, sampleRate, ct).ConfigureAwait(false));

    public void WarmUp()
    {
        _inner.WarmUp();
        // Warm the punctuation graph too. Skipping it moves a ~400 ms model load onto the end of
        // the user's first dictation, i.e. straight into the gap before the paste.
        if (_punctuation.IsModelInstalled) _punctuation.Apply("warm up the punctuation model");
    }

    public IStreamingSession OpenStream()
    {
        if (_inner is not IStreamingTranscriber streaming)
            throw new InvalidOperationException(
                $"{_inner.GetType().Name} does not support streaming.");
        return new Session(streaming.OpenStream(), this);
    }

    /// <summary>
    /// Punctuation is best-effort: if the stage throws, the user still gets their words. A crash
    /// here would lose a dictation that was already successfully transcribed.
    /// </summary>
    private string Restore(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        try
        {
            return _punctuation.Apply(text);
        }
        catch (Exception ex)
        {
            JotLog.Info($"punctuation failed, returning raw transcript: {ex.Message}");
            return text;
        }
    }

    private sealed class Session : IStreamingSession
    {
        private readonly IStreamingSession _inner;
        private readonly PunctuatingTranscriber _owner;

        internal Session(IStreamingSession inner, PunctuatingTranscriber owner)
        {
            _inner = inner;
            _owner = owner;
        }

        /// <summary>
        /// True regardless of what the inner engine reports: punctuation lands in one pass at
        /// Finish, so the final text re-cases and re-punctuates words the partials already showed.
        /// Even wrapping a strictly append-only engine, THIS session revises.
        /// </summary>
        public bool RevisesText => true;

        /// <summary>Raw — see the class remarks.</summary>
        public string Accept(float[] newSamples) => _inner.Accept(newSamples);

        public string Finish() => _owner.Restore(_inner.Finish());
    }

    public void Dispose()
    {
        (_inner as IDisposable)?.Dispose();
        _punctuation.Dispose();
    }
}
