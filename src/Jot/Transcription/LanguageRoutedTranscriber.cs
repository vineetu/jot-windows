using System;
using System.Threading;
using System.Threading.Tasks;
using Jot.Text;

namespace Jot.Transcription;

/// <summary>
/// Sends English to one engine and everything else to another.
///
/// Granite Speech 5.0 is English-only, so it cannot simply replace the multilingual ggml engine —
/// but the language is not known when the transcriber is constructed, and the user can change it in
/// Settings at any time without the app rebuilding its DI graph. Selecting an engine once at
/// construction would therefore pin whatever language happened to be set at startup, and a user who
/// switched to German would keep dictating into an English-only model.
///
/// So the choice is made per utterance instead, from the language currently applied. Streaming
/// sessions resolve the engine when the stream OPENS and hold it for the utterance, matching the
/// existing rule that a session snapshots its language rather than reacting mid-recording.
///
/// "auto" routes to ggml. Granite cannot detect a language, and auto-detect exists precisely for
/// users who do not want to declare one — sending them to an English-only model would transliterate
/// whatever they said into English words.
/// </summary>
public sealed class LanguageRoutedTranscriber : ITranscriber, IStreamingTranscriber, IDisposable
{
    private const string EnglishIso = "en";

    private readonly ITranscriber _english;
    private readonly ITranscriber _other;
    private readonly Action<string>? _log;
    private readonly Func<string> _englishLabel;

    private string _language = "";
    private string? _lastRouteLogged;

    /// <param name="englishLabel">What the English route IS, for the log line. Passed in because the
    /// router cannot tell: the English engine is Granite on one install and punctuation-restored ggml
    /// on another, and a hardcoded "granite" would name the wrong engine on the second — the one
    /// place a reader looks to find out what actually ran. Evaluated at LOG time, not construction,
    /// because the punctuation toggle can change what runs without rebuilding anything; the label
    /// changing is itself a route change worth a line.</param>
    public LanguageRoutedTranscriber(ITranscriber english, ITranscriber other,
                                     Action<string>? log = null,
                                     Func<string>? englishLabel = null)
    {
        _english = english;
        _other = other;
        _log = log;
        _englishLabel = englishLabel ?? (() => "granite (English)");
    }

    /// <summary>The multilingual engine behind the router. Exposed because callers that need the
    /// CONCRETE engine (not the ITranscriber contract) would otherwise silently no-op once routing
    /// wrapped it — that is exactly how the leftover-ONNX cleanup stopped running.</summary>
    public ITranscriber Multilingual => _other;

    /// <summary>The stored language value — a locale code, a legacy display name, or "auto".</summary>
    public void SetLanguage(string language)
    {
        _language = language ?? "";
        // The multilingual engine needs the value regardless of which engine ends up running, so a
        // later switch back to German does not find it still set to whatever it had at startup.
        TranscriberFactory.ApplyLanguage(_other, _language);
    }

    /// <summary>True when the engine that WOULD run is installed. Reporting the English engine's
    /// state while routing to ggml (or the reverse) is how a CLI ends up refusing to run a language
    /// it can actually handle.</summary>
    public bool IsModelInstalled => Selected().IsModelInstalled;

    /// <summary>Which engine handles the current language. English falls back to the multilingual
    /// engine when the Granite assets are absent, so a partial install degrades instead of failing.</summary>
    internal ITranscriber Selected()
    {
        bool english = LanguageCode.ToIso(_language) == EnglishIso;
        return english && _english.IsModelInstalled ? _english : _other;
    }

    public Task<string> TranscribeAsync(float[] samples, int sampleRate,
                                        CancellationToken ct = default)
    {
        ITranscriber engine = Selected();
        LogRoute(engine);
        return engine.TranscribeAsync(samples, sampleRate, ct);
    }

    public void WarmUp()
    {
        // Warm only the engine the current language will use. Warming both would load ~1.3 GB of
        // models at startup for a user who only ever dictates in one language.
        ITranscriber engine = Selected();
        // Logged here and not just on the first utterance: warm-up is where the engine is actually
        // loaded, and the startup log is otherwise ambiguous — the GPU probe loads ggml on another
        // thread at the same moment, so "ggml appeared in the log" does not tell you what warmed.
        LogRoute(engine);
        engine.WarmUp();
    }

    public IStreamingSession OpenStream()
    {
        ITranscriber engine = Selected();
        LogRoute(engine);
        if (engine is not IStreamingTranscriber streaming)
            throw new InvalidOperationException(
                $"{engine.GetType().Name} does not support streaming.");
        return streaming.OpenStream();
    }

    /// <summary>One line per route CHANGE, not per utterance — the engine actually in use is the
    /// first thing worth knowing from a log, and repeating it every dictation buries everything else.</summary>
    private void LogRoute(ITranscriber engine)
    {
        string name = ReferenceEquals(engine, _english) ? _englishLabel() : "ggml";
        if (name == _lastRouteLogged) return;
        _lastRouteLogged = name;
        _log?.Invoke($"engine route: {name} for language '{_language}'");
    }

    public void Dispose()
    {
        (_english as IDisposable)?.Dispose();
        (_other as IDisposable)?.Dispose();
    }
}
