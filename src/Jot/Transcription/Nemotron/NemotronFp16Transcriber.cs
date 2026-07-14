using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jot.Transcription.Onnx;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jot.Transcription.Nemotron;

/// <summary>
/// GPU (DirectML) speech-to-text using NVIDIA Nemotron 3.5 ASR streaming multilingual 0.6B — the FP16
/// ONNX export. Structurally a twin of <see cref="NemotronTranscriber"/> (the int4 CPU engine), but the
/// FP16 graphs run on DirectML, which the int4 build can't (int4 has no DML kernels). The int4 path is
/// left 100% intact; this engine is selected only when the user picks the GPU device.
///
/// Differences from the int4 export (per FP16_SPEC.md — do not assume int4 semantics):
/// <list type="bullet">
/// <item>All encoder I/O is float32 (weights are fp16 internally; ORT casts at the graph boundary).</item>
/// <item><c>audio_signal</c> is feature-major <c>[1,128,32]</c> (32 new mel frames per chunk), not the
///   int4 time-major <c>[1,65,128]</c>.</item>
/// <item>The 9-frame pre-encode carryover is an explicit <c>pre_cache [1,128,9]</c> tensor threaded
///   chunk→chunk; chunks are NON-overlapping 32-frame windows.</item>
/// <item>Language is a one-hot <c>language_mask [1,128]</c> (English → slot 0), not a scalar lang_id.</item>
/// <item>Encoder caches are layers-first (<c>[24,1,56,1024]</c> / <c>[24,1,1024,8]</c>); lengths int32.</item>
/// <item><c>decoder_output</c> is <c>[1,1,640]</c> and joint <c>logits</c> is <c>[1,1,13088]</c> (no
///   transpose / reshape needed). Detok uses <c>vocab.json</c> (id→token).</item>
/// </list>
/// The greedy cache-aware RNNT loop is identical: prime the prediction net with blank, then for each
/// encoder frame run joint→argmax, emit non-blanks and advance the decoder LSTM (≤10 symbols/frame).
/// </summary>
public sealed class NemotronFp16Transcriber : ITranscriber, IStreamingTranscriber, IDisposable
{
    private const int RequiredSampleRate = 16_000;
    private const int BlankId = 13_087;
    private const int MaxSymbolsPerStep = 10;

    private const int ChunkMel = 32;           // new mel frames fed per chunk (audio_signal time dim)
    private const int PreCache = 9;            // mel frames of left context (pre_cache tensor)
    private const int EndPadGuardFrames = 4;   // trailing mel frames perturbed by the moving end-pad; defer them

    private const int EncLayers = 24, ChannelCache = 56, Hidden = 1024, TimeCache = 8;
    private const int NumPrompts = 128;
    private const int DecLstmLayers = 2, DecDim = 640;

    private readonly NemotronFp16Model _model;
    private readonly OnnxSessionFactory _factory;
    private readonly ComputeBackend _backend;
    private readonly MelFrontend _mel = new();
    private readonly object _loadGate = new();
    private readonly object _inferenceGate = new();  // the ONNX sessions can't Run() concurrently

    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private InferenceSession? _joint;
    private string[] _vocab = [];
    private int _langSlot;                     // one-hot prompt slot; English = 0
    private volatile bool _loaded;

    public NemotronFp16Transcriber(
        NemotronFp16Model model,
        OnnxSessionFactory factory,
        ComputeBackend backend = ComputeBackend.DirectML)
    {
        _model = model;
        _factory = factory;
        _backend = backend;
    }

    public bool IsModelInstalled => _model.IsInstalled;

    /// <summary>Sets the language prompt slot (English = 0). Applied to new sessions' language_mask.</summary>
    public void SetLanguageSlot(long slot)
        => _langSlot = slot >= 0 && slot < NumPrompts ? (int)slot : 0;

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
                throw new ArgumentException($"Nemotron expects {RequiredSampleRate} Hz mono audio, got {sampleRate} Hz.", nameof(sampleRate));
            if (samples.Length == 0) return string.Empty;

