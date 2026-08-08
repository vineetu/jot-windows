using Jot.Cli;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// A bare subtag must never reach the engine APIs: NemotronLocales.Normalize("es") returns "en-US" and
/// TryGetSlot("es") falls back to the en-US slot, so an un-canonicalized "--language es" would
/// transcribe Spanish audio as English and report success.
/// </summary>
public class CliLanguageTests
{
    [Theory]
    [InlineData("en", "en-US")]
    [InlineData("es", "es-ES")]
    [InlineData("pt", "pt-BR")]
    [InlineData("fr", "fr-FR")]
    [InlineData("de", "de-DE")]
    public void BareSubtagResolvesToTheFirstLocaleInDeclarationOrder(string input, string expected)
    {
        Assert.True(CliLanguage.TryCanonicalize(input, out string code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData("de-DE", "de-DE")]
    [InlineData("DE-de", "de-DE")]
    [InlineData("pt-PT", "pt-PT")]
    [InlineData(" en-GB ", "en-GB")]
    public void ExactCodeWinsCaseInsensitively(string input, string expected)
    {
        Assert.True(CliLanguage.TryCanonicalize(input, out string code));
        Assert.Equal(expected, code);
    }

    [Fact]
    public void AutoPassesThrough()
    {
        Assert.True(CliLanguage.TryCanonicalize("auto", out string code));
        Assert.Equal("auto", code);
        Assert.True(CliLanguage.IsAuto(code));
    }

    [Theory]
    [InlineData("no")]        // the Norwegian codes are nb-NO / nn-NO — guessing between them is wrong
    [InlineData("klingon")]
    [InlineData("")]
    [InlineData(null)]
    public void UnresolvableInputIsRejected(string? input)
    {
        Assert.False(CliLanguage.TryCanonicalize(input, out _));
    }

    [Fact]
    public void LegacyDisplayNamesAreNotAcceptedFromTheCommandLine()
    {
        // The engine's own resolver takes "English" for settings.json compatibility; the CLI's surface
        // is locale codes, and "English" prefix-matching nothing keeps the error honest.
        Assert.False(CliLanguage.TryCanonicalize("English", out _));
    }
}
