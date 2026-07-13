using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Jot.Transcription.Onnx;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jot.Transcription.Nemotron;

/// <summary>
/// On-device speech-to-text using NVIDIA Nemotron 3.5 ASR streaming multilingual 0.6B (int4 ONNX).
///
/// A cache-aware streaming FastConformer encoder + LSTM prediction decoder + joint (RNNT transducer).
/// The engine is genuinely streaming: a <see cref="Session"/> keeps the encoder cache + decoder state
/// open and consumes each 560 ms chunk exactly once (threading the cache forward), so a live dictation
/// is essentially finished the moment you stop — no re-decoding of the whole clip. The batch
/// <see cref="ITranscriber"/> path just opens a session, feeds the whole clip, and finishes.
///
/// Ports the validated Python reference: 128-mel front-end → per-chunk encoder with a 9-frame
/// pre-encode carryover → greedy transducer decode (joint argmax; advance LSTM on a non-blank;
/// ≤10 symbols/frame) → SentencePiece detok. All graphs run on CPU by default; the encoder can
/// honour a GPU backend. Loads lazily on first use, off the UI thread.
/// </summary>
public sealed class NemotronTranscriber : ITranscriber, IStreamingTranscriber, IDisposable
{
    private const int RequiredSampleRate = 16_000;
    private const int BlankId = 13_087;
    private const int MaxSymbolsPerStep = 10;
    private const int WindowFrames = 65;       // 9 carryover + 56 new
    private const int NewFramesPerChunk = 56;
    private const int PreEncodeCache = 9;
    private const int EndPadGuardFrames = 4;   // trailing mel frames affected by center end-pad; defer them
    private const long EnglishLangId = 0;

    private const int EncLayers = 24, ChannelCache = 56, Hidden = 1024, TimeCache = 8;
    private const int DecLstmLayers = 2, DecDim = 640;

    private readonly NemotronModel _model;
    private readonly OnnxSessionFactory _factory;
    private readonly ComputeBackend _encoderBackend;
    private readonly MelFrontend _mel = new();
    private readonly object _loadGate = new();
    private readonly object _inferenceGate = new();  // the ONNX sessions can't Run() concurrently

    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private InferenceSession? _joint;
    private string[] _vocab = [];
    private long _langId = EnglishLangId;
    private volatile bool _loaded;

    public NemotronTranscriber(
        NemotronModel model,
        OnnxSessionFactory factory,
        ComputeBackend encoderBackend = ComputeBackend.Cpu)
    {
        _model = model;
        _factory = factory;
        _encoderBackend = encoderBackend;
    }

    public bool IsModelInstalled => _model.IsInstalled;

    /// <summary>Sets the spoken-language embedding index (English = 0). Applied to new sessions.</summary>
    public void SetLanguageId(long langId) => _langId = langId;

    /// <summary>Opens a live streaming session: feed audio as it arrives, read the growing transcript,
    /// and <see cref="Session.Finish"/> when the user stops (near-instant — only the tail remains).</summary>
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
    /// One in-flight utterance. Holds the persistent encoder cache + decoder LSTM state and the tokens
    /// emitted so far. <see cref="Accept"/> feeds newly-arrived audio and processes every chunk that is
    /// now stable (a one-chunk trailing margin is held back until <see cref="Finish"/>, since the mel
    /// front-end's end padding perturbs the last few frames).
    /// </summary>
    public sealed class Session : IStreamingSession
    {
        private readonly NemotronTranscriber _t;
        private readonly List<float> _audio = new();
        private readonly List<int> _tokens = new();
        private readonly float[] _cacheChannel = new float[EncLayers * ChannelCache * Hidden];
        private readonly float[] _cacheTime = new float[EncLayers * Hidden * TimeCache];
        private long _cacheLen;
        private float[] _h = new float[DecLstmLayers * DecDim];
        private float[] _c = new float[DecLstmLayers * DecDim];
        private float[] _g = [];
        private bool _primed;
        private int _fedChunks;

        internal Session(NemotronTranscriber t) => _t = t;

        /// <summary>Feeds new 16 kHz mono samples; returns the transcript so far. Cheap to call often.</summary>
        public string Accept(float[] newSamples)
        {
            _audio.AddRange(newSamples);
            lock (_t._inferenceGate)
            {
                Prime();
                float[][] mel = _t._mel.Compute(_audio.ToArray());
                int total = mel.Length;
                // Only process chunks whose frames are all clear of the end-pad zone.
                int stableFrames = total - EndPadGuardFrames;
                int stableChunks = stableFrames > 0 ? stableFrames / NewFramesPerChunk : 0;
                for (int ci = _fedChunks; ci < stableChunks; ci++) ProcessChunk(ci, mel, total);
                _fedChunks = Math.Max(_fedChunks, stableChunks);
                return _t.Detokenize(_tokens);
            }
        }

