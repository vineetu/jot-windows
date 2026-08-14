using Jot.Services.Abstractions;
using Jot.Transcription;
using Jot.Transcription.Ggml;
using Jot.Transcription.Nemotron;
using Jot.Transcription.Onnx;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// Phase 3 bar: the shipping ONNX path is still selected when the flag is off. The ggml engine
/// is a fourth factory choice, not a change to EngineSelector.
/// </summary>
public class TranscriberFactoryGgmlTests
{
    private static ITranscriber Create(JotSettings s, Func<string, string?>? env = null)
    {
        env ??= _ => null; // ignore process env so a leftover JOT_ENGINE can't flip the default
        return TranscriberFactory.Create(
            s,
            new NemotronModel(directory: @"C:\nope\int4"),
            new NemotronFp16Model(directory: @"C:\nope\fp16"),
            new NemotronGgufModel(directory: @"C:\nope\gguf", env: _ => null),
            new OnnxSessionFactory(),
            env: env);
    }

    [Fact]
    public void DefaultSettings_SelectOnnx_NotGgml()
    {
        ITranscriber t = Create(new JotSettings());
        Assert.IsNotType<GgmlNemotronTranscriber>(t);
        Assert.True(t is NemotronTranscriber or NemotronFp16Transcriber);
    }

    [Fact]
    public void AutoDevice_StillUsesEngineSelector_WhenFlagOff()
    {
        // No fp16, no verdict → Int4Cpu. Same as before this branch.
        ITranscriber t = Create(new JotSettings { TranscriptionDevice = TranscriptionDevices.Auto });
        Assert.IsType<NemotronTranscriber>(t);
    }

    [Fact]
    public void HiddenFlag_SelectsGgml()
    {
        ITranscriber t = Create(new JotSettings { UseGgmlEngine = true });
        Assert.IsType<GgmlNemotronTranscriber>(t);
    }

    [Fact]
    public void EnvGgml_SelectsGgml()
    {
        ITranscriber t = Create(new JotSettings(), k => k == "JOT_ENGINE" ? "ggml" : null);
        Assert.IsType<GgmlNemotronTranscriber>(t);
    }

    [Fact]
    public void EnvOrt_KeepsOnnx_EvenIfSettingIsOn()
    {
        ITranscriber t = Create(
            new JotSettings { UseGgmlEngine = true },
            k => k == "JOT_ENGINE" ? "ort" : null);
        Assert.IsNotType<GgmlNemotronTranscriber>(t);
    }

    [Fact]
    public void FlagOn_MissingGguf_IsNotInstalled_DoesNotThrow()
    {
        ITranscriber t = Create(new JotSettings { UseGgmlEngine = true });
        Assert.False(t.IsModelInstalled);
    }

    [Fact]
    public void ApplyLanguage_OnGgml_DoesNotThrow()
    {
        ITranscriber t = Create(new JotSettings { UseGgmlEngine = true });
        TranscriberFactory.ApplyLanguage(t, "auto");
        TranscriberFactory.ApplyLanguage(t, "el-GR");
        TranscriberFactory.ApplyLanguage(t, "en-US");
    }

    [Fact]
    public void EngineSelector_IsUnchanged_ByThisFactory()
    {
        // The factory must not start routing Auto to ggml. That's Phase 4.
        Assert.Equal(EngineChoice.Int4Cpu, EngineSelector.Select("Auto", false, null, false));
        Assert.Equal(EngineChoice.Fp16Dml, EngineSelector.Select("Auto", true, "GPU", true));
        Assert.Equal(EngineChoice.Fp16Dml, EngineSelector.Select("GPU (DirectML)", true, null, false));
    }
}
