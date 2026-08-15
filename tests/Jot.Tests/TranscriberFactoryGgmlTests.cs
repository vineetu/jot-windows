using Jot.Services.Abstractions;
using Jot.Transcription;
using Jot.Transcription.Ggml;
using Jot.Transcription.Nemotron;
using Jot.Transcription.Onnx;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// Default path is ggml when GGUF + natives are present, ONNX otherwise. JOT_ENGINE=ort is
/// the emergency back-out. The hidden UseGgmlEngine flag still force-selects ggml.
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
    public void DefaultSettings_WithoutGguf_IsNotBareGgml()
    {
        ITranscriber t = Create(new JotSettings());
        Assert.IsNotType<GgmlNemotronTranscriber>(t);
        Assert.True(t is SelectingTranscriber or NemotronTranscriber or NemotronFp16Transcriber);
        if (t is SelectingTranscriber sel)
            Assert.IsNotType<GgmlNemotronTranscriber>(sel.Active);
    }

    [Fact]
    public void AutoDevice_WithoutGguf_UsesOnnxInt4()
    {
        ITranscriber t = Create(new JotSettings { TranscriptionDevice = TranscriptionDevices.Auto });
        ITranscriber inner = t is SelectingTranscriber sel ? sel.Active : t;
        Assert.IsType<NemotronTranscriber>(inner);
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
        Assert.IsNotType<SelectingTranscriber>(t);
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
    public void ApplyLanguage_OnSelecting_DoesNotThrow()
    {
        ITranscriber t = Create(new JotSettings());
        TranscriberFactory.ApplyLanguage(t, "auto");
        TranscriberFactory.ApplyLanguage(t, "el-GR");
        TranscriberFactory.ApplyLanguage(t, "en-US");
    }

    [Fact]
    public void EngineSelector_OnnxRule_IsUnchanged()
    {
        // ONNX fallback still uses the proven Auto/fp16 matrix. ggml is a factory decision.
        Assert.Equal(EngineChoice.Int4Cpu, EngineSelector.Select("Auto", false, null, false));
        Assert.Equal(EngineChoice.Fp16Dml, EngineSelector.Select("Auto", true, "GPU", true));
        Assert.Equal(EngineChoice.Fp16Dml, EngineSelector.Select("GPU (DirectML)", true, null, false));
        Assert.Equal(EngineChoice.Fp16Dml, EngineSelector.Select("GPU (Vulkan)", true, null, false));
    }
}
