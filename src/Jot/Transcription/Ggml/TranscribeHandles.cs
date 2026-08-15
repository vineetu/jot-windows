using Microsoft.Win32.SafeHandles;

namespace Jot.Transcription.Ggml;

/// <summary>Owns a <c>transcribe_model</c>. Finalizer-safe: ReleaseHandle does not throw.</summary>
internal sealed class TranscribeModelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private TranscribeModelHandle() : base(ownsHandle: true) { }

    internal static TranscribeModelHandle Adopt(nint raw)
    {
        var h = new TranscribeModelHandle();
        h.SetHandle(raw);
        return h;
    }

    internal nint DangerousRaw => handle;

    protected override bool ReleaseHandle()
    {
        NativeMethods.transcribe_model_free(handle);
        return true;
    }
}

/// <summary>
/// Owns a <c>transcribe_session</c> and keeps the parent model alive via
/// <see cref="SafeHandle.DangerousAddRef"/> so a session cannot outlive a collected model
/// (finalizer order is otherwise undefined).
/// </summary>
internal sealed class TranscribeSessionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly TranscribeModelHandle _model;
    private readonly bool _addRefd;

    internal TranscribeSessionHandle(nint raw, TranscribeModelHandle model) : base(ownsHandle: true)
    {
        SetHandle(raw);
        _model = model;
        model.DangerousAddRef(ref _addRefd);
    }

    internal nint DangerousRaw => handle;

    protected override bool ReleaseHandle()
    {
        NativeMethods.transcribe_session_free(handle);
        if (_addRefd) _model.DangerousRelease();
        return true;
    }
}

/// <summary>
/// One-compute-at-a-time gate. Official 0.1.3 hangs (~343 s) on overlapping compute against a
/// single loaded model; Handy's private build AVed. Two separately loaded models may overlap.
/// Thread-safe: every <see cref="Run{T}"/> on one instance is exclusive.
/// </summary>
internal sealed class GgmlComputeGate
{
    private readonly object _gate = new();
    private int _inFlight;

    /// <summary>Peak concurrent holders. Stays 1 if every caller goes through <see cref="Run{T}"/>.</summary>
    internal int MaxInFlight { get; private set; }

    internal T Run<T>(Func<T> work)
    {
        lock (_gate)
        {
            int n = ++_inFlight;
            if (n > MaxInFlight) MaxInFlight = n;
            try { return work(); }
            finally { _inFlight--; }
        }
    }

    internal void Run(Action work) => Run<object?>(() => { work(); return null; });
}
