using System.Diagnostics;
using System.IO;
using Jot.Services;
using Jot.Transcription;
using Jot.Transcription.Ggml;

namespace Jot.Cli;

internal sealed record StreamOptions(
    PcmEncoding Encoding,
    bool NoVocab,
    string? VocabFile,
    string? Language,
    string? ModelDir,
    string? DataDir,
    string Device);

/// <summary>
/// `jot --stream` — raw PCM on stdin, NDJSON finals on stdout.
///
/// The error philosophy is the INVERSE of the app's wrappers: any engine error is fatal (stderr,
/// non-zero exit). A silently deaf agent is the worst failure mode; a dead process is something the
/// caller detects and handles.
///
/// Backpressure is structural — one thread reads, accumulates, accepts, emits, so a faster-than-realtime
/// producer is throttled by the blocking pipe. There is no queue to grow unbounded.
/// </summary>
internal static class StreamMode
{
    private const int SampleRate = 16_000;

    // Accept runs incremental inference but O(total-session) bookkeeping per call, so a sub-chunk Accept
    // does zero decoding and still pays the full tax: a 4 KiB read loop would Accept ~28 000x an hour.
    // ggml R=3 is 320 ms = 5120 samples — same feed the GPU probe uses.
    private const int ChunkSamples = 5_120;

    private const int ReadBufferBytes = 32 * 1024;

    // Rolling to a fresh session bounds memory and the O(n) constant to minutes, not hours. It happens at
    // a quiet boundary so a word spoken across the seam is unlikely to be the one that degrades.
    private static readonly TimeSpan RollAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RollHardCap = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan QuietBeforeRoll = TimeSpan.FromSeconds(2);

    /// <summary>Languages written without inter-word spaces, where the emitter's whitespace boundary
    /// never arrives.</summary>
    private static readonly HashSet<string> SpacelessScripts =
        new(StringComparer.OrdinalIgnoreCase) { "zh", "ja", "th", "km", "lo", "my" };

