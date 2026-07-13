using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Jot.Transcription.Onnx;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jot.Transcription;

/// <summary>
/// On-device speech-to-text using NVIDIA Parakeet-TDT-0.6B (int8) via ONNX Runtime.
///
/// Ports the reference onnx-asr pipeline: a <c>nemo128</c> log-mel preprocessor feeds a Conformer
/// encoder, then a greedy Token-and-Duration Transducer (TDT) loop runs the joint prediction network
/// frame by frame — each step emits a token (or blank) plus a <i>duration</i> (how many encoder frames
/// to skip), carrying the prediction-network state forward only when a real token is emitted.
///
/// The preprocessor and per-step decoder run on CPU (cheap, and DirectML has no upside for the tiny
/// autoregressive decoder); only the encoder honours <see cref="ComputeBackend"/>. The model loads
/// lazily on the first call, off the UI thread.
/// </summary>
public sealed class ParakeetTranscriber : ITranscriber, IDisposable
{
    private const int RequiredSampleRate = 16_000;
    private const int MaxTokensPerStep = 10; // NeMo default; config.json carries no override

    // SentencePiece detokenisation: drop leading/interior stray spaces, keep one space before a word.
    private static readonly Regex DecodeSpacePattern =
        new(@"\A\s|\s\B|(\s)\b", RegexOptions.Compiled);

    private readonly ParakeetModel _model;
    private readonly OnnxSessionFactory _factory;
    private readonly ComputeBackend _encoderBackend;
    private readonly object _loadGate = new();
    // Serialises inference: the sessions are a shared singleton, and the DirectML EP forbids
    // concurrent Run() on one session (e.g. a re-transcribe overlapping a dictation, or warmup
    // overlapping the first dictation).
    private readonly object _inferenceGate = new();

    private InferenceSession? _preprocessor;
    private InferenceSession? _encoder;
    private InferenceSession? _decoderJoint;
    private string[] _vocab = [];   // token id -> piece (U+2581 already mapped to space)
    private int _vocabSize;         // logit width for tokens (blank included), e.g. 1025
    private int _blankId;           // index of <blk>
    private int _stateLayers;       // input_states_* dim 0
    private int _stateDim;          // input_states_* dim 2
    private volatile bool _loaded;

    public ParakeetTranscriber(
        ParakeetModel model,
        OnnxSessionFactory factory,
        ComputeBackend encoderBackend = ComputeBackend.Cpu)
    {
        _model = model;
        _factory = factory;
        _encoderBackend = encoderBackend;
    }

    /// <summary>Whether the model assets are present. When false, transcription throws a clear error.</summary>
    public bool IsModelInstalled => _model.IsInstalled;

    public Task<string> TranscribeAsync(float[] samples, int sampleRate, CancellationToken ct = default)
        => Task.Run(() => Transcribe(samples, sampleRate, ct), ct);

    /// <summary>
    /// Loads the sessions and primes the ORT kernels with a short silent buffer, so the first real
    /// dictation doesn't pay the cold-start cost. Safe to call on a background thread at startup;
    /// no-ops (swallows) if the model isn't installed yet.
    /// </summary>
    public void WarmUp()
    {
        if (!_model.IsInstalled) return;
        try
        {
            Transcribe(new float[RequiredSampleRate / 2], RequiredSampleRate, CancellationToken.None);
        }
        catch
        {
            // Warmup is best-effort; a real transcription will surface any genuine failure.
        }
    }

    private string Transcribe(float[] samples, int sampleRate, CancellationToken ct)
    {
        if (sampleRate != RequiredSampleRate)
            throw new ArgumentException(
                $"Parakeet expects {RequiredSampleRate} Hz mono audio, got {sampleRate} Hz.", nameof(sampleRate));
        if (samples.Length == 0)
            return string.Empty;

        EnsureLoaded();
        ct.ThrowIfCancellationRequested();

        lock (_inferenceGate)
        {
            (float[] features, int[] featureDims, long featureLen) = RunPreprocessor(samples);
            ct.ThrowIfCancellationRequested();

            (float[] encoded, int[] encodedDims, int encodedLen) = RunEncoder(features, featureDims, featureLen);
            ct.ThrowIfCancellationRequested();

            List<int> tokenIds = DecodeGreedy(encoded, encodedDims, encodedLen, ct);
            return Detokenize(tokenIds);
        }
    }