            var session = OpenStream();
            session.Accept(samples);
            return session.Finish();
        }, ct);

    public void WarmUp()
    {
        if (!_model.IsInstalled) return;
        try { TranscribeAsync(new float[RequiredSampleRate / 2], RequiredSampleRate).GetAwaiter().GetResult(); }
        catch { /* best effort */ }
    }

    // ---- a live streaming utterance ------------------------------------------------------------

    /// <summary>
    /// One in-flight utterance. Holds the persistent encoder caches (pre_cache + channel/time) and the
    /// decoder LSTM state and cached prediction vector, plus the tokens emitted so far. Chunks are the
    /// fixed non-overlapping 32-frame mel windows; a small trailing guard is held back until
    /// <see cref="Finish"/> (the mel front-end's end padding perturbs the last few frames).
    /// </summary>
    public sealed class Session : IStreamingSession
    {
        private readonly NemotronFp16Transcriber _t;
        private readonly List<float> _audio = new();
        private readonly List<int> _tokens = new();

        // encoder caches (layers-first; float32 I/O boundary)
        private readonly float[] _preCache = new float[MelFrontend.NMels * PreCache];               // [1,128,9]
        private readonly float[] _cacheChannel = new float[EncLayers * ChannelCache * Hidden];              // [24,1,56,1024]
        private readonly float[] _cacheTime = new float[EncLayers * Hidden * TimeCache];                    // [24,1,1024,8]
        private int _cacheLen;

        private float[] _h = new float[DecLstmLayers * DecDim];
        private float[] _c = new float[DecLstmLayers * DecDim];
        private float[] _g = [];
        private readonly float[] _langMask;
        private bool _primed;
        private int _fedChunks;

        internal Session(NemotronFp16Transcriber t)
        {
            _t = t;
            _langMask = new float[NumPrompts];
            _langMask[_t._langSlot] = 1.0f;
        }

        /// <summary>Feeds new 16 kHz mono samples; returns the transcript so far. Cheap to call often.</summary>
        public string Accept(float[] newSamples)
        {
            _audio.AddRange(newSamples);
            lock (_t._inferenceGate)
            {
                Prime();
                float[] mel = _t._mel.ComputeFeatureMajor(_audio.ToArray(), out int total);
                // Only process chunks whose 32 frames are all clear of the moving end-pad zone.
                int stableFrames = total - EndPadGuardFrames;
                int stableChunks = stableFrames > 0 ? stableFrames / ChunkMel : 0;
                for (int ci = _fedChunks; ci < stableChunks; ci++) ProcessChunk(ci, mel, total);
                _fedChunks = Math.Max(_fedChunks, stableChunks);
                return _t.Detokenize(_tokens);
            }
        }

        /// <summary>Processes the remaining tail (incl. the zero-padded final partial chunk) and returns
        /// the final transcript.</summary>
        public string Finish()
        {
            lock (_t._inferenceGate)
            {
                Prime();
                float[] mel = _t._mel.ComputeFeatureMajor(_audio.ToArray(), out int total);
                if (total > 0)
                {
                    int chunks = (total + ChunkMel - 1) / ChunkMel;
                    for (int ci = _fedChunks; ci < chunks; ci++) ProcessChunk(ci, mel, total);
                    _fedChunks = chunks;
                }
                return _t.Detokenize(_tokens);
            }
        }

        private void Prime()
        {
            if (_primed) return;
            _g = _t.RunDecoder(BlankId, _h, _c, out _h, out _c); // prediction net primed on blank + zero state
            _primed = true;
        }

        // mel is feature-major [128, totalFrames], index [m * totalFrames + f].
        private void ProcessChunk(int ci, float[] mel, int totalFrames)
        {
            int start = ci * ChunkMel;
            int valid = Math.Min(ChunkMel, totalFrames - start);
            if (valid <= 0) return;

            // audio_signal [1,128,32] feature-major: index [m*32 + t]; pad the partial tail with zeros.
            var audioSignal = new float[MelFrontend.NMels * ChunkMel];
            for (int m = 0; m < MelFrontend.NMels; m++)
            {
                int melRow = m * totalFrames + start;
                int dstRow = m * ChunkMel;
                for (int t = 0; t < valid; t++) audioSignal[dstRow + t] = mel[melRow + t];
                // remaining columns [valid..32) stay 0 (already zeroed)
            }

            (float[] enc, int encLen) = _t.RunEncoder(
                audioSignal, valid, _langMask, _preCache, _cacheChannel, _cacheTime, ref _cacheLen);

            for (int ti = 0; ti < encLen; ti++)
            {
                var frame = new float[Hidden];
                Array.Copy(enc, ti * Hidden, frame, 0, Hidden);   // encoded_output [1,4,1024] -> frame t
                for (int sym = 0; sym < MaxSymbolsPerStep; sym++)
                {
                    int k = ArgMax(_t.RunJoint(frame, _g));
                    if (k == BlankId) break;
                    _tokens.Add(k);
                    _g = _t.RunDecoder(k, _h, _c, out _h, out _c);
                }
            }
        }
    }

    // ---- encoder: one cache-aware chunk (updates the caches in place) --------------------------

    private (float[] enc, int encLen) RunEncoder(
        float[] audioSignal, int validFrames, float[] langMask,
        float[] preCache, float[] cacheChannel, float[] cacheTime, ref int cacheLen)
    {
        var signal = new DenseTensor<float>(audioSignal, [1, MelFrontend.NMels, ChunkMel]);       // feature-major
        var length = new DenseTensor<int>(new[] { validFrames }, [1]);                            // int32
        var mask = new DenseTensor<float>(langMask, [1, NumPrompts]);
        var pre = new DenseTensor<float>(preCache, [1, MelFrontend.NMels, PreCache]);
        var chCache = new DenseTensor<float>(cacheChannel, [EncLayers, 1, ChannelCache, Hidden]);  // layers-first
        var tCache = new DenseTensor<float>(cacheTime, [EncLayers, 1, Hidden, TimeCache]);
        var chLen = new DenseTensor<int>(new[] { cacheLen }, [1]);                                // int32

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("audio_signal", signal),
            NamedOnnxValue.CreateFromTensor("audio_length", length),
            NamedOnnxValue.CreateFromTensor("language_mask", mask),
            NamedOnnxValue.CreateFromTensor("pre_cache", pre),
            NamedOnnxValue.CreateFromTensor("cache_last_channel", chCache),
            NamedOnnxValue.CreateFromTensor("cache_last_time", tCache),
            NamedOnnxValue.CreateFromTensor("cache_last_channel_len", chLen),
        };

        using var r = _encoder!.Run(inputs,
            ["encoded_output", "encoded_length", "new_pre_cache",
             "new_cache_last_channel", "new_cache_last_time", "new_cache_last_channel_len"]);

        int encLen = AsDense<int>(r, "encoded_length").Buffer.Span[0];
        float[] enc = AsDense<float>(r, "encoded_output").Buffer.ToArray();          // [1,4,1024]
        // Thread the "new" caches back into the same buffers for the following chunk.
        AsDense<float>(r, "new_pre_cache").Buffer.Span.CopyTo(preCache);
        AsDense<float>(r, "new_cache_last_channel").Buffer.Span.CopyTo(cacheChannel);
        AsDense<float>(r, "new_cache_last_time").Buffer.Span.CopyTo(cacheTime);
        cacheLen = AsDense<int>(r, "new_cache_last_channel_len").Buffer.Span[0];
        return (enc, encLen);
    }

    // ---- decoder: LSTM prediction network step -------------------------------------------------

    private float[] RunDecoder(int token, float[] hIn, float[] cIn, out float[] hOut, out float[] cOut)
    {
        var tok = new DenseTensor<long>(new[] { (long)token }, [1, 1]);
        var h = new DenseTensor<float>(hIn, [DecLstmLayers, 1, DecDim]);
        var c = new DenseTensor<float>(cIn, [DecLstmLayers, 1, DecDim]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("token", tok),
            NamedOnnxValue.CreateFromTensor("h", h),
            NamedOnnxValue.CreateFromTensor("c", c),
        };

        using var r = _decoder!.Run(inputs, ["decoder_output", "h_out", "c_out"]);
        float[] g = AsDense<float>(r, "decoder_output").Buffer.ToArray();   // [1,1,640] — no transpose needed
        hOut = AsDense<float>(r, "h_out").Buffer.ToArray();
        cOut = AsDense<float>(r, "c_out").Buffer.ToArray();
        return g;
    }

    // ---- joint: logits for one (encoder frame, prediction vector) ------------------------------

    private float[] RunJoint(float[] encFrame, float[] g)
    {
        var encoderOutput = new DenseTensor<float>(encFrame, [1, 1, Hidden]);
        var decoderOutput = new DenseTensor<float>(g, [1, 1, DecDim]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("encoder_output", encoderOutput),
            NamedOnnxValue.CreateFromTensor("decoder_output", decoderOutput),
        };

        using var r = _joint!.Run(inputs, ["logits"]);
        return AsDense<float>(r, "logits").Buffer.ToArray();   // [1,1,13088]
    }

    // ---- detok ---------------------------------------------------------------------------------

    private string Detokenize(IReadOnlyList<int> tokens)
    {
        var sb = new StringBuilder();
        foreach (int id in tokens)
        {
            if (id < 0 || id >= _vocab.Length) continue;
            string? piece = _vocab[id];
            if (piece is null) continue;
            if (piece.Length >= 2 && piece[0] == '<' && piece[^1] == '>') continue; // special / locale token
            sb.Append(piece.Replace('▁', ' '));                                // SentencePiece metaspace ▁
        }
        return Regex.Replace(sb.ToString(), " {2,}", " ").Trim();
    }

    // ---- loading & helpers ---------------------------------------------------------------------

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_loadGate)
        {
            if (_loaded) return;
            if (!_model.IsInstalled)
                throw new InvalidOperationException(
                    "The Nemotron FP16 (GPU) model isn't installed yet.");
            try
            {
                // The DirectML-safe encoder (Split→Slice) runs on the requested backend (DirectML for GPU).
                // decoder + joint are correct on DirectML as-is, so they share the backend.
                _encoder = _factory.Create(_model.Encoder, _backend);
                _decoder = _factory.Create(_model.Decoder, _backend);
                _joint = _factory.Create(_model.Joint, _backend);
                _vocab = LoadVocab(_model.Vocab);
                _loaded = true;
            }
            catch
            {
                _encoder?.Dispose(); _encoder = null;
                _decoder?.Dispose(); _decoder = null;
                _joint?.Dispose(); _joint = null;
                throw;
            }
        }
    }

    /// <summary>vocab.json is a JSON object {"id":"token", ...}; build an id→token array.</summary>
    private static string[] LoadVocab(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var pairs = new List<(int id, string token)>();
        int max = -1;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(prop.Name, out int id)) continue;
            string tok = prop.Value.GetString() ?? "";
            pairs.Add((id, tok));
            if (id > max) max = id;
        }
        var vocab = new string[max + 1];
        foreach (var (id, token) in pairs) vocab[id] = token;
        return vocab;
    }

    private static DenseTensor<T> AsDense<T>(IReadOnlyCollection<DisposableNamedOnnxValue> results, string name)
        => (DenseTensor<T>)results.First(r => r.Name == name).AsTensor<T>();

    private static int ArgMax(float[] v)
    {
        int best = 0; float bv = v[0];
        for (int i = 1; i < v.Length; i++) if (v[i] > bv) { bv = v[i]; best = i; }
        return best;
    }

    public void Dispose()
    {
        _encoder?.Dispose();
        _decoder?.Dispose();
        _joint?.Dispose();
    }
}
