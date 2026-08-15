using System;
using System.Diagnostics;
using System.IO;
using Jot.Services.Abstractions;
using Jot.Transcription;
using Jot.Transcription.Ctc;
using Jot.Transcription.Ggml;
using Jot.Transcription.Onnx;
using Jot.Vocabulary;
using Xunit;
using Xunit.Abstractions;

namespace Jot.Tests;

/// <summary>
/// Named kill: Vulkan (ggml) and DirectML (CTC spotter) in one process. The product now pins
/// the spotter to CPU whenever the GGUF is the live engine. This soak measures that pairing
/// over the planted-terms clip and fails if any pass exceeds VocabularyDeadlineMs.
/// </summary>
public class GgmlVocabularySoakTests
{
    private readonly ITestOutputHelper _out;
    public GgmlVocabularySoakTests(ITestOutputHelper output) => _out = output;

    [ModelFact("ggml-natives", "ggml-model", "installed-ctc", "audio:tts-terms.wav")]
    public void Soak_GgmlThenSpotter_StaysUnderDeadline()
    {
        string wav = Path.Combine(ModelGate.ResolvedAudioDir!, "tts-terms.wav");
        float[] samples = WavAudio.ReadMono16k(wav);
        var terms = new VocabularyStore(null);
        foreach (string t in new[] { "Nemotron", "Parakeet", "Sriram", "Okta", "Claude Code" })
            terms.Add(t);

        var settings = new JotSettings { Language = "en-US", VocabularyEnabled = true };
        var store = new FakeSettings();
        store.Current.Language = "en-US";

        var ggml = new GgmlNemotronTranscriber(
            new NemotronGgufModel(env: _ => GgmlAssets.ModelPath),
            new GgmlEngineOptions
            {
                Enabled = true,
                AttContextRight = 3,
                Backend = NativeMethods.BackendRequest.Vulkan,
                NativeDir = GgmlAssets.NativeDir,
            });
        ggml.SetLanguage("en-US");

        using var spotter = new CtcVocabularySpotter(new CtcModel(), new OnnxSessionFactory(), store);
        Assert.True(spotter.IsReady);

        // Cold ggml load + one dummy chunk (the app warm-up).
        var cold = Stopwatch.StartNew();
        ggml.WarmUp();
        cold.Stop();
        _out.WriteLine($"ggml warm-up: {cold.ElapsedMilliseconds} ms");

        spotter.Warm();

        const int deadlineMs = 4000;
        const int runs = 5;
        string? lastText = null;
        for (int i = 1; i <= runs; i++)
        {
            var decode = Stopwatch.StartNew();
            string text = ggml.TranscribeAsync(samples, 16_000).GetAwaiter().GetResult();
            decode.Stop();

            var spot = Stopwatch.StartNew();
            var hits = spotter.Spot(samples, 16_000, terms.Terms, default);
            spot.Stop();

            lastText = text;
            _out.WriteLine(
                $"run {i}: decode={decode.ElapsedMilliseconds} ms  spot={spot.ElapsedMilliseconds} ms  " +
                $"hits={hits.Count}  text={text}");

            Assert.True(spot.ElapsedMilliseconds < deadlineMs,
                $"spotter {spot.ElapsedMilliseconds} ms exceeded VocabularyDeadlineMs={deadlineMs} " +
                $"on soak run {i} — Vulkan+spotter kill criterion");
        }

        Assert.False(string.IsNullOrWhiteSpace(lastText));
        ggml.Dispose();
    }

    private sealed class FakeSettings : ISettingsStore
    {
        public JotSettings Current { get; } = new();
        public void Save() { }
        public void Reset() { }
        public event EventHandler? Changed { add { } remove { } }
    }
}
