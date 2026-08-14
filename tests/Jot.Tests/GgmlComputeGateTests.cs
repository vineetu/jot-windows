using Jot.Transcription.Ggml;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// Official 0.1.3 hangs (~343 s) if two computes overlap on one loaded model. The gate is what
/// stops every caller — including two IStreamingSession objects on the same transcriber — from
/// tripping that.
/// </summary>
public class GgmlComputeGateTests
{
    [Fact]
    public async Task TwoThreads_NeverOverlap()
    {
        var gate = new GgmlComputeGate();
        int inFlight = 0;
        int overlapped = 0;
        var start = new TaskCompletionSource();

        Task Worker() => Task.Run(async () =>
        {
            await start.Task;
            gate.Run(() =>
            {
                int n = Interlocked.Increment(ref inFlight);
                if (n > 1) Interlocked.Increment(ref overlapped);
                Thread.Sleep(80);
                Interlocked.Decrement(ref inFlight);
            });
        });

        Task a = Worker();
        Task b = Worker();
        start.SetResult();
        await Task.WhenAll(a, b);

        Assert.Equal(0, overlapped);
        Assert.Equal(1, gate.MaxInFlight);
    }

    [Fact]
    public void Exception_ReleasesTheGate()
    {
        var gate = new GgmlComputeGate();
        Assert.Throws<InvalidOperationException>(() => gate.Run(() => throw new InvalidOperationException("boom")));
        int ran = 0;
        gate.Run(() => ran++);
        Assert.Equal(1, ran);
        Assert.Equal(1, gate.MaxInFlight);
    }
}
