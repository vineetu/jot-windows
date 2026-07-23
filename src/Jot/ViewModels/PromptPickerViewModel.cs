using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jot.Models;
using Jot.Services;
using Jot.Services.Abstractions;

namespace Jot.ViewModels;

/// <summary>
/// Backs the rewrite prompt-picker overlay. Searchable view over the shared <see cref="PromptCatalog"/>,
/// sorted pinned → most-recently-used → alphabetical. Picking a prompt raises <see cref="Picked"/>.
///
/// Prompts flagged <see cref="PromptItem.NeedsInput"/> (e.g. Translate → which language) don't run on pick:
/// the palette switches to an inline "add a direction" step. The mic auto-starts there (no button press,
/// no silence timeout — the user needs time to think), the live caption streams into the field, and a stop
/// hint is shown. The user can instead just start typing — the first keystroke hands the field over from
/// the mic (keeping what they typed). <see cref="ConfirmAugment"/> then runs with body + detail.
/// </summary>
public sealed partial class PromptPickerViewModel : ObservableObject
{
    private readonly PromptCatalog _catalog;
    private readonly IPhraseDictation? _phrase;   // null only in headless tests that don't exercise voice
    private readonly ISettingsStore? _settings;   // null in headless tests; drives the one-time Shift+Enter tip
    private PromptItem? _augmentItem;             // the prompt awaiting its detail (needs-input or optional)
    private bool _applyingPartial;                // true while OnPartial writes the caption — so it isn't seen as typing

    private const int TipMaxOpens = 3;            // show the "⇧⏎ add direction" footer tip for this many opens

    public ICollectionView Prompts { get; }

    [ObservableProperty] private string _searchText = "";

    // Augment step state. IsAugmenting shows the input panel; IsListening is true while the mic is live
    // (auto-started on entry, ended by Enter/Stop/Esc or by the user starting to type).
    [ObservableProperty] private bool _isAugmenting;
    [ObservableProperty] private string _augmentLabel = "";
    [ObservableProperty] private string _augmentText = "";
    [ObservableProperty] private string _augmentPlaceholder = "";
    [ObservableProperty] private bool _isListening;

    // First-run coaching for Shift+Enter. True while the footer should showcase "⇧⏎ add a direction";
    // cleared the moment the user tries it (or after TipMaxOpens opens). Set from settings in the ctor.
    [ObservableProperty] private bool _showAugmentTip;

    /// <summary>Stop-hint line ("Esc to stop speaking…") — shown only while the mic is live.</summary>
    public bool ShowListeningHint => IsListening;
    /// <summary>Idle augment hint ("Enter rewrite · Esc back") — augment step, mic not live.</summary>
    public bool ShowAugmentIdleHint => IsAugmenting && !IsListening;

    /// <summary>Footer shows the normal shortcut row — prompt list up, no first-run tip active.</summary>
    public bool ShowShortcutHint => !IsAugmenting && !ShowAugmentTip;
    /// <summary>Footer shows the one-time "⇧⏎ add a direction" tip instead of the shortcut row.</summary>
    public bool ShowTipHint => !IsAugmenting && ShowAugmentTip;

