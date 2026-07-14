using System.IO;
using Jot.Delivery;
using Jot.Models;
using Jot.Recording;
using Jot.Services;
using Jot.Services.Abstractions;
using Jot.Services.Ai;
using Jot.Transcription;

namespace Jot.Rewrite;

/// <summary>What the rewrite pipeline is doing, for the status pill.</summary>
public enum RewritePhase { Idle, Listening, Working }

/// <summary>
/// The rewrite pipeline — the sibling of <see cref="RecorderController"/> for text transformation.
/// Captures the current selection in the focused app (synthetic Ctrl+C), applies an instruction via
/// the configured AI provider (a picked/default prompt, or a spoken instruction), pastes the result
/// back over the selection, and saves the run as a Rewrite row in the library. "Rewrite with voice"
/// toggles: first press captures the selection and records the spoken instruction, second press
/// transcribes it and runs the rewrite.
/// </summary>
public sealed class RewriteController
{
    private readonly ITranscriber _transcriber;
    private readonly AudioRecorder _recorder;
    private readonly ISettingsStore _settings;
    private readonly IRecordingStore _store;
    private readonly IAiClient _ai;
    private readonly AiCredentials _credentials;
    private readonly ISoundService _sound;
    private readonly PromptCatalog _catalog;

    private IntPtr _origin;       // the window the selection lives in
    private string _selection = "";

    public RewritePhase Phase { get; private set; } = RewritePhase.Idle;

    public event Action<RewritePhase>? PhaseChanged;
    public event Action<string>? Succeeded;         // rewritten text (for the pill)
    public event Action<string, string>? Failed;    // (title, message)
    public event Action? NothingSelected;

    public RewriteController(ITranscriber transcriber, AudioRecorder recorder, ISettingsStore settings,
        IRecordingStore store, IAiClient ai, AiCredentials credentials, ISoundService sound, PromptCatalog catalog)
    {
        _transcriber = transcriber;
        _recorder = recorder;
        _settings = settings;
        _store = store;
        _ai = ai;
        _credentials = credentials;
        _sound = sound;
        _catalog = catalog;
    }

    public PromptItem? DefaultPrompt => _catalog.Prompts.FirstOrDefault(p => p.IsDefault);

    /// <summary>Entry point for the Rewrite hotkey: capture the selection, then rewrite with the default
    /// prompt if one is set, otherwise open the picker (<paramref name="openPicker"/>) so the user chooses.</summary>
    public void BeginRewrite(Action openPicker)
    {
        if (!CaptureContext()) { NothingSelected?.Invoke(); return; }
        PromptItem? def = DefaultPrompt;
        if (def is not null) RunRewrite(def.Body);
        else openPicker();
    }

    /// <summary>Grabs the focused window + its current selection. False if nothing is selected.</summary>
    public bool CaptureContext()
    {
        _origin = TextInjector.CaptureForegroundWindow();
        _selection = TextInjector.CaptureSelection().Trim();
        return _selection.Length > 0;
    }

    /// <summary>Runs a rewrite of the already-captured selection with the given instruction (prompt body
    /// or spoken instruction). Assumes <see cref="CaptureContext"/> already succeeded.</summary>
    public async void RunRewrite(string instruction)
    {
        if (_selection.Length == 0) { NothingSelected?.Invoke(); return; }

        JotSettings s = _settings.Current;
        if (s.AiProvider == "None")
        {
            _sound.PlayError();
            Failed?.Invoke("No AI provider", "Pick a provider in Settings → AI to use Rewrite.");
            return;
        }

        SetPhase(RewritePhase.Working);
        try
        {
            var config = new AiConfig(s.AiProvider,
                string.IsNullOrWhiteSpace(s.AiBaseUrl) ? null : s.AiBaseUrl,
                string.IsNullOrWhiteSpace(s.AiModel) ? null : s.AiModel,
                _credentials.ApiKey);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string result = (await _ai.RewriteAsync(_selection, instruction, config, cts.Token)).Trim();

            if (result.Length == 0)
            {
                _sound.PlayError();
                Failed?.Invoke("Rewrite came back empty", "The provider returned no text. Try again.");
                return;
            }

            // Replace the selection: focus the origin window and paste over it.
            TextInjector.PasteAtCursor(result, _origin, s.KeepInClipboard, pressEnter: false);
            _store.Add(BuildRewrite(instruction, _selection, result, s.AiProvider));
            _sound.PlaySuccess();
            Succeeded?.Invoke(result);
        }
        catch (OperationCanceledException)
        {
            _sound.PlayError();
            Failed?.Invoke("Rewrite timed out", "The provider took too long to respond.");
        }
        catch (Exception ex)
        {
            _sound.PlayError();
            Failed?.Invoke("Rewrite failed", ex.Message);
        }
        finally
        {
            SetPhase(RewritePhase.Idle);
        }
    }

    /// <summary>Voice rewrite toggle: first call captures the selection + records the spoken instruction;
    /// second call stops, transcribes it, and runs the rewrite.</summary>
    public void ToggleVoiceRewrite()
    {
        switch (Phase)
        {
            case RewritePhase.Idle:
                if (_recorder.IsRecording) return; // a dictation is in flight — don't collide
                if (!CaptureContext()) { NothingSelected?.Invoke(); return; }
                try
                {
                    _recorder.Start();
                    _sound.PlayStart();
                    SetPhase(RewritePhase.Listening);
                }
                catch (Exception ex)
                {
                    _sound.PlayError();
                    Failed?.Invoke("Couldn't start recording", ex.Message);
                }
                break;

            case RewritePhase.Listening:
                _ = StopVoiceAndRewriteAsync();
                break;

            case RewritePhase.Working:
                break; // busy
        }
    }

    private async Task StopVoiceAndRewriteAsync()
    {
        SetPhase(RewritePhase.Working);
        _sound.PlayStop();
        string instruction;
        try
        {
            string wav = Path.Combine(Path.GetTempPath(), $"jot-rewrite-instruction-{Guid.NewGuid():N}.wav");
            RecordingResult res = await Task.Run(() => _recorder.Stop(wav));
            instruction = (await _transcriber.TranscribeAsync(res.Samples, res.SampleRate)).Trim();
            try { File.Delete(res.WavPath); } catch { /* instruction audio isn't kept */ }
        }
        catch (Exception ex)
        {
            SetPhase(RewritePhase.Idle);
            _sound.PlayError();
            Failed?.Invoke("Couldn't hear the instruction", ex.Message);
            return;
        }

        if (instruction.Length == 0)
        {
            SetPhase(RewritePhase.Idle);
            _sound.PlayError();
            Failed?.Invoke("Didn't catch that", "No spoken instruction was heard.");
            return;
        }

        RunRewrite(instruction); // transitions Working → Idle itself
    }

    private static RecordingItem BuildRewrite(string instruction, string original, string result, string provider) => new()
    {
        Kind = RecordingKind.Rewrite,
        CreatedAt = DateTime.Now,
        ModelLabel = provider,
        Instruction = instruction,
        Original = original,
        Transcript = result,
        Title = TitleFrom(result),
        Status = RecordingStatus.Complete,
    };

    private static string TitleFrom(string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "Rewrite";
        string title = string.Join(' ', words.Take(6));
        return words.Length > 6 ? title + "…" : title;
    }

    private void SetPhase(RewritePhase phase)
    {
        Phase = phase;
        PhaseChanged?.Invoke(phase);
    }
}