        /// <summary>Processes the remaining tail and returns the final transcript.</summary>
        public string Finish()
        {
            lock (_t._inferenceGate)
            {
                Prime();
                float[][] mel = _t._mel.Compute(_audio.ToArray());
                int total = mel.Length;
                if (total > 0)
                {
                    int chunks = (total + NewFramesPerChunk - 1) / NewFramesPerChunk;
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

        private void ProcessChunk(int ci, float[][] mel, int totalFrames)
        {
            int start = ci * NewFramesPerChunk;
            int valid = Math.Min(WindowFrames, (totalFrames + PreEncodeCache) - start);
            if (valid <= 0) return;

            var window = new float[WindowFrames * MelFrontend.NMels];         // [65,128] time-major, zero-padded
            for (int wf = 0; wf < WindowFrames; wf++)
            {
                int src = start + wf - PreEncodeCache;                        // first 9 are pre-encode zeros
                if (src < 0 || src >= totalFrames) continue;
                Array.Copy(mel[src], 0, window, wf * MelFrontend.NMels, MelFrontend.NMels);
            }

            (float[] enc, int encLen) = _t.RunEncoder(window, valid, _cacheChannel, _cacheTime, ref _cacheLen);
            for (int ti = 0; ti < encLen; ti++)
            {
                var frame = new float[Hidden];
                Array.Copy(enc, ti * Hidden, frame, 0, Hidden);
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
        float[] window, int validFrames, float[] cacheChannel, float[] cacheTime, ref long cacheLen)
    {
        var audioSignal = new DenseTensor<float>(window, [1, WindowFrames, MelFrontend.NMels]); // time-major
        var length = new DenseTensor<long>(new[] { (long)validFrames }, [1]);
        var chCache = new DenseTensor<float>(cacheChannel, [1, EncLayers, ChannelCache, Hidden]);
        var tCache = new DenseTensor<float>(cacheTime, [1, EncLayers, Hidden, TimeCache]);
        var chLen = new DenseTensor<long>(new[] { cacheLen }, [1]);
        var langId = new DenseTensor<long>(new[] { _langId }, [1]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("audio_signal", audioSignal),
            NamedOnnxValue.CreateFromTensor("length", length),
            NamedOnnxValue.CreateFromTensor("cache_last_channel", chCache),
            NamedOnnxValue.CreateFromTensor("cache_last_time", tCache),
            NamedOnnxValue.CreateFromTensor("cache_last_channel_len", chLen),
            NamedOnnxValue.CreateFromTensor("lang_id", langId),
        };

        using var r = _encoder!.Run(inputs,
            ["outputs", "encoded_lengths", "cache_last_channel_next", "cache_last_time_next", "cache_last_channel_len_next"]);
        int encLen = (int)AsDense<long>(r, "encoded_lengths").Buffer.Span[0];
        float[] enc = AsDense<float>(r, "outputs").Buffer.ToArray();          // [1,7,1024]
        // Thread the "next" caches back into the same buffers for the following chunk.
        AsDense<float>(r, "cache_last_channel_next").Buffer.Span.CopyTo(cacheChannel);
        AsDense<float>(r, "cache_last_time_next").Buffer.Span.CopyTo(cacheTime);
        cacheLen = AsDense<long>(r, "cache_last_channel_len_next").Buffer.Span[0];
        return (enc, encLen);
    }

    // ---- decoder: LSTM prediction network step -------------------------------------------------

    private float[] RunDecoder(int token, float[] hIn, float[] cIn, out float[] hOut, out float[] cOut)
    {
        var targets = new DenseTensor<long>(new[] { (long)token }, [1, 1]);
        var h = new DenseTensor<float>(hIn, [DecLstmLayers, 1, DecDim]);
        var c = new DenseTensor<float>(cIn, [DecLstmLayers, 1, DecDim]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("targets", targets),
            NamedOnnxValue.CreateFromTensor("h_in", h),
            NamedOnnxValue.CreateFromTensor("c_in", c),
        };

        using var r = _decoder!.Run(inputs, ["decoder_output", "h_out", "c_out"]);
        // decoder_output is [1, 640, 1]; with target_len=1 those 640 values are the prediction vector,
        // and reshaping to the joint's expected [1, 1, 640] is a no-op copy.
        float[] g = AsDense<float>(r, "decoder_output").Buffer.ToArray();
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

        using var r = _joint!.Run(inputs, ["joint_output"]);
        return AsDense<float>(r, "joint_output").Buffer.ToArray();            // [1,1,1,13088] -> 13088
    }

    // ---- detok ---------------------------------------------------------------------------------

    private string Detokenize(IReadOnlyList<int> tokens)
    {
        var sb = new StringBuilder();
        foreach (int id in tokens)
        {
            if (id < 0 || id >= _vocab.Length) continue;
            string piece = _vocab[id];
            if (piece.Length >= 2 && piece[0] == '<' && piece[^1] == '>') continue; // special / locale token
            sb.Append(piece.Replace('▁', ' '));                                     // SentencePiece metaspace
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
                    "The Nemotron model isn't installed yet. Download it from Settings before dictating.");
            try
            {
                _encoder = _factory.Create(_model.Encoder, _encoderBackend);
                _decoder = _factory.Create(_model.Decoder, ComputeBackend.Cpu);
                _joint = _factory.Create(_model.Joint, ComputeBackend.Cpu);
                _vocab = File.ReadAllLines(_model.Vocab);   // id -> line (0-indexed)
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
