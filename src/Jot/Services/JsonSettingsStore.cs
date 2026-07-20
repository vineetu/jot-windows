using System.IO;
using System.Text.Json;
using Jot.Services.Abstractions;

namespace Jot.Services;

/// <summary>
/// File-backed <see cref="ISettingsStore"/>: %LOCALAPPDATA%\Jot\settings.json. Loads once at
/// startup, tolerates a missing/corrupt file by falling back to defaults, and writes the whole
/// document on <see cref="Save"/> (the settings surface is tiny — no need for granular writes).
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public JotSettings Current { get; }

    public event EventHandler? Changed;

    public JsonSettingsStore()
    {
        Current = Load();
    }

    private static JotSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<JotSettings>(json, Options);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Corrupt or unreadable settings should never block launch — start fresh.
        }
        return new JotSettings();
    }

    public void Reset()
    {
        var defaults = new JotSettings();
        foreach (var p in typeof(JotSettings).GetProperties())
            if (p is { CanRead: true, CanWrite: true })
                p.SetValue(Current, p.GetValue(defaults));
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
        }
        catch
        {
            // Best-effort persistence; a failed write shouldn't crash the UI.
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
