namespace Jot.Services.Abstractions;

/// <summary>
/// Plays short UI feedback sounds for the recording pipeline. Each method is a no-op unless the
/// matching toggle is enabled in <see cref="JotSettings"/>; <see cref="Preview"/> always plays so the
/// Settings "Preview" button works regardless of the toggles.
/// </summary>
public interface ISoundService
{
    void PlayStart();
    void PlayStop();
    void PlayCancel();
    void PlaySuccess();
    void PlayError();

    /// <summary>Always plays (used by the Settings preview button).</summary>
    void Preview();
}
