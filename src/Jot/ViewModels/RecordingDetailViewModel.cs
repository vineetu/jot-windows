using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jot.Models;
using Jot.Services.Abstractions;
using Jot.Services.Navigation;
using Jot.Transcription;
using Microsoft.Extensions.DependencyInjection;

namespace Jot.ViewModels;

/// <summary>One diarized turn: who spoke, what they said, and the colour for their blocks.</summary>
public sealed record SpeakerTurn(string Speaker, string Text, Brush Color);

/// <summary>
/// The vocabulary dependencies this page needs, bundled into one optional constructor argument so
/// the existing three-argument shape keeps working (and so a test can drive the review + right-click
/// logic with in-memory stores).
/// </summary>
public sealed record VocabularyServices(
    Jot.Vocabulary.VocabularyStore Terms,
    Jot.Vocabulary.CorrectionStore Corrections,
    Jot.Vocabulary.CorrectionProvenance Provenance,
    ISettingsStore Settings,
    Jot.Vocabulary.IVocabularySpotter Spotter);

/// <summary>
/// The recording "reading surface": transcript, playback bar (real only when the row has audio on
/// disk), inline edit, tags, stub speaker detection, WebVTT export. Constructed with the selected
/// item as the page DataContext.
/// </summary>
public sealed partial class RecordingDetailViewModel : ObservableObject
{
    private readonly IRecordingStore _store;
    private readonly INavigator _navigator;
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _tick;
    private bool _mediaOpened;

    public RecordingItem Item { get; }

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editableTranscript = "";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private string _newTag = "";
    [ObservableProperty] private bool _showPlain = true;

    [ObservableProperty] private bool _isFindReplaceOpen;
    [ObservableProperty] private string _findText = "";
    [ObservableProperty] private string _replaceText = "";
    [ObservableProperty] private bool _matchCase;

    /// <summary>Live match count for the current Find text over the transcript.</summary>
    public string MatchSummary
    {
        get
        {
            if (string.IsNullOrEmpty(FindText)) return "";
            int n = CountMatches(Item.Transcript, FindText, MatchCase);
            return n switch { 0 => "No matches", 1 => "1 match", _ => $"{n} matches" };
        }
    }

    partial void OnFindTextChanged(string value) => OnPropertyChanged(nameof(MatchSummary));
    partial void OnMatchCaseChanged(bool value) => OnPropertyChanged(nameof(MatchSummary));

    public ObservableCollection<SpeakerTurn> SpeakerTurns { get; } = new();

    public bool IsDictation => Item.Kind == RecordingKind.Dictation;
    public bool IsRewrite => Item.Kind == RecordingKind.Rewrite;

    /// <summary>Sub-title meta line. Rewrites keep no audio, so show model + kind rather than a
    /// misleading 0:00 duration; dictations show model + length.</summary>
    public string MetaText => IsRewrite
        ? $"{Item.ModelLabel} · Rewrite"
        : $"{Item.ModelLabel} · {Item.DurationText}";
    public bool CanPlay => Item.HasAudio;
    public bool HasSpeakers => SpeakerTurns.Count > 0;

    /// <summary>The review + learn block (ux §5). Null when the page was built without vocabulary
    /// services — the block simply never renders, which is also what happens for a rewrite row.</summary>
    public VocabularyReviewViewModel? Review { get; }

    private readonly VocabularyServices? _vocabulary;

    public RecordingDetailViewModel(RecordingItem item, IRecordingStore store, INavigator navigator,
        VocabularyServices? vocabulary = null)
    {
        Item = item;
        _store = store;
        _navigator = navigator;
        _vocabulary = vocabulary;
        if (vocabulary is not null)
        {
            Review = new VocabularyReviewViewModel(
                item, vocabulary.Provenance, vocabulary.Corrections, vocabulary.Terms, vocabulary.Settings);
        }
        EditableTranscript = item.Transcript;
        Duration = item.DurationSeconds;

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _tick.Tick += (_, _) =>
        {
            if (_player.NaturalDuration.HasTimeSpan) Position = _player.Position.TotalSeconds;
        };
        _player.MediaOpened += (_, _) =>
        {
            if (_player.NaturalDuration.HasTimeSpan) Duration = _player.NaturalDuration.TimeSpan.TotalSeconds;
        };
        _player.MediaEnded += (_, _) => Stop();
    }

