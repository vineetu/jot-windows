using System.IO;
using System.Text;

namespace Jot.Cli;

/// <summary>Exit codes are a contract for scripted callers: 0 success, 1 runtime failure
/// (decode/models/engine), 2 usage. Errors go to stderr as <c>jot: error: …</c>.</summary>
internal static class Cli
{
    public const string Version = "0.1.0-windows";

    public const string Usage = """
        jot — on-device transcription utility (batch files and live streaming).

        USAGE:
          jot transcribe <file> [options]     Transcribe an audio/video file.
          jot --stream [options]              Stream raw PCM from stdin, emit NDJSON finals.
          jot --help | --version

        TRANSCRIBE OPTIONS (default output: cleaned plain text on stdout):
          --raw                Skip the cleanup chain (fillers, number normalization,
                                whitespace). Vocabulary still applies.
          --language <code>    Locale code (en-US, de-DE, …) or a bare subtag that
                                resolves to one (es -> es-ES). "auto" detects per
                                utterance. Default: the language Jot is set to.
          -o, --output <path>  Write to <path> instead of stdout.
          --model-dir <dir>    Folder CONTAINING the nemotron-* model folders.
          --data-dir <dir>     Jot data root (models\, Vocabulary\, settings.json).
          --device <mode>      auto (default, follows Jot's setting), cpu, or gpu.
          --                   End of options (for input files starting with "-").

        STREAM OPTIONS (in: 16 kHz mono PCM on stdin; out: one JSON object per line):
          --language <code>    One language per stream, fixed at startup.
          --rate <hz>          Input sample rate. Only 16000 is supported.
          --encoding <enc>     s16le (default) or f32le. A leading WAV header is
                                detected and skipped; --encoding governs decoding.
          --model-dir <dir>    As above.
          --data-dir <dir>     As above.
          --device <mode>      As above.
          Finals are vocabulary-corrected per segment; there is no cleanup chain
          in stream mode. PowerShell pipes re-encode binary data — use cmd's
          `type file.pcm | jot --stream`, ffmpeg, or any native producer.

        VOCABULARY:
          --no-vocab           Disable custom-vocabulary correction.
          --vocab <file>       Use <file> instead of the data root's
                                Vocabulary\vocabulary.json.

        EXIT STATUS: 0 success, 1 runtime failure (decode, models, engine), 2 usage.
        Models are downloaded by the Jot app — open Jot once to complete setup.
        """;

    public static int Fail(string message)
    {
        Console.Error.WriteLine($"jot: error: {message}");
        return 1;
    }

    public static int UsageFail(string message)
    {
        Console.Error.WriteLine($"jot: error: {message}");
        Console.Error.WriteLine();
        Console.Error.WriteLine(Usage);
        return 2;
    }

    /// <summary>UTF-8, no BOM, and no CRLF rewrite — .NET's Console would give both.</summary>
    public static void WriteStdout(string text)
    {
        using Stream stdout = Console.OpenStandardOutput();
        byte[] bytes = new UTF8Encoding(false).GetBytes(text);
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
    }
}

internal static class Program
{
    private static readonly HashSet<string> TranscribeFlags =
        new(StringComparer.Ordinal) { "--raw", "--no-vocab" };

    private static readonly Dictionary<string, string> TranscribeOptions =
        new(StringComparer.Ordinal)
        {
            ["--vocab"] = "--vocab",
            ["--language"] = "--language",
            ["-o"] = "-o",
            ["--output"] = "-o",
            ["--model-dir"] = "--model-dir",
            ["--data-dir"] = "--data-dir",
            ["--device"] = "--device",
        };

    private static readonly HashSet<string> StreamFlags = new(StringComparer.Ordinal) { "--no-vocab" };

    private static readonly Dictionary<string, string> StreamOptionNames =
        new(StringComparer.Ordinal)
        {
            ["--language"] = "--language",
            ["--rate"] = "--rate",
            ["--encoding"] = "--encoding",
            ["--vocab"] = "--vocab",
            ["--model-dir"] = "--model-dir",
            ["--data-dir"] = "--data-dir",
            ["--device"] = "--device",
        };

