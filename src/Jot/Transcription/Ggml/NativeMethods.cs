using System.Runtime.InteropServices;

namespace Jot.Transcription.Ggml;

/// <summary>
/// P/Invoke surface for official transcribe.cpp <c>v0.1.3</c> (<c>a94e021</c>).
/// Layouts are the 0.1.3 header, NOT main's 0.2.0 — 0.2.0 inserts <c>diarize</c> into
/// <see cref="RunParams"/> and adds speaker-segment exports this pin does not have.
/// <see cref="TranscribeRuntime"/> compares <c>transcribe_abi_struct_size</c> and the
/// export table against this managed surface and refuses to start on mismatch.
/// </summary>
internal static class NativeMethods
{
    internal const string Dll = "transcribe";

    internal const uint ExtKindParakeetStream = 0x54534B50; // 'PKST'

    internal enum Status
    {
        Ok = 0,
        InvalidArg = 1,
        NotImplemented = 2,
        FileNotFound = 3,
        Gguf = 4,
        UnsupportedArch = 5,
        UnsupportedVariant = 6,
        Oom = 7,
        Backend = 8,
        SampleRate = 9,
        UnsupportedLanguage = 10,
        UnsupportedTask = 11,
        UnsupportedTimestamps = 12,
        Aborted = 13,
        BadStructSize = 14,
        UnsupportedPnc = 15,
        UnsupportedItn = 16,
        InputTooLong = 17,
        OutputTruncated = 18,
    }

    internal enum AbiStruct
    {
        ModelLoadParams = 0,
        SessionParams = 1,
        RunParams = 2,
        StreamParams = 3,
        Capabilities = 4,
        Timings = 5,
        Segment = 6,
        Word = 7,
        Token = 8,
        StreamUpdate = 9,
        StreamText = 10,
        SessionLimits = 11,
        Ext = 12,
        BackendDevice = 13,
    }

    internal enum BackendRequest
    {
        Auto = 0,
        Cpu = 1,
        Metal = 2,
        Vulkan = 3,
        CpuAccel = 4,
        Cuda = 5,
    }

    internal enum DeviceType
    {
        Cpu = 0,
        Gpu = 1,
        Igpu = 2,
        Accel = 3,
    }

    internal enum Task { Transcribe = 0, Translate = 1 }

    internal enum TimestampKind { None = 0, Auto = 1, Segment = 2, Word = 3, Token = 4 }

    internal enum KvType { Auto = 0, F32 = 1, F16 = 2 }

    internal enum PncMode { Default = 0, Off = 1, On = 2 }

    internal enum ItnMode { Default = 0, Off = 1, On = 2 }

    internal enum ExtSlot { Run = 0, Stream = 1 }

    internal enum StreamCommitPolicy { Auto = 0, OnFinalize = 1, StablePrefix = 2 }

