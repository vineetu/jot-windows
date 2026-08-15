using System.Runtime.InteropServices;

namespace Jot.Transcription.Ggml;

/// <summary>
/// One loaded GGUF. Thread-safety: construction and <see cref="Dispose"/> are serialized on
/// this instance; all compute (run / stream begin / feed / finalize) must go through
/// <see cref="Compute"/>. Overlapping compute on one model hangs or AVs on 0.1.3.
/// Two separately constructed models may compute at the same time.
/// </summary>
internal sealed class TranscribeModel : IDisposable
{
    private readonly TranscribeModelHandle _handle;
    private bool _disposed;

    internal GgmlComputeGate Compute { get; } = new();

    internal string Arch { get; }
    internal string Variant { get; }
    internal string Backend { get; }
    internal int NativeSampleRate { get; }
    internal bool SupportsStreaming { get; }
    internal bool SupportsLanguageDetect { get; }
    internal string[] Languages { get; }
    internal bool AcceptsParakeetStream { get; }

    private TranscribeModel(TranscribeModelHandle handle)
    {
        _handle = handle;
        nint raw = handle.DangerousRaw;
        Arch = TranscribeException.Utf8(NativeMethods.transcribe_model_arch_string(raw));
        Variant = TranscribeException.Utf8(NativeMethods.transcribe_model_variant_string(raw));
        Backend = TranscribeException.Utf8(NativeMethods.transcribe_model_backend(raw));

        var caps = default(NativeMethods.Capabilities);
        NativeMethods.transcribe_capabilities_init(ref caps);
        var st = NativeMethods.transcribe_model_get_capabilities(raw, ref caps);
        if (st != NativeMethods.Status.Ok)
            throw new TranscribeException(st, "transcribe_model_get_capabilities");
        NativeSampleRate = caps.NativeSampleRate;
        SupportsStreaming = caps.SupportsStreaming;
        SupportsLanguageDetect = caps.SupportsLanguageDetect;
        Languages = ReadStringArray(caps.Languages, caps.NLanguages);
        AcceptsParakeetStream = NativeMethods.transcribe_model_accepts_ext_kind(
            raw, NativeMethods.ExtSlot.Stream, NativeMethods.ExtKindParakeetStream);
    }

    internal static TranscribeModel Load(
        string path,
        NativeMethods.BackendRequest backend,
        int gpuDevice = 0,
        string? nativeDir = null)
    {
        TranscribeRuntime.Startup(nativeDir);

        var p = default(NativeMethods.ModelLoadParams);
        NativeMethods.transcribe_model_load_params_init(ref p);
        p.Backend = backend;
        p.GpuDevice = gpuDevice;
        var st = NativeMethods.transcribe_model_load_file(path, ref p, out nint raw);
        if (st != NativeMethods.Status.Ok)
        {
            if (raw != 0) NativeMethods.transcribe_model_free(raw);
            throw new TranscribeException(st, $"transcribe_model_load_file({backend})");
        }

        var handle = TranscribeModelHandle.Adopt(raw);
        try { return new TranscribeModel(handle); }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal TranscribeSession NewSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var p = default(NativeMethods.SessionParams);
        NativeMethods.transcribe_session_params_init(ref p);
        var st = NativeMethods.transcribe_session_init(_handle.DangerousRaw, ref p, out nint raw);
        if (st != NativeMethods.Status.Ok)
        {
            if (raw != 0) NativeMethods.transcribe_session_free(raw);
            throw new TranscribeException(st, "transcribe_session_init");
        }
        return new TranscribeSession(new TranscribeSessionHandle(raw, _handle), this);
    }

    internal nint DangerousRaw
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _handle.DangerousRaw;
        }
    }

    internal bool IsHandleClosed => _handle.IsClosed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
        GC.SuppressFinalize(this);
    }

    internal static string[] ReadStringArray(nint table, int n)
    {
        if (table == 0 || n <= 0) return [];
        var result = new string[n];
        for (int i = 0; i < n; i++)
        {
            nint p = Marshal.ReadIntPtr(table, i * nint.Size);
            result[i] = TranscribeException.Utf8(p);
        }
        return result;
    }
}
