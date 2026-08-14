using System.IO;
using System.Runtime.InteropServices;
using Jot.Services;


namespace Jot.Transcription.Ggml;

/// <summary>
/// Process-wide init, ABI pin, and DLL search. Call <see cref="Startup"/> once before any
/// load. Thread-safe. The first successful (or failed) attempt wins — the DllImport resolver
/// cannot be remounted onto a second native directory in-process.
/// </summary>
internal static class TranscribeRuntime
{
    private static readonly object Gate = new();
    private static NativeMethods.LogCallback? _logKeepAlive;
    private static bool _started;
    private static Exception? _startupError;
    private static nint _dllModule;

    internal static string Version { get; private set; } = "";
    internal static string Commit { get; private set; } = "";
    internal static string NativeDir { get; private set; } = "";

    internal static bool IsStarted => _started;

    internal static void Startup(string? nativeDir = null, bool installLog = true)
    {
        lock (Gate)
        {
            if (_started) return;
            if (_startupError is not null) throw Wrap(_startupError);

            try
            {
                StartCore(nativeDir, installLog);
                _started = true;
            }
            catch (Exception ex)
            {
                _startupError = ex;
                throw;
            }
        }
    }

    private static void StartCore(string? nativeDir, bool installLog)
    {
        string? dir = nativeDir;
        if (string.IsNullOrWhiteSpace(dir))
            dir = GgmlNativeLocator.TryResolve();
        if (string.IsNullOrWhiteSpace(dir))
        {
            throw new TranscribeException(
                "transcribe.cpp natives are not installed. Place official v0.1.3 (a94e021) " +
                "transcribe.dll next to Jot.exe, or set JOT_GGML_NATIVE to that folder.");
        }

        string full = Path.GetFullPath(dir);
        if (GgmlNativeLocator.IsForbidden(full))
        {
            throw new TranscribeAbiException(
                $"Refusing to load transcribe.cpp from AppData\\Local\\Handy ({full}). " +
                $"Product code is pinned to {TranscribeAbi.PinBlurb}");
        }

        string dll = Path.Combine(full, GgmlNativeLocator.DllFileName);
        if (!File.Exists(dll))
            throw new TranscribeException($"transcribe.dll not found in {full}.");

        var contract = GgmlNativeLocator.ReadContract(full);
        TranscribeAbi.CheckContract(contract.Version, contract.HeaderHash);

        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, (name, _, _) =>
        {
            if (name is NativeMethods.Dll or "transcribe.dll")
                return NativeLibrary.Load(dll);
            string candidate = Path.Combine(full, name);
            if (File.Exists(candidate))
                return NativeLibrary.Load(candidate);
            return 0;
        });

        _dllModule = NativeLibrary.Load(dll);
        TranscribeAbi.CheckExports(
            NativeMethods.RequiredExports,
            export => NativeLibrary.TryGetExport(_dllModule, export, out _));

        if (installLog)
        {
            _logKeepAlive = (level, msg, _) =>
            {
                string text = TranscribeException.Utf8(msg).TrimEnd();
                if (text.Length == 0) return;
                // Native levels: 0=error, 1=warn, 2+=info/debug. Errors must survive; the rest is noise
                // during Vulkan shader compile.
                if (level <= 1) JotLog.Warn($"ggml L{level}: {text}");
                else JotLog.Info($"ggml L{level}: {text}");
            };
            NativeMethods.transcribe_log_set(_logKeepAlive, 0);
        }

        Version = TranscribeException.Utf8(NativeMethods.transcribe_version());
        Commit = TranscribeException.Utf8(NativeMethods.transcribe_version_commit());
        TranscribeAbi.CheckVersion(Version, Commit);

        CheckAbi(NativeMethods.AbiStruct.ModelLoadParams, Marshal.SizeOf<NativeMethods.ModelLoadParams>());
        CheckAbi(NativeMethods.AbiStruct.SessionParams, Marshal.SizeOf<NativeMethods.SessionParams>());
        CheckAbi(NativeMethods.AbiStruct.RunParams, Marshal.SizeOf<NativeMethods.RunParams>());
        CheckAbi(NativeMethods.AbiStruct.StreamParams, Marshal.SizeOf<NativeMethods.StreamParams>());
        CheckAbi(NativeMethods.AbiStruct.Capabilities, Marshal.SizeOf<NativeMethods.Capabilities>());
        CheckAbi(NativeMethods.AbiStruct.StreamUpdate, Marshal.SizeOf<NativeMethods.StreamUpdate>());
        CheckAbi(NativeMethods.AbiStruct.StreamText, Marshal.SizeOf<NativeMethods.StreamText>());
        CheckAbi(NativeMethods.AbiStruct.BackendDevice, Marshal.SizeOf<NativeMethods.BackendDevice>());
        CheckAbi(NativeMethods.AbiStruct.Timings, Marshal.SizeOf<NativeMethods.Timings>());
        CheckAbi(NativeMethods.AbiStruct.Ext, Marshal.SizeOf<NativeMethods.Ext>());

        var st = NativeMethods.transcribe_init_backends(full);
        if (st != NativeMethods.Status.Ok)
            throw new TranscribeException(st, "transcribe_init_backends");

        NativeDir = full;
    }

    private static void CheckAbi(NativeMethods.AbiStruct which, int managed)
        => TranscribeAbi.CheckStructSize(which.ToString(), managed, NativeMethods.transcribe_abi_struct_size(which));

    internal static bool BackendAvailable(NativeMethods.BackendRequest kind)
    {
        Startup();
        return NativeMethods.transcribe_backend_available(kind);
    }

    private static Exception Wrap(Exception ex) =>
        ex is TranscribeException or TranscribeAbiException
            ? ex
            : new TranscribeException(ex.Message, ex);
}
