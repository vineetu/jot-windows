using System.Runtime.InteropServices;
using System.Text;

namespace Jot.Transcription.Ggml;

/// <summary>UTF-8 + NUL on the native heap. A null string allocates nothing (C NULL).</summary>
internal readonly struct Utf8HGlobal : IDisposable
{
    public nint Ptr { get; }
    private Utf8HGlobal(nint p) => Ptr = p;

    public static Utf8HGlobal Alloc(string? s)
    {
        if (s is null) return new(0);
        byte[] bytes = Encoding.UTF8.GetBytes(s + "\0");
        nint p = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        return new(p);
    }

    public void Dispose()
    {
        if (Ptr != 0) Marshal.FreeHGlobal(Ptr);
    }
}