    // ---- Stage 1: waveform -> log-mel features -------------------------------------------------

    private (float[] features, int[] dims, long length) RunPreprocessor(float[] samples)
    {
        var waveform = new DenseTensor<float>(samples, [1, samples.Length]);
        var waveformLen = new DenseTensor<long>(new long[] { samples.Length }, [1]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("waveforms", waveform),
            NamedOnnxValue.CreateFromTensor("waveforms_lens", waveformLen),
        };

        using var results = _preprocessor!.Run(inputs, ["features", "features_lens"]);
        var features = AsDense<float>(results, "features");
        var lengths = AsDense<long>(results, "features_lens");
        return (features.Buffer.ToArray(), features.Dimensions.ToArray(), lengths.Buffer.Span[0]);
    }

    // ---- Stage 2: features -> encoder frames ---------------------------------------------------

    private (float[] encoded, int[] dims, int length) RunEncoder(float[] features, int[] featureDims, long featureLen)
    {
        var audioSignal = new DenseTensor<float>(features, featureDims);      // [1, 128, T]
        var length = new DenseTensor<long>(new[] { featureLen }, [1]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("audio_signal", audioSignal),
            NamedOnnxValue.CreateFromTensor("length", length),
        };

        using var results = _encoder!.Run(inputs, ["outputs", "encoded_lengths"]);
        var encoded = AsDense<float>(results, "outputs");                     // [1, 1024, T_enc]
        var encodedLen = AsDense<long>(results, "encoded_lengths");
        return (encoded.Buffer.ToArray(), encoded.Dimensions.ToArray(), (int)encodedLen.Buffer.Span[0]);
    }

    // ---- Stage 3: greedy TDT decode ------------------------------------------------------------

    private List<int> DecodeGreedy(float[] encoded, int[] dims, int encodedLen, CancellationToken ct)
    {
        int encDim = dims[1];            // 1024
        int frames = dims[2];            // padded time length
        int valid = Math.Min(encodedLen, frames);

        float[] state1 = new float[_stateLayers * _stateDim]; // [layers, 1, dim] flattened, zero-init
        float[] state2 = new float[_stateLayers * _stateDim];

        var tokens = new List<int>();
        var frame = new float[encDim];

        int t = 0;
        int emitted = 0;
        while (t < valid)
        {
            ct.ThrowIfCancellationRequested();

            // Column t of [1, encDim, frames]: element (0, d, t) sits at d*frames + t.
            for (int d = 0; d < encDim; d++)
                frame[d] = encoded[d * frames + t];

            int lastToken = tokens.Count > 0 ? tokens[^1] : _blankId;
            (float[] tokenLogits, float[] durationLogits, float[] nextState1, float[] nextState2) =
                DecodeStep(frame, lastToken, state1, state2);

            int token = ArgMax(tokenLogits);
            int duration = ArgMax(durationLogits);

            if (token != _blankId)
            {
                state1 = nextState1;
                state2 = nextState2;
                tokens.Add(token);
                emitted++;
            }

            // TDT: a positive predicted duration jumps ahead; otherwise advance one frame on
            // a blank or once we've emitted the per-step cap (guards against infinite loops).
            if (duration > 0)
            {
                t += duration;
                emitted = 0;
            }
            else if (token == _blankId || emitted == MaxTokensPerStep)
            {
                t += 1;
                emitted = 0;
            }
        }

        return tokens;
    }

