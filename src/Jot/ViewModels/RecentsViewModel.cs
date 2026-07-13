using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jot.Models;
using Jot.Recording;
using Jot.Services.Abstractions;
using Jot.Services.Navigation;
using Jot.Views;

namespace Jot.ViewModels;

/// <summary>
/// Drives the Recents landing + library list: the record button, a live grouped/filtered view over
/// the store (date buckets + search + tag chips), the empty state, and per-row actions. Singleton so
/// search/filter state survives navigation away and back.
/// </summary>
public sealed partial class RecentsViewModel : ObservableObject
{
    private readonly IRecordingStore _store;
    private readonly RecorderController _recorder;
    private readonly INavigator _navigator;

    public ICollectionView Items { get; }
    public ObservableCollection<string> Tags { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string? _selectedTag;
    [ObservableProperty] private string _hotkeyLabel = "Alt + Space";

    public RecentsViewModel(IRecordingStore store, RecorderController recorder, INavigator navigator)
    {
        _store = store;
        _recorder = recorder;
        _navigator = navigator;

        var cvs = new CollectionViewSource { Source = store.Items };
        cvs.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RecordingItem.DateGroup)));
        cvs.SortDescriptions.Add(new SortDescription(nameof(RecordingItem.DateGroupRank), ListSortDirection.Ascending));
        cvs.SortDescriptions.Add(new SortDescription(nameof(RecordingItem.CreatedAt), ListSortDirection.Descending));
        Items = cvs.View;
        Items.Filter = Matches;

        store.Items.CollectionChanged += (_, _) => { RefreshTags(); RaiseEmpty(); };
        RefreshTags();
    }

    public bool IsEmpty => _store.Items.Count == 0;
    public bool HasNoMatches => !IsEmpty && Items.IsEmpty;

    partial void OnSearchTextChanged(string value) => Items.Refresh();

    partial void OnSelectedTagChanged(string? value) => Items.Refresh();

    private bool Matches(object obj)
    {
        if (obj is not RecordingItem it) return false;

        if (!string.IsNullOrWhiteSpace(SelectedTag) &&
            !it.Tags.Contains(SelectedTag, StringComparer.OrdinalIgnoreCase))
            return false;

        string q = SearchText.Trim();
        if (q.Length == 0) return true;

        return Contains(it.Title, q) || Contains(it.Transcript, q)
            || Contains(it.Instruction, q) || Contains(it.Original, q)
            || it.Tags.Any(t => Contains(t, q));
    }

    private static bool Contains(string? s, string q)
        => !string.IsNullOrEmpty(s) && s.Contains(q, StringComparison.OrdinalIgnoreCase);

    private void RefreshTags()
    {
        Tags.Clear();
        foreach (string t in _store.AllTags()) Tags.Add(t);
    }

    private void RaiseEmpty()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoMatches));
    }

    [RelayCommand]
    private void StartDictation() => _recorder.Toggle();

    [RelayCommand]
    private void Open(RecordingItem? item)
    {
        if (item is null) return;
        _navigator.Navigate(typeof(RecordingDetailPage), item);
    }

    [RelayCommand]
    private void ClearTag() => SelectedTag = null;

    [RelayCommand]
    private void ToggleTag(string? tag)
        => SelectedTag = string.Equals(SelectedTag, tag, StringComparison.OrdinalIgnoreCase) ? null : tag;

    [RelayCommand]
    private void Copy(RecordingItem? item)
    {
        if (item is null) return;
        try { System.Windows.Clipboard.SetText(item.Transcript); } catch { /* clipboard busy */ }
    }

    [RelayCommand]
    private void Delete(RecordingItem? item)
    {
        if (item is not null) _store.Delete(item);
    }
}
