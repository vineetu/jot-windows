using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Jot.Models;
using Jot.Services.Abstractions;

namespace Jot.Services;

/// <summary>
/// Persists the recordings library to <c>%LOCALAPPDATA%\Jot\library.json</c> so transcripts survive
/// restarts (the old <see cref="MockRecordingStore"/> kept them in memory only, so every launch wiped
/// the list). Loads on construction; saves whenever a row is added/removed/renamed or an item's
/// transcript, title, tags, status, or audio path changes. Saves are small and synchronous —
/// the library is tiny and edits are committed explicitly, not per keystroke.
/// </summary>
public sealed class JsonRecordingStore : IRecordingStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _dir;
    private readonly string _filePath;
    private bool _loading;

    public ObservableCollection<RecordingItem> Items { get; } = new();

    public JsonRecordingStore(ISettingsStore settings)
    {
        _dir = JotPaths.DataDir(settings.Current);
        _filePath = JotPaths.LibraryFile(settings.Current);
        Load();
        // Persist structural changes (add/remove) that bypass the mutator methods too.
        Items.CollectionChanged += OnItemsChanged;
    }

    public void Add(RecordingItem item)
    {
        Items.Insert(0, item); // newest first
        // Subscription happens in OnItemsChanged; Save too.
    }

    public void Delete(RecordingItem item) => Items.Remove(item);

    public void Rename(RecordingItem item, string title) => item.Title = title; // fires PropertyChanged → Save

    public IReadOnlyList<string> AllTags() =>
        Items.SelectMany(i => i.Tags).Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

    // ---- persistence ----

    private void Load()
    {
        _loading = true;
        try
        {
            if (File.Exists(_filePath))
            {
                var dtos = JsonSerializer.Deserialize<List<RecordingDto>>(File.ReadAllText(_filePath), Options);
                if (dtos is not null)
                    foreach (RecordingDto d in dtos)
                        Items.Add(ToItem(d));
            }
        }
        catch { /* corrupt/unreadable library shouldn't block launch — start empty */ }
        finally { _loading = false; }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (RecordingItem it in e.NewItems.OfType<RecordingItem>()) Subscribe(it);
        if (e.OldItems is not null)
            foreach (RecordingItem it in e.OldItems.OfType<RecordingItem>()) Unsubscribe(it);
        Save();
    }

    private void Subscribe(RecordingItem item)
    {
        item.PropertyChanged += OnItemPropertyChanged;
        item.Tags.CollectionChanged += OnTagsChanged;
    }

    private void Unsubscribe(RecordingItem item)
    {
        item.PropertyChanged -= OnItemPropertyChanged;
        item.Tags.CollectionChanged -= OnTagsChanged;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only persist real state, not derived/display properties.
        switch (e.PropertyName)
        {
            case nameof(RecordingItem.Title):
            case nameof(RecordingItem.Transcript):
            case nameof(RecordingItem.Status):
            case nameof(RecordingItem.IsEdited):
            case nameof(RecordingItem.DurationSeconds):
            case nameof(RecordingItem.WavPath):
            case nameof(RecordingItem.Instruction):
            case nameof(RecordingItem.Original):
                Save();
                break;
        }
    }

    private void OnTagsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Save();

    private void Save()
    {
        if (_loading) return;
        try
        {
            Directory.CreateDirectory(_dir);
            var dtos = Items.Select(ToDto).ToList();
            File.WriteAllText(_filePath, JsonSerializer.Serialize(dtos, Options));
        }
        catch { /* best-effort; a failed write shouldn't crash the UI */ }
    }

    // ---- mapping (a flat DTO avoids serializing the item's computed/observable plumbing) ----

    private static RecordingDto ToDto(RecordingItem i) => new(
        i.Id, i.Kind, i.CreatedAt, i.DurationSeconds, i.WavPath, i.ModelLabel, i.Title, i.Transcript,
        i.Status, i.IsEdited, i.Instruction, i.Original, i.Tags.ToList());

    private static RecordingItem ToItem(RecordingDto d)
    {
        var item = new RecordingItem
        {
            Id = d.Id,
            Kind = d.Kind,
            CreatedAt = d.CreatedAt,
            DurationSeconds = d.DurationSeconds,
            WavPath = d.WavPath,
            ModelLabel = d.ModelLabel,
            Title = d.Title,
            Transcript = d.Transcript,
            Status = d.Status,
            IsEdited = d.IsEdited,
            Instruction = d.Instruction,
            Original = d.Original,
        };
        foreach (string tag in d.Tags ?? []) item.Tags.Add(tag);
        return item;
    }

    private sealed record RecordingDto(
        Guid Id, RecordingKind Kind, DateTime CreatedAt, double DurationSeconds, string? WavPath,
        string ModelLabel, string Title, string Transcript, RecordingStatus Status, bool IsEdited,
        string Instruction, string Original, List<string> Tags);
}
