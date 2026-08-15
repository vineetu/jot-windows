using System.IO;
using System.Text;
using Jot.Import;
using Jot.Services.Abstractions;
using Jot.Transcription;
using Jot.Transcription.Ggml;

namespace Jot.Cli;

internal sealed record BatchOptions(
    string InputPath,
    bool Raw,
    bool NoVocab,
    string? VocabFile,
    string? Language,
    string? OutputPath,
    string? ModelDir,
    string? DataDir,
    string Device);

/// <summary>`jot transcribe &lt;file&gt;` — file in, cleaned plain text out. Same classes, same order as
/// the app's own post-stop path.</summary>
internal static class BatchMode
{
    private const int SampleRate = 16_000;

    public static async Task<int> RunAsync(BatchOptions o)
    {
        if (!File.Exists(o.InputPath)) return Cli.Fail($"input file not found: {o.InputPath}");

        ResolvedPaths paths = CliPaths.Resolve(
            o.ModelDir, o.DataDir,
            CliPaths.DefaultRoots(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            Jot.Services.JotPaths.LegacyLocalAppDataDir);
        Console.Error.WriteLine($"jot: data root: {paths.DataRoot} (models: {paths.ModelsParent})");

        var gguf = new NemotronGgufModel(paths.GgufDir);
        // Models before decode: on a fresh install the actionable error should arrive immediately, not
        // after ffmpeg has chewed through the input. Leftover int4/fp16 folders are not an engine.
        if (!gguf.IsInstalled)
        {
            return Cli.Fail(
                $"No transcription model found under {paths.ModelsParent}. " +
                "Open Jot and complete setup, then retry (or pass --model-dir).");
        }

        string language = o.Language ?? paths.Settings.Language;
        if (!CliLanguage.TryCanonicalize(language, out string locale))
            return Cli.UsageFail($"unknown --language '{language}': valid codes are {CliLanguage.ValidCodes}");

        float[] samples;
        try
        {
            samples = Decode(o.InputPath, paths);
        }
        catch (Exception ex)
        {
            return Cli.Fail($"decode failed: {ex.Message}");
        }
        if (samples.Length == 0)
            return Cli.Fail($"decoded audio is empty (no audio track in {o.InputPath}?)");

        JotSettings settings = ApplyDevice(paths.Settings, o.Device);
        string text;
        try
        {
            ITranscriber transcriber = TranscriberFactory.Create(
                settings, gguf, msg => Console.Error.WriteLine("jot: " + msg));
            if (!transcriber.IsModelInstalled)
            {
                return Cli.Fail(
                    $"the selected engine's model is not installed under {paths.ModelsParent} — " +
                    "try --device cpu, or open Jot and complete setup.");
            }
            TranscriberFactory.ApplyLanguage(transcriber, locale);
            text = await transcriber.TranscribeAsync(samples, SampleRate);
        }
        catch (Exception ex)
        {
            return Cli.Fail($"transcription failed: {ex.Message}");
        }

        // The app's order exactly: cleanup first, vocabulary LAST. Nothing may touch the text after the
        // gate — its anchors assume it.
        if (!o.Raw && paths.Settings.OfflineCleanupEnabled)
            text = Jot.Text.TextPipeline.Clean(text, locale, isNemotron: true);

        var vocab = CliVocabulary.Create(paths, locale, o.NoVocab, o.VocabFile, out string? vocabError);
        if (vocabError is not null) return Cli.Fail(vocabError);
        if (vocab is not null) text = vocab.Apply(text, samples.Length / (double)SampleRate);

        // The app's trailing space exists for paste; a CLI emits one newline. Cosmetics on the last byte.
        string output = text.TrimEnd() + "\n";
        try
        {
            if (o.OutputPath is not null)
                File.WriteAllText(o.OutputPath, output, new UTF8Encoding(false));
            else
                Cli.WriteStdout(output);
        }
        catch (Exception ex)
        {
            return Cli.Fail($"failed to write {o.OutputPath}: {ex.Message}");
        }
        return 0;
    }

    // WAV goes through NAudio, but compressed-codec WAVs (µ-law, ADPCM) and mislabeled files fail there
    // and decode fine through ffmpeg — so a WAV failure is a fall-through, not the answer.
    private static float[] Decode(string path, ResolvedPaths paths)
    {
        if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            try { return WavAudio.ReadMono16k(path); }
            catch { /* fall through to ffmpeg */ }
        }
        return FfmpegDecoder.DecodeToMono16k(ResolveFfmpeg(paths), path).samples;
    }

    // tools\ hangs off the app-data root, not a moved data folder, so probe the roots themselves. The
    // download on a miss is the app's own behavior (~138 MB, atomic), not a new download surface.
    private static string ResolveFfmpeg(ResolvedPaths paths)
    {
        var roots = new List<string> { paths.Root };
        roots.AddRange(CliPaths.DefaultRoots(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        foreach (string root in roots)
        {
            string exe = Path.Combine(root, "tools", "ffmpeg.exe");
            try
            {
                if (File.Exists(exe) && new FileInfo(exe).Length > 0) return exe;
            }
            catch
            {
                // unreadable root — keep probing
            }
        }

        Console.Error.WriteLine($"jot: downloading FFmpeg (~138 MB) into {paths.ToolsDir}…");
        new FfmpegInstaller().EnsureInstalledAsync(paths.ToolsDir).GetAwaiter().GetResult();
        return Path.Combine(paths.ToolsDir, "ffmpeg.exe");
    }

    // auto keeps whatever the app is configured for (including its GPU-tier verdict); cpu/gpu are the
    // same two explicit picks the Settings picker offers.
    public static JotSettings ApplyDevice(JotSettings s, string device)
    {
        s.TranscriptionDevice = device switch
        {
            "cpu" => TranscriptionDevices.Cpu,
            "gpu" => TranscriptionDevices.GpuVulkan,
            _ => s.TranscriptionDevice,
        };
        return s;
    }
}
