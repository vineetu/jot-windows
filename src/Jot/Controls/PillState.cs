namespace Jot.Controls;

/// <summary>
/// Visible states of the floating status pill. Exhaustive — every consumer switches on all of
/// these so a new state can't be silently dropped (mirrors the Mac app's PillState discipline).
/// </summary>
public enum PillState
{
    Hidden,
    Recording,
    Transcribing,
    CleaningUp,
    Rewriting,
    Success,
    Notice,   // mic-resilience / non-fatal info (e.g. "Recorded with system default")
    Error,
}
