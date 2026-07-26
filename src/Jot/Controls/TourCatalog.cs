using System;
using System.Collections.Generic;
using System.Linq;
using Jot.Recording;
using Jot.Services.Abstractions;
using Wpf.Ui.Controls;

namespace Jot.Controls;

/// <summary>One card in a tour: an icon, a title, and a body built at show-time so it can splice in live
/// values (e.g. the user's real chord — never a hardcoded shortcut). Adding a card is a one-entry change
/// to a tour's list; the window renders whatever's here.</summary>
internal sealed record TourCard(SymbolRegular Icon, string Title, Func<JotSettings, string> Body);

/// <summary>A named, self-contained tour: the window chrome text plus its ordered cards. One
/// <see cref="QuickTourWindow"/> renders any tour — the catalog below is the single registry.</summary>
internal sealed record Tour(string Id, string Title, string Heading, string Subhead, IReadOnlyList<TourCard> Cards);

/// <summary>
/// The single registry of tours. The original getting-started tour lives here alongside the per-feature
/// tours (shortcuts, AI setup, rewrite/transform, import, feedback). Keeping them in one list is the
/// whole point of the generalisation: shipping a new tour is one <see cref="Tour"/> entry — no new window,
/// no new plumbing. Copy bar: 2–4 cards, one short verb-first sentence each (~≤15 words), every chord via
/// <see cref="HotkeyChord.Display"/>.
/// </summary>
internal static class TourCatalog
{
    public const string GettingStartedId = "getting-started";

    /// <summary>The post-wizard essentials tour — content unchanged from the original, so its first-run
    /// behaviour and tests stay byte-for-byte. Its show-once state is <see cref="JotSettings.FirstRunTipsDone"/>,
    /// NOT <see cref="JotSettings.ShownTours"/> (a distinct wizard-tied lifecycle).</summary>
    public static readonly Tour GettingStarted = new(
        GettingStartedId, "Welcome to Jot", "A quick tour",
        "A few things and you're off — no setup needed.", new[]
        {
            new TourCard(SymbolRegular.Keyboard24, "Start and stop dictating",
                s => $"Press {HotkeyChord.Display(s.ToggleRecordingHotkey)} anywhere to start, then press it again " +
                     "to stop — or click the Jot icon in your taskbar tray."),
            new TourCard(SymbolRegular.Mic24, "Watch it as you speak",
                _ => "A small floating pill shows your words appearing live. Press Esc anytime to stop and save what " +
                     "you've said."),
            new TourCard(SymbolRegular.Settings24, "Make it yours",
                _ => "Change your shortcuts anytime on the Shortcuts page — you can even set a key to hold down while " +
                     "you talk. Pick your language and AI helper in Settings."),
        });

    /// <summary>Shortcuts tour — the three keyboard actions and where to rebind them.</summary>
    public static readonly Tour Shortcuts = new(
        "shortcuts", "Shortcuts", "Your shortcuts",
        "Do everything from the keyboard.", new[]
        {
            new TourCard(SymbolRegular.Keyboard24, "Dictate anywhere",
                s => $"Press {HotkeyChord.Display(s.ToggleRecordingHotkey)} to start, then press it again to stop."),
            new TourCard(SymbolRegular.Wand24, "Rewrite text",
                s => $"Select text, then press {HotkeyChord.Display(s.RewriteHotkey)} to transform it."),
            new TourCard(SymbolRegular.Options24, "Rebind anything",
                _ => "Change any shortcut on the Shortcuts page."),
        });

    /// <summary>AI-setup tour — offered to heavy users with no provider; opened from the Help hub too.</summary>
    public static readonly Tour Ai = new(
        "ai", "Set up AI", "Set up AI",
        "Rewrite and transform text with a provider you choose.", new[]
        {
            new TourCard(SymbolRegular.Brain24, "Pick a provider",
                _ => "Open Settings → AI to connect one."),
            new TourCard(SymbolRegular.Key24, "Your choice",
                _ => "Use OpenAI, Anthropic, Gemini, or a fully-local Ollama."),
            new TourCard(SymbolRegular.Wand24, "Unlocks rewrite",
                _ => "AI powers rewriting and transforming your text."),
        });

