using System;
using System.Collections.Generic;
using System.IO;
using Jot.Transcription.Onnx;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jot.Transcription.Ctc;

/// <summary>
/// Minimal one-shot wrapper over the Parakeet CTC 110M ONNX export.
/// Graph contract (read off the export, not assumed):
///   in  audio_signal float32 [batch, 80, frames]   — per_feature-normalized log-mel, FEATURE-major
///   in  length       int64   [batch]               — UNsubsampled frame count
///   out logprobs     float32 [batch, frames/8, 1025] — already log-softmaxed
/// Backend is the CALLER's choice (D10a amends D1): the CPU pin protected live captions, and this pass
/// runs post-stop when there are none. <see cref="OnnxSessionFactory"/> falls back to CPU on its own.
/// </summary>
internal sealed class CtcEncoder : IDisposable
{
    /// <summary>FastConformer subsampling; also the divisor sherpa applies to <c>length</c>.</summary>
    public const int SubsamplingFactor = 8;

    /// <summary>Seconds of audio per encoder output frame — 10 ms hop x 8. Detection timings hang off this.</summary>
    public const double FrameSeconds = 0.010 * SubsamplingFactor;

    private readonly InferenceSession _session;
    private readonly string _inFeatures, _inLength, _outLogProbs;

    public int VocabSize { get; }

    public CtcEncoder(string modelPath, OnnxSessionFactory factory,
                      ComputeBackend backend = ComputeBackend.Cpu)
    {
        _session = factory.Create(modelPath, backend);

        var inputs = new List<string>(_session.InputMetadata.Keys);
        var outputs = new List<string>(_session.OutputMetadata.Keys);
        if (inputs.Count < 2 || outputs.Count < 1)
            throw new InvalidDataException($"unexpected CTC graph: {inputs.Count} inputs, {outputs.Count} outputs");
        _inFeatures = inputs[0];
        _inLength = inputs[1];
        _outLogProbs = outputs[0];

        var dims = _session.OutputMetadata[_outLogProbs].Dimensions;
        VocabSize = dims.Length == 3 && dims[2] > 0 ? dims[2] : 0;
    }

    /// <summary>Runs the encoder+CTC head. Returns flat [frames, vocab] log-probs.</summary>
    public float[] Run(float[] featureMajor, int frames, out int outFrames, out int vocab)
    {
        var feats = new DenseTensor<float>(featureMajor, [1, CtcMelFrontend.NMels, frames]);
        var len = new DenseTensor<long>(new long[] { frames }, [1]);

        using var results = _session.Run(
        [
            NamedOnnxValue.CreateFromTensor(_inFeatures, feats),
            NamedOnnxValue.CreateFromTensor(_inLength, len),
        ]);

        var t = results[0].AsTensor<float>();
        var shape = t.Dimensions;              // [1, outFrames, vocab]
        vocab = shape[2];
        // Trust length/8 over the tensor's own frame count: sherpa decodes exactly that many frames,
        // and a padded graph can return more.
        outFrames = Math.Min(shape[1], frames / SubsamplingFactor);
        return t.ToArray();
    }

    public void Dispose() => _session.Dispose();
}
