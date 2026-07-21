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
    private static readonly string DefaultDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    // Instance-scoped so tests can point at a temp dir and never touch the real user's prompts.json.
    private readonly string _dir;
    private readonly string _filePath;

    // The curated 33-prompt library, ported byte-for-byte from the Mac app
    // (projects/JOT-Transcribe/Resources/prompt-library.json) and shipped next to Jot.dll via the Assets glob.
    private static readonly string BundledLibrary =
        Path.Combine(AppContext.BaseDirectory, "Assets", "prompt-library.json");
    private static readonly JsonSerializerOptions BundledOptions =
        new(JsonSerializerDefaults.Web); // camelCase keys: sampleInput, voiceAugmentHint, …

    /// <summary>Slug of the prompt made default on a fresh/upgraded install — the one-press "articulate" rewrite.</summary>
    private const string DefaultSlug = "rewrite";

    private bool _loading;
    private long _pickSeq;

    public ObservableCollection<PromptItem> Prompts { get; } = new();

    /// <param name="storageDir">Override for the persisted-state directory (pins/default/picks).
    /// Null uses <c>%LOCALAPPDATA%\Jot</c> (production). Tests pass a temp dir so they never touch user data.</param>
    public PromptCatalog(string? storageDir = null)
    {
        _dir = storageDir ?? DefaultDir;
        _filePath = Path.Combine(_dir, "prompts.json");
        _loading = true;
        SeedBuiltIns();
        Load();
        _loading = false;
        SeedDefaultIfNone(); // after _loading=false so its Save() persists the seeded default
    }

    private void SeedBuiltIns()
    {
        // Read-only, ported catalog. A missing/corrupt bundled file is a packaging bug, not user data — so
        // fall back to a minimal seed (just the default) rather than leaving the picker empty and Rewrite dead.
        try
        {
            string json = File.ReadAllText(BundledLibrary);
            BundledLibraryDto? lib = JsonSerializer.Deserialize<BundledLibraryDto>(json, BundledOptions);
            if (lib?.Prompts is null || lib.Prompts.Count == 0) { SeedFallback(); return; }
            foreach (BundledPromptDto p in lib.Prompts)
                Prompts.Add(new PromptItem
                {
                    Slug = p.Id, Category = p.Category, Title = p.Title, Body = p.Body,
                    Description = p.Description ?? "", Tier = p.Tier, Tags = p.Tags ?? [],
                    SampleInput = p.SampleInput ?? "", SampleOutput = p.SampleOutput ?? "",
                    VoiceAugmentHint = p.VoiceAugmentHint, IsBuiltIn = true,
                });
        }
        catch (Exception ex)
        {
            JotLog.Info($"prompt library load failed: {ex.Message}");
            SeedFallback();
        }
    }

    /// <summary>Last-ditch seed if the bundled JSON is missing/corrupt — just the default, so Rewrite still works.</summary>
    private void SeedFallback() => Prompts.Add(new PromptItem
    {
        Slug = DefaultSlug, Category = "Essentials", Title = "Rewrite", Body = "Rewrite this.",
        Tier = 1, IsBuiltIn = true,
    });

    /// <summary>First run OR upgrade from the pre-33 catalog (whose saved default key no longer matches any
    /// slug): if nothing is marked default, promote the "rewrite" prompt so the picker always opens on it.</summary>
    private void SeedDefaultIfNone()
    {
        if (Prompts.Any(p => p.IsDefault)) return;
        PromptItem? def = Prompts.FirstOrDefault(p => p.Slug == DefaultSlug) ?? Prompts.FirstOrDefault();
        if (def is null) return;
        def.IsDefault = true;
        Save(); // _loading is false here, so this persists the seeded default under the stable key b:rewrite
    }

    private sealed record BundledLibraryDto(int Version, List<BundledPromptDto> Prompts);
    private sealed record BundledPromptDto(string Id, string Title, int Tier, string Category,
        string[]? Tags, string Body, string? Description, string? SampleInput, string? SampleOutput,
        string? VoiceAugmentHint);

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

    /// <summary>Make one prompt the default — the one pre-selected at the top of the rewrite picker.
    /// Toggles off if it was already the default.</summary>
    public void SetDefault(PromptItem item)
    {
        bool wasDefault = item.IsDefault;
        foreach (PromptItem p in Prompts) p.IsDefault = false;
        item.IsDefault = !wasDefault;
        Save();
    }


    // Built-ins key off the stable slug (not category/title), so retitling a prompt on a library re-sync
    // never orphans a saved pin/default. Old category/title keys from the pre-33 catalog simply no longer
    // match and are ignored on Load — SeedDefaultIfNone then re-establishes the default.
    private static string Key(PromptItem p) => p.IsBuiltIn ? $"b:{p.Slug}" : $"u:{p.Id}";

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var state = JsonSerializer.Deserialize<CatalogState>(File.ReadAllText(_filePath), Options);
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
            Directory.CreateDirectory(_dir);
            var state = new CatalogState(
                User: Prompts.Where(p => !p.IsBuiltIn).Select(p => new UserPromptDto(p.Id, p.Title, p.Body)).ToList(),
                Pinned: Prompts.Where(p => p.IsPinned).Select(Key).ToList(),
                Default: Prompts.FirstOrDefault(p => p.IsDefault) is { } d ? Key(d) : null,
                Picks: Prompts.Where(p => p.LastPickedSeq > 0).ToDictionary(Key, p => p.LastPickedSeq),
                PickCounter: _pickSeq);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(state, Options));
        }
        catch { /* best-effort */ }
    }

    private sealed record UserPromptDto(Guid Id, string Title, string Body);

    private sealed record CatalogState(
        List<UserPromptDto> User, List<string> Pinned, string? Default,
        Dictionary<string, long> Picks, long PickCounter);
}
