using System;
using Jot.Services;
using Jot.Services.Abstractions;
using Xunit;

namespace Jot.Tests;

/// <summary>The feedback report's privacy promises are load-bearing: dictated text never leaves the
/// machine, the username never appears, and the report stays under the API-safe size cap.</summary>
public class FeedbackReportTests
{
    [Fact]
    public void Scrub_RedactsDictatedText_KeepsDiagnosticShape()
    {
        string line = "2026-07-24 13:53:19  INFO   SAVED: \"my secret dictated words\" (24 chars); library items=4";
        string scrubbed = FeedbackReport.Scrub(line, "vinee");
        Assert.DoesNotContain("secret", scrubbed);
        Assert.Contains("SAVED: \"[redacted 24 chars]\"", scrubbed);
        Assert.Contains("library items=4", scrubbed); // the diagnostic part survives
    }

    [Fact]
    public void Scrub_RedactsQuotesInsideTranscript()
    {
        // A transcript containing its own quotes must still be fully redacted (last-quote matching).
        string line = "INFO   SAVED: \"he said \"hello\" twice\" (20 chars)";
        string scrubbed = FeedbackReport.Scrub(line, null);
        Assert.DoesNotContain("hello", scrubbed);
    }

    [Fact]
    public void Scrub_HidesUsernameInPaths()
    {
        string line = @"data relocated from C:\Users\vinee\AppData\Local\Jot into the package container";
        string scrubbed = FeedbackReport.Scrub(line, "vinee");
        Assert.DoesNotContain(@"\vinee", scrubbed);
        Assert.Contains(@"\<user>\", scrubbed);
    }

    [Fact]
    public void Scrub_LeavesOrdinaryLinesAlone()
    {
        string line = "2026-07-24 12:34:29  INFO   engine: Fp16Dml (device=Auto, verdict=GPU)";
        Assert.Equal(line, FeedbackReport.Scrub(line, "someone"));
    }

    [Fact]
    public void Build_ContainsTheEssentials_AndRespectsTheCap()
    {
        string report = FeedbackReport.Build(new JotSettings(), "test note — please ignore");
        Assert.StartsWith(FeedbackReport.SubjectTag, report);
        Assert.Contains("test note — please ignore", report);
        Assert.Contains("-- app / machine --", report);
        Assert.Contains("-- engine --", report);
        Assert.True(report.Length <= FeedbackReport.MaxChars + 200, $"report too big: {report.Length}");
        // The username must never appear in path form anywhere in the report.
        Assert.DoesNotContain($@"\{Environment.UserName}\", report, StringComparison.OrdinalIgnoreCase);
    }
}
