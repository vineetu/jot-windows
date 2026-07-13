using System.Collections.ObjectModel;
using Jot.Models;

namespace Jot.Services;

/// <summary>
/// The rewrite prompt catalog: bundled read-only prompts across categories plus user-authored ones.
/// In-memory for now; the real build persists user prompts (SQLite/JSON). Shared by the Prompts page
/// and the rewrite prompt-picker overlay.
/// </summary>
public sealed class PromptCatalog
{
    public ObservableCollection<PromptItem> Prompts { get; } = new();

    public PromptCatalog()
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

    public void AddUserPrompt(string title, string body)
        => Prompts.Add(new PromptItem { Category = "My prompts", Title = title, Body = body, IsBuiltIn = false });

    public IEnumerable<string> Categories =>
        Prompts.Select(p => p.Category).Distinct();

    private long _pickSeq;

    /// <summary>Record that a prompt was chosen from the picker so it floats up as "recent" next time.</summary>
    public void RecordPick(PromptItem item) => item.LastPickedSeq = ++_pickSeq;

    /// <summary>Make one prompt the default (used when the user rewrites without opening the picker).</summary>
    public void SetDefault(PromptItem item)
    {
        foreach (PromptItem p in Prompts) p.IsDefault = ReferenceEquals(p, item);
    }
}
