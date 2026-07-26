using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jot.Services.Abstractions;
using Jot.Services.Navigation;
using Jot.Transcription.Nemotron;
using Jot.Vocabulary;

namespace Jot.ViewModels;

/// <summary>
/// One card row on the Vocabulary page. A flat projection rebuilt on every change rather than a
/// binding straight onto <see cref="VocabularyTerm"/>: the core model is a plain data class shared
/// with the gate and the fixture tests, and giving it change notification purely so a ListView can
/// see an in-place edit would put UI concerns inside the ported subsystem.
/// </summary>
public sealed class VocabularyTermRow
{
    public VocabularyTermRow(VocabularyTerm term, string? warning)
    {
        Term = term;
        Warning = warning;
    }

    public VocabularyTerm Term { get; }
    public string Text => Term.Text;

    /// <summary>Copy lifted verbatim from the Mac's row so the three Windows entry points read the
    /// same.</summary>
    public string AliasSummary =>
        Term.Aliases.Count == 0 ? "" : "also heard as: " + string.Join(" · ", Term.Aliases);

    /// <summary>Never blocks saving — the user is trusted (ux §2.4).</summary>
    public string? Warning { get; }
    public bool HasWarning => Warning is not null;

    /// <summary>Aliases if there are any, else the warning, else nothing.</summary>
    public string Subtitle => AliasSummary.Length > 0 ? AliasSummary : Warning ?? "";
    public bool HasSubtitle => Subtitle.Length > 0;

    // WPF does NOT expose ToolTip as an automation name, so icon-only buttons carry their own.
    public string EditName => $"Edit {Text}";
    public string DeleteName => $"Delete {Text}";
}

/// <summary>
/// Backs <c>Views/VocabularyPage.xaml</c> — the dedicated term-management surface (DECIDE-2), built
/// on the PromptsPage list pattern but with <c>RecordingDetailPage</c>'s header, because it has no
/// nav-sidebar entry and <c>MainWindow</c> sets <c>IsBackButtonVisible="Collapsed"</c>: without
/// <see cref="BackCommand"/> the user lands on a page with no exit.
///
/// Editing is NEVER blocked on the vocabulary model, the language, or the master toggle. People
/// switch languages and download models later; silently locking their data behind either is worse
/// than telling them the truth, which is what the header note and the InfoBars do.
/// </summary>
public sealed partial class VocabularyViewModel : ObservableObject
{
    /// <summary>Above this many terms the search box appears — a 3-term list fronted by a search box
    /// reads as clutter.</summary>
    private const int SearchThreshold = 15;

    /// <summary>D12's copy, in ONE place: both the finished row and the add/edit form show it, and two
    /// wordings of the same fact is how a user learns to distrust both.</summary>
    public const string UnsupportedWarning =
        "Jot can't listen for this — the vocabulary model only knows plain English letters, " +
        "so digits, accents and hyphens can never be matched.";

    private readonly VocabularyStore _store;
    private readonly CorrectionStore _corrections;
    private readonly ISettingsStore _settings;
    private readonly IVocabularySpotter _spotter;
    private readonly INavigator _navigator;
    private readonly ICommonWordsProvider _commonWords;

    public ObservableCollection<VocabularyTermRow> Rows { get; } = [];

    /// <summary>Chips in the add/edit form's "When Jot hears" field.</summary>
    public ObservableCollection<string> NewAliases { get; } = [];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _newTerm = "";
    [ObservableProperty] private string _newAlias = "";
    [ObservableProperty] private VocabularyTerm? _editingTerm;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _statusSeverity = "Success";   // ui:InfoBarSeverity name
    [ObservableProperty] private bool _isStatusOpen;

    // Bulk paste import (ux §2.5): the Term field is single-line, so a pasted list would otherwise
    // become one absurd term. The form switches shape instead of silently mangling it.
    [ObservableProperty] private bool _isImportMode;
    [ObservableProperty] private string _importPreview = "";
    private IReadOnlyList<VocabularyTerm> _importCandidates = [];

