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
/// The recording "reading surface": transcript, slim playback bar (real only when the row has audio
/// on disk), inline edit with an "edited" marker, tags, stub speaker detection, and real WebVTT
/// export. Constructed by the caller with the selected item and passed in as the page DataContext.
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

    public ObservableCollection<SpeakerTurn> SpeakerTurns { get; } = new();

    public bool IsDictation => Item.Kind == RecordingKind.Dictation;
    public bool IsRewrite => Item.Kind == RecordingKind.Rewrite;

    /// <summary>Sub-title meta line. Rewrites keep no audio, so show the model + kind rather than a
    /// misleading 0:00 duration; dictations show model + length.</summary>
    public string MetaText => IsRewrite
        ? $"{Item.ModelLabel} · Rewrite"
        : $"{Item.ModelLabel} · {Item.DurationText}";
    public bool CanPlay => Item.HasAudio;
    public bool HasSpeakers => SpeakerTurns.Count > 0;

    public RecordingDetailViewModel(RecordingItem item, IRecordingStore store, INavigator navigator)
    {
        Item = item;
        _store = store;
        _navigator = navigator;
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
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

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
