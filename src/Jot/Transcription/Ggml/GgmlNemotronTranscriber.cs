using Jot.Services;

namespace Jot.Transcription.Ggml;

/// <summary>
/// Nemotron 3.5 via official transcribe.cpp 0.1.3 (Q8_0 GGUF, Vulkan or CPU). Default engine
/// when the GGUF and official natives are present. One loaded model at a time is serialized
/// inside the binding; sessions are SafeHandle-owned. Custom vocabulary stays on the separate
/// Parakeet CTC ONNX spotter (plan §1 option A).
/// </summary>
public sealed class GgmlNemotronTranscriber : ITranscriber, IStreamingTranscriber, IDisposable
{
    private const int RequiredSampleRate = 16_000;

    private readonly NemotronGgufModel _model;
    private readonly GgmlEngineOptions _options;
    private readonly object _loadGate = new();
    private TranscribeModel? _loaded;
    private string _languageSetting = Nemotron.NemotronLocales.DefaultCode;

    internal GgmlNemotronTranscriber(NemotronGgufModel model, GgmlEngineOptions options)
    {
        _model = model;
        _options = options;
    }

    /// <summary>True only when the GGUF is on disk AND official natives resolve. Missing either is
    /// "not installed" — never a crash and never a silent ONNX/CPU success.</summary>
    public bool IsModelInstalled => _model.IsInstalled && GgmlNativeLocator.IsPresent();

    /// <summary>Stores the settings string; mapping onto the native pointer happens at stream begin
    /// so a capabilities list from the loaded GGUF is what we consult, not a hardcoded 32.</summary>
    public void SetLanguage(string language) => _languageSetting = language ?? "";

    public Session OpenStream()
    {
        EnsureLoaded();
        return new Session(this);
    }

    IStreamingSession IStreamingTranscriber.OpenStream() => OpenStream();

    public Task<string> TranscribeAsync(float[] samples, int sampleRate, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (sampleRate != RequiredSampleRate)
                throw new ArgumentException(
                    $"Nemotron expects {RequiredSampleRate} Hz mono audio, got {sampleRate} Hz.",
                    nameof(sampleRate));
            if (samples.Length == 0) return string.Empty;

            using var session = OpenStream();
            session.Accept(samples);
            return session.Finish();
        }, ct);

    public void WarmUp()
    {
        if (!IsModelInstalled) return;
        // Rethrow: a swallowed failure here would look like a successful warm-up and then
        // hitch (or error) on first dictation. There is no ONNX fallback.
        TranscribeAsync(new float[RequiredSampleRate / 2], RequiredSampleRate).GetAwaiter().GetResult();
    }

    /// <summary>
    /// One in-flight utterance. The native session is a SafeHandle; <see cref="Finish"/> frees it,
    /// and abandoning the object (Cancel without Finish) still finalizes the handle.
    /// </summary>
    public sealed class Session : IStreamingSession, IDisposable
    {
        private readonly GgmlNemotronTranscriber _t;
        private readonly TranscribeSession _native;
        private readonly string? _language;
        private bool _begun;
        private bool _finished;
        private bool _disposed;

        internal Session(GgmlNemotronTranscriber t)
        {
            _t = t;
            _native = t._loaded!.NewSession();
            _language = GgmlLanguage.Map(t._languageSetting, t._loaded.Languages);
        }

        public string Accept(float[] newSamples)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            BeginIfNeeded();
            if (newSamples.Length > 0)
            {
                var st = _native.Feed(newSamples, 0, newSamples.Length);
                if (st != NativeMethods.Status.Ok)
                    throw new TranscribeException(st, "transcribe_stream_feed");
            }
            return _native.StreamText();
        }

        public string Finish()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_finished) return _native.StreamText();
            BeginIfNeeded();
            try
            {
                var st = _native.Finalize();
                if (st != NativeMethods.Status.Ok)
                    throw new TranscribeException(st, "transcribe_stream_finalize");
                return _native.StreamText();
            }
            finally
            {
                _finished = true;
                Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _native.Dispose();
        }

        private void BeginIfNeeded()
        {
            if (_begun) return;
            var st = _native.StreamBegin(_language, _t._options.AttContextRight);
            if (st != NativeMethods.Status.Ok)
                throw new TranscribeException(st, "transcribe_stream_begin");
            _begun = true;
        }
    }

    private TranscribeModel LoadPreferringVulkan()
    {
        var requested = _options.Backend;
        try
        {
            return TranscribeModel.Load(_model.ModelPath, requested, nativeDir: _options.NativeDir);
        }
        catch (TranscribeException ex) when (
            requested is NativeMethods.BackendRequest.Vulkan or NativeMethods.BackendRequest.Auto &&
            ex.StatusCode == (int)NativeMethods.Status.Backend)
        {
            JotLog.Warn($"ggml {requested} backend unavailable ({ex.Message}) — retrying on CPU");
            return TranscribeModel.Load(
                _model.ModelPath, NativeMethods.BackendRequest.Cpu, nativeDir: _options.NativeDir);
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded is not null) return;
        lock (_loadGate)
        {
            if (_loaded is not null) return;
            if (!_model.IsInstalled)
                throw new InvalidOperationException(
                    $"The Nemotron Q8_0 GGUF isn't installed at {_model.ModelPath}.");
            if (!GgmlNativeLocator.IsPresent())
                throw new InvalidOperationException(
                    "transcribe.cpp natives are not installed. Place official v0.1.3 (a94e021) " +
                    "transcribe.dll next to Jot.exe, or set JOT_GGML_NATIVE.");
            try
            {
                _loaded = LoadPreferringVulkan();
                JotLog.Info(
                    $"ggml loaded arch={_loaded.Arch} variant={_loaded.Variant} backend={_loaded.Backend} " +
                    $"langs={_loaded.Languages.Length} r={_options.AttContextRight}");
            }
            catch
            {
                _loaded?.Dispose();
                _loaded = null;
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_loadGate)
        {
            _loaded?.Dispose();
            _loaded = null;
        }
    }
}
