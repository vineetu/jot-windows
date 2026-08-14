using Jot.Transcription.Ggml;
using Jot.Transcription.Nemotron;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// transcribe.cpp 0.1.3 hard-errors the string "auto", "", and any code not in
/// capabilities.languages. Autodetect is a C NULL. This is the whole product mapping.
/// </summary>
public class GgmlLanguageTests
{
    private static readonly string[] Gguf32 =
    [
        "en-US", "en-GB", "es-US", "es-ES", "fr-FR", "fr-CA", "it-IT", "pt-BR", "pt-PT",
        "nl-NL", "de-DE", "tr-TR", "ru-RU", "ar-AR", "hi-IN", "ja-JP", "ko-KR", "vi-VN", "uk-UA",
        "pl-PL", "sv-SE", "cs-CZ", "nb-NO", "da-DK", "bg-BG", "fi-FI", "hr-HR", "sk-SK", "zh-CN",
        "hu-HU", "ro-RO", "et-EE",
    ];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("Auto")]
    public void AutoAndEmpty_AreNullPointer(string? setting)
    {
        Assert.Null(GgmlLanguage.Map(setting, Gguf32));
    }

    [Theory]
    [InlineData("el-GR")]
    [InlineData("he-IL")]
    [InlineData("lt-LT")]
    [InlineData("sl-SI")]
    [InlineData("lv-LV")]
    [InlineData("mt-MT")]
    [InlineData("th-TH")]
    [InlineData("nn-NO")]
    [InlineData("EL-gr")]
    public void AdaptationReady_AreNull_NeverForwarded(string code)
    {
        Assert.Contains(code, GgmlLanguage.AdaptationReady, StringComparer.OrdinalIgnoreCase);
        Assert.Null(GgmlLanguage.Map(code, Gguf32));
    }

    [Fact]
    public void AdaptationReadyList_IsExactlyTheEightJotPickerCodes()
    {
        string[] fromTable = NemotronLocales.All
            .Where(l => l.Tier == LocaleTier.AdaptationReady)
            .Select(l => l.Code)
            .ToArray();
        Assert.Equal(8, fromTable.Length);
        Assert.Equal(fromTable.OrderBy(c => c), GgmlLanguage.AdaptationReady.OrderBy(c => c));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("en-us")]
    [InlineData("nb-NO")]
    [InlineData("zh-CN")]
    [InlineData("fr-CA")]
    public void SupportedCodes_PassThroughCanonical(string setting)
    {
        string? mapped = GgmlLanguage.Map(setting, Gguf32);
        Assert.NotNull(mapped);
        Assert.NotEqual("auto", mapped, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(mapped, Gguf32, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(mapped, GgmlLanguage.AdaptationReady, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("English", "en-US")]
    [InlineData("Norwegian", "nb-NO")]
    [InlineData("Spanish", "es-ES")]
    public void LegacyDisplayNames_ResolveToSupportedCodes(string name, string expected)
    {
        Assert.Equal(expected, GgmlLanguage.Map(name, Gguf32));
    }

    [Fact]
    public void UnknownJunk_FallsBackToEnUs_WhenPresent()
    {
        Assert.Equal("en-US", GgmlLanguage.Map("Klingon", Gguf32));
    }

    [Fact]
    public void UnknownJunk_IsNull_WhenEnUsMissingFromCapabilities()
    {
        Assert.Null(GgmlLanguage.Map("Klingon", ["de-DE"]));
    }

    [Fact]
    public void EveryGguf32Code_IsAccepted()
    {
        foreach (string code in Gguf32)
            Assert.Equal(code, GgmlLanguage.Map(code, Gguf32));
    }

    [Fact]
    public void NeverReturnsAutoOrEmptyOrAdaptationReady()
    {
        string?[] inputs =
        [
            null, "", "auto", "AUTO", "English", "el-GR", "nn-NO", "Klingon", "en-US", "garbage",
        ];
        foreach (string? input in inputs)
        {
            string? mapped = GgmlLanguage.Map(input, Gguf32);
            if (mapped is null) continue;
            Assert.NotEqual("", mapped);
            Assert.NotEqual("auto", mapped, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(mapped, GgmlLanguage.AdaptationReady, StringComparer.OrdinalIgnoreCase);
        }
    }
}
