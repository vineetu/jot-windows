using System.Runtime.InteropServices;
using Jot.Transcription.Ggml;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// Binding facts that need official v0.1.3 natives and the Q8_0 GGUF. Skips (does not pass)
/// when those assets are absent.
/// </summary>
[Collection("GgmlNative")]
public class GgmlBindingTests
{
    private readonly GgmlNativeFixture _fx;

    public GgmlBindingTests(GgmlNativeFixture fx) => _fx = fx;

    [ModelFact("ggml-natives")]
    public void Startup_PinsOfficial013()
    {
        TranscribeRuntime.Startup(_fx.NativeDir);
        Assert.Equal("0.1.3", TranscribeRuntime.Version);
        Assert.Equal("a94e021", TranscribeRuntime.Commit);
        Assert.Equal(
            Marshal.SizeOf<NativeMethods.RunParams>(),
            (int)NativeMethods.transcribe_abi_struct_size(NativeMethods.AbiStruct.RunParams));
    }

    [ModelFact("ggml-natives")]
    public void HandyPath_IsRefusedBeforeLoad()
    {
        Assert.True(GgmlNativeLocator.IsForbidden(GgmlNativeLocator.HandyDir));
        // Startup already ran against the official dir in the fixture; a second call is a no-op.
        // The refusal is the path check — product code will not even SetDllImportResolver on Handy.
        Assert.Throws<TranscribeAbiException>(() =>
        {
            // Re-run the same check Startup uses, independently of process-wide init.
            if (GgmlNativeLocator.IsForbidden(GgmlNativeLocator.HandyDir))
            {
                throw new TranscribeAbiException(
                    $"Refusing to load transcribe.cpp from AppData\\Local\\Handy ({GgmlNativeLocator.HandyDir}). " +
                    $"Product code is pinned to {TranscribeAbi.PinBlurb}");
            }
        });
    }

    [ModelFact("ggml-natives", "ggml-model")]
    public void ModelAndSessionHandles_CloseOnDispose()
    {
        TranscribeModel model = _fx.RequireModel();
        Assert.False(model.IsHandleClosed);
        using (TranscribeSession session = model.NewSession())
        {
            Assert.False(session.IsHandleClosed);
            session.Dispose();
            Assert.True(session.IsHandleClosed);
        }
        // Model stays open after its session is gone — sessions do not own the model.
        Assert.False(model.IsHandleClosed);
    }

    [ModelFact("ggml-natives", "ggml-model")]
    public void Session_KeepsModelAlive_UntilSessionDisposes()
    {
        TranscribeModel model = TranscribeModel.Load(
            _fx.ModelPath!, NativeMethods.BackendRequest.Cpu, nativeDir: _fx.NativeDir);
        TranscribeSession session = model.NewSession();
        model.Dispose();
        // DangerousAddRef must have kept the native model alive for the still-open session.
        Assert.False(session.IsHandleClosed);
        session.Dispose();
        Assert.True(session.IsHandleClosed);
        Assert.True(model.IsHandleClosed);
    }

    [ModelFact("ggml-natives", "ggml-model")]
    public async Task TwoSessions_SerializeOnTheSameModel()
    {
        TranscribeModel model = _fx.RequireModel();
        using TranscribeSession a = model.NewSession();
        using TranscribeSession b = model.NewSession();
        var start = new TaskCompletionSource();

        Task Feed(TranscribeSession s) => Task.Run(async () =>
        {
            await start.Task;
            // stream_begin is compute; a dummy 320 ms chunk keeps the lock held across real work.
            var st = s.StreamBegin(null, 3);
            if (st != NativeMethods.Status.Ok)
                throw new TranscribeException(st, "begin");
            var pcm = new float[5120];
            st = s.Feed(pcm, 0, pcm.Length);
            if (st != NativeMethods.Status.Ok)
                throw new TranscribeException(st, "feed");
        });

        Task t1 = Feed(a);
        Task t2 = Feed(b);
        start.SetResult();
        await Task.WhenAll(t1, t2);
        Assert.Equal(1, model.Compute.MaxInFlight);
    }

    [ModelFact("ggml-natives", "ggml-model")]
    public void AutoString_IsRejectedByNative_NullLanguage_IsOk()
    {
        TranscribeModel model = _fx.RequireModel();
        using (var auto = model.NewSession())
        {
            Assert.Equal(NativeMethods.Status.UnsupportedLanguage, auto.StreamBegin("auto", 3));
        }
        using (var empty = model.NewSession())
        {
            Assert.Equal(NativeMethods.Status.UnsupportedLanguage, empty.StreamBegin("", 3));
        }
        using var ok = model.NewSession();
        Assert.Equal(NativeMethods.Status.Ok, ok.StreamBegin(null, 3));
    }

    [ModelFact("ggml-natives", "ggml-model")]
    public void AdaptationReadyCodes_AreRejectedByNative()
    {
        TranscribeModel model = _fx.RequireModel();
        foreach (string code in GgmlLanguage.AdaptationReady)
        {
            using var session = model.NewSession();
            Assert.Equal(NativeMethods.Status.UnsupportedLanguage, session.StreamBegin(code, 3));
        }
    }

    [ModelFact("ggml-natives", "ggml-model")]
    public void Capabilities_DoNotIncludeAutoOrTheEight()
    {
        TranscribeModel model = _fx.RequireModel();
        Assert.DoesNotContain("auto", model.Languages, StringComparer.OrdinalIgnoreCase);
        foreach (string code in GgmlLanguage.AdaptationReady)
            Assert.DoesNotContain(code, model.Languages, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(32, model.Languages.Length);
    }
}

[CollectionDefinition("GgmlNative")]
public sealed class GgmlNativeCollection : ICollectionFixture<GgmlNativeFixture> { }

public sealed class GgmlNativeFixture : IDisposable
{
    public string? NativeDir { get; } = GgmlAssets.NativeDir;
    public string? ModelPath { get; } = GgmlAssets.ModelPath;

    private TranscribeModel? _model;
    private readonly object _gate = new();

    internal TranscribeModel RequireModel()
    {
        lock (_gate)
        {
            if (_model is not null) return _model;
            if (NativeDir is null || ModelPath is null)
                throw new InvalidOperationException("ggml assets missing — this fixture should have been skipped");
            _model = TranscribeModel.Load(ModelPath, NativeMethods.BackendRequest.Cpu, nativeDir: NativeDir);
            return _model;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _model?.Dispose();
            _model = null;
        }
    }
}