    public static int Run(StreamOptions o)
    {
        ResolvedPaths paths = CliPaths.Resolve(
            o.ModelDir, o.DataDir,
            CliPaths.DefaultRoots(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            JotPaths.LegacyLocalAppDataDir);
        Console.Error.WriteLine($"jot: data root: {paths.DataRoot} (models: {paths.ModelsParent})");

        var gguf = new NemotronGgufModel(paths.GgufDir);
        if (!gguf.IsInstalled)
        {
            return Cli.Fail(
                $"No transcription model found under {paths.ModelsParent}. " +
                "Open Jot and complete setup, then retry (or pass --model-dir).");
        }

        string language = o.Language ?? paths.Settings.Language;
        if (!CliLanguage.TryCanonicalize(language, out string locale))
            return Cli.UsageFail($"unknown --language '{language}': valid codes are {CliLanguage.ValidCodes}");

        var vocab = CliVocabulary.Create(paths, locale, o.NoVocab, o.VocabFile, out string? vocabError);
        if (vocabError is not null) return Cli.Fail(vocabError);

        ITranscriber transcriber;
        try
        {
            transcriber = TranscriberFactory.Create(
                BatchMode.ApplyDevice(paths.Settings, o.Device), gguf,
                new Jot.Transcription.Granite.GraniteModel(paths.GraniteDir),
                new Jot.Text.PunctCapSegModel(paths.PunctDir),
                msg => Console.Error.WriteLine("jot: " + msg));
        }
        catch (Exception ex)
        {
            return Cli.Fail($"failed to load the transcription engine: {ex.Message}");
        }
        if (!transcriber.IsModelInstalled)
        {
            return Cli.Fail(
                $"the selected engine's model is not installed under {paths.ModelsParent} — " +
                "try --device cpu, or open Jot and complete setup.");
        }
        TranscriberFactory.ApplyLanguage(transcriber, locale);
        if (transcriber is not IStreamingTranscriber streaming)
            return Cli.Fail("the selected engine does not support streaming");

        Ndjson.Install();
        var emitter = new FinalEmitter(
            vocab is null ? null : vocab.ApplySegment,
            Ndjson.EmitFinal,
            SpacelessScripts.Contains(locale.Split('-')[0]));

        return new Pump(streaming, emitter, ChunkSamples).Run(o.Encoding);
    }

    /// <summary>
    /// One gate shared by the read loop, EOF finalization and the Ctrl-C handler. The handler fires on
    /// its own thread while the main thread may be blocked in a Read that will never return (redirected
    /// stdin), so the handler-side finalize is what saves the first Ctrl-C.
    /// </summary>
    private sealed class ShutdownGate
    {
        private readonly object _lock = new();
        private int _sigints;
        private volatile bool _stopRequested;
        private bool _finalizing;

        /// <summary>Interlocked, not a read-modify-write under the lock the finalize path holds: two fast
        /// Ctrl-Cs must never both see 0.</summary>
        public int NoteSigint() => Interlocked.Increment(ref _sigints);

        public bool ShouldStop => _stopRequested;

        public void RequestStop() => _stopRequested = true;

        /// <summary>True exactly once, for the caller that owns the flush.</summary>
        public bool BeginFinalize()
        {
            lock (_lock)
            {
                if (_finalizing) return false;
                _finalizing = true;
                _stopRequested = true;
                return true;
            }
        }
    }

    private enum FeedResult
    {
        Ok,
        Stopped,
        Failed,
    }

    private sealed class Pump(IStreamingTranscriber engine, FinalEmitter emitter, int chunkSamples)
    {
        private readonly ShutdownGate _gate = new();
        private volatile IStreamingSession _session = null!;

        public int Run(PcmEncoding encoding)
        {
            try
            {
                _session = engine.OpenStream();
            }
            catch (Exception ex)
            {
                return Cli.Fail($"failed to open a streaming session: {ex.Message}");
            }
            Console.CancelKeyPress += OnCancel;

            var reader = new StdinAudioReader(encoding);
            var stdin = Console.OpenStandardInput();
            var buffer = new byte[ReadBufferBytes];
            var accumulated = new List<float>(chunkSamples * 2);

            var sessionClock = Stopwatch.StartNew();
            var quietClock = Stopwatch.StartNew();
            string lastPartial = "";

            while (!_gate.ShouldStop)
            {
                int read;
                try
                {
                    read = stdin.Read(buffer, 0, buffer.Length);
                }
                catch (IOException)
                {
                    break; // producer went away — same as EOF
                }
                if (read == 0) break;

                try
                {
                    accumulated.AddRange(reader.Feed(buffer, read));
                }
                catch (PcmDecodeException ex)
                {
                    return Cli.Fail(ex.Message);
                }
                if (accumulated.Count < chunkSamples) continue;

                switch (Drain(accumulated, sessionClock, quietClock, ref lastPartial))
                {
                    case FeedResult.Failed: return 1;
                    case FeedResult.Stopped: goto finalize;
                }
            }

            if (!_gate.ShouldStop)
            {
                // The feed floor holds back sub-chunk audio; at EOF that tail must still reach the engine
                // or the last half-second of the call is simply lost.
                try
                {
                    accumulated.AddRange(reader.Flush());
                }
                catch (PcmDecodeException ex)
                {
                    return Cli.Fail(ex.Message);
                }
                if (accumulated.Count > 0 &&
                    Drain(accumulated, sessionClock, quietClock, ref lastPartial) == FeedResult.Failed)
                {
                    return 1;
                }
            }

        finalize:
            FlushAndExit();
            return 0; // FlushAndExit exits the process; the compiler does not know that.
        }

        private FeedResult Drain(
            List<float> accumulated, Stopwatch sessionClock, Stopwatch quietClock, ref string lastPartial)
        {
            float[] chunk = [.. accumulated];
            accumulated.Clear();

            string partial;
            try
            {
                partial = _session.Accept(chunk);
            }
            catch (Exception ex)
            {
                // An engine error AFTER finalization began belongs to the shutdown path, not to us — it
                // must not flip the exit code.
                if (_gate.ShouldStop) return FeedResult.Stopped;
                Cli.Fail($"streaming transcription failed: {ex.Message}");
                return FeedResult.Failed;
            }

            // A revising engine (the Granite English path) must NOT have its partials committed:
            // this protocol cannot retract a printed final, so committing them duplicates or drops
            // words at every revision. Its text is emitted once, by FinishSession below. The partial
            // is still used for endpointing — it is a fine activity signal, just not a commitment.
            bool committed = !_session.RevisesText && emitter.AcceptPartial(partial);

            if (!string.Equals(partial, lastPartial, StringComparison.Ordinal))
            {
                lastPartial = partial;
                quietClock.Restart();
            }
            bool quiet = quietClock.Elapsed >= QuietBeforeRoll;
            bool due = sessionClock.Elapsed >= RollHardCap
                ? committed || quiet
                : sessionClock.Elapsed >= RollAfter && quiet;
            if (!due || _gate.ShouldStop) return FeedResult.Ok;

            // Nothing is lost across the roll: the tail flushes through the emitter first, and audio still
            // under the feed floor has not been fed to the old session at all.
            try
            {
                emitter.FinishSession(_session.Finish());
                _session = engine.OpenStream();
            }
            catch (Exception ex)
            {
                if (_gate.ShouldStop) return FeedResult.Stopped;
                Cli.Fail($"streaming transcription failed: {ex.Message}");
                return FeedResult.Failed;
            }
            // The language lives on the transcriber, not the session, so a fresh session inherits it.
            emitter.ResetSession();
            lastPartial = "";
            sessionClock.Restart();
            quietClock.Restart();
            return FeedResult.Ok;
        }

        private void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            // A hung Finish must not make the process unkillable: the second Ctrl-C is the user choosing
            // truncation over hanging.
            if (_gate.NoteSigint() >= 2) Environment.Exit(130);
            e.Cancel = true;
            _gate.RequestStop();
            FlushAndExit();
        }

        /// <summary>Flushes the engine and exits. Exactly one caller gets to do it. (Not named Finalize —
        /// that is object's destructor slot.)</summary>
        private void FlushAndExit()
        {
            if (!_gate.BeginFinalize())
            {
                // The winner is mid-flush; exiting here would truncate its tail write. Park and let it
                // end the process.
                Thread.Sleep(Timeout.Infinite);
                return;
            }
            try
            {
                emitter.FinishSession(_session.Finish());
            }
            catch (Exception ex)
            {
                Cli.Fail($"failed to finalize stream: {ex.Message}");
                Environment.Exit(1);
            }
            Console.Out.Flush();
            Environment.Exit(0);
        }
    }
}
