using System.IO;
using Jot.Services.Abstractions;
using Jot.Text;
using Jot.Transcription;
using Jot.Transcription.Ggml;
using Jot.Transcription.Granite;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// The factory now returns a LANGUAGE ROUTER, not an engine: English goes to Granite and everything
/// else to ggml. These facts pin the ggml side of that — with no Granite assets on disk, every
/// language including English must still land on ggml. JOT_ENGINE=ort is ignored (ONNX Nemotron is
/// gone), and a missing GGUF is "not installed", never a throw and never a silent ONNX success.
/// </summary>
public class TranscriberFactoryGgmlTests
{
    /// <summary>Deliberately absent Granite + punctuation assets, so these facts measure the
    /// fallback rather than whatever happens to be installed on the machine running them.</summary>
    private static GraniteModel NoGranite => new(directory: @"C:\nope\granite");
    private static PunctCapSegModel NoPunct => new(directory: @"C:\nope\punct");

    private static ITranscriber Create(JotSettings s, Func<string, string?>? env = null)
    {
        env ??= _ => null; // ignore process env so a leftover JOT_ENGINE can't flip the default
        return TranscriberFactory.Create(
            s,
            new NemotronGgufModel(directory: @"C:\nope\gguf", env: _ => null),
            NoGranite,
            NoPunct,
            env: env);
    }

    private static ITranscriber Engine(ITranscriber t) =>
        Assert.IsType<LanguageRoutedTranscriber>(t).Selected();

    [Fact]
    public void DefaultSettings_RoutesToGgml_EvenWithoutAssets()
    {
        ITranscriber t = Create(new JotSettings());
        Assert.IsType<GgmlNemotronTranscriber>(Engine(t));
        Assert.False(t.IsModelInstalled);
    }

    [Fact]
    public void English_RoutesToGgml_WhenGraniteIsNotInstalled()
    {
        // The fallback that keeps a partial install working: no Granite download means English
        // dictation must keep working on the multilingual engine, not fail.
        ITranscriber t = Create(new JotSettings { Language = "en-US" });
        Assert.IsType<GgmlNemotronTranscriber>(Engine(t));
    }

    [Fact]
    public void EnvOrt_StillRoutesToGgml()
    {
        ITranscriber t = Create(new JotSettings(), k => k == "JOT_ENGINE" ? "ort" : null);
        Assert.IsType<GgmlNemotronTranscriber>(Engine(t));
    }

    [Fact]
    public void EnvOrt_LogsThatItIsIgnored()
    {
        string? line = null;
        TranscriberFactory.Create(
            new JotSettings(),
            new NemotronGgufModel(directory: @"C:\nope\gguf", env: _ => null),
            NoGranite, NoPunct,
            log: msg => line ??= msg,
            env: k => k == "JOT_ENGINE" ? "ort" : null);
        Assert.Contains("engine: ggml", line);
        Assert.Contains("JOT_ENGINE=ort ignored", line);
    }

    [Fact]
    public void ApplyLanguage_ThroughTheRouter_DoesNotThrow()
    {
        ITranscriber t = Create(new JotSettings());
        TranscriberFactory.ApplyLanguage(t, "auto");
        TranscriberFactory.ApplyLanguage(t, "el-GR");
        TranscriberFactory.ApplyLanguage(t, "en-US");
    }

    [Fact]
    public void LogLine_NamesGgmlBackendAndGraniteAvailability()
    {
        string? line = null;
        TranscriberFactory.Create(
            new JotSettings { TranscriptionDevice = TranscriptionDevices.Auto },
            new NemotronGgufModel(directory: @"C:\nope\gguf", env: _ => null),
            NoGranite, NoPunct,
            log: msg => line ??= msg,
            env: _ => null);
        Assert.Contains("engine: ggml", line);
        Assert.Contains("backend=Vulkan", line);
        // Whether the English engine is available is the first thing you need from a bug report
        // about casing or punctuation, so it belongs on the same launch line.
        Assert.Contains("granite (english=False, punct=False)", line);
    }
}
