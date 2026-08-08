# `jot` CLI for Windows — design

**Status:** design, review-hardened (19 findings from the adversarial pass folded in; the three
blockers — assembly-name collision, O(n)-per-Accept stream growth, English-vocab-off — are addressed
in §3, §6, §5.3). Implements the Windows counterpart of the Mac `jot` CLI v2 (JOT-Transcribe
`docs/jot-cli/design.md` §9–§17, branch `claude/new-session-4vdumt`, paired with jot-shared branch
`claude/jot-cli-v2`). **Ask (user):** a native Windows CLI like the Mac one — reuse the code from the
Windows app; for audio/video decode use the app's exact FFmpeg path (not a lookalike); WSL comes later
(v2, design note only — §10).

## 0. Principles (inherited from the Mac design + this repo's rules)

- **Everything substantive comes from the app.** The CLI is a thin console front-end over the exact
  classes the app runs: `NemotronTranscriber`/`NemotronFp16Transcriber`, `TextPipeline`,
  `VocabularyCorrector`+`VocabularyGate`, `WavAudio`, FFmpeg decode, `JotPaths`. No re-implementations.
  Where a needed piece is currently private/inline in the app (engine-choice lambda,
  `MediaImporter.Decode`, `ApplyLanguage`, `VocabularyRunner.Enrich`), it is **extracted to one shared
  source of truth** that both the app and the CLI call — never copied.
