using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Jot.Models;

public enum RecordingKind { Dictation, Rewrite }

public enum RecordingStatus { Complete, NeedsTranscription }

/// <summary>
/// One row in the library — a dictation or a rewrite session. Observable so inline edits
/// (rename, transcript edit, tags, status) reflect live in the list and detail. Until the real
/// engine + SQLite store land, these are produced by <see cref="Services.MockRecordingStore"/>;
/// the shape matches what the real store will persist so wiring it later is a swap.
/// </summary>
public sealed partial class RecordingItem : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public RecordingKind Kind { get; init; } = RecordingKind.Dictation;
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    /// <summary>Length in seconds (dictations only; 0 for rewrites, which keep no audio). Settable so
    /// an imported row can fill it in once the file has been decoded.</summary>
    [ObservableProperty] private double _durationSeconds;

    /// <summary>Absolute path to the on-disk WAV, when this is a genuinely-recorded item. Null for
    /// rewrites and for rows whose audio has been pruned by retention (the transcript is kept). The
    /// detail view disables playback when there's no audio.</summary>
    [ObservableProperty] private string? _wavPath;

    public string ModelLabel { get; init; } = "Parakeet";

    [ObservableProperty] private string _title = "Untitled";
    [ObservableProperty] private string _transcript = "";
    [ObservableProperty] private RecordingStatus _status = RecordingStatus.Complete;
    [ObservableProperty] private bool _isEdited;

    // Rewrite-only fields.
    [ObservableProperty] private string _instruction = "";
    [ObservableProperty] private string _original = "";

    public ObservableCollection<string> Tags { get; } = new();

    public bool HasAudio => Kind == RecordingKind.Dictation
        && Status == RecordingStatus.Complete
        && !string.IsNullOrEmpty(WavPath);

    public bool IsPending => Status == RecordingStatus.NeedsTranscription;

    public bool IsRewriteRow => Kind == RecordingKind.Rewrite;
    public bool IsDictationRow => Kind == RecordingKind.Dictation;

    public string TimeText => CreatedAt.ToString("t"); // short time, culture-aware

    partial void OnDurationSecondsChanged(double value) => OnPropertyChanged(nameof(DurationText));
    partial void OnWavPathChanged(string? value) => OnPropertyChanged(nameof(HasAudio));

    public string DurationText
    {
        get
        {
            var t = TimeSpan.FromSeconds(DurationSeconds);
            return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }
    }

    /// <summary>Time-bucket label used to group the library list (Today / Yesterday / …).</summary>
    public string DateGroup
    {
        get
        {
            DateTime today = DateTime.Today;
            DateTime day = CreatedAt.Date;
            if (day == today) return "Today";
            if (day == today.AddDays(-1)) return "Yesterday";
            if (day > today.AddDays(-7)) return "Previous 7 days";
            if (day > today.AddDays(-30)) return "Previous 30 days";
            return "Older";
        }
    }

    /// <summary>Ordering key so groups sort newest-first regardless of label.</summary>
    public int DateGroupRank => DateGroup switch
    {
        "Today" => 0,
        "Yesterday" => 1,
        "Previous 7 days" => 2,
        "Previous 30 days" => 3,
        _ => 4,
    };
}
