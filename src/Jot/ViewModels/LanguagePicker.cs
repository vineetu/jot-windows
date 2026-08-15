using System.Windows.Data;
using Jot.Transcription.Nemotron;

namespace Jot.ViewModels;

/// <summary>One row of the language dropdown. Code is what settings store; Label is what users see.
/// <see cref="IsAvailable"/> is false for the 8 AdaptationReady locales on the ggml engine —
/// the GGUF hard-errors them, so the picker must not let the user pick a tag that will fail.</summary>
public sealed record LanguageOption(string Code, string Label, string Group, bool IsAvailable = true);

/// <summary>
/// Builds the grouped language picker BOTH the Settings page and the setup wizard bind — one builder so
/// the two dropdowns can never drift. Rows come straight from <see cref="NemotronLocales.All"/> (Auto
/// detect first, then the model card's quality tiers under friendly headers), alphabetical within each
/// group, native name after an em dash when it differs ("German — Deutsch").
/// </summary>
public static class LanguagePicker
{
    /// <summary>A fresh grouped view per caller — ListCollectionView carries per-control currency, so
    /// sharing one instance between two ComboBoxes would tie their selections together.</summary>
    /// <param name="ggmlEngine">When true (the shipping default), AdaptationReady locales are listed
    /// as unavailable instead of "Basic support" — they are not in the Q8_0 GGUF.</param>
    public static ListCollectionView BuildView(bool ggmlEngine = true)
    {
        var options = NemotronLocales.All
            .OrderBy(l => l.Tier)
            .ThenBy(l => l.EnglishName, StringComparer.OrdinalIgnoreCase)
            .Select(l => ToOption(l, ggmlEngine))
            .ToList();
        var view = new ListCollectionView(options);
        view.GroupDescriptions!.Add(new PropertyGroupDescription(nameof(LanguageOption.Group)));
        return view;
    }

    private static LanguageOption ToOption(NemotronLocale l, bool ggmlEngine)
    {
        string name = l.NativeName == l.EnglishName ? l.EnglishName : $"{l.EnglishName} — {l.NativeName}";
        if (ggmlEngine && l.Tier == LocaleTier.AdaptationReady)
        {
            return new LanguageOption(
                l.Code,
                name + " — not available on this engine",
                "Not available on this engine",
                IsAvailable: false);
        }
        return new LanguageOption(l.Code, name, GroupName(l.Tier));
    }

    private static string GroupName(LocaleTier tier) => tier switch
    {
        LocaleTier.Auto => "Automatic",
        LocaleTier.TranscriptionReady => "Best accuracy",
        LocaleTier.BroadCoverage => "Good accuracy",
        _ => "Basic support",
    };
}
