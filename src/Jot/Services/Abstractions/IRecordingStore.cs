using System.Collections.ObjectModel;
using Jot.Models;

namespace Jot.Services.Abstractions;

/// <summary>
/// The library's data source. Backed today by an in-memory mock; later by SQLite fed from real
/// dictations. The UI binds to <see cref="Items"/> and calls the mutators — it never assumes how
/// rows are stored.
/// </summary>
public interface IRecordingStore
{
    ObservableCollection<RecordingItem> Items { get; }

    void Add(RecordingItem item);
    void Delete(RecordingItem item);
    void Rename(RecordingItem item, string title);

    /// <summary>All tags currently in use across the library (for the filter chip bar).</summary>
    IReadOnlyList<string> AllTags();
}
