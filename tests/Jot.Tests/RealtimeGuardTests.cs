using Jot.Recording;
using Xunit;

namespace Jot.Tests;

/// <summary>All three gates must hold — a single slow chunk or a cold start must never kill captions.</summary>
public class RealtimeGuardTests
{
    private static readonly RealtimeGuard Guard = new(); // defaults: 5 s audio, RTF 1.0, 2 s backlog

    [Fact]
    public void Degrades_WhenAllThreeGatesHold()
    {
        Assert.True(Guard.ShouldDegrade(audioFedSeconds: 6, processingSeconds: 7, backlogSeconds: 3));
    }

    [Fact]
    public void ColdStart_TooLittleAudio_NeverDegrades()
    {
        // Even catastrophic RTF + backlog: below the audio floor it's not a fair judgment yet.
        Assert.False(Guard.ShouldDegrade(audioFedSeconds: 4.9, processingSeconds: 60, backlogSeconds: 30));
    }

    [Theory]
    [InlineData(10, 9.9, false)]  // RTF just under 1 → keeping up (barely) → keep streaming
    [InlineData(10, 10.0, true)]  // RTF exactly 1 → can only fall behind
    [InlineData(10, 15.0, true)]
    public void RtfThreshold_IsTheBoundary(double fed, double spent, bool expected)
    {
        Assert.Equal(expected, Guard.ShouldDegrade(fed, spent, backlogSeconds: 2));
    }

    [Theory]
    [InlineData(1.9, false)] // behind on compute but the backlog symptom hasn't materialized
    [InlineData(2.0, true)]
    public void BacklogGate_RequiresRealPileUp(double backlog, bool expected)
    {
        Assert.Equal(expected, Guard.ShouldDegrade(audioFedSeconds: 10, processingSeconds: 12, backlogSeconds: backlog));
    }

    [Fact]
    public void LoweredRtfThreshold_ForTesting_FiresEarly()
    {
        var testGuard = new RealtimeGuard(rtfThreshold: 0.01); // the JOT_DEGRADE_RTF debug override
        Assert.True(testGuard.ShouldDegrade(audioFedSeconds: 6, processingSeconds: 0.1, backlogSeconds: 2));
    }
}
