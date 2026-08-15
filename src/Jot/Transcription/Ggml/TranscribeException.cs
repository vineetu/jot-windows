using System.Runtime.InteropServices;

namespace Jot.Transcription.Ggml;

/// <summary>A transcribe.cpp status that is not Ok, or an ABI / load refusal before any call.</summary>
public sealed class TranscribeException : Exception
{
    public int StatusCode { get; }

    public TranscribeException(string message) : base(message) { }

    public TranscribeException(string message, Exception inner) : base(message, inner) { }

    internal TranscribeException(NativeMethods.Status status, string where)
        : base($"{where}: {status} ({Utf8(NativeMethods.transcribe_status_string((int)status))})")
    {
        StatusCode = (int)status;
    }

    internal static string Utf8(nint p) => p == 0 ? "" : (Marshal.PtrToStringUTF8(p) ?? "");
}

/// <summary>
/// The loaded native bits are not official v0.1.3 / a94e021. Distinct from a runtime
/// <see cref="TranscribeException"/> so a caller can tell "wrong DLL" from "this clip failed".
/// </summary>
public sealed class TranscribeAbiException : Exception
{
    public TranscribeAbiException(string message) : base(message) { }
}
