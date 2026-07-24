namespace Jot.Recording;

/// <summary>
/// Decides when live streaming transcription has PROVEN it can't keep up with the microphone and the
/// session should degrade to batch mode (record fully, transcribe on stop — the fallback path
/// RecorderController already has). Pure logic so the thresholds are unit-testable. All three gates
/// must hold, so a one-off hiccup can't kill captions:
///  - enough audio fed to judge fairly (cold-start chunks are always slow);
///  - real-time factor at or past the threshold (processing time ≥ audio time — the loop can only
///    fall further behind);
///  - an actual backlog piled up (the symptom the user would see as frozen captions).
/// </summary>
public sealed class RealtimeGuard(
    double minAudioSeconds = 5.0, double rtfThreshold = 1.0, double minBacklogSeconds = 2.0)
{
    public bool ShouldDegrade(double audioFedSeconds, double processingSeconds, double backlogSeconds) =>
        audioFedSeconds >= minAudioSeconds
        && processingSeconds >= audioFedSeconds * rtfThreshold
        && backlogSeconds >= minBacklogSeconds;
}
