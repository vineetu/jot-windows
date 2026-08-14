using Jot.Transcription.Ggml;
using Xunit;

namespace Jot.Tests;

/// <summary>The pin is the product. A wrong DLL must refuse loudly, not bind 0.2.0 layouts.</summary>
public class TranscribeAbiTests
{
    [Fact]
    public void MatchingPin_DoesNotThrow()
    {
        TranscribeAbi.CheckVersion("0.1.3", "a94e021");
        TranscribeAbi.CheckContract("0.1.3", "86b16dd97ad1cb58");
        TranscribeAbi.CheckStructSize("RunParams", 64, 64);
        TranscribeAbi.CheckExports(["transcribe_version"], _ => true);
    }

    [Theory]
    [InlineData("0.2.0", "a94e021")]
    [InlineData("0.1.3", "unknown")]
    [InlineData("0.1.3", "deadbeef")]
    [InlineData("", "")]
    public void VersionOrCommitMismatch_ThrowsWithPin(string version, string commit)
    {
        var ex = Assert.Throws<TranscribeAbiException>(() => TranscribeAbi.CheckVersion(version, commit));
        Assert.Contains("0.1.3", ex.Message);
        Assert.Contains("a94e021", ex.Message);
        Assert.Contains("Handy", ex.Message);
    }

    [Fact]
    public void HandyUnknownCommit_IsAnAbiMismatch()
    {
        // Round 1: Handy ships version 0.1.3 and commit "unknown". That is not the official pin.
        var ex = Assert.Throws<TranscribeAbiException>(() => TranscribeAbi.CheckVersion("0.1.3", "unknown"));
        Assert.Contains("commit='unknown'", ex.Message);
    }

    [Fact]
    public void ContractMismatch_Throws()
    {
        var ex = Assert.Throws<TranscribeAbiException>(
            () => TranscribeAbi.CheckContract("0.2.0", "ffffffff"));
        Assert.Contains("86b16dd97ad1cb58", ex.Message);
    }

    [Fact]
    public void MissingContract_IsIgnored()
    {
        TranscribeAbi.CheckContract(null, null);
        TranscribeAbi.CheckContract("", "");
    }

    [Fact]
    public void StructSizeMismatch_NamesTheField()
    {
        var ex = Assert.Throws<TranscribeAbiException>(
            () => TranscribeAbi.CheckStructSize("RunParams", 64, 72));
        Assert.Contains("RunParams", ex.Message);
        Assert.Contains("72", ex.Message);
        Assert.Contains("64", ex.Message);
        Assert.Contains("diarize", ex.Message);
    }

    [Fact]
    public void NativeSizeZero_IsUnknownStructId()
    {
        var ex = Assert.Throws<TranscribeAbiException>(
            () => TranscribeAbi.CheckStructSize("SpeakerSegment", 16, 0));
        Assert.Contains("SpeakerSegment", ex.Message);
        Assert.Contains("unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingExports_AreListed()
    {
        var ex = Assert.Throws<TranscribeAbiException>(
            () => TranscribeAbi.CheckExports(
                ["transcribe_version", "transcribe_raw_text", "transcribe_diarize"],
                name => name == "transcribe_version"));
        Assert.Contains("transcribe_raw_text", ex.Message);
        Assert.Contains("transcribe_diarize", ex.Message);
        Assert.DoesNotContain("transcribe_version,", ex.Message + ",");
    }
}
