using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jot.Models;
using Jot.Services;

namespace Jot.ViewModels;

/// <summary>
/// Backs the rewrite prompt-picker overlay — a keyboard-first command palette shown at rewrite time.
/// A flat, searchable view over the shared <see cref="PromptCatalog"/> where pinned prompts sort
/// first, then most-recently-used, then alphabetical. Picking a prompt raises <see cref="Picked"/>
/// (the real build kicks off the rewrite with that instruction); this phase is UI-only.
/// </summary>
public sealed partial class PromptPickerViewModel : ObservableObject
{
    private readonly PromptCatalog _catalog;

    public ICollectionView Prompts { get; }

    [ObservableProperty] private string _searchText = "";

    /// <summary>Raised when the user commits a prompt (Enter / click). The overlay closes and, in the
    /// real build, the rewrite runs with <see cref="PromptItem.Body"/> as the instruction.</summary>
    public event Action<PromptItem>? Picked;

    public PromptPickerViewModel(PromptCatalog catalog)
    {
        _catalog = catalog;
        var cvs = new CollectionViewSource { Source = catalog.Prompts };
        cvs.SortDescriptions.Add(new SortDescription(nameof(PromptItem.IsPinned), ListSortDirection.Descending));
        cvs.SortDescriptions.Add(new SortDescription(nameof(PromptItem.LastPickedSeq), ListSortDirection.Descending));
        cvs.SortDescriptions.Add(new SortDescription(nameof(PromptItem.Title), ListSortDirection.Ascending));
        Prompts = cvs.View;
        Prompts.Filter = Matches;
    }

    partial void OnSearchTextChanged(string value)
    {
        Prompts.Refresh();
        Prompts.MoveCurrentToFirst(); // keep the top result selected as the user types
    }

    private bool Matches(object obj)
    {
        if (obj is not PromptItem p) return false;
        string q = SearchText.Trim();
        if (q.Length == 0) return true;
        return p.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Category.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Body.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    public void Pick(PromptItem? p)
    {
        if (p is null) return;
        _catalog.RecordPick(p);
        Picked?.Invoke(p);
    }

    [RelayCommand]
    private void TogglePin(PromptItem? p)
    {
        if (p is null) return;
        p.IsPinned = !p.IsPinned;
        Prompts.Refresh();
    }

    [RelayCommand]
    private void SetDefault(PromptItem? p)
    {
        if (p is not null) _catalog.SetDefault(p);
    }
}