    /// <summary>Rewrite / "transform pane" tour — auto-opens right after AI is configured.</summary>
    public static readonly Tour Rewrite = new(
        "rewrite", "Rewrite & transform", "Rewrite & transform",
        "Reshape any text, right where it sits.", new[]
        {
            new TourCard(SymbolRegular.Wand24, "Transform a selection",
                s => $"Select text anywhere, then press {HotkeyChord.Display(s.RewriteHotkey)}."),
            new TourCard(SymbolRegular.TextBulletListSquare24, "Pick a prompt",
                _ => "Choose Summarize, Make formal, and more — pin favourites on the Prompts page."),
            new TourCard(SymbolRegular.Mic24, "Or just say it",
                s => $"Press {HotkeyChord.Display(s.RewriteWithVoiceHotkey)} to speak the instruction."),
        });

    /// <summary>Import tour — turning a recording into a transcript from the Recents drop zone.</summary>
    public static readonly Tour Import = new(
        "import", "Import a recording", "Recording to transcript",
        "Turn any audio or video into text.", new[]
        {
            new TourCard(SymbolRegular.ArrowUpload24, "Drop a file",
                _ => "Drop an audio or video file onto Recents."),
            new TourCard(SymbolRegular.Document24, "Or browse",
                _ => "Click browse to pick a file to transcribe."),
        });

    /// <summary>Feedback tour — how (and how safely) to report anything that breaks. Discoverable-only:
    /// the failure balloon and slow-stop notice are the contextual nudges, so this tour adds no new nudge.</summary>
    public static readonly Tour Feedback = new(
        "feedback", "Send feedback", "Something wrong?",
        "Tell the developer — every report helps.", new[]
        {
            new TourCard(SymbolRegular.PersonFeedback24, "Report a problem",
                _ => "Broke or felt slow? Open Help → Send feedback."),
            new TourCard(SymbolRegular.ShieldQuestion24, "Diagnostics, previewed",
                _ => "The diagnostics box adds hardware and recent activity — never your dictated text."),
            new TourCard(SymbolRegular.Send24, "Straight to the developer",
                _ => "It goes right to the developer. No email or account."),
        });

    /// <summary>
    /// Vocabulary tour — what custom vocabulary is, its two honest limits, and where the terms live.
    ///
    /// DISCOVERABLE-ONLY, like <see cref="Feedback"/>: no auto-trigger anywhere. The feature ships
    /// default-OFF inside Advanced features, so a tour that fired on its own would land almost entirely
    /// on people who never enabled it — noise, and about a feature they can't act on. The teachable
    /// moment (switching the toggle on) is already spoken for by the model-download prompt, and stacking
    /// a tour on top of a modal is worse than either alone.
    /// </summary>
    public static readonly Tour Vocabulary = new(
        "vocabulary", "Custom vocabulary", "Custom vocabulary",
        "Teach Jot the words it keeps getting wrong.", new[]
        {
            new TourCard(SymbolRegular.BookLetter24, "Add your words",
                _ => "Turn it on in Settings → Vocabulary, then open Manage… to add names, products and jargon."),
            new TourCard(SymbolRegular.ArrowDownload24, "One extra download",
                _ => $"Jot asks before fetching a {Services.CtcModelDownload.SizeMb} MB model. Like everything " +
                     "else, it stays on this PC."),
            // The honest limit, and it stopped being "English only" when E6 measured the model-free
            // corrector in 18 more languages (docs/plans/vocabulary-brake-per-language.md). The count
            // is pinned by VocabularyLimitsTests, so a language leaving the set makes this card wrong
            // somewhere that fails a build rather than somewhere a user finds it.
            new TourCard(SymbolRegular.Beaker24, "Experimental, and not every language",
                _ => $"Your terms work in {Jot.Vocabulary.VocabularyLimits.Languages.Count} languages. In " +
                     "English Jot listens for them in the audio; in the other " +
                     $"{Jot.Vocabulary.VocabularyLimits.Languages.Count - 1} it corrects near-miss " +
                     "spellings instead. Anything else is left untouched, and Settings says which " +
                     "you are in."),
        });

