using System.IO;
using Jot.Services.Abstractions;

namespace Jot.Services;

/// <summary>
/// The single source of truth for where Jot stores everything. <see cref="AppDataRoot"/> is the one Jot
/// root (packaged: the MSIX container that Windows auto-wipes on uninstall; unpackaged: <c>%LOCALAPPDATA%\Jot</c>);
/// EVERY other location — settings, prompts, logs, tools, models, recordings, the credential store — is
/// derived from it or from the user-chosen <see cref="JotSettings.DataDirectory"/>. Keeping it centralized
/// is deliberate: a past move-location change broke cleanup because the root was recomputed in ~8 places.
/// </summary>
public static class JotPaths
{
    /// <summary>The pre-container real per-user folder <c>%LOCALAPPDATA%\Jot</c>. Public so startup can
    /// ADOPT it on an upgrade (existing data lives here) without moving files. Not the default anymore
    /// under MSIX — <see cref="AppDataRoot"/> is.</summary>
    public static string LegacyLocalAppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot");

    /// <summary>
    /// The one Jot data/config root. Under the Store (MSIX) build this is the package container, so an
    /// uninstall removes every trace (settings, model, transcripts, keys) — MSIX runs no uninstall code, so
    /// the container is the only auto-clean location. Unpackaged (dev/Velopack) it's <c>%LOCALAPPDATA%\Jot</c>.
    /// Data can still be moved off this root to another drive via <see cref="JotSettings.DataDirectory"/>
    /// (that folder is outside the container, so it must be erased in-app before uninstall — Settings warns).
    /// </summary>
    public static string AppDataRoot => PackagePaths.ContainerRoot ?? LegacyLocalAppDataDir;

    /// <summary>The fixed config folder (settings.json, prompts.json, migration + wipe markers). Always the
    /// root itself, never a moved data folder, so the record describing a move can't get moved mid-op.</summary>
    public static string ConfigDir => AppDataRoot;

    /// <summary>Default data folder when the user hasn't chosen one: the app data root (container/%LOCALAPPDATA%).</summary>
    public static string DefaultDataDir => AppDataRoot;

    /// <summary>The effective data folder (user-chosen, or the default).</summary>
    public static string DataDir(JotSettings s) =>
        string.IsNullOrWhiteSpace(s.DataDirectory) ? DefaultDataDir : s.DataDirectory!;

    public static string RecordingsDir(JotSettings s) => Path.Combine(DataDir(s), "recordings");

    public static string LibraryFile(JotSettings s) => Path.Combine(DataDir(s), "library.json");

    /// <summary>Where on-device models live (under the data folder, so they stay off the system drive).</summary>
    public static string ModelsDir(JotSettings s) => Path.Combine(DataDir(s), "models");

    /// <summary>Models dir resolved from the default location (used before settings are available).</summary>
    public static string DefaultModelsDir => Path.Combine(DefaultDataDir, "models");

    /// <summary>Folder name for the vocabulary learning data. A const because THREE places must agree
    /// on it — this accessor, <see cref="JotDataPurge"/>'s artifact list, and
    /// <c>DataFolderMigrator.MigratedItems</c> — and a folder registered in only some of them either
    /// survives "Erase all data" (contradicting the privacy claim) or gets stranded by a folder move.
    /// VocabStoreProvenanceTests asserts all three against this one name.</summary>
    public const string VocabularyFolderName = "Vocabulary";

    /// <summary>Where the correction store's ledger (<c>corrections.json</c>) and the per-transcript
    /// provenance payloads live. This is the <c>containerRoot</c> the shared vocabulary core appends
    /// <c>Vocabulary\</c> to, so pass <see cref="DataDir"/> — not this — to those constructors.</summary>
    public static string VocabularyDir(JotSettings s) => Path.Combine(DataDir(s), VocabularyFolderName);
}
