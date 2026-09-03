using System.Threading;
using System.Threading.Tasks;
using Jot.Transcription;
using Jot.Transcription.Ggml;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// Finding the concrete ggml engine through whatever wrappers Create put around it.
///
/// This exists because the failure is SILENT. English routing wrapped ggml in
/// LanguageRoutedTranscriber, the leftover-ONNX cleanup still tested
/// `transcriber is GgmlNemotronTranscriber`, and it simply stopped matching — no crash, no log,
/// just ~2 GB of dead int4/fp16 files left on every upgraded install. Nothing failed a test
/// because nothing asserted the unwrap.
///
/// No model or natives are touched: the paths deliberately do not exist, since what is under test
/// is the type walk, not whether an engine can load.
/// </summary>
public class TranscriberFactoryUnwrapTests
{
    private static GgmlNemotronTranscriber MakeGgml() =>
        new(new NemotronGgufModel(directory: @"C:\nope\gguf-missing", env: _ => null),
            new GgmlEngineOptions());

    private sealed class FakeEngine : ITranscriber
    {
        public bool IsModelInstalled => true;
        public Task<string> TranscribeAsync(float[] samples, int sampleRate,
                                            CancellationToken ct = default) => Task.FromResult("");
        public void WarmUp() { }
    }

    [Fact]
    public void Finds_the_engine_through_the_language_router()
    {
        var ggml = MakeGgml();
        var routed = new LanguageRoutedTranscriber(english: new FakeEngine(), other: ggml);

        Assert.Same(ggml, TranscriberFactory.GgmlEngineOf(routed));
    }

    [Fact]
    public void Finds_the_engine_when_it_is_not_wrapped()
    {
        var ggml = MakeGgml();

        Assert.Same(ggml, TranscriberFactory.GgmlEngineOf(ggml));
    }

    [Fact]
    public void Returns_null_when_ggml_is_not_the_engine()
    {
        // English-only routing with no ggml behind it: cleanup must NOT arm, because the leftover
        // ONNX folders would then be deleted without a verified replacement engine on disk.
        var routed = new LanguageRoutedTranscriber(english: new FakeEngine(), other: new FakeEngine());

        Assert.Null(TranscriberFactory.GgmlEngineOf(routed));
        Assert.Null(TranscriberFactory.GgmlEngineOf(new FakeEngine()));
    }
}