    internal enum StreamState { Idle = 0, Active = 1, Finished = 2, Failed = 3 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Ext
    {
        public ulong Size;
        public uint Kind;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BackendDevice
    {
        public ulong StructSize;
        public nint Name;
        public nint Description;
        public nint Kind;
        public nint DeviceId;
        public ulong MemoryTotal;
        public ulong MemoryFree;
        public DeviceType DeviceType;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ModelLoadParams
    {
        public ulong StructSize;
        public BackendRequest Backend;
        public int GpuDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SessionParams
    {
        public ulong StructSize;
        public int NThreads;
        public KvType KvType;
        public int NCtx;
    }

    // 0.1.3 layout: NO diarize field. 0.2.0 inserts it after Itn and shifts Language.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RunParams
    {
        public ulong StructSize;
        public Task Task;
        public TimestampKind Timestamps;
        public PncMode Pnc;
        public ItnMode Itn;
        public nint Language;
        public nint TargetLanguage;
        [MarshalAs(UnmanagedType.U1)] public bool KeepSpecialTags;
        public nint Family;
        public int SpecKDrafts;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Capabilities
    {
        public ulong StructSize;
        public int NativeSampleRate;
        public int NLanguages;
        public nint Languages;
        public TimestampKind MaxTimestampKind;
        [MarshalAs(UnmanagedType.U1)] public bool SupportsLanguageDetect;
        [MarshalAs(UnmanagedType.U1)] public bool SupportsTranslate;
        [MarshalAs(UnmanagedType.U1)] public bool SupportsStreaming;
        [MarshalAs(UnmanagedType.U1)] public bool SupportsSpecDecode;
        public long MaxAudioMs;
        public int NTranslateTargetLanguages;
        public nint TranslateTargetLanguages;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StreamParams
    {
        public ulong StructSize;
        public nint Family;
        public StreamCommitPolicy CommitPolicy;
        public uint StablePrefixAgreementN;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StreamUpdate
    {
        public ulong StructSize;
        [MarshalAs(UnmanagedType.U1)] public bool ResultChanged;
        [MarshalAs(UnmanagedType.U1)] public bool IsFinal;
        public int Revision;
        public long InputReceivedMs;
        public long AudioCommittedMs;
        public long BufferedMs;
        [MarshalAs(UnmanagedType.U1)] public bool CommittedChanged;
        [MarshalAs(UnmanagedType.U1)] public bool TentativeChanged;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StreamText
    {
        public ulong StructSize;
        public nint FullText;
        public ulong FullTextBytes;
        public nint CommittedText;
        public ulong CommittedTextBytes;
        public nint TentativeText;
        public ulong TentativeTextBytes;
        public ulong RawTentativeStartBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Timings
    {
        public ulong StructSize;
        public float LoadMs;
        public float MelMs;
        public float EncodeMs;
        public float DecodeMs;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ParakeetStreamExt
    {
        public Ext Ext;
        public int AttContextRight;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void LogCallback(int level, nint msg, nint userdata);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint transcribe_version();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint transcribe_version_commit();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nuint transcribe_abi_struct_size(AbiStruct which);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nuint transcribe_abi_struct_align(AbiStruct which);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint transcribe_status_string(int status);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_log_set(LogCallback? cb, nint userdata);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status transcribe_init_backends([MarshalAs(UnmanagedType.LPUTF8Str)] string artifactDir);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status transcribe_init_backends_default();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int transcribe_backend_device_count();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_backend_device_init(ref BackendDevice p);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status transcribe_get_backend_device(int index, ref BackendDevice outDev);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool transcribe_backend_available(BackendRequest kind);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status transcribe_model_get_device(nint model, ref BackendDevice outDev);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_model_load_params_init(ref ModelLoadParams p);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_session_params_init(ref SessionParams p);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_run_params_init(ref RunParams p);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_capabilities_init(ref Capabilities p);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_stream_params_init(ref StreamParams p);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_stream_update_init(ref StreamUpdate p);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_stream_text_init(ref StreamText p);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_timings_init(ref Timings p);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_parakeet_stream_ext_init(ref ParakeetStreamExt p);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status transcribe_model_load_file(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref ModelLoadParams p, out nint outModel);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_model_free(nint model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status transcribe_model_get_capabilities(nint model, ref Capabilities outCaps);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint transcribe_model_arch_string(nint model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint transcribe_model_variant_string(nint model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint transcribe_model_backend(nint model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool transcribe_model_accepts_ext_kind(nint model, ExtSlot slot, uint kind);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status transcribe_session_init(nint model, ref SessionParams p, out nint outSession);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_session_free(nint session);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status transcribe_run(nint session, ref float pcm, int nSamples, ref RunParams p);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status transcribe_stream_begin(nint session, ref RunParams run, ref StreamParams stream);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status transcribe_stream_feed(nint session, ref float pcm, int nSamples, ref StreamUpdate update);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status transcribe_stream_finalize(nint session, ref StreamUpdate update);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_stream_reset(nint session);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern StreamState transcribe_stream_get_state(nint session);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status transcribe_stream_get_text(nint session, ref StreamText outText);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status transcribe_stream_last_status(nint session);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint transcribe_full_text(nint session);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint transcribe_detected_language(nint session);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status transcribe_get_timings(nint session, ref Timings outTimings);

    /// <summary>Exports the binding actually calls. Checked by name before any other native entry.</summary>
    internal static readonly string[] RequiredExports =
    [
        "transcribe_version",
        "transcribe_version_commit",
        "transcribe_abi_struct_size",
        "transcribe_status_string",
        "transcribe_log_set",
        "transcribe_init_backends",
        "transcribe_backend_device_count",
        "transcribe_backend_device_init",
        "transcribe_get_backend_device",
        "transcribe_backend_available",
        "transcribe_model_load_params_init",
        "transcribe_model_load_file",
        "transcribe_model_free",
        "transcribe_model_get_capabilities",
        "transcribe_model_arch_string",
        "transcribe_model_variant_string",
        "transcribe_model_backend",
        "transcribe_model_accepts_ext_kind",
        "transcribe_model_get_device",
        "transcribe_session_params_init",
        "transcribe_session_init",
        "transcribe_session_free",
        "transcribe_run_params_init",
        "transcribe_run",
        "transcribe_capabilities_init",
        "transcribe_stream_params_init",
        "transcribe_stream_update_init",
        "transcribe_stream_text_init",
        "transcribe_parakeet_stream_ext_init",
        "transcribe_stream_begin",
        "transcribe_stream_feed",
        "transcribe_stream_finalize",
        "transcribe_stream_reset",
        "transcribe_stream_get_state",
        "transcribe_stream_get_text",
        "transcribe_full_text",
        "transcribe_detected_language",
        "transcribe_get_timings",
        "transcribe_timings_init",
    ];
}
