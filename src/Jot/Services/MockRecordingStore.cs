using System.Collections.ObjectModel;
using Jot.Models;
using Jot.Services.Abstractions;

namespace Jot.Services;

/// <summary>
/// In-memory library seeded with realistic sample rows so every list/detail/search/tag surface is
/// exercisable before the real engine exists. Seeding is gated on <see cref="JotSettings.ShowSampleData"/>
/// so the empty state is reachable too. Swap for a SQLite-backed store in the STT milestone — the
/// interface stays identical.
/// </summary>
public sealed class MockRecordingStore : IRecordingStore
{
    public ObservableCollection<RecordingItem> Items { get; } = new();

    public MockRecordingStore(ISettingsStore settings)
    {
        if (settings.Current.ShowSampleData)
            Seed();
    }

    public void Add(RecordingItem item) => Items.Insert(0, item);

    public void Delete(RecordingItem item) => Items.Remove(item);

    public void Rename(RecordingItem item, string title) => item.Title = title;

    public IReadOnlyList<string> AllTags() =>
        Items.SelectMany(i => i.Tags).Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

    private void Seed()
    {
        DateTime now = DateTime.Now;

        Dictation("Standup notes", now.AddMinutes(-18), 42,
            "Yesterday I finished the settings pane and started on the status pill. Today I'm wiring the recordings list and the detail reading surface. No blockers.",
            ["work", "standup"]);

        Dictation("Email to the team", now.AddHours(-3), 27,
            "Hey everyone, quick heads up that the Windows build is coming along nicely. The native shell and the dictation pill are both working. I'll share a screenshot later today.",
            ["work", "email"]);

        var pending = new RecordingItem
        {
            Kind = RecordingKind.Dictation,
            Title = "Voice memo",
            CreatedAt = now.AddHours(-5),
            DurationSeconds = 63,
            Status = RecordingStatus.NeedsTranscription,
            ModelLabel = "Parakeet",
        };
        Items.Add(pending);

        Rewrite("Make this more formal", now.AddDays(-1).AddHours(-2),
            instruction: "Make this more formal",
            original: "hey can you send me that doc when you get a sec, thanks",
            rewritten: "Could you please send me that document when you have a moment? Thank you.");

        Dictation("Grocery list", now.AddDays(-2), 15,
            "Milk, eggs, bread, coffee, a bag of spinach, two lemons, and some dark chocolate.",
            ["personal"]);

        Dictation("Meeting recap", now.AddDays(-9), 118,
            "We agreed to ship the Windows preview by the end of the month. Design sign-off is done. The remaining work is the setup wizard and a polish pass on the settings screens.",
            ["work", "meeting"]);
    }

    private void Dictation(string title, DateTime at, double seconds, string transcript, string[] tags)
    {
        var item = new RecordingItem
        {
            Kind = RecordingKind.Dictation,
            Title = title,
            CreatedAt = at,
            DurationSeconds = seconds,
            Transcript = transcript,
            ModelLabel = "Parakeet",
        };
        foreach (string t in tags) item.Tags.Add(t);
        Items.Add(item);
    }

    private void Rewrite(string title, DateTime at, string instruction, string original, string rewritten)
    {
        Items.Add(new RecordingItem
        {
            Kind = RecordingKind.Rewrite,
            Title = title,
            CreatedAt = at,
            Instruction = instruction,
            Original = original,
            Transcript = rewritten, // the rewritten output
            ModelLabel = "GPT-4o",
        });
    }
}
