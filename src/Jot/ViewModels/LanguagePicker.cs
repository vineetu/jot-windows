using System.Windows.Data;
using Jot.Transcription.Nemotron;

namespace Jot.ViewModels;

/// <summary>One row of the language dropdown. Code is what settings store; Label is what users see.</summary>
public sealed record LanguageOption(string Code, string Label, string Group);

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
    public static ListCollectionView BuildView()
    {
        var options = NemotronLocales.All
            .OrderBy(l => l.Tier)
            .ThenBy(l => l.EnglishName, StringComparer.OrdinalIgnoreCase)
            .Select(l => new LanguageOption(
                l.Code,
                l.NativeName == l.EnglishName ? l.EnglishName : $"{l.EnglishName} — {l.NativeName}",
                GroupName(l.Tier)))
            .ToList();
        var view = new ListCollectionView(options);
        view.GroupDescriptions!.Add(new PropertyGroupDescription(nameof(LanguageOption.Group)));
        return view;
    }

    private static string GroupName(LocaleTier tier) => tier switch
    {
        LocaleTier.Auto => "Automatic",
        LocaleTier.TranscriptionReady => "Best accuracy",
        LocaleTier.BroadCoverage => "Good accuracy",
        _ => "Basic support",
    };
}
