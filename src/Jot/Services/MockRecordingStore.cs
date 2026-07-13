using System.Collections.ObjectModel;
using Jot.Models;
using Jot.Services.Abstractions;

namespace Jot.Services;

/// <summary>
/// In-memory recordings library. Real dictations are inserted here by <see cref="Recording.RecorderController"/>
/// as they complete, newest first. It starts empty — the demo/sample rows were retired once the
/// on-device engine landed. Not yet persisted across launches; a SQLite/JSON-backed store can replace
/// this behind <see cref="IRecordingStore"/> without touching callers.
/// </summary>
public sealed class MockRecordingStore : IRecordingStore
{
    public ObservableCollection<RecordingItem> Items { get; } = new();

    public void Add(RecordingItem item) => Items.Insert(0, item);

    public void Delete(RecordingItem item) => Items.Remove(item);

    public void Rename(RecordingItem item, string title) => item.Title = title;

    public IReadOnlyList<string> AllTags() =>
        Items.SelectMany(i => i.Tags).Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
}
