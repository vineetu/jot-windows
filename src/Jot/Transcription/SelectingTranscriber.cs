using Jot.Services;

namespace Jot.Transcription;

/// <summary>
/// Picks ggml when its assets are present, otherwise the ONNX engine, and can flip mid-launch
/// (wizard / background fetch just landed the GGUF). A ggml load or first-use failure falls
/// through to ONNX with a loud log — never a crash, never a silent empty transcript that
/// looks like success.
/// </summary>
internal sealed class SelectingTranscriber : ITranscriber, IStreamingTranscriber, IDisposable
{
    private readonly ITranscriber _ggml;
    private readonly ITranscriber _onnx;
    private readonly Func<bool> _ortForced;
    private readonly object _gate = new();
    private ITranscriber? _active;
    private bool _ggmlFailed;
    private string _language = Nemotron.NemotronLocales.DefaultCode;

    internal SelectingTranscriber(ITranscriber ggml, ITranscriber onnx, Func<bool>? ortForced = null)
    {
        _ggml = ggml;
        _onnx = onnx;
        _ortForced = ortForced ?? (() => false);
    }

    /// <summary>True when either engine has its model. Missing GGUF with int4 still present is
    /// "installed" — an upgrader must not be sent back through the wizard.</summary>
    public bool IsModelInstalled => _ggml.IsModelInstalled || _onnx.IsModelInstalled;

    internal ITranscriber Active
    {
        get
        {
            lock (_gate)
            {
                ITranscriber want = WantGgml() ? _ggml : _onnx;
                if (!ReferenceEquals(want, _active))
                {
                    _active = want;
                    TranscriberFactory.ApplyLanguage(_active, _language);
                    JotLog.Info(ReferenceEquals(want, _ggml)
                        ? "engine: ggml (assets present)"
                        : _ggmlFailed
                            ? "engine: onnx (ggml failed this launch)"
                            : "engine: onnx");
                }
                return _active;
            }
        }
    }

    internal bool UsingGgml
    {
        get
        {
            lock (_gate) { return WantGgml(); }
        }
    }

    public void SetLanguage(string language)
    {
        _language = language ?? "";
        lock (_gate)
        {
            if (_active is not null) TranscriberFactory.ApplyLanguage(_active, _language);
        }
    }

    public Task<string> TranscribeAsync(float[] samples, int sampleRate, CancellationToken ct = default)
    {
        try
        {
            return Active.TranscribeAsync(samples, sampleRate, ct);
        }
        catch (Exception ex) when (ShouldFallBack())
        {
            MarkFailed(ex);
            return _onnx.TranscribeAsync(samples, sampleRate, ct);
        }
    }

    public void WarmUp()
    {
        try
        {
            Active.WarmUp();
        }
        catch (Exception ex) when (ShouldFallBack())
        {
            MarkFailed(ex);
            _onnx.WarmUp();
        }
    }

    public IStreamingSession OpenStream()
    {
        try
        {
            return ((IStreamingTranscriber)Active).OpenStream();
        }
        catch (Exception ex) when (ShouldFallBack())
        {
            MarkFailed(ex);
            if (_onnx is IStreamingTranscriber s) return s.OpenStream();
            throw;
        }
    }

    IStreamingSession IStreamingTranscriber.OpenStream() => OpenStream();

    public void Dispose()
    {
        (_ggml as IDisposable)?.Dispose();
        (_onnx as IDisposable)?.Dispose();
    }

    private bool WantGgml() => !_ortForced() && !_ggmlFailed && _ggml.IsModelInstalled;

    private bool ShouldFallBack()
    {
        if (_ggmlFailed || _ortForced()) return false;
        lock (_gate)
        {
            return ReferenceEquals(_active, _ggml);
        }
    }

    private void MarkFailed(Exception ex)
    {
        lock (_gate)
        {
            _ggmlFailed = true;
            _active = null;
        }
        JotLog.Warn($"ggml failed — falling back to ONNX: {ex.Message}");
    }
}
