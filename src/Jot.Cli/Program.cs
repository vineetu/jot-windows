using System.IO;
using System.Text;

namespace Jot.Cli;

/// <summary>Exit codes are a contract for scripted callers: 0 success, 1 runtime failure
/// (decode/models/engine), 2 usage. Errors go to stderr as <c>jot: error: …</c>.</summary>
internal static class Cli
{
    public const string Version = "0.1.0-windows";

    public const string Usage = """
        jot — on-device transcription utility.

        USAGE:
          jot transcribe <file> [options]     Transcribe an audio/video file.
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
        if (head.Contains("--stream"))
        {
            Console.Error.WriteLine("jot: error: stream mode not yet wired");
            return 1;
        }
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
}
