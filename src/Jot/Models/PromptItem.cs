using CommunityToolkit.Mvvm.ComponentModel;

namespace Jot.Models;

/// <summary>
/// One rewrite prompt in the catalog. Bundled prompts ship read-only; user prompts are editable.
/// Pinned prompts float to the top of the rewrite picker. Body is the instruction sent to the LLM.
/// </summary>
public sealed partial class PromptItem : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Stable built-in id ("improve-writing") — the persistence key, so retitling never orphans
    /// saved pins/default. Empty for user prompts (they key off <see cref="Id"/>).</summary>
    public string Slug { get; init; } = "";
    public string Category { get; init; } = "Essentials";
    public bool IsBuiltIn { get; init; } = true;

    /// <summary>Curation tier: 1 = Essentials (front of the list), 2/3 = long tail (found via search).</summary>
    public int Tier { get; init; } = 1;

    /// <summary>Short, human one-liner shown under the title in the picker/pane (NOT the LLM body). Empty for
    /// user prompts and any built-in without one — <see cref="Subtitle"/> then falls back to the flattened body.</summary>
    public string Description { get; init; } = "";

    /// <summary>Extra search terms, verbatim from the bundled library.</summary>
    public string[] Tags { get; init; } = [];
    /// <summary>Before/after examples for the preview drawer (v1.1). Empty on user prompts.</summary>
    public string SampleInput { get; init; } = "";
    public string SampleOutput { get; init; } = "";
    /// <summary>v2 "Rewrite by voice" hint (e.g. "Add a tone or audience direction"). Carried but INERT in v1.</summary>
    public string? VoiceAugmentHint { get; init; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _body = "";
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isDefault;

    /// <summary>The one-line summary the picker/pane show under the title: the hand-written
    /// <see cref="Description"/> when present, otherwise the LLM body flattened to a single line (so a
    /// multi-paragraph body like "Rewrite" never sprawls across the list). Trimmed with an ellipsis by the UI.</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(Description) ? Body.ReplaceLineEndings(" ").Trim() : Description;
    partial void OnBodyChanged(string value) => OnPropertyChanged(nameof(Subtitle));

    /// <summary>Monotonic pick order (0 = never used); the rewrite picker floats recent prompts up.</summary>
    [ObservableProperty] private long _lastPickedSeq;
}