    [RelayCommand]
    private void Back() => _navigator.GoBack();

    [RelayCommand]
    private void ToggleEdit()
    {
        EditableTranscript = Item.Transcript;
        IsEditing = true;
    }

    [RelayCommand]
    private void SaveEdit()
    {
        if (EditableTranscript != Item.Transcript)
        {
            Item.Transcript = EditableTranscript;
            Item.IsEdited = true;
        }
        IsEditing = false;
        OnPropertyChanged(nameof(MatchSummary)); // transcript may have changed under an open find bar
        // A free-form rewrite has no geometry to shift. The review block re-reads through the
        // provenance reconcile, which either re-maps the anchors through a real diff or — when the
        // edit was wholesale — leaves nothing resolvable, which the block reports honestly instead
        // of silently vanishing. Deliberately NOT "hide the block when IsEdited": that latch is
        // one-way and is also set by ReplaceNext, ReplaceAll and this feature's own right-click add.
        Review?.Reload();
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    // Find & Replace commands mutate Item.Transcript directly; the store auto-persists.

    [RelayCommand]
    private void OpenFindReplace() => IsFindReplaceOpen = true;

    [RelayCommand]
    private void CloseFindReplace() => IsFindReplaceOpen = false;

    [RelayCommand]
    private void ReplaceNext()
    {
        if (string.IsNullOrEmpty(FindText)) return;
        var cmp = MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int i = Item.Transcript.IndexOf(FindText, cmp);
        if (i < 0) return;
        Item.Transcript = Item.Transcript.Remove(i, FindText.Length).Insert(i, ReplaceText ?? "");
        Item.IsEdited = true;
        EditableTranscript = Item.Transcript; // keep edit-mode snapshot in sync so a later Save can't clobber
        OnPropertyChanged(nameof(MatchSummary));
        Review?.Reload();   // one known-geometry splice; the reconcile diff maps it exactly
    }

    [RelayCommand]
    private void ReplaceAll()
    {
        if (string.IsNullOrEmpty(FindText)) return;
        var cmp = MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string updated = Item.Transcript.Replace(FindText, ReplaceText ?? "", cmp);
        if (updated == Item.Transcript) return;
        Item.Transcript = updated;
        Item.IsEdited = true;
        EditableTranscript = Item.Transcript; // keep edit-mode snapshot in sync so a later Save can't clobber
        OnPropertyChanged(nameof(MatchSummary));
        // N sites, N deltas, and this method never computes the indices — so nothing here can report
        // a span. The reconcile's diff maps them anyway; anything it can't is hidden, and a
        // whole-transcript miss degrades to the honest one-liner.
        Review?.Reload();
    }

    // MARK: - Add to Vocabulary (ux §2.7)

    /// <summary>
    /// Is this selection a plausible term? Mirrors Mac + iOS: ≤4 words, ≤60 chars, contains letters,
    /// edge punctuation trimmed, and not already a term. A FAILING selection leaves the menu item
    /// present but disabled — a missing item reads as a bug, a disabled one reads as a rule.
    /// </summary>
    public bool CanAddToVocabulary(string? selection)
    {
        if (_vocabulary is null || !IsDictation) return false;
        string clean = Jot.Vocabulary.VocabularyStore.SanitizeTerm(selection);
        if (clean.Length is 0 or > 60) return false;
        if (!clean.Any(char.IsLetter)) return false;
        if (Jot.Vocabulary.VocabularyStore.WordCount(clean) > Jot.Vocabulary.VocabularyStore.MaxTermWords) return false;
        return _vocabulary.Terms.Find(clean) is null;
    }

    /// <summary>Status line under the two fields — it must say what will ACTUALLY happen, including
    /// "saved, but nothing will use it yet" (ux §2.7's table).</summary>
    public string AddToVocabularyStatus()
    {
        if (_vocabulary is null) return "";
        JotSettings s = _vocabulary.Settings.Current;
        if (!s.VocabularyEnabled)
            return "Custom vocabulary is off — turn it on in Settings to use this.";
        if (Transcription.Nemotron.NemotronLocales.Normalize(s.Language)
                .Equals(Transcription.Nemotron.NemotronLocales.AutoCode, StringComparison.OrdinalIgnoreCase))
            return "Saved. Vocabulary needs a language — yours is on Auto detect.";
        Jot.Vocabulary.VocabularyRunner.VocabularyMode mode =
            Jot.Vocabulary.VocabularyRunner.ModeFor(s.Language);
        if (mode == Jot.Vocabulary.VocabularyRunner.VocabularyMode.Off)
            return "Saved. Vocabulary doesn't cover this language yet.";
        if (mode == Jot.Vocabulary.VocabularyRunner.VocabularyMode.Textual)
            return "Future dictations will fix near-miss spellings of this term.";
        if (!_vocabulary.Spotter.IsReady)
            return "Saved. Jot matches spellings now, and listens for the term once the vocabulary " +
                   "model (about 130 MB) has downloaded.";
        return "Future dictations will prefer this spelling.";
    }

    /// <summary>
    /// The gesture's three effects, in one action:
    /// (1) the selected span in THIS transcript becomes the canonical term — the user's immediate
    ///     annoyance is fixed;
    /// (2) the term is created (or the heard form appended as an alias of an existing one), so
    ///     FUTURE dictations improve;
    /// (3) the pair starts at net 1, which arms the learned override immediately for a rare/OOV
    ///     original.
    ///
    /// Guard rail from iOS scar tissue: if every word of the typed term is an everyday word, fix the
    /// text and create NO term — "that's the 'what is this?' test; ordinary rewording isn't
    /// vocabulary." Returns the line to show the user.
    /// </summary>
    public string AddToVocabulary(int selectionStart, int selectionLength, string? heardRaw, string? termRaw)
    {
        if (_vocabulary is null) return "";
        string heard = Jot.Vocabulary.VocabularyStore.SanitizeTerm(heardRaw);
        string term = Jot.Vocabulary.VocabularyStore.SanitizeTerm(termRaw);
        if (term.Length == 0) return "";

        // (1) Fix this transcript. Bounds-checked against the LIVE string (the selection was taken
        // from the control, and nothing guarantees it is still valid) and, crucially, whitespace-
        // trimmed: WPF's double-click hands us "Venith ", and splicing the trimmed term over that
        // raw range is what published "My name is VineetSriram."
        if (Jot.Vocabulary.VocabularySplice.TrySelectionSpan(
                Item.Transcript, selectionStart, selectionLength,
                out Jot.Vocabulary.VocabularySplice.Span span))
        {
            string updated = Jot.Vocabulary.VocabularySplice.Replace(Item.Transcript, span, term);
            if (updated != Item.Transcript)
            {
                Item.Transcript = updated;
                Item.IsEdited = true;
                EditableTranscript = updated;
                OnPropertyChanged(nameof(MatchSummary));
            }
        }

        if (IsEverydayPhrase(term))
        {
            Review?.Reload();
            return $"Fixed here. \"{term}\" is an everyday phrase, so it wasn't added to your vocabulary.";
        }

        // (2) + (3). Note what (3) does NOT do for a COMMON-word original: Decide step (0) requires
        // !isCommon, so such a pair stays blocked forever while now carrying Prior ≥ 1 — which is
        // exactly why the ask deck is filtered to applied corrections (VocabularyRunner.SelectAsks).
        if (heard.Length > 0)
        {
            _vocabulary.Terms.AddMapping(heard, term);
            _vocabulary.Corrections.Adjust(heard, term, +1);
        }
        else
        {
            _vocabulary.Terms.Add(term);
        }

        Review?.Reload();
        return AddToVocabularyStatus();
    }

    private bool IsEverydayPhrase(string term)
    {
        if (_vocabulary is null) return false;
        string? resource = Jot.Vocabulary.EmbeddedCommonWordsProvider.ResourceFor(
            Transcription.Nemotron.NemotronLocales.Normalize(_vocabulary.Settings.Current.Language));
        IReadOnlySet<string> common = Jot.Vocabulary.EmbeddedCommonWordsProvider.Shared.Words(resource);
        if (common.Count == 0) return false;
        string[] words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0
            && words.All(w => common.Contains(Jot.Vocabulary.CorrectionKey.Normalize(w)));
    }

    private static int CountMatches(string haystack, string needle, bool matchCase)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, cmp)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (!CanPlay || Item.WavPath is null) return;
        if (!_mediaOpened) { _player.Open(new Uri(Item.WavPath)); _mediaOpened = true; }

