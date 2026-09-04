using System;
using System.Threading;
using System.Threading.Tasks;
using Jot.Services;
using Jot.Text;

namespace Jot.Transcription.Granite;

/// <summary>
/// Restores punctuation, casing and sentence breaks on ENGLISH, with punct_cap_seg_en.
///
/// It wraps two very different engines and the difference is what <c>enabled</c> encodes:
///   * GRANITE, which emits no marks at all. Not a preference — without this the user gets a
///     lowercase run-on, so it is always on and both models are required.
///   * GGML/NEMOTRON, which punctuates perfectly well on its own. Here this is a QUALITY choice
///     rather than a necessity: Jot for iOS measured the same model beating Parakeet's native
///     punctuation 31–16 with three blind judges on 420 real recordings, so it is offered as a
///     setting, defaults on, and degrades to the engine's own marks when off or when the model is
///     not downloaded.
///
/// Never wrap a NON-English route in this: the model is English-only and would re-punctuate German
/// as though it were English.
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
    private readonly Func<bool>? _enabled;
    private readonly bool _ownsInner;

    /// <param name="enabled">Read per utterance, not captured once, because Settings changes the
    /// toggle without rebuilding the DI graph. Null means always on — the Granite path, where this
    /// is not a preference: that engine emits no marks at all, so switching it off would hand the
    /// user a lowercase run-on rather than a plainer sentence.</param>
    /// <param name="ownsInner">False when the inner engine is SHARED with the language router — the
    /// ggml engine is both the multilingual route and the thing being punctuated for English, and
    /// disposing it here would tear down the engine the other route is still using.</param>
    public PunctuatingTranscriber(ITranscriber inner, PunctCapSeg punctuation,
                                  Func<bool>? enabled = null, bool ownsInner = true)
    {
        _inner = inner;
        _punctuation = punctuation;
        _enabled = enabled;
        _ownsInner = ownsInner;
    }

    /// <summary>
    /// Whether restoration actually runs right now. Off ⇒ this decorator is a pass-through and the
    /// inner engine's own punctuation is what ships. Toggle FIRST: when the user has switched
    /// restoration off there is no reason to ask the punctuation stage anything, including whether
    /// its model is on disk.
    /// </summary>
    private bool Active => (_enabled?.Invoke() ?? true) && _punctuation.IsModelInstalled;

    /// <summary>
    /// Reports the INNER engine when restoration is only a polish pass over an engine that already
    /// punctuates, and requires both models when it is not optional. Getting this wrong either way
    /// is user-visible: too strict and a CLI refuses a language it can transcribe, too loose and
    /// the router picks an engine whose model is missing.
    /// </summary>
    public bool IsModelInstalled =>
        _enabled is null ? _inner.IsModelInstalled && _punctuation.IsModelInstalled
                         : _inner.IsModelInstalled;

    public async Task<string> TranscribeAsync(float[] samples, int sampleRate,
                                              CancellationToken ct = default)
        => Restore(await _inner.TranscribeAsync(samples, sampleRate, ct).ConfigureAwait(false));

    public void WarmUp()
    {
        _inner.WarmUp();
        // Warm the punctuation graph too. Skipping it moves a ~400 ms model load onto the end of
        // the user's first dictation, i.e. straight into the gap before the paste.
        if (Active) _punctuation.Apply("warm up the punctuation model");
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
        if (string.IsNullOrWhiteSpace(text) || !Active) return text;
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
        /// True whenever restoration will actually run: punctuation lands in one pass at Finish, so
        /// the final text re-cases and re-punctuates words the partials already showed. Even
        /// wrapping a strictly append-only engine, THIS session revises.
        ///
        /// It falls back to the inner engine's own answer when the toggle is off, because then this
        /// decorator is a pass-through — claiming revision anyway would cost ggml the append-only
        /// guarantee the CLI's finals-only protocol relies on, for no reason.
        /// </summary>
        public bool RevisesText => _owner.Active || _inner.RevisesText;

        /// <summary>Raw — see the class remarks.</summary>
        public string Accept(float[] newSamples) => _inner.Accept(newSamples);

        public string Finish() => _owner.Restore(_inner.Finish());
    }

    public void Dispose()
    {
        if (_ownsInner) (_inner as IDisposable)?.Dispose();
        _punctuation.Dispose();
    }
}
