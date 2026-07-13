using CommunityToolkit.Mvvm.ComponentModel;

namespace Jot.Models;

/// <summary>
/// One rewrite prompt in the catalog. Bundled prompts ship read-only; user prompts are editable.
/// Pinned prompts float to the top of the rewrite picker. Body is the instruction sent to the LLM.
/// </summary>
public sealed partial class PromptItem : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Category { get; init; } = "Essentials";
    public bool IsBuiltIn { get; init; } = true;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _body = "";
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isDefault;

    /// <summary>Monotonic pick order (0 = never used); the rewrite picker floats recent prompts up.</summary>
    [ObservableProperty] private long _lastPickedSeq;
}