    public static async Task<int> Main(string[] args)
    {
        IReadOnlyList<string> head = CliArgParser.PreEndOfOptions(args);

        if (args.Length == 0) return Cli.UsageFail("missing command");
        if (head.Contains("--help") || head.Contains("-h"))
        {
            Console.WriteLine(Cli.Usage);
            return 0;
        }
        if (head.Contains("--version"))
        {
            Console.WriteLine($"jot {Cli.Version}");
            return 0;
        }
        if (head.Contains("--stream")) return RunStream(args, head.Count);
        if (args[0] != "transcribe") return Cli.UsageFail($"unknown command '{args[0]}'");

        ParsedArgs parsed = CliArgParser.Parse(args.Skip(1).ToList(), TranscribeFlags, TranscribeOptions);
        if (parsed.Error is not null) return Cli.UsageFail(parsed.Error);

        if (parsed.Positionals.Count == 0) return Cli.UsageFail("missing <file> argument");
        if (parsed.Positionals.Count > 1)
        {
            return Cli.UsageFail(
                "unexpected extra argument(s): " + string.Join(' ', parsed.Positionals.Skip(1)));
        }

        string device = parsed.Options.GetValueOrDefault("--device", "auto");
        if (device is not ("auto" or "cpu" or "gpu"))
            return Cli.UsageFail($"unsupported --device '{device}': use auto, cpu or gpu");

        return await BatchMode.RunAsync(new BatchOptions(
            InputPath: parsed.Positionals[0],
            Raw: parsed.Flags.Contains("--raw"),
            NoVocab: parsed.Flags.Contains("--no-vocab"),
            VocabFile: parsed.Options.GetValueOrDefault("--vocab"),
            Language: parsed.Options.GetValueOrDefault("--language"),
            OutputPath: parsed.Options.GetValueOrDefault("-o"),
            ModelDir: parsed.Options.GetValueOrDefault("--model-dir"),
            DataDir: parsed.Options.GetValueOrDefault("--data-dir"),
            Device: device));
    }

    /// <param name="boundary">How many tokens precede "--". A "--stream" past it is a positional, not the
    /// mode switch, so only the ones before it are stripped.</param>
    private static int RunStream(string[] args, int boundary)
    {
        List<string> rest = args
            .Where((tok, i) => !(tok == "--stream" && i < boundary))
            .ToList();

        ParsedArgs parsed = CliArgParser.Parse(rest, StreamFlags, StreamOptionNames);
        if (parsed.Error is not null) return Cli.UsageFail(parsed.Error);
        if (parsed.Positionals.Count > 0)
            return Cli.UsageFail($"unexpected argument '{parsed.Positionals[0]}' in --stream mode");

        string rate = parsed.Options.GetValueOrDefault("--rate", "16000");
        if (rate != "16000")
            return Cli.UsageFail($"unsupported --rate '{rate}': only 16000 is supported");

        string encodingRaw = parsed.Options.GetValueOrDefault("--encoding", "s16le");
        if (!StdinAudioReader.TryParseEncoding(encodingRaw, out PcmEncoding encoding))
            return Cli.UsageFail($"unsupported --encoding '{encodingRaw}': use s16le or f32le");

        string device = parsed.Options.GetValueOrDefault("--device", "auto");
        if (device is not ("auto" or "cpu" or "gpu"))
            return Cli.UsageFail($"unsupported --device '{device}': use auto, cpu or gpu");

        return StreamMode.Run(new StreamOptions(
            Encoding: encoding,
            NoVocab: parsed.Flags.Contains("--no-vocab"),
            VocabFile: parsed.Options.GetValueOrDefault("--vocab"),
            Language: parsed.Options.GetValueOrDefault("--language"),
            ModelDir: parsed.Options.GetValueOrDefault("--model-dir"),
            DataDir: parsed.Options.GetValueOrDefault("--data-dir"),
            Device: device));
    }
}
