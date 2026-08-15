namespace Jot.Transcription.Ggml;

/// <summary>
/// Pure pin for official transcribe.cpp v0.1.3. Kept free of P/Invoke so the mismatch
/// messages are unit-testable without loading a DLL.
/// </summary>
internal static class TranscribeAbi
{
    internal const string PinnedVersion = "0.1.3";
    internal const string PinnedCommit = "a94e021";
    internal const string PinnedHeaderHash = "86b16dd97ad1cb58";

    internal const string PinBlurb =
        "official transcribe.cpp v0.1.3 (commit a94e021). " +
        "Do not use Handy-shipped DLLs or a newer tarball — 0.2.0 inserts diarize into run_params.";

    internal static void CheckVersion(string version, string commit)
    {
        if (!string.Equals(version, PinnedVersion, StringComparison.Ordinal) ||
            !string.Equals(commit, PinnedCommit, StringComparison.Ordinal))
        {
            throw new TranscribeAbiException(
                $"transcribe.cpp ABI mismatch: this build is pinned to {PinBlurb} " +
                $"Loaded version='{version}' commit='{commit}'.");
        }
    }

    internal static void CheckContract(string? version, string? headerHash)
    {
        if (string.IsNullOrWhiteSpace(version) && string.IsNullOrWhiteSpace(headerHash))
            return;
        if (!string.Equals(version, PinnedVersion, StringComparison.Ordinal) ||
            !string.Equals(headerHash, PinnedHeaderHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new TranscribeAbiException(
                $"transcribe.cpp contract.json mismatch: expected version={PinnedVersion} " +
                $"header_hash={PinnedHeaderHash}, got version='{version}' header_hash='{headerHash}'. " +
                $"Pinned to {PinBlurb}");
        }
    }

    internal static void CheckStructSize(string name, int managed, nuint native)
    {
        if (native == 0)
        {
            throw new TranscribeAbiException(
                $"transcribe.cpp ABI mismatch: {name} native size 0 (unknown struct id). " +
                $"Pinned to {PinBlurb}");
        }
        if ((int)native != managed)
        {
            throw new TranscribeAbiException(
                $"transcribe.cpp ABI mismatch: {name} native size {native} != managed {managed}. " +
                $"Pinned to {PinBlurb}");
        }
    }

    internal static void CheckExports(IReadOnlyList<string> required, Func<string, bool> hasExport)
    {
        var missing = new List<string>();
        foreach (string name in required)
        {
            if (!hasExport(name)) missing.Add(name);
        }
        if (missing.Count > 0)
        {
            throw new TranscribeAbiException(
                $"transcribe.cpp ABI mismatch: missing export(s) {string.Join(", ", missing)}. " +
                $"Pinned to {PinBlurb}");
        }
    }
}
