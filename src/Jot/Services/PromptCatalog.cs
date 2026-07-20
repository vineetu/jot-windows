using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Jot.Models;

namespace Jot.Services;

/// <summary>
/// The rewrite prompt catalog: bundled read-only prompts across categories plus user-authored ones.
/// Bundled prompts are re-seeded each launch; user prompts, pins, the default, and pick-order persist
/// to <c>%LOCALAPPDATA%\Jot\prompts.json</c> so the catalog survives restarts. Shared by the Prompts
/// page and the rewrite prompt-picker overlay.
/// </summary>
public sealed class PromptCatalog
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot");
    private static readonly string FilePath = Path.Combine(Dir, "prompts.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private bool _loading;
    private long _pickSeq;

    public ObservableCollection<PromptItem> Prompts { get; } = new();

    public PromptCatalog()
    {
        _loading = true;
        SeedBuiltIns();
        Load();
        _loading = false;
    }

    private void SeedBuiltIns()
    {
        Add("Essentials", "Improve writing", "Improve the clarity and flow of this text without changing its meaning.");
        Add("Essentials", "Fix spelling & grammar", "Fix any spelling and grammar mistakes. Keep the wording otherwise unchanged.");
        Add("Essentials", "Make formal", "Rewrite this in a more formal, professional tone.");
        Add("Essentials", "Make casual", "Rewrite this in a friendlier, more casual tone.");
        Add("Essentials", "Make shorter", "Make this more concise while keeping every key point.");
        Add("Essentials", "Summarize", "Summarize this text in a few clear sentences.");
        Add("Convert", "To bullet points", "Convert this into a clear bulleted list.");
        Add("Convert", "To action items", "Extract the action items as a checklist.");
        Add("Convert", "To Jira ticket", "Rewrite this as a Jira ticket with a title, description, and acceptance criteria.");
        Add("Email", "Polite reply", "Write a polite, professional reply to this message.");
        Add("Email", "Status update", "Rewrite this as a concise status update.");
        Add("Rewrite", "Make assertive", "Rewrite this to sound more confident and assertive.");
        Add("Rewrite", "Plain English", "Rewrite this in plain, simple English.");
        Add("Code", "Add comments", "Add clear explanatory comments to this code without changing its behavior.");
        Add("Translate", "To Spanish", "Translate this text into Spanish.");
    }

    private void Add(string category, string title, string body)
        => Prompts.Add(new PromptItem { Category = category, Title = title, Body = body, IsBuiltIn = true });

    public IEnumerable<string> Categories => Prompts.Select(p => p.Category).Distinct();


    public PromptItem AddUserPrompt(string title, string body)
    {
        var item = new PromptItem { Category = "My prompts", Title = title, Body = body, IsBuiltIn = false };
        Prompts.Add(item);
        Save();
        return item;
    }

    /// <summary>Edits a user prompt's title/body (built-ins are read-only and ignored).</summary>
    public void EditUserPrompt(PromptItem item, string title, string body)
    {
        if (item.IsBuiltIn) return;
        item.Title = title;
        item.Body = body;
        Save();
    }

    /// <summary>Deletes a user prompt (built-ins are read-only and ignored).</summary>
    public void DeleteUserPrompt(PromptItem item)
    {
        if (item.IsBuiltIn) return;
        Prompts.Remove(item);
        Save();
    }

    public void TogglePin(PromptItem item)
    {
        item.IsPinned = !item.IsPinned;
        Save();
    }

    /// <summary>Record that a prompt was chosen from the picker so it floats up as "recent" next time.</summary>
    public void RecordPick(PromptItem item)
    {
        item.LastPickedSeq = ++_pickSeq;
        Save();
    }

    /// <summary>Make one prompt the default (used when the user rewrites without opening the picker).
    /// Toggles off if it was already the default.</summary>
    public void SetDefault(PromptItem item)
    {
        bool wasDefault = item.IsDefault;
        foreach (PromptItem p in Prompts) p.IsDefault = false;
        item.IsDefault = !wasDefault;
        Save();
    }


    private static string Key(PromptItem p) => p.IsBuiltIn ? $"b:{p.Category}/{p.Title}" : $"u:{p.Id}";

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var state = JsonSerializer.Deserialize<CatalogState>(File.ReadAllText(FilePath), Options);
            if (state is null) return;

            foreach (UserPromptDto u in state.User ?? [])
                Prompts.Add(new PromptItem
                {
                    Id = u.Id, Category = "My prompts", Title = u.Title, Body = u.Body, IsBuiltIn = false,
                });

            var pinned = new HashSet<string>(state.Pinned ?? [], StringComparer.Ordinal);
            foreach (PromptItem p in Prompts)
            {
                string k = Key(p);
                if (pinned.Contains(k)) p.IsPinned = true;
                if (state.Picks is not null && state.Picks.TryGetValue(k, out long seq)) p.LastPickedSeq = seq;
                if (state.Default is not null && k == state.Default) p.IsDefault = true;
            }
            _pickSeq = state.PickCounter;
        }
        catch { /* corrupt prompts file — keep the built-ins, drop the saved state */ }
    }

    private void Save()
    {
        if (_loading) return;
        try
        {
            Directory.CreateDirectory(Dir);
            var state = new CatalogState(
                User: Prompts.Where(p => !p.IsBuiltIn).Select(p => new UserPromptDto(p.Id, p.Title, p.Body)).ToList(),
                Pinned: Prompts.Where(p => p.IsPinned).Select(Key).ToList(),
                Default: Prompts.FirstOrDefault(p => p.IsDefault) is { } d ? Key(d) : null,
                Picks: Prompts.Where(p => p.LastPickedSeq > 0).ToDictionary(Key, p => p.LastPickedSeq),
                PickCounter: _pickSeq);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, Options));
        }
        catch { /* best-effort */ }
    }

    private sealed record UserPromptDto(Guid Id, string Title, string Body);

    private sealed record CatalogState(
        List<UserPromptDto> User, List<string> Pinned, string? Default,
        Dictionary<string, long> Picks, long PickCounter);
}
