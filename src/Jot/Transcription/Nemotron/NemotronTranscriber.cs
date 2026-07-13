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
/// Ports the validated Python reference: 128-mel front-end → per-560ms-chunk encoder passes that thread
/// cache tensors chunk-to-chunk (with a 9-frame pre-encode carryover) → greedy transducer decode
/// (joint argmax; on a non-blank, advance the LSTM state; ≤10 symbols/frame) → SentencePiece detok.
///
/// For the batch <see cref="ITranscriber"/> contract, the whole clip is fed through the streaming loop
/// and the full transcript returned. All three graphs run on CPU by default; the encoder can honour a
/// GPU backend. Model loads lazily on first use, off the UI thread.
/// </summary>
public sealed class NemotronTranscriber : ITranscriber, IDisposable
{
    private const int RequiredSampleRate = 16_000;
    private const int BlankId = 13_087;
    private const int MaxSymbolsPerStep = 10;
    private const int WindowFrames = 65;       // 9 carryover + 56 new
    private const int NewFramesPerChunk = 56;
    private const int PreEncodeCache = 9;
    private const long EnglishLangId = 0;      // Nemotron language-embedding index for English

    // Cache tensor dims (from the model's input shapes).
    private const int EncLayers = 24, ChannelCache = 56, Hidden = 1024, TimeCache = 8;
    private const int DecLstmLayers = 2, DecDim = 640;

    private readonly NemotronModel _model;
    private readonly OnnxSessionFactory _factory;
    private readonly ComputeBackend _encoderBackend;
    private readonly MelFrontend _mel = new();
    private readonly object _loadGate = new();
    private readonly object _inferenceGate = new();

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

    /// <summary>Sets the spoken-language embedding index (English = 0). Applied on the next call.</summary>
    public void SetLanguageId(long langId) => _langId = langId;

    public Task<string> TranscribeAsync(float[] samples, int sampleRate, CancellationToken ct = default)
        => Task.Run(() => Transcribe(samples, sampleRate, ct), ct);

    public void WarmUp()
    {
        if (!_model.IsInstalled) return;
        try { Transcribe(new float[RequiredSampleRate / 2], RequiredSampleRate, CancellationToken.None); }
        catch { /* best effort */ }
    }

    private string Transcribe(float[] samples, int sampleRate, CancellationToken ct)
    {
        if (sampleRate != RequiredSampleRate)
            throw new ArgumentException($"Nemotron expects {RequiredSampleRate} Hz mono audio, got {sampleRate} Hz.", nameof(sampleRate));
        if (samples.Length == 0) return string.Empty;

        EnsureLoaded();
        ct.ThrowIfCancellationRequested();

        lock (_inferenceGate)
        {
            float[][] mel = _mel.Compute(samples);       // [T][128]
            int t = mel.Length;
            if (t == 0) return string.Empty;

            // Encoder cache (zeros) + decoder LSTM state (zeros), threaded across chunks.
            var cacheChannel = new float[EncLayers * ChannelCache * Hidden];      // [1,24,56,1024]
            var cacheTime = new float[EncLayers * Hidden * TimeCache];            // [1,24,1024,8]
            long cacheLen = 0;
            var h = new float[DecLstmLayers * DecDim];                            // [2,1,640]
            var c = new float[DecLstmLayers * DecDim];

            // Prime the prediction network with the blank token and zero state.
            var tokens = new List<int>();
            float[] g = RunDecoder(BlankId, h, c, out h, out c);                  // [1,1,640]

            int nChunks = (t + NewFramesPerChunk - 1) / NewFramesPerChunk;
            var window = new float[WindowFrames * MelFrontend.NMels];             // [65,128] time-major

            for (int chunk = 0; chunk < nChunks; chunk++)
            {
                ct.ThrowIfCancellationRequested();
                int start = chunk * NewFramesPerChunk;                            // index into the 9-padded stream
                int valid = Math.Min(WindowFrames, (t + PreEncodeCache) - start);

                // Fill the 65-frame window from the pre-padded mel stream (first 9 frames are zeros).
                Array.Clear(window);
                for (int wf = 0; wf < WindowFrames; wf++)
                {
                    int src = start + wf - PreEncodeCache;                        // -9..: <0 is pre-encode zero pad
                    if (src < 0 || src >= t) continue;
                    Array.Copy(mel[src], 0, window, wf * MelFrontend.NMels, MelFrontend.NMels);
                }

                (float[] enc, int encLen, cacheChannel, cacheTime, cacheLen) =
                    RunEncoder(window, valid, cacheChannel, cacheTime, cacheLen);

                for (int ti = 0; ti < encLen; ti++)
                {
                    var frame = new float[Hidden];
                    Array.Copy(enc, ti * Hidden, frame, 0, Hidden);

                    for (int sym = 0; sym < MaxSymbolsPerStep; sym++)
                    {
                        float[] logits = RunJoint(frame, g);                      // [13088]
                        int k = ArgMax(logits);
                        if (k == BlankId) break;
                        tokens.Add(k);
                        g = RunDecoder(k, h, c, out h, out c);
                    }
                }
            }

            return Detokenize(tokens);
        }
    }

    // ---- encoder: one cache-aware chunk --------------------------------------------------------

    private (float[] enc, int encLen, float[] cacheChannel, float[] cacheTime, long cacheLen) RunEncoder(
        float[] window, int validFrames, float[] cacheChannel, float[] cacheTime, long cacheLen)
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
        var outputs = AsDense<float>(r, "outputs");                 // [1, 7, 1024]
        int encLen = (int)AsDense<long>(r, "encoded_lengths").Buffer.Span[0];
        float[] enc = outputs.Buffer.ToArray();
        float[] nextCh = AsDense<float>(r, "cache_last_channel_next").Buffer.ToArray();
        float[] nextT = AsDense<float>(r, "cache_last_time_next").Buffer.ToArray();
        long nextLen = AsDense<long>(r, "cache_last_channel_len_next").Buffer.Span[0];
        return (enc, encLen, nextCh, nextT, nextLen);
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
        // decoder_output is [1, 640, 1] (channel-major); with target_len=1 the 640 values are the
        // prediction vector — reshaping to the joint's expected [1, 1, 640] is a no-op copy.
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
        return AsDense<float>(r, "joint_output").Buffer.ToArray();  // [1,1,1,13088] -> 13088
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
            sb.Append(piece.Replace('▁', ' '));                                // SentencePiece metaspace
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
