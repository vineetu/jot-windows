using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jot.Models;
using Jot.Services;

namespace Jot.ViewModels;

/// <summary>
/// Backs the Prompts catalog page: a searchable, category-grouped view over <see cref="PromptCatalog"/>
/// with pin/default toggles and simple "My prompts" authoring. Shared catalog with the rewrite picker.
/// </summary>
public sealed partial class PromptsViewModel : ObservableObject
{
    private readonly PromptCatalog _catalog;

    public ICollectionView Prompts { get; }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _newTitle = "";
    [ObservableProperty] private string _newBody = "";

    public PromptsViewModel(PromptCatalog catalog)
    {
        _catalog = catalog;
        var cvs = new CollectionViewSource { Source = catalog.Prompts };
        cvs.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PromptItem.Category)));
        cvs.SortDescriptions.Add(new SortDescription(nameof(PromptItem.Category), ListSortDirection.Ascending));
        cvs.SortDescriptions.Add(new SortDescription(nameof(PromptItem.IsPinned), ListSortDirection.Descending));
        cvs.SortDescriptions.Add(new SortDescription(nameof(PromptItem.Title), ListSortDirection.Ascending));
        Prompts = cvs.View;
        Prompts.Filter = Matches;
    }

    partial void OnSearchTextChanged(string value) => Prompts.Refresh();

    private bool Matches(object obj)
    {
        if (obj is not PromptItem p) return false;
        string q = SearchText.Trim();
        if (q.Length == 0) return true;
        return p.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Body.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Category.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void TogglePin(PromptItem? p)
    {
        if (p is null) return;
        p.IsPinned = !p.IsPinned;
        Prompts.Refresh();
    }

    [RelayCommand]
    private void AddPrompt()
    {
        string title = NewTitle.Trim();
        string body = NewBody.Trim();
        if (title.Length == 0 || body.Length == 0) return;
        _catalog.AddUserPrompt(title, body);
        NewTitle = "";
        NewBody = "";
    }
}