        if (IsPlaying) { _player.Pause(); IsPlaying = false; _tick.Stop(); }
        else { _player.Play(); IsPlaying = true; _tick.Start(); }
    }

    private void Stop()
    {
        _player.Stop();
        IsPlaying = false;
        Position = 0;
        _tick.Stop();
    }

    [RelayCommand]
    private void Copy()
    {
        try { System.Windows.Clipboard.SetText(Item.Transcript); } catch { /* clipboard busy */ }
    }

    [RelayCommand]
    private void RevealInExplorer()
    {
        if (Item.WavPath is not null && File.Exists(Item.WavPath))
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{Item.WavPath}\"");
    }

    [RelayCommand]
    private void Delete()
    {
        _store.Delete(Item);
        _navigator.GoBack();
    }

    [RelayCommand]
    private void AddTag()
    {
        string t = NewTag.Trim();
        if (t.Length > 0 && !Item.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))
            Item.Tags.Add(t);
        NewTag = "";
    }

    [RelayCommand]
    private void RemoveTag(string? tag)
    {
        if (tag is not null) Item.Tags.Remove(tag);
    }

    [RelayCommand]
    private async Task ReTranscribe()
    {
        if (!Item.IsPending) return;

        if (Item.WavPath is null || !File.Exists(Item.WavPath))
        {
            SetTranscript("(No audio file is available to transcribe.)");
            return;
        }

        var transcriber = App.Services.GetRequiredService<ITranscriber>();
        try
        {
            float[] samples = await Task.Run(() => WavAudio.ReadMono16k(Item.WavPath));
            string text = await transcriber.TranscribeAsync(samples, WavAudio.SampleRate);
            SetTranscript(string.IsNullOrWhiteSpace(text) ? "(Nothing was transcribed.)" : text.Trim());
        }
        catch (Exception ex)
        {
            SetTranscript($"(Transcription failed: {ex.Message})");
        }
    }

    private void SetTranscript(string text)
    {
        Item.Transcript = text;
        Item.Status = RecordingStatus.Complete;
        EditableTranscript = text;
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(MatchSummary));
    }

    [RelayCommand]
    private void DetectSpeakers()
    {
        // Stub diarization: alternate speakers by sentence so the per-speaker rendering is visible.
        SpeakerTurns.Clear();
        string[] sentences = Item.Transcript.Split(
            '.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Brush a = new SolidColorBrush(Color.FromRgb(0x4C, 0x8B, 0xF5));
        Brush b = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
        for (int i = 0; i < sentences.Length; i++)
            SpeakerTurns.Add(new SpeakerTurn($"Speaker {i % 2 + 1}", sentences[i] + ".", i % 2 == 0 ? a : b));

        ShowPlain = false;
        OnPropertyChanged(nameof(HasSpeakers));
    }

    [RelayCommand]
    private void ExportVtt()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = SanitizeFileName(Item.Title) + ".vtt",
            Filter = "WebVTT (*.vtt)|*.vtt",
        };
        if (dlg.ShowDialog() == true)
            File.WriteAllText(dlg.FileName, BuildVtt());
    }

    private string BuildVtt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT").AppendLine();

        if (HasSpeakers)
        {
            double t = 0;
            foreach (SpeakerTurn turn in SpeakerTurns)
            {
                double end = t + 4;
                sb.AppendLine($"{Ts(t)} --> {Ts(end)}");
                sb.AppendLine($"<v {turn.Speaker}>{turn.Text}").AppendLine();
                t = end;
            }
        }
        else
        {
            sb.AppendLine($"{Ts(0)} --> {Ts(Math.Max(1, Item.DurationSeconds))}");
            sb.AppendLine(Item.Transcript).AppendLine();
        }
        return sb.ToString();
    }

    private static string Ts(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.000";
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
