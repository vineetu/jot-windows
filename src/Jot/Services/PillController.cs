using System.Windows;
using System.Windows.Threading;
using Jot.Controls;
using Jot.Recording;
using Jot.Rewrite;

namespace Jot.Services;

/// <summary>
/// Bridges the record/transcribe pipeline to the floating <see cref="PillWindow"/>: maps recorder
/// states to pill states, streams the mic level into the waveform (coalesced onto the Dispatcher),
/// runs the elapsed-time counter, and auto-dismisses the transient success/error/notice states.
/// The pill is created lazily on first use so a session that never dictates never builds it.
/// </summary>
public sealed class PillController
{
    private readonly RecorderController _recorder;
    private readonly RewriteController _rewrite;
    private readonly AudioRecorder _audio;
    private readonly Dispatcher _dispatcher;

    private PillWindow? _pill;
    private DispatcherTimer? _elapsedTimer;
    private DispatcherTimer? _autoHideTimer;
    private DateTime _startedAt;
    private bool _transient; // a success/error/notice is showing and manages its own dismissal

    public PillController(RecorderController recorder, RewriteController rewrite)
    {
        _recorder = recorder;
        _rewrite = rewrite;
        _audio = recorder.Recorder;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
    }

    /// <summary>Subscribe to the pipeline. Call once at startup.</summary>
    public void Attach()
    {
        _recorder.StateChanged += OnStateChanged;
        _recorder.TranscriptReady += OnTranscriptReady;
        _recorder.PartialTranscript += OnPartial;
        _recorder.Failed += OnFailed;
        _recorder.NothingTranscribed += OnNothing;
        _audio.LevelChanged += OnLevel;

        // Rewrite pipeline drives the same pill (all events marshalled onto the UI thread).
        _rewrite.PhaseChanged += p => _dispatcher.BeginInvoke(() => OnRewritePhase(p));
        _rewrite.Succeeded += t => _dispatcher.BeginInvoke(() => OnTranscriptReady(t));
        _rewrite.Failed += (title, msg) => _dispatcher.BeginInvoke(() => OnFailed(title, msg));
        _rewrite.NothingSelected += () => _dispatcher.BeginInvoke(() =>
        {
            _transient = true;
            Pill.SetState(PillState.Notice, "Select some text first");
            ScheduleHide(3000);
        });
    }

    private void OnRewritePhase(RewritePhase phase)
    {
        switch (phase)
        {
            case RewritePhase.Listening: // recording the spoken instruction — show the waveform
                CancelAutoHide();
                _transient = false;
                _startedAt = DateTime.Now;
                StartElapsed();
                Pill.SetState(PillState.Recording);
                Pill.SetStopAction(_rewrite.ToggleVoiceRewrite);
                break;
            case RewritePhase.Working:
                _transient = false;
                StopElapsed();
                Pill.SetState(PillState.Rewriting);
                _pill?.SetStopAction(null);
                break;
            case RewritePhase.Idle:
                StopElapsed();
                if (!_transient) _pill?.SetState(PillState.Hidden);
                _pill?.SetStopAction(null);
                break;
        }
    }

    private PillWindow Pill => _pill ??= new PillWindow();

    // Capture-thread callback → hop onto the UI thread at render priority.
    private void OnLevel(float level)
        => _dispatcher.BeginInvoke(DispatcherPriority.Render, () => _pill?.PushLevel(level));

    // Live-caption partial (background thread) → UI thread. Only while still recording, so a late
    // partial can't overwrite the "Transcribing…"/Success line after the user has stopped.
    private void OnPartial(string text)
        => _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_recorder.State == RecorderState.Recording) _pill?.SetLiveText(text);
        });

    private void OnStateChanged(RecorderState state)
    {
        switch (state)
        {
            case RecorderState.Recording:
                CancelAutoHide();
                _transient = false;
                _startedAt = DateTime.Now;
                StartElapsed();
                Pill.SetState(PillState.Recording);
                Pill.SetStopAction(_recorder.Toggle);
                break;

            case RecorderState.Transcribing:
                _transient = false;
                StopElapsed();
                Pill.SetState(PillState.Transcribing);
                _pill?.SetStopAction(null);
                break;

            case RecorderState.Idle:
                StopElapsed();
                // Success/error/notice fire just before Idle and own their own timed dismissal;
                // a plain idle with nothing pending simply hides the pill.
                if (!_transient) _pill?.SetState(PillState.Hidden);
                _pill?.SetStopAction(null);
                break;
        }
    }

    private void OnTranscriptReady(string text)
    {
        _transient = true;
        Pill.SetState(PillState.Success, text.Trim());
        ScheduleHide(4000);
    }

    private void OnFailed(string title, string message)
    {
        _transient = true;
        Pill.SetState(PillState.Error, $"{title}: {message}");
        ScheduleHide(6000);
    }

    private void OnNothing()
    {
        _transient = true;
        Pill.SetState(PillState.Notice, "No speech detected");
        ScheduleHide(3000);
    }

    // ---------------- timers ----------------

    private void StartElapsed()
    {
        _elapsedTimer ??= new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
        _elapsedTimer.Tick -= OnElapsedTick;
        _elapsedTimer.Tick += OnElapsedTick;
        OnElapsedTick(null, EventArgs.Empty);
        _elapsedTimer.Start();
    }

    private void OnElapsedTick(object? sender, EventArgs e)
    {
        TimeSpan t = DateTime.Now - _startedAt;
        _pill?.SetElapsed($"{(int)t.TotalMinutes}:{t.Seconds:00}");
    }

    private void StopElapsed() => _elapsedTimer?.Stop();

    private void ScheduleHide(int milliseconds)
    {
        CancelAutoHide();
        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        _autoHideTimer.Tick += (_, _) =>
        {
            CancelAutoHide();
            _transient = false;
            _pill?.SetState(PillState.Hidden);
        };
        _autoHideTimer.Start();
    }

    private void CancelAutoHide()
    {
        _autoHideTimer?.Stop();
        _autoHideTimer = null;
    }
}
