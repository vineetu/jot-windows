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
    private readonly LiveTranscription? _live;   // null if the engine can't stream
    private bool _liveActive;                     // is this voice-rewrite being live-streamed?
    private GlobalHotkey? _stopHotkey;            // Esc, armed only while listening for the spoken instruction

    private IntPtr _origin;       // the window the selection lives in
    private string _selection = "";

    public RewritePhase Phase { get; private set; } = RewritePhase.Idle;

    public event Action<RewritePhase>? PhaseChanged;
    public event Action<string>? Succeeded;         // rewritten text (for the pill)
    public event Action<string>? PartialTranscript; // live caption of the spoken instruction (background thread)
    public event Action<string, string>? Failed;    // (title, message)
    public event Action? NothingSelected;

    public RewriteController(ITranscriber transcriber, AudioRecorder recorder, ISettingsStore settings,
        IRecordingStore store, IAiClient ai, AiCredentials credentials, ISoundService sound)
    {
        _transcriber = transcriber;
        _recorder = recorder;
        _settings = settings;
        _store = store;
        _ai = ai;
        _credentials = credentials;
        _sound = sound;
        // Same streaming path as dictation, so the spoken instruction shows live in the pill.
        if (transcriber is IStreamingTranscriber streaming)
        {
            _live = new LiveTranscription(recorder, streaming);
            _live.PartialReady += text => PartialTranscript?.Invoke(text);
        }
    }

    /// <summary>Entry point for the Rewrite hotkey: capture the selection, then always open the picker
    /// (<paramref name="openPicker"/>). The default prompt is pre-selected at the top of the picker, so the
    /// seamless path is press-hotkey → Enter. Which prompt is default lives in the catalog (<c>IsDefault</c>).</summary>
    public void BeginRewrite(Action openPicker)
    {
        if (!CaptureContext()) { NothingSelected?.Invoke(); return; }
        openPicker();
    }

    /// <summary>Grabs the focused window + its current selection. False if nothing is selected.</summary>
    public bool CaptureContext()
    {
        _origin = TextInjector.CaptureForegroundWindow();
        Services.JotLog.Info($"rewrite: begin capture, foreground='{TextInjector.DescribeWindow(_origin)}'");

        // 1) UI Automation first: reads the selection directly with NO keystroke and NO clipboard, so it
        //    can't be broken by a still-held Alt or clipboard timing. Handles Notepad/Word/native edits.
        string uia = (UiaSelectionReader.TryReadSelection() ?? "").Trim();
        if (uia.Length > 0)
        {
            _selection = uia;
            Services.JotLog.Info($"rewrite: captured via UIA, len={_selection.Length}");
            return true;
        }

        // 2) Fallback: synthetic Ctrl+C → clipboard, for apps UIA can't read (Chromium/Electron/Java).
        _selection = TextInjector.CaptureSelection().Trim();
        Services.JotLog.Info($"rewrite: captured via clipboard fallback, len={_selection.Length}");
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
                _credentials.GetKey(s.AiProvider));

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
                    _recorder.Start(_settings.Current.InputDeviceId);
                    _sound.PlayStart();
                    // Stream the instruction so the pill shows it live (falls back to a batch decode on any hiccup).
                    try { _liveActive = _settings.Current.LiveCaptions && _live is not null; if (_liveActive) _live!.Start(); }
                    catch { _liveActive = false; }
                    ArmVoiceStopHotkey();   // Esc stops too (in addition to pressing the rewrite-voice hotkey again)
                    SetPhase(RewritePhase.Listening);
                }
                catch (Exception ex)
                {
                    DisarmVoiceStopHotkey();
                    _liveActive = false;
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
        DisarmVoiceStopHotkey();
        SetPhase(RewritePhase.Working);
        _sound.PlayStop();
        string instruction;
        try
        {
            // Finish the live stream WHILE the recorder is still capturing (catches the last words), then stop.
            string liveText = "";
            if (_liveActive && _live is not null)
            {
                try { liveText = (await _live.FinishAsync()).Trim(); }
                catch (Exception ex) { Services.JotLog.Info("rewrite live finish failed: " + ex.Message); }
            }
            string wav = Path.Combine(Path.GetTempPath(), $"jot-rewrite-instruction-{Guid.NewGuid():N}.wav");
            RecordingResult res = await Task.Run(() => _recorder.Stop(wav));
            instruction = liveText;
            if (string.IsNullOrWhiteSpace(instruction))
                instruction = (await _transcriber.TranscribeAsync(res.Samples, res.SampleRate)).Trim();
            try { File.Delete(res.WavPath); } catch { /* instruction audio isn't kept */ }
        }
        catch (Exception ex)
        {
            _liveActive = false;
            SetPhase(RewritePhase.Idle);
            _sound.PlayError();
            Failed?.Invoke("Couldn't hear the instruction", ex.Message);
            return;
        }
        _liveActive = false;

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

    // Esc stops the spoken-instruction recording, mirroring the dictation Esc-to-stop. Armed only while
    // Listening; the rewrite-voice hotkey itself also stops (it's a toggle). id:10 to avoid colliding with
    // the dictation stop-hotkey (id:9) — though the two are never armed at the same time.
    private void ArmVoiceStopHotkey()
    {
        DisarmVoiceStopHotkey();
        if (!HotkeyChord.TryParse(RecorderController.StopRecordingChord, out HotkeyChord chord)) return;
        try
        {
            _stopHotkey = new GlobalHotkey(chord.Modifiers, chord.VirtualKey, id: 10);
            _stopHotkey.Pressed += OnVoiceStopHotkey;
        }
        catch { _stopHotkey = null; } // Esc unavailable shouldn't stop recording — just skip Esc-to-stop
    }

    private void DisarmVoiceStopHotkey()
    {
        try { _stopHotkey?.Dispose(); } catch { /* best effort — never surface a disposal fault */ }
        _stopHotkey = null;
    }

    private void OnVoiceStopHotkey()
    {
        if (Phase != RewritePhase.Listening) return;
        // Defer off the hotkey's WndProc before it disposes its own window (re-entrancy hazard), as dictation does.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null) dispatcher.BeginInvoke(new Action(ToggleVoiceRewrite));
        else ToggleVoiceRewrite();
    }
}