    private VocabularyTerm? _lastDeleted;
    private int _lastDeletedIndex;

    public VocabularyViewModel(
        VocabularyStore store,
        CorrectionStore corrections,
        ISettingsStore settings,
        IVocabularySpotter spotter,
        INavigator navigator,
        ICommonWordsProvider? commonWords = null)
    {
        _store = store;
        _corrections = corrections;
        _settings = settings;
        _spotter = spotter;
        _navigator = navigator;
        _commonWords = commonWords ?? EmbeddedCommonWordsProvider.Shared;
        _store.Terms.CollectionChanged += (_, _) => Refresh();
        Refresh();
    }

    // MARK: - Header

    public string Subtitle =>
        "Terms Jot should prefer when it transcribes. Kept on this PC. Works best under about 100 terms."
        + Mode switch
        {
            VocabularyRunner.VocabularyMode.Acoustic => "",
            VocabularyRunner.VocabularyMode.Textual => " · spelling matching only",
            _ => " · not active in this language",
        };

    /// <summary>D5 — which matching path this language gets. Stated on this page too, so the state is
    /// visible from the management surface and not only from Settings.</summary>
    public VocabularyRunner.VocabularyMode Mode => VocabularyRunner.ModeFor(_settings.Current.Language);
    public bool LanguageOk => Mode == VocabularyRunner.VocabularyMode.Acoustic;

    /// <summary>Model state, as a one-line tertiary note under the subtitle. Never a blocker — and
    /// never shown outside English, where the checkpoint is not what the terms are waiting on.</summary>
    public string ModelNote => _spotter.IsReady || !LanguageOk
        ? ""
        : "Vocabulary model not downloaded — Jot is matching spellings only until it is.";
    public bool HasModelNote => ModelNote.Length > 0;

    // MARK: - List

    public bool ShowSearch => _store.Terms.Count > SearchThreshold;
    public bool IsEmpty => _store.Terms.Count == 0;
    public bool HasNoMatches => !IsEmpty && Rows.Count == 0;
    public bool CanUndoDelete => _lastDeleted is not null;

    /// <summary>The cap is a LATENCY budget — every term is a spotter query — so it disables the
    /// Add button rather than silently dropping the term.</summary>
    public bool AtCap => _store.Terms.Count >= VocabularyStore.MaxTerms;
    public bool CanAdd => !AtCap || IsEditing;
    public string CapMessage => AtCap
        ? $"You've reached {VocabularyStore.MaxTerms} terms. Remove one to add another."
        : "";
    public bool ShowCapMessage => AtCap && !IsEditing;

    partial void OnSearchTextChanged(string value) => Refresh();

    // MARK: - Form

    public bool IsEditing => EditingTerm is not null;
    public string FormHeader => IsEditing ? "Edit term" : "Add a term";
    public string AddButtonText => IsEditing ? "Save changes" : "Add term";

    /// <summary>Renaming orphans every learned net keyed on the OLD pair
    /// (<c>CorrectionRecord.MappingKey</c>), so v1.3's rule is rename = delete + create — and says
    /// so, inline, only when there is history to lose. Migrating nets across a rename is v1.4:
    /// doing it wrong silently transfers a learned override onto a different word.</summary>
    public bool ShowRenameWarning =>
        EditingTerm is { } t
        && VocabularyStore.SanitizeTerm(NewTerm) != t.Text
        && HasCorrectionHistory(t.Text);

    /// <summary>D12 — the model can never hear this one, so the form says so before it is saved
    /// too, not only on the finished row.</summary>
    public bool ShowSpottabilityWarning =>
        VocabularyStore.SanitizeTerm(NewTerm).Length > 0
        && _spotter.CheckTerm(NewTerm) == TermSpottability.Unsupported;
    public string SpottabilityWarning => UnsupportedWarning;

