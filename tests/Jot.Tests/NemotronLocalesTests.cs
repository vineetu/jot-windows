using System;
using System.Linq;
using Jot.Services;
using Jot.Services.Abstractions;
using Jot.Text;
using Jot.Transcription.Nemotron;
using Xunit;

namespace Jot.Tests;

/// <summary>Guards the locale table (the model card's 40 locales + auto) and the legacy-name bridge.</summary>
public class NemotronLocalesTests
{
    [Fact]
    public void Table_Has41Entries_WithModelCardTierCounts()
    {
        Assert.Equal(41, NemotronLocales.All.Count);
        Assert.Equal(1, NemotronLocales.All.Count(l => l.Tier == LocaleTier.Auto));
        Assert.Equal(19, NemotronLocales.All.Count(l => l.Tier == LocaleTier.TranscriptionReady));
        Assert.Equal(13, NemotronLocales.All.Count(l => l.Tier == LocaleTier.BroadCoverage));
        Assert.Equal(8, NemotronLocales.All.Count(l => l.Tier == LocaleTier.AdaptationReady));
    }

    [Fact]
    public void CodesAndSlots_AreUnique_AndSlotsInRange()
    {
        Assert.Equal(41, NemotronLocales.All.Select(l => l.Code.ToLowerInvariant()).Distinct().Count());
        Assert.Equal(41, NemotronLocales.All.Select(l => l.Slot).Distinct().Count());
        Assert.All(NemotronLocales.All, l => Assert.InRange(l.Slot, 0, 127));
    }

    // Spot checks against the export's languages.json (the authoritative map).
    [Theory]
    [InlineData("en-US", 0)]
    [InlineData("en-GB", 1)]
    [InlineData("es-ES", 2)]
    [InlineData("es-US", 3)]
    [InlineData("fr-CA", 100)]
    [InlineData("auto", 101)]
    [InlineData("mt-MT", 102)]
    [InlineData("nb-NO", 103)]
    [InlineData("nn-NO", 104)]
    public void KnownSlots_MatchLanguagesJson(string code, long slot)
    {
        Assert.True(NemotronLocales.TryGetSlot(code, out long got));
        Assert.Equal(slot, got);
    }

    // Every legacy display name must keep the EXACT slot it had in the shipped 33-name map.
    [Theory]
    [InlineData("English", 0)]
    [InlineData("Spanish", 2)]
    [InlineData("Chinese", 4)]
    [InlineData("Hindi", 6)]
    [InlineData("Portuguese", 12)]
    [InlineData("Vietnamese", 33)]
    [InlineData("Estonian", 60)]
    [InlineData("Hebrew", 64)]
    [InlineData("Norwegian", 103)]
    public void LegacyNames_KeepTheirShippedSlots(string name, long slot)
    {
        Assert.True(NemotronLocales.TryGetSlot(name, out long got));
        Assert.Equal(slot, got);
    }

    [Fact]
    public void UnknownOrEmpty_FallsBackToEnUs_AndReportsFalse()
    {
        Assert.False(NemotronLocales.TryGetSlot("Klingon", out long slot));
        Assert.Equal(0, slot);
        Assert.False(NemotronLocales.TryGetSlot("", out slot));
        Assert.Equal(0, slot);
        Assert.False(NemotronLocales.TryGetSlot(null, out slot));
        Assert.Equal(0, slot);
    }

    [Theory]
    [InlineData("English", "en-US")]
    [InlineData("Spanish", "es-ES")]   // preserves the shipped slot-2 behavior
    [InlineData("Norwegian", "nb-NO")]
    [InlineData("es-us", "es-US")]     // case-insensitive, canonical casing out
    [InlineData("en-US", "en-US")]     // already canonical → unchanged (migration no-ops)
    [InlineData("", "en-US")]
    [InlineData("garbage", "en-US")]
    public void Normalize_ProducesCanonicalCodes(string input, string expected)
    {
        Assert.Equal(expected, NemotronLocales.Normalize(input));
    }

    [Fact]
    public void CaseInsensitive_Lookup()
    {
        Assert.True(NemotronLocales.TryGetSlot("AUTO", out long slot));
        Assert.Equal(NemotronLocales.AutoSlot, slot);
    }
}

public class LanguageCodeLocaleTests
{
    [Theory]
    [InlineData("pt-BR", "pt")]
    [InlineData("de-DE", "de")]
    [InlineData("en-US", "en")]
    [InlineData("nb-NO", "nb")]
    [InlineData("German", "de")]   // legacy display names still work
    [InlineData("auto", "")]       // auto-detect must NOT enable any language-specific cleanup
    [InlineData("None", "")]
    [InlineData("", "")]
    [InlineData("x1-YY", "")]      // non-alpha junk stays unmapped
    public void ToIso_HandlesCodesNamesAndAuto(string? input, string expected)
    {
        Assert.Equal(expected, LanguageCode.ToIso(input));
    }
}

public class LanguageSettingMigrationTests
{
    private sealed class FakeSettingsStore : ISettingsStore
    {
        public JotSettings Current { get; } = new();
        public int Saves;
        public void Save() => Saves++;
        public void Reset() { }
        public event EventHandler? Changed { add { } remove { } }
    }

    [Fact]
    public void LegacyName_IsNormalizedOnce()
    {
        var store = new FakeSettingsStore();
        store.Current.Language = "Spanish";
        StartupMigration.MigrateLanguageSetting(store);
        Assert.Equal("es-ES", store.Current.Language);
        Assert.Equal(1, store.Saves);
        StartupMigration.MigrateLanguageSetting(store); // idempotent: code is already canonical
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void CanonicalCode_IsUntouched_NoSave()
    {
        var store = new FakeSettingsStore();
        store.Current.Language = "en-US";
        StartupMigration.MigrateLanguageSetting(store);
        Assert.Equal("en-US", store.Current.Language);
        Assert.Equal(0, store.Saves);
    }
}
