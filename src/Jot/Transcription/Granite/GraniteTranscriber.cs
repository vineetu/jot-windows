using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Jot.Transcription.Onnx;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jot.Transcription.Granite;

/// <summary>
/// Granite Speech 5.0 TurboCTC on ONNX Runtime — Jot's ENGLISH engine. Everything else stays on
/// ggml/Nemotron, which is multilingual; this model is English-only by construction.
///
/// CPU on purpose, not by fallback. The int8 graph runs at ~25x realtime on the CPU EP, which is
/// far inside budget, so there is nothing to gain from a GPU EP and a great deal to lose: the only
/// ORT GPU provider available is DirectML, and DirectML alongside the live Vulkan ggml engine is a
/// named kill in this codebase (it took the vocabulary spotter from ~1 s to 15.7 s against a 4 s
/// deadline and silently dropped corrections). See <see cref="OnnxSessionFactory"/>.
///
/// Output is lowercase and unpunctuated by design — the CTC head has no casing or punctuation in
/// its vocabulary. <see cref="Text.PunctCapSeg"/> restores both downstream; this class deliberately
/// returns the raw model output so the two stages stay independently testable.
/// </summary>
public sealed class GraniteTranscriber : ITranscriber, IStreamingTranscriber, IDisposable
{
    private const int RequiredSampleRate = 16_000;
    private const string InputName = "input_features";

    private readonly GraniteModel _model;
    private readonly OnnxSessionFactory _sessions;
    private readonly GraniteMelFrontend _frontend = new();
    private readonly object _loadGate = new();

    private InferenceSession? _session;
    private GraniteTokens? _tokens;

    public GraniteTranscriber(GraniteModel model, OnnxSessionFactory sessions)
    {
        _model = model;
        _sessions = sessions;
    }

    public bool IsModelInstalled => _model.IsInstalled;

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
                throw new ArgumentException(
                    $"Granite expects {RequiredSampleRate} Hz mono audio, got {sampleRate} Hz.",
                    nameof(sampleRate));
            if (samples.Length == 0) return string.Empty;
            return Run(samples);
        }, ct);

    public void WarmUp()
    {
        if (!IsModelInstalled) return;
        // Rethrow rather than swallow: a warm-up that "succeeds" while the graph is unloadable
        // would surface as a failure on the user's first dictation instead of at startup, where
        // EngineSelector can still fall back to ggml.
        Run(new float[RequiredSampleRate / 2]);
    }

    /// <summary>Features -> encoder -> CTC greedy collapse -> text, for a whole buffer.</summary>
    private string Run(float[] samples)
    {
        EnsureLoaded();
        float[] feats = _frontend.Compute(samples, out int rows);
        if (rows == 0) return string.Empty;

        var input = new DenseTensor<float>(feats, [1, rows, GraniteMelFrontend.InputDim]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            _session!.Run([NamedOnnxValue.CreateFromTensor(InputName, input)]);

        foreach (DisposableNamedOnnxValue output in outputs)
        {
            Tensor<float> logits = output.AsTensor<float>();
            return _tokens!.Decode(CollapseGreedy(logits));
        }
        return string.Empty;
    }

    /// <summary>
    /// Standard CTC greedy collapse over <c>[1, frames, vocab]</c>: argmax per frame, drop repeats,
    /// then drop blanks. The repeat-collapse must come BEFORE the blank filter — a blank between two
    /// identical tokens is exactly what encodes a genuine doubled token ("bookkeeper"), and filtering
    /// blanks first would silently merge those into one.
    /// </summary>
    internal static List<int> CollapseGreedy(Tensor<float> logits)
    {
        int frames = logits.Dimensions[1];
        int vocab = logits.Dimensions[2];
        var ids = new List<int>(frames);
        int previous = -1;

        for (int t = 0; t < frames; t++)
        {
            int best = 0;
            float bestScore = float.NegativeInfinity;
            for (int v = 0; v < vocab; v++)
            {
                float score = logits[0, t, v];
                if (score > bestScore) { bestScore = score; best = v; }
            }
            if (best != previous && best != GraniteTokens.BlankId) ids.Add(best);
            previous = best;
        }
        return ids;
    }

    private void EnsureLoaded()
    {
        if (_session is not null) return;
        lock (_loadGate)
        {
            if (_session is not null) return;
            if (!_model.IsInstalled)
                throw new InvalidOperationException(
                    $"Granite model is not installed at {_model.Directory}.");
            _tokens = GraniteTokens.Load(_model.Vocab);
            _session = _sessions.Create(_model.Graph, ComputeBackend.Cpu);
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }

    /// <summary>
    /// One in-flight utterance.
    ///
    /// This model CANNOT be fed incrementally, and the reason is worth stating because the obvious
    /// implementation looks right and is wrong. Two independent things block it:
    ///   * the front-end floors every log-mel at (utterance max - 8 dB), so the features for audio
    ///     already seen change when louder audio arrives later;
    ///   * the encoder's attention blocks are non-causal within a block, so the trailing partial
    ///     block's outputs revise until the block completes.
    /// So each update re-runs the whole buffer, and the transcript is allowed to REVISE rather than
    /// only grow. <see cref="IStreamingSession.Accept"/> returns the full transcript each time
    /// (not a delta), so revision needs no interface change.
    ///
    /// The cost of re-running grows with the utterance, so partial updates are self-throttled: after
    /// a pass costing d, the next partial is skipped until d has elapsed. That keeps short dictations
    /// (the common case) responsive without letting a long one saturate a core producing captions
    /// nobody reads. <see cref="Finish"/> ALWAYS runs a final exact pass, so the delivered transcript
    /// never depends on how many partials happened to run.
    /// </summary>
    public sealed class Session : IStreamingSession
    {
        private readonly GraniteTranscriber _t;
        private readonly List<float> _buffer = new(RequiredSampleRate * 8);
        private readonly Stopwatch _sinceLastPass = new();

        private string _text = string.Empty;
        private TimeSpan _lastPassCost = TimeSpan.Zero;
        private bool _finished;

        internal Session(GraniteTranscriber t) => _t = t;

        /// <summary>True — see the class remarks. Consumers that commit partial text must not.</summary>
        public bool RevisesText => true;

        public string Accept(float[] newSamples)
        {
            ObjectDisposedException.ThrowIf(_finished, this);
            _buffer.AddRange(newSamples);

            if (_sinceLastPass.IsRunning && _sinceLastPass.Elapsed < _lastPassCost)
                return _text;   // still paying for the previous pass; keep the last partial

            _sinceLastPass.Restart();
            _text = _t.Run(_buffer.ToArray());
            _lastPassCost = _sinceLastPass.Elapsed;
            _sinceLastPass.Restart();
            return _text;
        }

        public string Finish()
        {
            if (_finished) return _text;
            _finished = true;
            if (_buffer.Count > 0) _text = _t.Run(_buffer.ToArray());
            return _text;
        }
    }
}
