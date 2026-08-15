using Jot.Services.Abstractions;
using Jot.Transcription;
using Jot.Transcription.Ggml;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// The factory always returns ggml. JOT_ENGINE=ort is ignored (ONNX Nemotron is gone).
/// Missing GGUF is "not installed", never a throw and never a silent ONNX success.
/// </summary>
public class TranscriberFactoryGgmlTests
{
    private static ITranscriber Create(JotSettings s, Func<string, string?>? env = null)
    {
        env ??= _ => null; // ignore process env so a leftover JOT_ENGINE can't flip the default
        return TranscriberFactory.Create(
            s,
            new NemotronGgufModel(directory: @"C:\nope\gguf", env: _ => null),
            env: env);
    }

    [Fact]
    public void DefaultSettings_ReturnsGgml_EvenWithoutAssets()
    {
        ITranscriber t = Create(new JotSettings());
        Assert.IsType<GgmlNemotronTranscriber>(t);
        Assert.False(t.IsModelInstalled);
    }

    [Fact]
    public void EnvOrt_StillReturnsGgml()
    {
        ITranscriber t = Create(new JotSettings(), k => k == "JOT_ENGINE" ? "ort" : null);
        Assert.IsType<GgmlNemotronTranscriber>(t);
    }

    [Fact]
    public void EnvOrt_LogsThatItIsIgnored()
    {
        string? line = null;
        TranscriberFactory.Create(
            new JotSettings(),
            new NemotronGgufModel(directory: @"C:\nope\gguf", env: _ => null),
            log: msg => line = msg,
            env: k => k == "JOT_ENGINE" ? "ort" : null);
        Assert.Contains("engine: ggml", line);
        Assert.Contains("JOT_ENGINE=ort ignored", line);
    }

    [Fact]
    public void ApplyLanguage_OnGgml_DoesNotThrow()
    {
        ITranscriber t = Create(new JotSettings());
        TranscriberFactory.ApplyLanguage(t, "auto");
        TranscriberFactory.ApplyLanguage(t, "el-GR");
        TranscriberFactory.ApplyLanguage(t, "en-US");
    }

    [Fact]
    public void LogLine_NamesGgmlBackend()
    {
        string? line = null;
        TranscriberFactory.Create(
            new JotSettings { TranscriptionDevice = TranscriptionDevices.Auto },
            new NemotronGgufModel(directory: @"C:\nope\gguf", env: _ => null),
            log: msg => line = msg,
            env: _ => null);
        Assert.Contains("engine: ggml", line);
        Assert.Contains("backend=Vulkan", line);
    }
}