    partial void OnIsAugmentingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowAugmentIdleHint));
        OnPropertyChanged(nameof(ShowShortcutHint));
        OnPropertyChanged(nameof(ShowTipHint));
    }

    partial void OnShowAugmentTipChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowShortcutHint));
        OnPropertyChanged(nameof(ShowTipHint));
    }

    partial void OnIsListeningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowListeningHint));
        OnPropertyChanged(nameof(ShowAugmentIdleHint));
    }

    // A text change that ISN'T from the live caption, while the mic is live, means the user began typing —
    // hand the field to them: stop the mic and keep what they typed (don't overwrite with the transcription).
    partial void OnAugmentTextChanged(string value)
    {
        if (!_applyingPartial && IsListening) _ = StopListeningAsync(adopt: false);
    }

    /// <summary>Raised when a prompt is committed: the rewrite runs with <c>item.BuildInstruction(detail)</c>.
    /// <c>detail</c> is null for plain prompts, or the typed/spoken parameter for needs-input prompts.</summary>
    public event Action<PromptItem, string?>? Picked;

    public PromptPickerViewModel(PromptCatalog catalog, IPhraseDictation? phrase = null, ISettingsStore? settings = null)
    {
        _catalog = catalog;
        _phrase = phrase;
        _settings = settings;

        // One-time Shift+Enter coach: show it for the first few opens until the user tries the feature.
        // A transient VM is created per open, so counting opens here is one increment per picker appearance.
        JotSettings? s = settings?.Current;
        ShowAugmentTip = s is { DirectionTipDone: false } && s.DirectionTipOpens < TipMaxOpens;
        if (ShowAugmentTip) { s!.DirectionTipOpens++; settings!.Save(); }

        var cvs = new CollectionViewSource { Source = catalog.Prompts };
        // Default first (so it lands at index 0, pre-selected → Enter runs it), then pinned, recent, alphabetical.
        cvs.SortDescriptions.Add(new SortDescription(nameof(PromptItem.IsDefault), ListSortDirection.Descending));
        cvs.SortDescriptions.Add(new SortDescription(nameof(PromptItem.IsPinned), ListSortDirection.Descending));
        cvs.SortDescriptions.Add(new SortDescription(nameof(PromptItem.LastPickedSeq), ListSortDirection.Descending));
        cvs.SortDescriptions.Add(new SortDescription(nameof(PromptItem.Title), ListSortDirection.Ascending));
        Prompts = cvs.View;
        Prompts.Filter = Matches;
    }

    partial void OnSearchTextChanged(string value)
    {
        Prompts.Refresh();
        Prompts.MoveCurrentToFirst(); // keep the top result selected as the user types
    }

    private bool Matches(object obj)
    {
        if (obj is not PromptItem p) return false;
        string q = SearchText.Trim();
        if (q.Length == 0) return true;
        return p.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Category.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Body.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    public void Pick(PromptItem? p)
    {
        if (p is null) return;
        _catalog.RecordPick(p);
        if (p.NeedsInput) EnterAugment(p);   // required (e.g. Translate) — collect the parameter first
        else Picked?.Invoke(p, null);        // runs immediately with the plain body
    }

    /// <summary>Shift+Enter: open the augment step for ANY prompt — even ones that run fine by default — so
    /// the user can add an optional direction (e.g. "for execs" to Summarize). Same field/mic as the required
    /// path; an empty answer just runs the plain body.</summary>
    [RelayCommand]
    public void PickWithAugment(PromptItem? p)
    {
        if (p is null) return;
        _catalog.RecordPick(p);
        MarkTipDone();   // they found the feature — stop coaching it
        EnterAugment(p);
    }

    // Open the type-or-speak step for a prompt. Needs-input prompts show their specific question; optional
    // ones show a generic header with the prompt's own hint as the field placeholder. Then auto-arm the mic
    // (mic not live yet, so clearing the field can't trigger the typing-takeover).
    private void EnterAugment(PromptItem p)
    {
        _augmentItem = p;
        AugmentLabel = p.NeedsInput ? (p.AugmentLabel ?? "") : "Add a direction";
        AugmentPlaceholder = string.IsNullOrWhiteSpace(p.VoiceAugmentHint) ? "Speak now, or type…" : p.VoiceAugmentHint!;
        AugmentText = "";
        IsAugmenting = true;
        StartListening();
    }

    // The Shift+Enter tip is one-shot: mark it done the first time the feature is used so it never nags again.
    private void MarkTipDone()
    {
        ShowAugmentTip = false;
        if (_settings is { Current.DirectionTipDone: false })
        {
            _settings.Current.DirectionTipDone = true;
            _settings.Save();
        }
    }

    /// <summary>Run the needs-input prompt with the collected detail. Stops the mic first (adopting the final
    /// transcription) so pressing Enter mid-speech just uses what was heard. Empty detail runs the plain body.</summary>
    [RelayCommand]
    public async Task ConfirmAugment()
    {
        if (_augmentItem is null) return;
        await StopListeningAsync(adopt: true);
        Picked?.Invoke(_augmentItem, AugmentText);
    }

    /// <summary>Back out of the augment step to the prompt list (no run).</summary>
    [RelayCommand]
    public async Task CancelAugment()
    {
        await StopListeningAsync(adopt: false);
        IsAugmenting = false;
        _augmentItem = null;
    }

    /// <summary>Stop the mic but stay in the augment step, keeping the transcription so far — bound to Esc
    /// (while listening) and the Stop button, so the user can review/edit before running.</summary>
    [RelayCommand]
    public Task StopSpeaking() => StopListeningAsync(adopt: true);

    // Arm the mic and stream the caption into the field. Robust: a start failure (no mic / recorder busy /
    // no streaming engine) just leaves the field editable for typing — the user is never stuck.
    private void StartListening()
    {
        if (_phrase is null || IsListening) return;
        _phrase.Partial += OnPartial;
        bool started = false;
        try { started = _phrase.Start(); }
        catch (Exception ex) { JotLog.Info("augment mic start failed: " + ex.Message); }
        if (started) IsListening = true;
        else _phrase.Partial -= OnPartial;
    }

    private async Task StopListeningAsync(bool adopt)
    {
        if (!IsListening || _phrase is null) return;
        IsListening = false;
        _phrase.Partial -= OnPartial;
        string text = await _phrase.StopAsync();
        // Adopt the final transcription only on the speak path (Enter / Stop / Esc-stop). Typing-takeover
        // passes adopt:false so the user's keystrokes aren't clobbered. IsListening is already false here,
        // so writing AugmentText can't re-trigger the typing-takeover.
        if (adopt && !string.IsNullOrWhiteSpace(text)) AugmentText = text.Trim();
    }

    // Live caption fires on a background thread; marshal to the UI, and ignore a late partial that lands
    // after the mic was stopped or the user took over (IsListening is false by then).
    private void OnPartial(string text)
    {
        void Apply()
        {
            if (!IsListening) return;
            _applyingPartial = true;
            AugmentText = text;
            _applyingPartial = false;
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Apply);
        else Apply();
    }

    [RelayCommand]
    private void TogglePin(PromptItem? p)
    {
        if (p is null) return;
        _catalog.TogglePin(p);
        Prompts.Refresh();
    }

    [RelayCommand]
    private void SetDefault(PromptItem? p)
    {
        if (p is not null) _catalog.SetDefault(p);
    }
}