    /// <summary>Add-to-Vocabulary tour — the right-click gesture on a recording's transcript, which is
    /// the feature's flagship discovery moment and is otherwise invisible. Discoverable-only, for the
    /// same reason as <see cref="Vocabulary"/>.</summary>
    public static readonly Tour AddToVocabulary = new(
        "add-to-vocabulary", "Fix a word", "Fix a word for good",
        "Correct a transcript and teach Jot in one go.", new[]
        {
            new TourCard(SymbolRegular.Highlight24, "Select what Jot got wrong",
                _ => "Open a recording from Recents and select the misspelled word in the transcript."),
            new TourCard(SymbolRegular.Cursor24, "Right-click → Add to Vocabulary…",
                _ => "Type the spelling you wanted. Jot fixes this transcript and saves the term."),
            new TourCard(SymbolRegular.CheckmarkCircle24, "It sticks",
                _ => "Next time you say it, Jot writes it your way."),
        });

    /// <summary>Every named tour, in Help-hub display order. Getting-started leads; the rest are the
    /// per-feature tours. This is the list the dev <c>--tour &lt;name&gt;</c> hook and the Help hub read.</summary>
    public static readonly IReadOnlyList<Tour> All = new[]
        { GettingStarted, Shortcuts, Ai, Rewrite, Import, Vocabulary, AddToVocabulary, Feedback };

    /// <summary>Look a tour up by its Id (case-insensitive); null if the name is unknown.</summary>
    public static Tour? ById(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Record a tour as seen. Getting-started keeps its dedicated wizard-tied flag; every
    /// per-feature tour records its Id in the JSON-stable <see cref="JotSettings.ShownTours"/> list.
    /// Idempotent — re-opening a tour from the Help hub just re-saves the same state.</summary>
    public static void MarkShown(JotSettings s, string id)
    {
        if (id == GettingStartedId) { s.FirstRunTipsDone = true; return; }
        if (!s.ShownTours.Contains(id)) s.ShownTours.Add(id);
    }

    /// <summary>Whether a tour has already been shown (used to gate the once-ever auto-triggers).</summary>
    public static bool WasShown(JotSettings s, string id) =>
        id == GettingStartedId ? s.FirstRunTipsDone : s.ShownTours.Contains(id);
}

/// <summary>
/// Pure decision rules for the behavioural tour triggers — no UI, no side effects, so they're unit-tested
/// directly. Each answers "does this condition hold right now?"; the caller layers on the hard etiquette
/// gates (once-ever persistence, one nudge per launch, never over a live recording).
/// </summary>
internal static class TourTriggers
{
    /// <summary>The "real user" bar for the AI-setup nudge: enough dictations to have felt the core workflow
    /// before we suggest the optional AI layer.</summary>
    public const int AiNudgeMinDictations = 10;

    /// <summary>True once a real AI provider is set (anything but unset/"None").</summary>
    public static bool IsAiConfigured(string? provider) =>
        !string.IsNullOrWhiteSpace(provider) && !string.Equals(provider, "None", StringComparison.OrdinalIgnoreCase);

    /// <summary>AI-setup nudge: a real user who still has no provider and has never been nudged.</summary>
    public static bool ShouldNudgeAiSetup(JotSettings s, int totalDictations) =>
        totalDictations >= AiNudgeMinDictations && !IsAiConfigured(s.AiProvider) && !s.AiSetupNudgeDone;

    /// <summary>Rewrite/transform tour: the teachable moment AI flips from unset to configured.</summary>
    public static bool IsAiJustConfigured(string? before, string? after) =>
        !IsAiConfigured(before) && IsAiConfigured(after);
}
