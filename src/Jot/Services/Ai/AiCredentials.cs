using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jot.Services.Abstractions;

namespace Jot.Services.Ai;

/// <summary>
/// Holds the AI provider API keys, persisted encrypted at rest with Windows DPAPI (per-user, per-
/// machine) so cleanup/rewrite keep working across restarts. Keys are kept <b>per provider</b> — a
/// JSON map of provider name → key. The file lives under the user's chosen data folder
/// (<c>&lt;DataDir&gt;\aikey.dat</c>).
///
/// SAFETY INVARIANT (added after a failed load cascaded into deleting the user's saved key): while
/// the on-disk store has not been faithfully loaded into memory (<see cref="_loadedOk"/> is false),
/// <see cref="Save"/> refuses to overwrite or delete the file. Every access retries the load first,
/// so a transient startup failure self-heals instead of presenting an empty store.
/// </summary>
public sealed class AiCredentials
{
    private readonly ISettingsStore _settings;

    // Resolved from the current data directory each access, so it follows a changed save location.
    private string FilePath => Path.Combine(JotPaths.DataDir(_settings.Current), "aikey.dat");

    // provider (case-insensitive) -> key. Loaded from disk; mutated in place and re-persisted on change.
    private readonly Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);

    // True only when _keys faithfully represents the on-disk store (file read OK, or no file exists).
    private bool _loadedOk;

    public AiCredentials(ISettingsStore settings)
    {
        _settings = settings;
        Load();
    }

    /// <summary>The saved key for a provider, or null if none is stored.</summary>
    public string? GetKey(string? provider)
    {
        EnsureLoaded();
        return !string.IsNullOrEmpty(provider) && _keys.TryGetValue(provider, out string? k) ? k : null;
    }

    /// <summary>Store (or, when blank, clear) the key for one provider, leaving other providers' keys intact.</summary>
    public void SetKey(string? provider, string? value)
    {
        if (string.IsNullOrEmpty(provider)) return;
        EnsureLoaded();
        if (string.IsNullOrEmpty(value))
        {
            // Removing a key that isn't stored is a no-op — don't touch the file at all.
            if (!_keys.Remove(provider)) return;
        }
        else
        {
            if (_keys.TryGetValue(provider, out string? existing) && existing == value) return;
            _keys[provider] = value;
        }
        Save();
    }

    /// <summary>Retry a failed load before any read/write, so a transient startup failure self-heals.</summary>
    private void EnsureLoaded()
    {
        if (!_loadedOk) Load();
    }

    private void Load()
    {
        string path = FilePath;
        try
        {
            _keys.Clear();
            if (!File.Exists(path))
            {
                _loadedOk = true; // nothing on disk — empty memory faithfully represents it
                return;
            }
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            string text = Encoding.UTF8.GetString(plain);

            // New format: a JSON map of provider -> key. Legacy format: a single bare key string.
            if (text.TrimStart().StartsWith('{'))
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
                if (map is not null)
                    foreach (var kv in map)
                        if (!string.IsNullOrEmpty(kv.Value)) _keys[kv.Key] = kv.Value;
            }
            else if (!string.IsNullOrEmpty(text))
            {
                // Migrate the single legacy key onto whichever provider is configured.
                string provider = _settings.Current.AiProvider;
                if (!string.IsNullOrEmpty(provider) && provider != "None")
                    _keys[provider] = text;
            }
            _loadedOk = true;
            JotLog.Info($"aikey: loaded {_keys.Count} key(s) from {path}");
        }
        catch (Exception ex)
        {
            // File exists but couldn't be read — keep the fuse blown so Save() can't clobber it.
            _loadedOk = false;
            JotLog.Error($"aikey: LOAD FAILED from {path} — saved keys are preserved on disk", ex);
        }
    }

    private void Save()
    {
        string path = FilePath;
        try
        {
            if (!_loadedOk && File.Exists(path))
            {
                // SAFETY: never overwrite a store we failed to read — that would destroy saved keys.
                JotLog.Warn($"aikey: save blocked — previous load failed, keeping {path} untouched");
                return;
            }
            if (_keys.Count == 0)
            {
                if (File.Exists(path)) { File.Delete(path); JotLog.Info("aikey: store emptied, file removed"); }
                return;
            }
            string json = JsonSerializer.Serialize(_keys);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, cipher);
            JotLog.Info($"aikey: saved {_keys.Count} key(s)");
        }
        catch (Exception ex)
        {
            JotLog.Error("aikey: save failed", ex);
        }
    }
}