    private (float[] tokenLogits, float[] durationLogits, float[] state1, float[] state2) DecodeStep(
        float[] frame, int lastToken, float[] state1, float[] state2)
    {
        var encoderOutputs = new DenseTensor<float>(frame, [1, frame.Length, 1]);
        var targets = new DenseTensor<int>(new[] { lastToken }, [1, 1]);          // INT32
        var targetLength = new DenseTensor<int>(new[] { 1 }, [1]);                // INT32
        var inState1 = new DenseTensor<float>(state1, [_stateLayers, 1, _stateDim]);
        var inState2 = new DenseTensor<float>(state2, [_stateLayers, 1, _stateDim]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("encoder_outputs", encoderOutputs),
            NamedOnnxValue.CreateFromTensor("targets", targets),
            NamedOnnxValue.CreateFromTensor("target_length", targetLength),
            NamedOnnxValue.CreateFromTensor("input_states_1", inState1),
            NamedOnnxValue.CreateFromTensor("input_states_2", inState2),
        };

        using var results = _decoderJoint!.Run(inputs, ["outputs", "output_states_1", "output_states_2"]);
        float[] output = AsDense<float>(results, "outputs").Buffer.ToArray(); // [1,1,1, vocab+durations]
        float[] nextState1 = AsDense<float>(results, "output_states_1").Buffer.ToArray();
        float[] nextState2 = AsDense<float>(results, "output_states_2").Buffer.ToArray();

        float[] tokenLogits = output[.._vocabSize];
        float[] durationLogits = output[_vocabSize..];
        return (tokenLogits, durationLogits, nextState1, nextState2);
    }

    // ---- Stage 4: tokens -> text ---------------------------------------------------------------

    private string Detokenize(List<int> tokenIds)
    {
        var builder = new StringBuilder();
        foreach (int id in tokenIds)
            builder.Append(_vocab[id]);

        return DecodeSpacePattern.Replace(builder.ToString(), m => m.Groups[1].Success ? " " : "");
    }

    // ---- Loading & helpers ---------------------------------------------------------------------

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_loadGate)
        {
            if (_loaded) return;
            if (!_model.IsInstalled)
                throw new InvalidOperationException(
                    "The speech model isn't installed yet. Download it from Settings before dictating.");

            try
            {
                // FFT/mel ops and the tiny per-frame decoder stay on CPU; only the encoder can go to a GPU.
                _preprocessor = _factory.Create(_model.Preprocessor, ComputeBackend.Cpu);
                _encoder = _factory.Create(_model.Encoder, _encoderBackend);
                _decoderJoint = _factory.Create(_model.DecoderJoint, ComputeBackend.Cpu);

                LoadVocab();
                LoadStateShape();
                _loaded = true;
            }
            catch
            {
                // A partial load (bad file, native load failure) must not leak the native sessions,
                // and must leave a clean slate so a later retry starts fresh.
                _preprocessor?.Dispose(); _preprocessor = null;
                _encoder?.Dispose(); _encoder = null;
                _decoderJoint?.Dispose(); _decoderJoint = null;
                throw;
            }
        }
    }

    private void LoadVocab()
    {
        // Each line is "<piece> <id>" with exactly one space; U+2581 marks a leading space.
        string[] lines = File.ReadAllLines(_model.Vocab);
        var pieces = new List<(int id, string piece)>(lines.Length);
        int maxId = -1;
        foreach (string line in lines)
        {
            if (line.Length == 0) continue;
            int sep = line.LastIndexOf(' ');
            if (sep <= 0) continue;
            string piece = line[..sep].Replace('▁', ' ');
            int id = int.Parse(line[(sep + 1)..]);
            pieces.Add((id, piece));
            if (id > maxId) maxId = id;
        }

        var vocab = new string[maxId + 1];
        foreach ((int id, string piece) in pieces)
            vocab[id] = piece;

        _vocab = vocab;
        _vocabSize = vocab.Length;
        _blankId = Array.IndexOf(vocab, "<blk>");
        if (_blankId < 0)
            throw new InvalidOperationException("vocab.txt is missing the <blk> token.");
    }

    private void LoadStateShape()
    {
        int[] dims = _decoderJoint!.InputMetadata["input_states_1"].Dimensions; // [2, -1, 640]
        _stateLayers = dims[0] > 0 ? dims[0] : 2;
        _stateDim = dims[2] > 0 ? dims[2] : 640;
    }

    private static DenseTensor<T> AsDense<T>(IReadOnlyCollection<DisposableNamedOnnxValue> results, string name)
    {
        var value = results.First(r => r.Name == name);
        return (DenseTensor<T>)value.AsTensor<T>();
    }

    private static int ArgMax(float[] values)
    {
        int best = 0;
        float bestValue = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] > bestValue)
            {
                bestValue = values[i];
                best = i;
            }
        }
        return best;
    }

    public void Dispose()
    {
        _preprocessor?.Dispose();
        _encoder?.Dispose();
        _decoderJoint?.Dispose();
    }
}
