namespace Jot.Transcription;

/// <summary>Outcome of one GPU speed/sanity check. <see cref="Reason"/> is diagnostics-grade text.</summary>
public sealed record ProbeResult(
    bool GpuViable, double AvgChunkMs, double MaxAcceptMs, string Transcript, string Reason);
