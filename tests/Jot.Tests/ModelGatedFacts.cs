using System;
using System.IO;
using Jot.Transcription.Ctc;
using Jot.Transcription.Nemotron;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// HONEST SKIPS for the facts that need a real checkpoint on disk.
///
/// These tests used to open with <c>if (!Have) { output.WriteLine("SKIP"); return; }</c>, which xunit
/// reports as PASSED. Green therefore did not mean the fact ran — the exact false green that lets a
/// broken calibration or a broken port ship. xunit 2.x has no <c>Assert.Skip</c>, but it DOES read
/// <see cref="FactAttribute.Skip"/> off the constructed attribute instance at discovery time, so a
/// subclass that sets <c>Skip</c> in its constructor is a real, reported skip with no extra package.
///
/// Requirement tokens are strings rather than named properties on purpose: named properties are
/// assigned AFTER the constructor runs, so <c>Skip</c> could not be computed from them here.
/// </summary>
internal static class ModelGate
{
    // The spike's own env vars (the C1/C2/C3 fidelity work), kept as-is so the recorded runs still
    // reproduce with the same environment.
    public const string SpikeDir = "JOT_CTC_SPIKE_DIR";
    public const string SpotterDir = "JOT_CTC_MODEL_DIR";
    public const string AudioDir = "JOT_CTC_SPIKE_AUDIO";
    public const string FeatsDir = "JOT_CTC_SPIKE_FEATS";
    public const string SpmModel = "JOT_CTC_SPIKE_SPM";
    public const string SpmIds = "JOT_CTC_SPIKE_SPM_IDS";

    public static string? Env(string name)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    /// <summary>The audio corpus: the env var when set, else the dev box's staged copy. The fallback is
    /// what lets the end-to-end proof run on the machine that has the clips without every future run
    /// needing an environment; everywhere else it simply skips.</summary>
    public const string DefaultAudioDir = @"D:\caches\jot-ctc-spike\audio";

    public static string? ResolvedAudioDir
    {
        get
        {
            string? d = Env(AudioDir) ?? DefaultAudioDir;
            return Directory.Exists(d) ? d : null;
        }
    }

    /// <returns>A human-readable description of the FIRST unmet requirement, or null when all are met.</returns>
    public static string? FirstMissing(string[] requires)
    {
        foreach (string r in requires)
        {
            string? why = Check(r);
            if (why is not null) return why;
        }
        return null;
    }

    private static string? Check(string requirement)
    {
        // audio:<file> — the corpus AND one named clip inside it.
        if (requirement.StartsWith("audio:", StringComparison.Ordinal))
        {
            string? dir = ResolvedAudioDir;
            if (dir is null) return $"{AudioDir} (or {DefaultAudioDir})";
            string name = requirement["audio:".Length..];
            return File.Exists(Path.Combine(dir, name)) ? null : $"{name} in {dir}";
        }

        // spike-audio:<file> — the env-var corpus AND one named clip inside it.
        if (requirement.StartsWith("spike-audio:", StringComparison.Ordinal))
        {
            string? dir = Env(AudioDir);
            if (dir is null || !Directory.Exists(dir)) return AudioDir;
            string name = requirement["spike-audio:".Length..];
            return File.Exists(Path.Combine(dir, name)) ? null : $"{name} in {dir}";
        }

        return requirement switch
        {
            "audio" => ResolvedAudioDir is null ? $"{AudioDir} (or {DefaultAudioDir})" : null,

            // STRICTLY the env var, no fallback. The spike facts were written against whatever corpus
            // the env var pointed at; silently pointing them at a different one would change what the
            // recorded fidelity numbers mean.
            "spike-audio" => Env(AudioDir) is { } ad && Directory.Exists(ad) ? null : AudioDir,

            // The spike's copy of the CTC checkpoint. Deliberately the SAME weak test the spike's own
            // `HaveModel` used (tokens.txt only): the spike predates the three-file layout and accepts
            // either model.onnx or model.int8.onnx, so a stricter check here would silently stop
            // running facts that used to run.
            "spike-model" => Env(SpikeDir) is { } d && File.Exists(Path.Combine(d, "tokens.txt"))
                ? null : $"{SpikeDir} pointing at a sherpa-onnx Parakeet-CTC export",

            // CtcSpotterTests' copy, a separate env var by history.
            "spotter-model" => Env(SpotterDir) is { } s && new CtcModel(directory: s).IsInstalled
                ? null : $"{SpotterDir} pointing at a complete CTC model",

            // The INSTALLED model, at the path the shipping app resolves — no env var, because the
            // end-to-end proof must exercise exactly what a user's machine has.
            "installed-ctc" => new CtcModel().IsInstalled
                ? null : $"the CTC spotter model installed at {new CtcModel().Directory}",
            "installed-nemotron" => new NemotronModel().IsInstalled || new NemotronFp16Model().IsInstalled
                ? null : "an installed Nemotron model (int4 or fp16)",

            "feats" => Env(FeatsDir) is not null ? null : FeatsDir,
            "spm" => Env(SpmModel) is { } m && File.Exists(m) ? null : SpmModel,
            "spm-ids" => Env(SpmIds) is { } i && File.Exists(i) ? null : SpmIds,

            _ => throw new ArgumentException($"unknown requirement '{requirement}'"),
        };
    }
}

/// <summary>A <see cref="FactAttribute"/> that reports a REAL skip when an asset it needs is absent.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ModelFactAttribute : FactAttribute
{
    public ModelFactAttribute(params string[] requires)
    {
        string? missing = ModelGate.FirstMissing(requires);
        if (missing is not null) Skip = "needs " + missing;
    }
}

/// <summary>Theory counterpart of <see cref="ModelFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ModelTheoryAttribute : TheoryAttribute
{
    public ModelTheoryAttribute(params string[] requires)
    {
        string? missing = ModelGate.FirstMissing(requires);
        if (missing is not null) Skip = "needs " + missing;
    }
}
