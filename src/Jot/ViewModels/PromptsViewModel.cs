using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jot.Models;
using Jot.Services;

namespace Jot.ViewModels;

/// <summary>
/// Backs the Prompts catalog page: searchable, category-grouped view over <see cref="PromptCatalog"/>
/// (shared with the rewrite picker) with pin/default toggles and "My prompts" authoring.
/// </summary>
public sealed partial class PromptsViewModel : ObservableObject
{
    private readonly PromptCatalog _catalog;

    public ICollectionView Prompts { get; }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _newTitle = "";
    [ObservableProperty] private string _newBody = "";
    [ObservableProperty] private PromptItem? _editingPrompt;

    public bool IsEditing => EditingPrompt is not null;
    public string AddButtonText => IsEditing ? "Save changes" : "Add prompt";
    partial void OnEditingPromptChanged(PromptItem? value)
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(AddButtonText));
    }

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
        _catalog.TogglePin(p);
        Prompts.Refresh();
    }

    [RelayCommand]
    private void SetDefault(PromptItem? p)
    {
        if (p is not null) _catalog.SetDefault(p);
    }

    [RelayCommand]
    private void EditPrompt(PromptItem? p)
    {
        if (p is null || p.IsBuiltIn) return;
        EditingPrompt = p;
        NewTitle = p.Title;
        NewBody = p.Body;
    }

    [RelayCommand]
    private void DeletePrompt(PromptItem? p)
    {
        if (p is null || p.IsBuiltIn) return;
        if (ReferenceEquals(p, EditingPrompt)) CancelEdit();
        _catalog.DeleteUserPrompt(p);
    }

    [RelayCommand]
    private void CancelEdit()
    {
        EditingPrompt = null;
        NewTitle = "";
        NewBody = "";
    }

    [RelayCommand]
    private void AddPrompt()
    {
        string title = NewTitle.Trim();
        string body = NewBody.Trim();
        if (title.Length == 0 || body.Length == 0) return;

        if (EditingPrompt is not null)
            _catalog.EditUserPrompt(EditingPrompt, title, body);
        else
            _catalog.AddUserPrompt(title, body);

        CancelEdit();
    }
}