- **Parallel tools, on-disk conventions only.** The CLI and the app never talk at runtime. The CLI
  reads the app's model dir, `settings.json`, `vocabulary.json`, `corrections.json`; it **never writes
  any app state** (no learn-ledger commits, no provenance, no settings writes, no library rows, no
  `logs\` — `NoopDiagnosticsSink` everywhere; see §5.3 for the two store classes that would write).
- **The CLI never downloads models.** Missing model ⇒ exit 1 with "Open Jot and complete setup".
  Exception, deliberate: **FFmpeg auto-download on first non-WAV input** — that *is* the app's own
  behavior (`FfmpegInstaller.EnsureInstalledAsync`, ~138 MB, atomic `.part`-then-move), so the CLI
  matching it is reuse, not a new download surface. A one-line notice goes to stderr.
- **Machine mode fails loudly.** In `--stream`, any engine error ⇒ stderr + non-zero exit immediately.
  No log-and-continue, no silently deaf agent (Call Assist contract).
- **Verified before claimed.** Nothing in this doc is "done" until it has run end-to-end on real audio
  on this machine (models are installed in both the dev root and the MSIX container here).

## 1. Scope

Two modes, matching the Mac CLI's contract so Call Assist can treat the platforms identically:

- `jot transcribe <file>` — batch: file in, **cleaned plain text** on stdout.
- `jot --stream` — live: raw 16 kHz mono PCM on stdin, **NDJSON finals** on stdout, flushed per line.

**Not in v1, deliberately:**
- `--vtt` / `--diarize` — Windows has no diarizer and Nemotron returns no token timings, so cues would
  be one degenerate block. Omitting beats shipping a dishonest format.
- `--mic` — the man-page recipe is `ffmpeg -f dshow … - | jot --stream`; the app owns interactive mic UX.
- WSL/Linux native build — §10.

## 2. Interface

```
jot transcribe <file> [--raw] [--no-vocab] [--vocab <file>] [--language <code>]
                      [-o <path>] [--model-dir <dir>] [--data-dir <dir>] [--device auto|cpu|gpu] [--]
jot --stream [--language <code>] [--rate 16000] [--encoding s16le|f32le]
             [--no-vocab] [--vocab <file>] [--model-dir <dir>] [--data-dir <dir>] [--device ...]
jot --help | --version
```

Semantics identical to the Mac CLI unless noted:
- Default `transcribe` output: vocab-corrected, cleanup-chained plain text. `--raw` skips the cleanup
  chain (vocabulary still applies unless `--no-vocab`). The cleanup default follows the resolved
  `settings.json`'s `OfflineCleanupEnabled` (the app's own switch, `RecorderController.cs:222`) — a
  user who turned cleanup off in the app gets the same raw output here; `--raw` forces off.
- **`--language <code>` — canonicalized ONCE, up front; only the canonical code flows onward** (review
  finding 9: `NemotronLocales.Normalize("es")` silently returns `"en-US"`, and `TryGetSlot("es")`
  falls back to the en-US slot with `false` — a bare subtag must never reach those APIs). Resolution:
  exact case-insensitive hit in `NemotronLocales.All` → that code; else first entry in `All`
  declaration order whose primary subtag matches (`es`→`es-ES`, `en`→`en-US`, `fr`→`fr-FR`,
  `pt`→`pt-BR`); `auto` → auto; anything else ⇒ exit 2 listing the valid codes (`no` has no prefix
  match — the codes are `nb-NO`/`nn-NO` — so it errors rather than guessing). Default: the app's
  configured `Language` from the resolved `settings.json` (already canonical), else `en-US`.
  (Divergence from Mac, which defaults to `en` unconditionally — on Windows we can read the app's
  setting, and "same result as the app" is the less surprising default.) Under `auto`, cleanup is a
  scrubber-only pass (`ModelArtifactScrubber` runs before the iso gate — not full identity; deliberate)
  and vocabulary is off.
- `--rate` only `16000`; `--encoding` only `s16le` (default) / `f32le`; anything else exits 2 at startup.
- `--device`: `auto` (default — honor the app's `TranscriptionDevice` + GPU-tier verdict via
  `EngineSelector.Select`, same rule, same inputs), `cpu` (int4/CPU), `gpu` (fp16/DML if installed,
  else int4 + DML encoder — exactly `TranscriptionDevices.Gpu` routing). The CLI never runs the GPU
  probe; with no verdict, Auto lands on int4/CPU just as the app does pre-probe.
- `--` ends option parsing (filenames starting with `-`). Parser is a single left-to-right pass, the
  Mac parser's rules verbatim: option values must exist and not start with `-`; unknown `-token` ⇒
  usage error; mode/help/version detection never looks past `--`.
- **Exit codes:** 0 success · 1 runtime failure (decode/models/engine) · 2 usage. Errors on stderr as
  `jot: error: …`.

## 3. Architecture

**New console project `src/Jot.Cli/Jot.Cli.csproj`** (`OutputType=Exe`, same TFM
`net10.0-windows10.0.26100.0`, `Platforms=x64`, `RuntimeIdentifier=win-x64`, `UseWPF=true` solely so
the `Jot.csproj` `ProjectReference` resolves the WindowsDesktop framework — nothing WPF ever runs).
Added to `Jot.slnx`.

**Hard constraint (review finding 1, reproduced empirically): the assembly must NOT be named
`jot`/`Jot`.** `AssemblyName=jot` fails restore (`NuGet error: Ambiguous project name 'jot'` — NuGet
keys on AssemblyName when PackageId is unset), and with a PackageId workaround it *builds clean* then
crashes at startup: on case-insensitive NTFS the CLI's `jot.dll` overwrites the referenced `Jot.dll`
in the output dir (`FileNotFoundException: Could not load … 'Jot, Version=…'`). Use
`<AssemblyName>Jot.Cli</AssemblyName>`; the `jot.exe` name comes from an `AfterBuild`/publish step
that copies `Jot.Cli.exe` → `jot.exe` (safe: the apphost embeds the managed dll name `Jot.Cli.dll` at
build time — renaming the exe file does not change what it loads; verify once in §8).

ProjectReference facts (verified by scratch build): it works — no App.xaml/entry-point conflict; the
app's `Content Include="Assets\**"` flows into the CLI output (harmless tile art) and total output is
~80 MB (WindowsSDK.NET, Wpf.Ui, Velopack, …) — accepted for a local tool. `EmbeddedCommonWordsProvider`
resolves its resources from `typeof(...).Assembly` = `Jot.dll`, so the common-word brake works under
ProjectReference (verified: 24 059 words load). The `<Compile Include>` fallback is NOT needed; if it
is ever revived, it must re-declare the `common-words*.txt` `EmbeddedResource` AND
`RootNamespace=Jot`, or the brake goes silently dead (finding 17).

Files (all thin; every heavy lift is an app class): `Program.cs` (args, dispatch, usage),
`CliPaths.cs` (§4), `BatchMode.cs` (§5), `StreamMode.cs` + `FinalEmitter.cs` + `StdinAudioReader.cs`
+ `Ndjson.cs` (§6), `CliVocabulary.cs` (§5.3).

**Extractions in the app (blast-radius rule — one source of truth, behavior byte-identical):**
1. `src/Jot/Transcription/TranscriberFactory.cs` — the engine-choice lambda at `App.xaml.cs:2422–2444`
   becomes `static ITranscriber Create(JotSettings s, NemotronModel int4, NemotronFp16Model fp16,
   OnnxSessionFactory f, Action<string>? log)`. The DI registration calls it **and keeps passing
   `JotLog.Info`** (the per-launch `engine: …` log line must survive). Inputs stay exactly:
   `s.TranscriptionDevice`, `fp16.IsInstalled`, `s.GpuProbeVerdict`, `s.GpuProbeKey` vs
   `GpuInfo.TryGetPrimaryAdapter()?.CacheKey`.
2. `SettingsViewModel.ApplyLanguage(ITranscriber, string)` (already a public static that does the
   engine downcasts, `SettingsViewModel.cs:304`) **moves to** `TranscriberFactory.ApplyLanguage`;
   the VM and `App.xaml.cs:389` call the moved one. The CLI never duplicates the two lines (finding 18).
3. `src/Jot/Import/FfmpegDecoder.cs` — `MediaImporter.Decode` moves here as
   `static (float[] samples, double duration) DecodeToMono16k(string ffmpegExe, string path)` — the
   exe path becomes a **parameter** (finding 4: the current `FfmpegInstaller.ExePath` is a static over
   `JotPaths.AppDataRoot`, which for an unpackaged CLI is always `%LOCALAPPDATA%\Jot` and can never see
   a Store install's `…\LocalCache\tools\ffmpeg.exe`). `MediaImporter` passes `FfmpegInstaller.ExePath`
   — behavior-preserving. `FfmpegInstaller.EnsureInstalledAsync` gains an optional install-dir
   parameter (default: current behavior) so the CLI can install into its resolved root.
4. `VocabularyRunner.Enrich(detections, terms)` (private, `VocabularyRunner.cs:367`) becomes an
   internal static reused by the CLI (finding 8 — skipping it silently changes the gate's plausibility
   inputs).

No `Jot.Core` split in v1 — flagged as the follow-up if the CLI and app ever need independent shipping.

## 4. Data root, models, settings — the package-identity gotcha

A separate console exe has **no MSIX package identity**, so `PackagePaths.ContainerRoot` is null in
the CLI even when the app is the Store build: `JotPaths.AppDataRoot` resolves to `%LOCALAPPDATA%\Jot`
while the Store app's data lives in `%LOCALAPPDATA%\Packages\<PFN>\LocalCache`. Resolution order:

1. `--model-dir <dir>` — **the models parent** (the CLI appends `NemotronModel.ModelFolder` /
   `NemotronFp16Model.ModelFolder` itself; finding 12 — the locators' explicit-directory ctor takes the
   *leaf* folder, which cannot serve both engines, so the CLI owns the join).
2. `--data-dir <dir>` — the Jot data root: models at `<dir>\models`, vocabulary at `<dir>\Vocabulary\`,
   settings at `<dir>\settings.json` if present.
3. Default probe, first root that has an installed int4 or fp16 model wins:
   a. the unpackaged root `%LOCALAPPDATA%\Jot` (honoring its `settings.json` → `DataDirectory` redirect);
   b. every `%LOCALAPPDATA%\Packages\*\LocalCache` whose `models\` contains a `nemotron-*` folder
      (name-agnostic on purpose — Partner Center *generates* Identity names, so a `Vineetsriram.Jot*`
      glob is fragile; finding 13), honoring each container's `DataDirectory` redirect. Unreadable /
      missing-drive roots are skipped, probing continues (moved-to-D: case).
   The chosen root is reported on stderr (one line) so a wrong pick is diagnosable.
4. Nothing found ⇒ exit 1: "No transcription model found. Open Jot and complete setup, then retry
   (or pass --model-dir)."

The **resolved root's `settings.json`** supplies: `Language` default, `TranscriptionDevice` /
`GpuProbeVerdict` / `GpuProbeKey` for `--device auto`, `OfflineCleanupEnabled`, and the vocabulary
master toggle (`VocabularyEnabled`). CLI flags override. `JsonSettingsStore` is not reused — **not**
because it writes on load (it doesn't; finding 11), but because its `Dir`/`FilePath` are `static
readonly` snapshots of `JotPaths.ConfigDir`, un-overridable. Instead: a tiny read-only
`ISettingsStore` implementation (`Save`/`Reset` no-ops) deserializing the same `JotSettings` type with
the same tolerate-missing/corrupt behavior — the model locators take `ISettingsStore`, so this slots
straight in.

FFmpeg: probe the same roots for `tools\ffmpeg.exe` (note: `tools\` hangs off `AppDataRoot`, not the
moved `DataDir` — a moved-to-D: user still has ffmpeg on C:); if absent and the input isn't WAV,
`EnsureInstalledAsync` into the CLI's resolved root with a stderr notice.

## 5. Batch mode (`jot transcribe`)

1. **Decode.** `.wav` → `WavAudio.ReadMono16k` (NAudio; PCM 8/16/24/32 + float, any rate/channels),
   **wrapped in try — on failure fall back to the FFmpeg path** (compressed-codec WAVs — µ-law/ADPCM —
   and mislabeled files; finding 14). Everything else → the extracted `FfmpegDecoder.DecodeToMono16k`
   (exact app decode: `-ac 1 -ar 16000 -f f32le -`). Empty samples ⇒ exit 1 ("no audio track?").
2. **Transcribe.** Models-present check **before** decode (fresh install fails fast, actionable).
   `TranscriberFactory.Create(...)` → `TranscriberFactory.ApplyLanguage(transcriber, canonicalCode)` →
   `TranscribeAsync(samples, 16000)`.
3. **Pipeline, exactly the app's order** (`RecorderController.StopAndDeliverAsync`):
   `TextPipeline.Clean(text, canonicalCode, isNemotron: true)` first (skipped under `--raw` /
   `OfflineCleanupEnabled=false`), **vocabulary last** — nothing may touch the text after the gate
   (its anchors assume it). Then, CLI-only: trim trailing whitespace (the app's trailing space exists
   for paste; a CLI emits `text + "\n"`). This trim is after-the-gate cosmetics on the very last
   byte — accepted, matching the Mac CLI's output shape.
4. **Output.** stdout, or `-o <path>` (UTF-8, no BOM).

### 5.3 Vocabulary (both modes)

Model-free corrector only. **The CLI's gating rule is NOT `VocabularyRunner.ModeFor`** (finding 3,
blocker: `ModeFor` routes English to `Acoustic` — the 283 MB CTC spotter the CLI deliberately omits —
so "ModeFor + no spotter" would silently disable vocabulary in the default language). The CLI rule:

> run the textual corrector iff `VocabularyLimits.TextualShips(canonicalLocale)` — which includes
> `en` (measured E5: 0.27/0.22 FP/1k, the "window before the checkpoint arrives" setting) and returns
> false for `auto` — and `EmbeddedCommonWordsProvider.ResourceFor(locale) != null`.

**Documented divergence #3 from the app** (alongside the two existing ones): *in English the CLI runs
the textual corrector where the app runs the acoustic spotter — same file, same gate, different
detector; recall differs slightly.* (Same divergence the Mac CLI documents in its man page.)

The gate call is the app's call **verbatim** (finding 8), minus the write-side stores:
```
VocabularyGate.ApplyFromDetections(
    text, TranscriberFactory-extracted Enrich(detections, terms), durationSeconds,
    EmbeddedCommonWordsProvider.Shared,
    EmbeddedCommonWordsProvider.ResourceFor(canonicalLocale),   // resource NAME, not a locale string
    overrides, NoopDiagnosticsSink.Instance);
```
with detections from `VocabularyCorrector` (the app's `ITextVocabularySpotter`).

Read-only wiring (finding 10 audit):
- `VocabularyStore` — safe to reuse (ctor loads, never writes; `Save` guarded). Terms from the
  resolved `<dataRoot>\Vocabulary\vocabulary.json`, or `--vocab <file>` (same JSON schema; unreadable
  explicit path ⇒ exit 1; absent default ⇒ vocab silently off).
- `CorrectionStore` — **must NOT be constructed**: `FilePath()` runs `Directory.CreateDirectory` on
  the read path (`CorrectionStore.cs:337`). Overrides are read by direct
  `JsonSerializer.Deserialize` of the shared record types with `CorrectionStore`'s serializer options.
- `CorrectionProvenance` — never touched. Diagnostics: `NoopDiagnosticsSink` (the JotLog sink creates
  `logs\` on first write).

`--no-vocab` disables. Master-toggle default comes from the resolved settings' `VocabularyEnabled`.

## 6. Stream mode (`jot --stream`)

- **stdin contract (byte-identical to Mac):** raw 16 kHz mono PCM, `s16le` default / `f32le` opt-in;
  a leading RIFF header is sniffed — `RIFF` + non-`WAVE` ⇒ exit 1 ("RIFF container but not WAV");
  `WAVE` ⇒ minimal chunk walk to the `data` chunk (word-aligned, 1 MiB bound, truncated-WAV ⇒ exit 1);
  `--encoding` (not the WAV `fmt`) governs decoding. Trailing partial frame dropped. Binary read over
  `Console.OpenStandardInput()`.
- **Output framing:** `Console.OutputEncoding = new UTF8Encoding(false)` and `Console.Out.NewLine =
  "\n"` — .NET emits CRLF by default, which breaks "byte-identical to Mac" (finding 16). Byte-level
  test in §8.
- **Engine + feed sizing (finding 2, blocker).** Same construction as batch, but the run loop must
  respect the engine's cost model: `Session.Accept` runs incremental *inference* (new 56/32-frame
  chunks only) but **O(total-session) bookkeeping per call** (full `_audio.ToArray()` copy, full
  feature-major rebuild on fp16, full detokenize). Two consequences, both mandatory:
  1. **Feed floor:** accumulate stdin into a buffer and call `Accept` only with ≥ one engine chunk of
     new audio — 8 960 samples (560 ms) int4 / 5 120 (320 ms) fp16. Sub-chunk `Accept`s do zero
     inference and pay the full O(n) tax (a 4 KiB-read loop would `Accept` ~28 000×/hour).
  2. **Session recycling:** long streams roll to a fresh `Session` at a quiet commit boundary —
     when ≥ `RollMinutes` (10) have elapsed AND the partial hasn't grown for ≥ 2 s, call `Finish()`,
     flush the tail through `FinalEmitter.FinishSession`, open a new session + fresh emitter state
     (nothing is lost; finals already committed). Hard cap at 15 min: roll at the next whitespace
     commit even if never quiet. This bounds memory (~minutes, not hours, of `_audio`/`_frames`) and
     the O(n) constant. The real fix — making `Session` drop dead audio and build features
     incrementally — is filed as an app-side follow-up benefiting `LiveTranscription` too; the CLI
     must not fork the engine.
- **Backpressure is structural:** one thread reads stdin, accumulates, `Accept`s, emits — a
  faster-than-realtime producer (piped file) is simply throttled by the blocking pipe once its buffer
  fills. No unbounded queue exists (unlike the Mac's `AsyncStream(.unbounded)`). The app's
  `RealtimeGuard` batch-degrade has no CLI equivalent, deliberately: the CLI's contract is
  "process everything, in order", not "keep up at any cost".
- **Final derivation — `FinalEmitter`, ported logic-for-logic from the Mac CLI** (its behavior is the
  frozen protocol; the Swift source is the spec):
  - Append-only commits: each partial extending the last ⇒ commit up to the last whitespace boundary
    (never split an in-progress token).
  - Revision branch: warn ONCE on stderr, adopt the new hypothesis wholesale. **On this engine the
    branch is provably unreachable** (finding 7: `_tokens` append-only, `_fedChunks` monotone,
    detokenize a pure prefix function) — it stays as defense and is covered by synthetic unit tests,
    not live measurement.
  - Spaceless scripts (`zh ja th km lo my`): commit at CJK sentence punctuation `。！？；，、`
    (deliberately not ASCII — "3,000"), else length backstop: pending ≥ 48 chars ⇒ commit all but 16.
  - `Finish()` flush: compare against the right-trimmed committed prefix (else every clean session
    ends in a spurious revision warning); on divergence inside committed text, emit the tail snapped
    back to the previous word boundary — bounded one-word duplication beats losing the last utterance.
  - Each committed segment: vocab-correct (§5.3, per-segment), then `{"type":"final","text":"..."}`,
    flushed. Fields additive-only. **No cleanup chain in stream mode.**
  - **Accepted stream-mode divergences (finding 19):** a multi-word term straddling a commit boundary
    cannot be spotted (the corrector windows over the segment only), and the gate's positional axis is
    per-segment, not whole-transcript. Same trade the Mac CLI ships.
- **Shutdown (finding 6 — a real gate, not a bool):** `Console.CancelKeyPress` fires on a threadpool
  thread while the main thread may be blocked in `Read`. Port the Mac `ShutdownGate` semantics with
  actual synchronization: `Interlocked.Increment` on the Ctrl-C counter (two fast Ctrl-Cs must not
  both read 0); count ≥ 2 ⇒ `Environment.Exit(130)` unconditionally (a hung `Finish()` must not make
  the process unkillable); count == 1 ⇒ `e.Cancel = true`, set a `volatile` stop flag, and run
  finalization through a `beginFinalize` check-and-set under a lock — exactly one entry point
  (Ctrl-C handler, or the main loop on EOF/flag) owns `Finish()` + tail flush + `exit 0`; the loser
  parks. `FinalEmitter` takes the same lock internally (handler-thread finalize races the main loop's
  emit path). If the main thread is blocked in a `Read` that will never return (redirected-file stdin),
  the handler-side finalize is what saves the first Ctrl-C; the second Ctrl-C remains the escape hatch.
- **Fail loudly.** Any `Accept`/`Finish` exception (except after finalize began) ⇒
  `jot: error: streaming transcription failed: …` + exit 1. `OnnxSessionFactory.BackendFallback`
  (DML→CPU degrade) is app behavior, not an error — surfaced once on stderr so a consumer's logs
  explain a latency shift.

## 7. jot-shared sync (paired work, same branch)

The jot-shared branch `claude/jot-cli-v2` and this repo have flowed **both directions** (E7/E8 went
Windows→Swift; the Swift review then found issues "not in the Windows spec"). The sync task:

1. **Run the branch's new golden fixtures against the C# port**: `post_processing.json`,
   `corrector_apply.json`, and the ~400 new lines of `detections_apply.json` from
   `jot-shared/Tests/*/Fixtures/`. Fixture-driven tests land in `tests/Jot.Tests` next to the existing
   golden tests.
2. **Backport whatever fails**: expected deltas are the multi-word placement + tail-duplication fixes,
   the precision guards, and the "§2/§3-family third bug" the Swift branch calls out as not in the
   Windows spec. Respect the documented deliberate divergences — a fixture that codifies a divergence
   gets the same documented exception here, not a silent behavior change.
3. **Port `PostProcessing`** (jot-shared `JotTextPipeline/PostProcessing.swift`, 71 lines) into
   `src/Jot/Text/PostProcessing.cs`, inserted into `TextPipeline.Clean` **between `NumberNormalizer`
   and `EnsureSingleTrailingSpace`** — NOT as the final stage (finding 5: PostProcessing trims
   trailing whitespace, so appending it last would silently destroy the trailing-space paste contract
   that `TextPipelineTests.cs:68` asserts, and undo `FillerWordCleaner`'s `s + " "` for all six filler
   languages). It is **gated off for spaceless scripts** (the Swift doc-comment's own instruction:
   "CJK … route them around it"). Expectation updates in existing tests are limited to the `" ."` →
   `"."` class — knowingly, never incidentally.

## 8. Testing & verification

- **Unit (new, in `tests/Jot.Tests`):** `FinalEmitter` (append-only commits, whitespace holdback,
  spaceless-script boundaries, synthetic revision recovery, finish-flush trim — vectors transcribed
  from the Swift source's documented cases); `StdinAudioReader` WAV sniff (RIFF-not-WAVE, truncated
  WAV, odd-size chunk padding, header split across reads, sub-12-byte raw PCM); arg parser (missing
  value, unknown option, `--` routing, `--rate`/`--encoding` validation, bare-language
  canonicalization incl. the `es`→`es-ES` and `no`→error cases); `CliPaths` resolution order;
  fixture suites from §7; NDJSON byte-level framing (`\n`, UTF-8 no BOM).
- **End-to-end on this machine (models installed in both roots):** `jot transcribe probe.wav`;
  `jot.exe` rename-of-apphost sanity; a non-WAV file via the ffmpeg path; binary-safe stdin piping
  (`cmd /c "type raw.pcm | jot --stream"` or ffmpeg — PowerShell pipes re-encode and are documented
  as unsupported, finding R4); WAV-header and raw stdin variants; Ctrl-C finalize (first = flush +
  exit 0, second = 130); `--language de-DE` and bare `es`; `--device cpu` vs `auto` (verdict=GPU on
  this box); vocab correction against the machine's real `vocabulary.json`; a ≥ 12-min stream to
  exercise one session roll; exit codes for missing model dir, garbage input, RIFF-not-WAV.
- **Regression:** full `dotnet test` green; app builds and dictates unchanged (app-side edits are the
  four §3 extractions + §7.3's pipeline insertion, all fixture-covered).

## 9. Distribution

v1: `jot.exe` out of `dotnet publish -r win-x64` alongside the app's publish output; documented in
README. **MSIX/AppExecutionAlias integration is deferred** — a 1.2.1 Store submission is in flight and
the package must not churn under it. Follow-up: add `jot.exe` to the package with an
`AppExecutionAlias` (`jot.exe` on PATH for Store installs — and alias-launched exes DO get package
identity, which dissolves the §4 gotcha for Store users).

## 10. v2 direction — WSL2 (design note only, per the ask)

- **Near-free interop path:** WSL2 runs Windows exes natively with binary-faithful pipes
  (`ffmpeg … - | /mnt/c/.../jot.exe --stream` from a WSL shell crosses the boundary byte-clean).
  Shipping = "put jot.exe on PATH" (the MSIX alias above, or a doc line). This covers "use jot from my
  Linux shell" with zero new code — the right v2.0.
- **True native Linux build** would need: ONNX Runtime Linux (CPU EP; DirectML is Windows-only — GPU
  would mean CUDA/ROCm EPs), a `net10.0` (non-Windows) split of the engine + text/vocab code (already
  WPF-free; the TFM and NAudio are the blockers — the stream path never touches NAudio), and a Linux
  model-path convention. Real work; only worth it if Call Assist runs Linux-side. Deferred.

## 11. Risks (post-review)

- **R1 — session-roll seam quality.** Rolling loses encoder cache continuity; a word spoken exactly
  across the roll may degrade. Mitigated by rolling only at quiet boundaries (§6); measure on the
  ≥ 12-min E2E stream before freezing `RollMinutes`.
- **R2 — apphost rename.** `jot.exe` copied from `Jot.Cli.exe` must still resolve `Jot.Cli.dll`
  (embedded name, not filename — verify once in §8).
- **R3 — corrections.json schema drift.** The CLI's direct deserialization must reuse the shared
  record types + serializer options, and a schema change in `CorrectionStore` must fail soft (vocab
  runs without overrides, stderr note) — never crash the CLI.
- **R4 — stdin binary fidelity on Windows.** PowerShell pipes re-encode; docs steer to `cmd /c type`,
  ffmpeg, or any native producer. The CLI itself reads the raw handle.
- **R5 — container probe hits a moved data root** whose drive is absent. Tolerated: skip unreadable
  roots, keep probing (§4.3).
