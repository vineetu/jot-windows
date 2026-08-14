using System.Runtime.InteropServices;

namespace Jot.Transcription.Ggml;

/// <summary>
/// One native session. Thread-safety: every compute entry takes the parent model's
/// <see cref="TranscribeModel.Compute"/> lock. Dispose is idempotent and is safe if
/// <see cref="FinishStream"/> already ran.
/// </summary>
internal sealed class TranscribeSession : IDisposable
{
    private readonly TranscribeSessionHandle _handle;
    private readonly TranscribeModel _model;
    private bool _disposed;

    internal TranscribeSession(TranscribeSessionHandle handle, TranscribeModel model)
    {
        _handle = handle;
        _model = model;
    }

    internal bool IsHandleClosed => _handle.IsClosed;

    internal NativeMethods.Status Run(float[] pcm, string? language)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _model.Compute.Run(() =>
        {
            using var lang = Utf8HGlobal.Alloc(language);
            var p = default(NativeMethods.RunParams);
            NativeMethods.transcribe_run_params_init(ref p);
            p.Language = lang.Ptr;
            float[] buf = pcm.Length == 0 ? s_dummyPcm : pcm;
            return NativeMethods.transcribe_run(
                _handle.DangerousRaw, ref buf[0], pcm.Length, ref p);
        });
    }

    /// <param name="language">BCP-47, or null for autodetect. Never the string "auto".</param>
    internal NativeMethods.Status StreamBegin(string? language, int attContextRight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _model.Compute.Run(() => BeginUnlocked(language, attContextRight));
    }

    internal NativeMethods.Status Feed(float[] pcm, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)offset > (uint)pcm.Length || count < 0 || offset + count > pcm.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        return _model.Compute.Run(() =>
        {
            var update = default(NativeMethods.StreamUpdate);
            NativeMethods.transcribe_stream_update_init(ref update);
            if (count == 0)
                return NativeMethods.transcribe_stream_feed(
                    _handle.DangerousRaw, ref s_dummyPcm[0], 0, ref update);
            return NativeMethods.transcribe_stream_feed(
                _handle.DangerousRaw, ref pcm[offset], count, ref update);
        });
    }

    internal NativeMethods.Status Finalize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _model.Compute.Run(() =>
        {
            var update = default(NativeMethods.StreamUpdate);
            NativeMethods.transcribe_stream_update_init(ref update);
            return NativeMethods.transcribe_stream_finalize(_handle.DangerousRaw, ref update);
        });
    }

    internal string StreamText()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _model.Compute.Run(() =>
        {
            var t = default(NativeMethods.StreamText);
            NativeMethods.transcribe_stream_text_init(ref t);
            var st = NativeMethods.transcribe_stream_get_text(_handle.DangerousRaw, ref t);
            if (st != NativeMethods.Status.Ok)
                throw new TranscribeException(st, "transcribe_stream_get_text");
            string full = TranscribeException.Utf8(t.FullText);
            if (full.Length > 0) return full;
            string committed = TranscribeException.Utf8(t.CommittedText);
            string tentative = TranscribeException.Utf8(t.TentativeText);
            if (committed.Length == 0) return tentative;
            if (tentative.Length == 0) return committed;
            return committed + " " + tentative;
        });
    }

    internal string FullText()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return TranscribeException.Utf8(NativeMethods.transcribe_full_text(_handle.DangerousRaw));
    }

    internal string DetectedLanguage()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return TranscribeException.Utf8(NativeMethods.transcribe_detected_language(_handle.DangerousRaw));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
        GC.SuppressFinalize(this);
    }

    private NativeMethods.Status BeginUnlocked(string? language, int attContextRight)
    {
        using var lang = Utf8HGlobal.Alloc(language);
        var run = default(NativeMethods.RunParams);
        NativeMethods.transcribe_run_params_init(ref run);
        run.Language = lang.Ptr;

        var stream = default(NativeMethods.StreamParams);
        NativeMethods.transcribe_stream_params_init(ref stream);

        if (!_model.AcceptsParakeetStream)
            return NativeMethods.transcribe_stream_begin(_handle.DangerousRaw, ref run, ref stream);

        // Family ext is copied by the library before begin returns. Heap so we don't need unsafe.
        var ext = default(NativeMethods.ParakeetStreamExt);
        NativeMethods.transcribe_parakeet_stream_ext_init(ref ext);
        ext.AttContextRight = attContextRight;
        nint extPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.ParakeetStreamExt>());
        try
        {
            Marshal.StructureToPtr(ext, extPtr, fDeleteOld: false);
            stream.Family = extPtr;
            return NativeMethods.transcribe_stream_begin(_handle.DangerousRaw, ref run, ref stream);
        }
        finally
        {
            Marshal.FreeHGlobal(extPtr);
        }
    }

    // n_samples is 0 on the empty-feed path, so this is never dereferenced — it only has to stay rooted.
    private static readonly float[] s_dummyPcm = [0f];
}