    partial void OnEditingTermChanged(VocabularyTerm? value)
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(FormHeader));
        OnPropertyChanged(nameof(AddButtonText));
        OnPropertyChanged(nameof(ShowRenameWarning));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(ShowCapMessage));
    }

    partial void OnNewTermChanged(string value)
    {
        OnPropertyChanged(nameof(ShowRenameWarning));
        OnPropertyChanged(nameof(ShowSpottabilityWarning));
    }

    // MARK: - Commands

    [RelayCommand]
    private void Back() => _navigator.GoBack();

    [RelayCommand]
    private void AddAlias()
    {
        string clean = VocabularyStore.SanitizeTerm(NewAlias);
        NewAlias = "";
        if (clean.Length == 0) return;
        if (NewAliases.Any(a => CorrectionKey.Normalize(a) == CorrectionKey.Normalize(clean))) return;
        NewAliases.Add(clean);
    }

    [RelayCommand]
    private void RemoveAlias(string? alias)
    {
        if (alias is not null) NewAliases.Remove(alias);
    }

    [RelayCommand]
    private void EditTerm(VocabularyTermRow? row)
    {
        if (row is null) return;
        CancelImport();
        EditingTerm = row.Term;
        NewTerm = row.Term.Text;
        NewAliases.Clear();
        foreach (string a in row.Term.Aliases) NewAliases.Add(a);
        StatusMessage = "";
    }

    [RelayCommand]
    private void DeleteTerm(VocabularyTermRow? row)
    {
        if (row is null) return;
        if (ReferenceEquals(row.Term, EditingTerm)) CancelEdit();
        _lastDeletedIndex = _store.Terms.IndexOf(row.Term);
        _lastDeleted = row.Term;
        _store.Remove(row.Term);
        // No modal: nothing else in this app confirms a delete, and an inline undo is both cheaper
        // and more reversible than a yes/no box.
        OnPropertyChanged(nameof(CanUndoDelete));
        Report($"Removed {row.Term.Text}.", "Informational");
    }

    [RelayCommand]
    private void UndoDelete()
    {
        if (_lastDeleted is not { } term) return;
        _lastDeleted = null;
        _store.Add(term.Text, term.Aliases);
        // Add appends; restore the author order the user had.
        VocabularyTerm? restored = _store.Terms.LastOrDefault(
            t => CorrectionKey.Normalize(t.Text) == CorrectionKey.Normalize(term.Text));
        if (restored is not null && _lastDeletedIndex >= 0 && _lastDeletedIndex < _store.Terms.Count)
            _store.Terms.Move(_store.Terms.IndexOf(restored), _lastDeletedIndex);
        StatusMessage = "";
        OnPropertyChanged(nameof(CanUndoDelete));
    }

    [RelayCommand]
    private void CancelEdit()
    {
        EditingTerm = null;
        NewTerm = "";
        NewAlias = "";
        NewAliases.Clear();
        CancelImport();
    }

    [RelayCommand]
    private void AddTerm()
    {
        if (IsImportMode) { AddImported(); return; }

        string clean = VocabularyStore.SanitizeTerm(NewTerm);
        if (clean.Length == 0) return;

        if (EditingTerm is { } editing)
        {
            string message = MovedAliasNotice(NewAliases, editing);
            _store.Update(editing, clean, NewAliases);
            CancelEdit();
            Refresh();
            if (message.Length > 0) Report(message, "Informational");
            return;
        }

        VocabularyTerm? existing = _store.Terms.FirstOrDefault(
            t => CorrectionKey.Normalize(t.Text) == CorrectionKey.Normalize(clean));
        if (existing is not null)
        {
            // Never a second row for the same term: merge, then say exactly what changed.
            int before = existing.Aliases.Count;
            _store.Add(clean, NewAliases);
            int added = existing.Aliases.Count - before;
            CancelEdit();
            Refresh();
            Report(added == 0
                ? "Already in your list."
                : $"Already in your list — added {added} new {(added == 1 ? "spelling" : "spellings")}.",
                "Informational");
            return;
        }

        if (AtCap) { Report(CapMessage, "Warning"); return; }

        string moved = MovedAliasNotice(NewAliases, null);
        _store.Add(clean, NewAliases);
        CancelEdit();
        Refresh();
        if (moved.Length > 0) Report(moved, "Informational");
    }

    // MARK: - Bulk paste import

    /// <summary>
    /// Called from the page's paste handler. Returns true when the pasted body looks like a LIST —
    /// a newline, comma or semicolon and ≥2 candidates — in which case the form switches to import
    /// mode and the paste is swallowed rather than dumped into a single-line field.
    /// </summary>
    public bool TryBeginImport(string? pasted)
    {
        if (string.IsNullOrWhiteSpace(pasted)) return false;
        if (!pasted.Any(c => c is '\n' or '\r' or ',' or ';')) return false;

        // The cross-platform "simple format" parser, so a list authored on Mac/iOS (Term: a, b)
        // pastes in here unchanged rather than needing its own dialect.
        IReadOnlyList<VocabularyTerm> parsed = VocabularyFile.Parse(pasted.Replace(',', '\n').Replace(';', '\n'));
        if (parsed.Count < 2) return false;

        _importCandidates = parsed;
        ImportPreview = string.Join(", ", parsed.Take(6).Select(t => t.Text))
                        + (parsed.Count > 6 ? ", …" : "");
        IsImportMode = true;
        NewTerm = pasted.ReplaceLineEndings(" ").Trim();
        OnPropertyChanged(nameof(ImportCountText));
        OnPropertyChanged(nameof(ImportButtonText));
        return true;
    }

    public string ImportCountText => $"Looks like a list — {_importCandidates.Count} terms found.";
    public string ImportButtonText => $"Add {_importCandidates.Count} terms";

    /// <summary>"Add as one term": the user meant the pasted blob literally after all.</summary>
    [RelayCommand]
    private void AddAsOneTerm()
    {
        IsImportMode = false;
        _importCandidates = [];
        AddTerm();
    }

    private void AddImported()
    {
        int added = 0;
        int skipped = 0;
        bool hitCap = false;
        foreach (VocabularyTerm candidate in _importCandidates)
        {
            string clean = VocabularyStore.SanitizeTerm(candidate.Text);
            // Terms under 3 characters are skipped by the gate anyway, so importing them would
            // silently pad the list with rows that can never fire.
            if (clean.Length < 3) { skipped++; continue; }
            if (_store.Terms.Any(t => CorrectionKey.Normalize(t.Text) == CorrectionKey.Normalize(clean)))
            {
                skipped++;
                continue;
            }
            if (AtCap) { hitCap = true; skipped++; continue; }
            _store.Add(clean, candidate.Aliases);
            added++;
        }

        int candidates = _importCandidates.Count;
        CancelEdit();
        Refresh();

        if (added == 0)
            Report($"Nothing added — those {candidates} terms are already in your list.", "Informational");
        else if (skipped == 0)
            Report($"Added {added} {(added == 1 ? "term" : "terms")}.", "Success");
        else if (hitCap)
            Report($"Added {added} terms. Skipped {skipped} — the list is full at {VocabularyStore.MaxTerms}.", "Warning");
        else
            Report($"Added {added} terms. Skipped {skipped} — already in your list, or under 3 characters.", "Success");
    }

    private void CancelImport()
    {
        IsImportMode = false;
        _importCandidates = [];
        ImportPreview = "";
    }

    // MARK: - Internals

    /// <summary>An alias belonging to two terms makes the gate's only plausibility lever ambiguous,
    /// so the store never lets the duplicate persist — last write wins. That has to be LOUD, or the
    /// alias silently vanishes from the term the user put it on first.</summary>
    private string MovedAliasNotice(IEnumerable<string> aliases, VocabularyTerm? self)
    {
        foreach (string alias in aliases)
        {
            string key = CorrectionKey.Normalize(alias);
            if (key.Length == 0) continue;
            VocabularyTerm? owner = _store.Terms.FirstOrDefault(
                t => !ReferenceEquals(t, self) && t.Aliases.Any(a => CorrectionKey.Normalize(a) == key));
            if (owner is not null) return $"\"{alias}\" was listed under {owner.Text} — moved it here.";
        }
        return "";
    }

    private bool HasCorrectionHistory(string term)
    {
        string key = CorrectionKey.Lowercased(term);
        return _corrections.Snapshot().Any(o => CorrectionKey.Lowercased(o.Term) == key && o.Net != 0);
    }

    private void Report(string message, string severity)
    {
        StatusSeverity = severity;
        StatusMessage = message;
        IsStatusOpen = message.Length > 0;
    }

    /// <summary>Rebuild the projected rows. Called on every store change AND after every in-place
    /// edit, because <see cref="VocabularyStore.Update"/> mutates a term without touching the
    /// collection.</summary>
    public void Refresh()
    {
        string q = SearchText.Trim();
        Rows.Clear();
        foreach (VocabularyTerm t in _store.Terms)
        {
            if (q.Length > 0
                && !t.Text.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !t.Aliases.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase)))
                continue;
            Rows.Add(new VocabularyTermRow(t, WarningFor(t)));
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(ShowSearch));
        OnPropertyChanged(nameof(AtCap));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(CapMessage));
        OnPropertyChanged(nameof(ShowCapMessage));
    }

    /// <summary>
    /// Re-check every saved row's warning against the spotter as it is NOW.
    ///
    /// <see cref="VocabularyTermRow.Warning"/> is a snapshot taken in <see cref="Refresh"/>, and the
    /// D12 half of it (<see cref="IVocabularySpotter.CheckTerm"/>) is an answer that CHANGES under it:
    /// it is <c>Unknown</c> — deliberately silent — until the checkpoint is readable. So a list built
    /// before the model finished downloading keeps its blank warnings forever, and the user sees the
    /// add form warn "Jot can't listen for this" about the very term whose saved row above says
    /// nothing. (The other half of that bug was <c>CtcVocabularySpotter.CheckTerm</c> answering
    /// <c>Unknown</c> under lock contention with startup's Warm; that is fixed at the source.)
    ///
    /// Rebuilds ONLY when a warning actually changed — arriving at an already-correct list must not
    /// clear the ListView's selection out from under the user.
    /// </summary>
    public void Revalidate()
    {
        OnPropertyChanged(nameof(ModelNote));
        OnPropertyChanged(nameof(HasModelNote));
        OnPropertyChanged(nameof(ShowSpottabilityWarning));
        if (Rows.Any(r => r.Warning != WarningFor(r.Term))) Refresh();
    }

    /// <summary>
    /// ux §2.4's table, in priority order. Deliberately checks the real bundled 24k list rather than
    /// the Mac's hard-coded ~90-word watchlist — and its copy states what the gate ACTUALLY does:
    /// it blocks the swap and surfaces it on the recording. It does NOT ask, because D8's deck
    /// filter admits applied corrections only.
    /// </summary>
    public string? WarningFor(VocabularyTerm term)
    {
        string text = term.Text;
        if (text.Trim().Length <= 2)
            return "Too short — terms under 3 characters are skipped to avoid false replacements.";
        if (VocabularyStore.WordCount(text) > VocabularyStore.MaxTermWords)
            return $"Use a single word or short phrase (max {VocabularyStore.MaxTermWords} words).";
        // D12 · the acoustic answer, from the spotter itself. `Unknown` (no model on disk yet) is
        // deliberately silent: warning there would flag EVERY term as broken before the download
        // finishes, which is the opposite of honest.
        if (_spotter.CheckTerm(text) == TermSpottability.Unsupported)
            return UnsupportedWarning;
        if (IsCommonWord(text))
            return "Common word — Jot won't swap this on its own. You can confirm it on the recording.";
        if (text.Trim().Length <= 4 && term.Aliases.Count == 0)
            return "Add a sounds-like spelling — Jot rarely hears short acronyms correctly on its own.";
        return null;
    }

    private bool IsCommonWord(string text)
    {
        string? resource = EmbeddedCommonWordsProvider.ResourceFor(
            NemotronLocales.Normalize(_settings.Current.Language));
        IReadOnlySet<string> words = _commonWords.Words(resource);
        return words.Count > 0 && words.Contains(CorrectionKey.Normalize(text));
    }
}
