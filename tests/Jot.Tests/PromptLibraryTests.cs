using System.IO;
using System.Threading;
using Jot.Models;
using Jot.Services;
using Jot.ViewModels;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// Fidelity + behavior tests for the ported 33-prompt library and the pinned-default picker flow.
/// Every test uses a throwaway temp dir so the real %LOCALAPPDATA%\Jot\prompts.json is never touched.
/// </summary>
public sealed class PromptLibraryTests : IDisposable
{
    // The pinned 33 ids from docs/plans/prompt-library-windows.md — the fidelity gate against Mac drift.
    private static readonly string[] ExpectedSlugs =
    [
        "improve-writing", "rewrite", "fix-spelling-grammar", "make-formal", "make-casual", "make-shorter",
        "make-longer", "summarize", "extract-key-points", "convert-to-ai-prompt", "translate",
        "respond-to-email", "convert-to-jira-ticket", "convert-to-action-items", "convert-to-outline",
        "convert-to-markdown-doc", "tighten-and-clarify", "make-assertive", "plain-english", "friendly-tone",
        "confident-tone", "bluf-email", "convert-to-mermaid", "polish-for-publication", "status-update-email",
        "meeting-minutes-to-actions", "convert-to-checklist", "convert-to-faq", "pros-and-cons", "slide-bullets",
        "add-comments-to-code", "polite-decline", "trim-ai-fluff",
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jot-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void Bundled_library_seeds_exactly_the_33_curated_prompts()
    {
        var catalog = new PromptCatalog(_dir);
        List<PromptItem> builtins = catalog.Prompts.Where(p => p.IsBuiltIn).ToList();

        Assert.Equal(33, builtins.Count);
        Assert.Equal(ExpectedSlugs.OrderBy(s => s), builtins.Select(p => p.Slug).OrderBy(s => s));
        Assert.All(builtins, p => Assert.False(string.IsNullOrWhiteSpace(p.Body)));
        Assert.All(builtins, p => Assert.False(string.IsNullOrWhiteSpace(p.Title)));
    }

    [Fact]
    public void Bundled_bodies_are_not_truncated_copies()
    {
        var catalog = new PromptCatalog(_dir);
        PromptItem Body(string slug) => catalog.Prompts.First(p => p.Slug == slug);

        // Anchor substrings from the canonical bodies — a truncated/mangled copy would miss these.
        Assert.Contains("articulate", Body("rewrite").Body);
        Assert.Contains("Return only the rewritten text", Body("improve-writing").Body);
        Assert.Contains("conversational scaffolding", Body("trim-ai-fluff").Body);
    }

    [Fact]
    public void Fresh_install_marks_exactly_rewrite_as_default()
    {
        var catalog = new PromptCatalog(_dir);
        List<PromptItem> defaults = catalog.Prompts.Where(p => p.IsDefault).ToList();

        Assert.Single(defaults);
        Assert.Equal("rewrite", defaults[0].Slug);
    }

    [Fact]
    public void Upgrade_from_pre33_catalog_reseeds_default_and_keeps_user_prompts()
    {
        // Simulate a prompts.json written by the OLD 15-prompt build: default/pins keyed by category/title
        // (now orphaned), plus one user prompt (stable u:{Guid} key).
        Directory.CreateDirectory(_dir);
        Guid userId = Guid.NewGuid();
        string legacy = $$"""
        {
          "User": [ { "Id": "{{userId}}", "Title": "My note", "Body": "custom body" } ],
          "Pinned": [ "b:Essentials/Improve writing" ],
          "Default": "b:Essentials/Summarize",
          "Picks": {},
          "PickCounter": 0
        }
        """;
        File.WriteAllText(Path.Combine(_dir, "prompts.json"), legacy);

        var catalog = new PromptCatalog(_dir);

        // Orphaned old default did not match → SeedDefaultIfNone promoted rewrite.
        List<PromptItem> defaults = catalog.Prompts.Where(p => p.IsDefault).ToList();
        Assert.Single(defaults);
        Assert.Equal("rewrite", defaults[0].Slug);

        // No built-in got the orphaned pin.
        Assert.DoesNotContain(catalog.Prompts.Where(p => p.IsBuiltIn), p => p.IsPinned);

        // The user prompt survived intact.
        PromptItem? user = catalog.Prompts.FirstOrDefault(p => !p.IsBuiltIn);
        Assert.NotNull(user);
        Assert.Equal("My note", user!.Title);
        Assert.Equal("custom body", user.Body);
    }

    [Fact]
    public void Default_and_pins_persist_under_stable_slug_keys_across_reload()
    {
        var first = new PromptCatalog(_dir);
        first.SetDefault(first.Prompts.First(p => p.Slug == "summarize"));
        first.TogglePin(first.Prompts.First(p => p.Slug == "translate"));

        var reloaded = new PromptCatalog(_dir);
        Assert.True(reloaded.Prompts.First(p => p.Slug == "summarize").IsDefault);
        Assert.True(reloaded.Prompts.First(p => p.Slug == "translate").IsPinned);
        Assert.Single(reloaded.Prompts, p => p.IsDefault); // rewrite was demoted, only summarize remains
    }

    [Fact]
    public void Picker_sorts_default_to_the_top_even_over_pinned_and_recent()
    {
        var catalog = new PromptCatalog(_dir); // rewrite is default
        catalog.TogglePin(catalog.Prompts.First(p => p.Slug == "translate"));    // a pinned non-default
        catalog.RecordPick(catalog.Prompts.First(p => p.Slug == "summarize"));   // a recently-picked non-default

        // CollectionViewSource has WPF thread affinity — build + read on a single STA thread.
        string? topSlug = RunSta(() =>
        {
            var vm = new PromptPickerViewModel(catalog);
            return (vm.Prompts.Cast<PromptItem>().First()).Slug;
        });

        Assert.Equal("rewrite", topSlug);
    }

    private static T RunSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
        return result;
    }
}
