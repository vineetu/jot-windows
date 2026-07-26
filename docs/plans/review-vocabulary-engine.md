# Adversarial review — `vocabulary-ctc-port.md` (engine design) vs. the actual codebase

> Reviewed 2026-07-25 against `src/Jot` at working-tree state, plus the companion repos
> `C:\Users\vinee\projects\jot-shared` (HEAD `5326460`) and `C:\Users\vinee\projects\JOT-Transcribe`.
> Scope: the ENGINE half only. UX is covered by `review-vocabulary-ux.md`.
>
> **Verdict: NEEDS REVISION.** The central idea is sound and the finished half is genuinely
> solid. What is wrong is the *unknown-facing* half: M0's exit criteria cannot validate what
> they claim to validate, one required capability (a SentencePiece encoder) is missing from the
> plan entirely, one shipped user-facing promise (auto-detect gating) is not buildable today,
> and the "stream it during recording" design puts load on the one path the app cannot afford
> to slow down. Estimates in §5 are low by roughly 2×.

---

## 0. What checks out — read this first

These claims were verified and are load-bearing. Do not re-litigate them.

| Doc claim | Verdict | Evidence |
|---|---|---|
| Full 16 kHz mono float audio is available **at stop** | **TRUE** | `Recording/AudioRecorder.cs:154-165` — `Stop()` returns `RecordingResult.Samples` (float[], 16 kHz mono) and writes the WAV *from* it (`:155`). A post-stop spotter has a clean, free, already-paid-for buffer. |
| No decoder surgery needed | **TRUE** | The RNNT greedy loop (`NemotronFp16Transcriber.cs:213-224`, `NemotronTranscriber.cs:176+`) is untouched by anything in this design. |
| The download engine is reusable | **TRUE, and better than claimed** | `Services/Download/AssetDownloader.cs:31` `EnsureAsync(manifest, targetDir, …)` has zero Nemotron assumptions; free-space guard `:225-243` is generic (`needBytes + 1 GB` headroom); `AssetManifest.DescribeProgress` (`AssetManifest.cs:22`) derives text from `TotalBytes`. Retry/Range-resume/SHA-256 all come free. |
| Gate after cleanup, before save, gives valid offsets | **TRUE — and it is the strongest property in the whole design** | `RecorderController.cs:226` `Clean` → `:235` `_store.Add` → `:244` `PasteAtCursor` → `:253` `TranscriptReady`, all on one `text` local. Nothing mutates the string in between. `ApplyFromDetections` returns `PublishedStart/PublishedLength` against the string it returns (`VocabularyGate.cs:461, 480-481`), so offsets are exact against what is saved *and* pasted. (See MAJOR-6 for what happens *after* the save.) |
| Purge and data-folder-move handle a third model for free | **TRUE for the model** | `JotDataPurge.cs:26` `DataSubdirs = ["models", …]` and `DataFolderMigrator.cs:33` `MigratedItems = ["models", …]` are folder-level. (See MAJOR-8 — this is **not** true for `vocabulary.json`.) |
| The gate is ported and fixture-green | **TRUE** | `src/Jot/Vocabulary/{VocabularyGate,CommonWords,CorrectionKey,CorrectionRecord,AskPolicy,Seams}.cs`; 21 embedded lists via a single `EmbeddedResource` glob (`Jot.csproj:39`) with the right rationale in the comment; all 7 jot-shared fixtures vendored at `tests/Jot.Tests/Fixtures/vocab/` and wired at `Jot.Tests.csproj:31-34`. `5326460` is jot-shared HEAD — the pin is current, 0 commits behind. |
| Common-word lists must fail closed | **TRUE and already implemented** | `CommonWords.cs:53-93` — named-but-missing resource reports once and returns empty; `Jot.csproj:34-39` uses `EmbeddedResource` (not `Content`) *specifically* to make the csproj trap impossible. This one is done right. |

---

## BLOCKERS

### BLOCKER-1 · The mel/feature gap is a new front-end, not a parameter — and M0 cannot detect a partial failure

**Doc claim** (§2 table, §4.1, §5 M0): *"Mostly have it… Whether the CTC export wants identical
80-bin log-mel or expects raw samples is a spike item — the sherpa export bundles its own
preprocessor expectations"*, resolvable in *"~half day: load the model, feed one WAV, check the
greedy CTC decode is sensible English."*

**Evidence this is wrong on three counts.**

1. **`MelFrontend` is hard-wired to Nemotron and cannot be reconfigured.**
   `Transcription/Nemotron/MelFrontend.cs:16-23` — every parameter is a `const`:
   `NFft=512`, `Hop=160`, `WinLength=400`, `NMels=128`, `Preemph=0.97`, `LogGuard=1e-10`.
   Filterbank is Slaney, 0–8000 Hz (`:151-176`). **There is no normalization of any kind.**
   `NMels` is `public const` and is read from 9 sites in the shipping engines
   (`NemotronFp16Transcriber.cs:125, 201, 202, 234, 237`; `NemotronTranscriber.cs:182, 187, 211`;
   `StreamingMel.cs:33, 37`). Parameterizing it means touching the hot path of the app's core
   value — the exact thing `Jot.csproj:29` (`InternalsVisibleTo` "for MelFrontend/StreamingMel
   byte-exactness" tests) exists to protect. So this is a *second* front-end, not a flag.

2. **A NeMo CTC export's feature contract differs in the expensive direction.** A
   `parakeet-tdt_ctc-110m` CTC branch expects **80** log-mel bins (not 128), log guard `2^-24`
   (not `1e-10`), and — critically — **`normalize_type = per_feature`**: per-mel-bin mean/variance
   normalization computed **over the utterance**. Two of three differ from what we produce, and
   the third is utterance-global.

   **That last one silently invalidates the doc's own streaming design.** §3.3 says *"port
   `StreamingCtcSpotter`'s merge verbatim — it is the difference between 'identical to one-shot'
   and 'subtly wrong at boundaries'."* If the encoder input is normalized over the utterance,
   a 15 s chunk normalized against its own statistics is **not** a slice of the one-shot input,
   and no log-sum-exp merge of the *outputs* recovers the one-shot result. The Mac's merge is an
   approximation whose error is bounded by whatever `MelSpectrogram.mlmodelc` does internally —
   which is not readable from either repo. The "identical to one-shot" claim is unsupported.

3. **The Mac precedent the doc leans on says the opposite of what the doc says it says.**
   Verified: `JOT-Transcribe/Sources/Transcription/CtcModelCache.swift:53-54` names the required
   file set as *"MelSpectrogram, AudioEncoder, CtcHead bundles + vocabulary.json + tokenizer.json"* —
   `MelSpectrogram` is a **compiled CoreML model**. Every Swift call site passes raw Float32
   samples and nothing else (`StreamingCtcSpotter.swift:148`, `VocabularyRescorerHolder.swift:269,
   :330`); a grep for `n_mels|nMels|melBins` across both Swift repos returns zero implementation
   hits. Apple bundles the preprocessor **because CoreML can package a DSP graph**. A sherpa-onnx
   NeMo CTC export does **not** contain the front-end in the ONNX graph — sherpa-onnx computes
   features externally (kaldi-native-fbank) and applies the normalization named in the ONNX
   metadata. Feeding the `.onnx` raw samples will fail at the first `Run`. So the Mac gives us
   **zero** guidance here, and the doc's "the sherpa export bundles its own preprocessor
   expectations" is an assumption with the precedent pointing the other way.

**Why "½ day, check the decode is sensible" is not a valid exit criterion.** There is **no
reference oracle in this repository**: zero `.py` files, no `tools/`, no NeMo, no
onnxruntime-python (`scripts/` holds four files — PowerShell + two static HTML pages). The
existing mel was built against *"the validated Python reference"* (`MelFrontend.cs:8`); for CTC
there is nothing to validate against. And "sensible English" is a weak oracle by construction: a
*slightly* wrong front-end (correct filterbank, missing per-feature normalization) commonly still
decodes recognizable-but-degraded English. That failure ships as "the spotter has poor recall",
gets attributed to the DP or the thresholds, and costs days in M2 to trace back here.

**Recommended fix.**
- Rewrite M0's exit criterion from "output looks sensible" to a **numeric target**: pull
  `org.k2fsa.sherpa.onnx` (.NET package) into a **throwaway scratch console project** — never the
  shipping app — run its offline recognizer over `src/Jot/Assets/probe.wav`, dump its computed
  feature matrix and its CTC log-prob matrix, and require our C# front-end to reproduce the
  features to ≤1e-3 and the argmax path exactly. That is the only way "½–1 day" is even close,
  and it converts an unfalsifiable spike into a pass/fail.
- **Do not take a runtime dependency on sherpa-onnx.** `Jot.csproj:52-56` already documents that
  a duplicate `onnxruntime.dll` breaks MSIX packaging (APPX1101), and sherpa-onnx ships its own.
- Plan for `Vocabulary/CtcMelFrontend.cs` as a **separate 80-bin class** with per-feature
  normalization. Do not touch `MelFrontend`. Factor the shared FFT/window/reflect-pad only if it
  falls out cleanly; duplication is cheaper than risking the RNNT's byte-exactness.
- Re-estimate M0 at **2–3 days**, and make it a real kill gate.

### BLOCKER-2 · There is no way to tokenize a user's term into CTC ids — the plan omits it entirely

The whole spotter depends on turning `"Nemotron"` into a sequence of ids in the CTC model's
1024-token BPE vocabulary (the Mac's `loadWithCtcTokens`). The doc's §3 file list —
`CtcModel`, `CtcModelInstaller`, `CtcSpotter`, `CtcWordSpotter`, `VocabularyGate`, `CommonWords` —
**contains no tokenizer**, and §4/§5 budget no time for one.

**Evidence Windows has no such capability.** The only tokenizer code in the repo is
*de*tokenization: `NemotronFp16Transcriber.LoadVocab` (`:354-369`) builds an id→token array, and
`Detokenize` (`:308-320`) joins pieces and replaces the `▁` metaspace. That is the easy direction.
Going text→ids for an *arbitrary user-supplied string* requires the actual SentencePiece model
(`tokenizer.json` / `.model`) plus a BPE/unigram encoder with the correct normalizer, prefix-space
handling, and byte-fallback behavior. `grep` for `sherpa|SentencePiece|Tokenizer` in `src/Jot`
returns nothing but doc prose. No such package is referenced (`Jot.csproj` ONNX deps are only
`Microsoft.ML.OnnxRuntime.DirectML 1.20.1` and `Microsoft.AI.DirectML 1.15.4`).

This is not incidental. A tokenizer that disagrees with the model's by even one merge produces a
term-id sequence the DP will never find in the log-prob matrix — i.e. **silent zero recall**, with
no error and no log line. It is exactly the class of bug that reads as "the feature doesn't work"
rather than "the feature is broken".

**Recommended fix.**
- Add `Vocabulary/CtcTokenizer.cs` to the §3 file list and the M2 estimate.
- Evaluate `Microsoft.ML.Tokenizers` (`SentencePieceTokenizer`) as the implementation — it is a
  first-party .NET package and avoids a native dep, but it is a new correctness surface.
- Add a conformance test the same way the gate got one: take ~200 terms, encode with the
  reference (sherpa-onnx scratch harness from BLOCKER-1, or an offline dump), assert id-for-id
  equality in xUnit. Without this, nothing downstream can be trusted.
- The tokenizer file must ship in the CTC model manifest (Mac's set includes `tokenizer.json`,
  352 KB — `CtcModelCache.swift` file list). Add it to the manifest plan in §3.2.

### BLOCKER-3 · Gating the spotter on the **resolved** language is not implementable in auto-detect — and that promise is already written into the UX

`vocabulary-ux.md:784-787` promises the user: *"Jot will apply your terms only when it detects
English… the spotter must be gated on the resolved language of the actual recording, not the
setting."* This cannot be built today.

**Evidence.**
- Language is a **global setting applied once**, not a per-recording result:
  `App.xaml.cs:389` → `SettingsViewModel.ApplyLanguage(transcriber, settings.Current.Language)` →
  `SettingsViewModel.cs:268-275` → `SetLanguageId` / `SetLanguageSlot`. The session snapshots the
  slot at open (`NemotronTranscriber.cs:117-118, :125`).
- Auto-detect is just another slot: `NemotronLocales.cs:21-22` `AutoCode = "auto"`, `AutoSlot = 101`.
- The model **does** emit a `<xx-YY>` locale tag as the first token — and **both** transcribers
  throw it away:
  `NemotronTranscriber.cs:293` and `NemotronFp16Transcriber.cs:316`, identical line:
  `if (piece.Length >= 2 && piece[0] == '<' && piece[^1] == '>') continue; // special / locale token`
- The only reader is dev-only: `NemotronTranscriber.Tokens` (`:129-131`) + `Piece(int)` (`:282-284`),
  consumed by `--langprobe` at `App.xaml.cs:1607-1644`.
  **`NemotronFp16Transcriber` has neither `Tokens` nor `Piece`** — so on the GPU tier (precisely
  where spotter contention matters most, see MAJOR-4) the detected language is unrecoverable even
  for diagnostics.
- `RecordingItem` (`Models/RecordingItem.cs`) has no language field and `RecordingDto`
  (`JsonRecordingStore.cs:150-152`) does not carry one, so nothing records what was detected.

**Sequencing problem the docs also miss:** the tag is emitted at the *start* of the token stream,
but a spotter streamed *during* recording must start before any token exists. Streaming and
auto-detect gating are mutually exclusive unless you run speculatively and discard.

**The doc's justification for this whole section is also factually false.** §2 says *"The Mac has
the same constraint on its Nemotron path."* Verified untrue:
`JOT-Transcribe/Sources/Transcription/Transcriber.swift:331-343` gates only **Japanese**, and on
*model identity* (`if modelID == .tdt_0_6b_ja`), not language. The Nemotron path gates on **engine**
(`DualPipelineTranscriber.swift:80-83`) and hardcodes `language: .english` at `:347` purely to
select the common-word list. On the v3 European path the spotter **does** run on non-English, with
the common-word brake *absent* (`CommonWords.swift:96-100`). So there is no "vocabulary disables
itself on non-English" behavior to inherit — Windows would be the first platform to build it. That
is arguably the right call, but the doc must stop claiming precedent it does not have, and should
note the `lista→Lisa` incident is evidence *for* the Windows design, not evidence the Mac fixed it.

**Recommended fix (cheap, honest, and it deletes work from v1.3).**
- Scope-cut: **vocabulary is off whenever `Settings.Language` is not an `en-*` locale, and off in
  auto-detect.** Gate with `NemotronLocales.Normalize(s.Language).StartsWith("en", OrdinalIgnoreCase)`
  — trivially implementable, one line, zero engine change.
- Precedent for exactly this: `TextPipeline` has the same problem and already punts —
  `LanguageCode.cs:38` excludes `"auto"`, so auto-detect dictations today get no filler removal and
  no number normalization. Being consistent with shipped behavior is defensible and honest.
- Change the UX copy from *"only when it detects English"* to *"Automatic language detection is on,
  so Jot can't tell which language you're speaking — set your language to English to use vocabulary."*
- Move "surface the leading locale token on `ITranscriber` for **both** engines (fp16 needs
  `Tokens`/`Piece` added)" to a v1.4 ticket. Note it also unlocks per-recording language on
  `RecordingItem`, which `TextPipeline` wants too — so it's shared value, not vocabulary-only cost.

### BLOCKER-4 · A spotter or gate exception destroys the user's dictation

`RecorderController.StopAndDeliverAsync` wraps everything from `:195` in one `try`, whose `catch`
at `:267-271` plays an error sound and fires `Failed?.Invoke("Transcription failed", ex.Message)` —
**and never reaches `_store.Add` (`:235`) or `PasteAtCursor` (`:244`)**. The transcript is gone.

Inserting the gate between `:226` and `:235` therefore puts a brand-new, model-driven, ONNX-backed
subsystem on the one code path in the app that must never lose text. The design doc does not mention
error handling anywhere.

**Recommended fix.** State it as a hard rule in §3: the gate call site is
```
string gated = text;
try   { gated = RunVocabulary(text, result.Samples, result.Duration); }
catch (Exception ex) { JotLog.Error("vocabulary gate failed — delivering ungated text", ex); }
text = gated;
```
with a matching unit test that a throwing spotter still yields the original transcript, saved and
pasted. Also add a wall-clock timeout — a hung ONNX session must not hold the paste.
(Related non-issue, worth stating so nobody worries: the gate is safe to place before the empty
check at `:228` — `VocabularyGate.cs:375-376` returns the input unchanged for an empty transcript.)

---

## MAJORS

### MAJOR-1 · "A streamed chunk-wise spotter can run during recording" — the audio tap is not free, and it is O(n) per poll

**What is actually retained.** `AudioRecorder._buffer` (`:41`) is a `MemoryStream` of **raw
device-native bytes** — typically 48 kHz stereo float32 under WASAPI shared mode — not 16 kHz mono
float. Conversion happens only in `SnapshotSamples()` (`:73-92`) and `Stop()` (`:135-166`). Nothing
frees or overwrites it mid-recording; it only grows.

**But `SnapshotSamples()` re-decodes the entire buffer from byte 0 on every call**
(`:80` `raw = _buffer.ToArray()`, then a fresh `RawSourceWaveStream` + `WdlResamplingSampleProvider`
+ `ReadAll`), and `ReadAll` (`:192-200`) allocates twice per 1 s chunk
(`AddRange(buf.AsSpan(0, read).ToArray())`). There is no delta API. For an 80 s dictation at
48 kHz stereo float32 that is ~30 MB copied and resampled **per call**, and `LiveTranscription`
already calls it every 300 ms (`LiveTranscription.cs:27`). A second consumer doubles an
already-quadratic-across-a-dictation cost.

Note the audio is already resident in three forms during a recording: the raw device-format
`MemoryStream`, the per-poll 16 kHz snapshot, and the streaming session's own
`List<float> _audio` (`NemotronFp16Transcriber.cs:121`, `NemotronTranscriber.cs:107`) which is
itself `ToArray()`'d on every `Accept` (`:154`, `:140`). A spotter adds a fourth.

**Recommended fix.** If a second consumer is kept, add
`SnapshotSamplesFrom(int sampleOffset)` to `AudioRecorder` (or an incremental resampler that
retains its position), and have *both* consumers use it — that fixes an existing cost, not just
the new one. Otherwise, see MAJOR-4: run the spotter post-stop off `result.Samples` and this
entire problem disappears.

### MAJOR-2 · A second audio poller races `Stop()` / `Discard()`, which mutate `_buffer` without the lock

`SnapshotSamples()` reads `_buffer` under `_bufferLock` (`:78-82`). **`Stop()` and `Discard()` never
take that lock**: `Stop()` streams the same `MemoryStream` at `:146-154` and then disposes it and
nulls the fields at `:159-163`; `Discard()` does the same at `:178-182`.

Today this is safe **only by call ordering**: `RecorderController` awaits `_live.FinishAsync()`
(`:205`) — which cancels and awaits the poll loop (`LiveTranscription.cs:96-98`) — *before* calling
`_recorder.Stop(wav)` (`:210`). A second, independently-scheduled poller has no such guarantee and
will hit `ObjectDisposedException` on `_buffer.ToArray()`, or read a half-disposed stream.

Also: `RecorderController.Cancel()` (`:165-186`) cancels only `_live`. `AudioRecorder.Discard()`
(`:169-183`) throws the audio away. A streaming spotter must be cancelled on both paths or it
leaks a thread and an ONNX session per cancelled recording.

**Recommended fix.** Either (a) run post-stop and sidestep it entirely (preferred), or (b) make
the spotter a subscriber of `LiveTranscription`'s existing lifecycle rather than an independent
poller, and separately fix `Stop`/`Discard` to take `_bufferLock` around the buffer teardown.

### MAJOR-3 · Doc names a call site that does not exist; two other §2b statements are stale

- **§3.7: "gate after `MaybeCleanupAsync`".** `MaybeCleanupAsync` does not exist anywhere in
  `src/` — zero hits. The AI cleanup pass was removed 2026-07-20. The real call is
  `Jot.Text.TextPipeline.Clean(text, _settings.Current.Language, isNemotron: true)` at
  `RecorderController.cs:226`, and it is **conditional** on `_settings.Current.OfflineCleanupEnabled`
  (`:225`). This matters because that sentence is the load-bearing one for the entire offset story.
  The UX companion already has it right (`vocabulary-ux.md:130-135`); the engine doc is stale.
- **§2b: "Our offline-cleanup design (`docs/plans/`, not yet implemented)".** It **is** implemented
  and wired — `src/Jot/Text/TextPipeline.cs` (+ `ModelArtifactScrubber`, `FillerWordCleaner`,
  `NumberNormalizer`), called at `RecorderController.cs:226`. The "port `JotTextPipeline` instead of
  building from scratch" recommendation is retroactive advice on shipped code. Retarget it as
  "reconcile against jot-shared's fixtures", or delete it.
- **§5b: "M1 status: the gate itself is DONE… 316 tests".** The 316 is jot-shared's *Swift* suite.
  The Windows suite is 163 `[Fact]/[Theory]` across 27 files (with `[InlineData]` expansion higher),
  of which the vocabulary tests are 2 files
  (`tests/Jot.Tests/Vocabulary/VocabGoldenFixtureTests.cs`, `VocabPortFidelityTests.cs`).
  As written the line reads as if Windows has 316 tests. Per the standing "never claim it works
  without testing it" rule, restate as "N Windows tests, 7/7 jot-shared fixtures green".
  Also: §5b claims M1 scope is "the whole of `JotVocabCore`", but `CorrectionStore`,
  `CorrectionProvenance`, `VocabTerm`, `VocabularyFile`, and `VocabularyStore` are **not** in
  `src/Jot/Vocabulary/` yet. The status line overstates completion.

### MAJOR-4 · Two resident ONNX models + DirectML contention is a direct threat to dictation latency, and it is under-rated as "a spike item"

**Memory is the small part.** The RNNT already holds **three** sessions for process lifetime
(`NemotronFp16Transcriber.cs:337-339`, `NemotronTranscriber.cs:311-313`), never disposed at
shutdown. int4 encoder weights are 690 MB (`NemotronModelInstaller.cs:27`); fp16 is 1.236 GB
(`NemotronFp16ModelInstaller.cs:25`). A 100–150 MB CTC encoder is ~10–20% on top. Survivable.

**GPU contention is the real problem, and it is quantified in our own code.**
`OnnxSessionFactory.DirectMlOptions()` (`:51-65`) does `AppendExecutionProvider_DML(0)` with
`ExecutionMode.ORT_SEQUENTIAL` and `EnableMemoryPattern = false`. Two independent sessions each
create their own DML device/queue on adapter 0; work is serialized by the GPU scheduler with no
priority control from ORT or the app.

Two concrete consequences the doc does not draw:

1. **It invalidates the cached GPU verdict.** `GpuProbe.MaxAvgChunkMs = 150` (`GpuProbe.cs:24`) is
   the *entire* admission bar for the GPU tier, and it is measured with the GPU otherwise idle
   (`GpuProbe.Run` is a solo fp16 pass, `:49-77`). The verdict is cached and keyed to
   adapter+driver (`EngineSelector.cs:39-41`) — it says nothing about "this machine with a second
   model resident."
2. **It can silently downgrade live captions to batch.** `RealtimeGuard`
   (`RealtimeGuard.cs:16-19`, defaults `minAudioSeconds 5.0 / rtfThreshold 1.0 /
   minBacklogSeconds 2.0`) degrades the session the moment cumulative processing time reaches
   wall-clock audio with ≥2 s backlog (`LiveTranscription.cs:72-81`). A concurrent DML spotter is
   exactly the load that pushes a marginal machine over. User-visible result: captions freeze and
   the transcript only appears at stop — the vocabulary feature degrading the app's headline
   feature, on the machines that were barely passing.

The CPU tier (the *default* — `EngineSelector.cs:43`) is no safer: the CTC encoder competes for the
same cores, and `NemotronTranscriber` holds `_inferenceGate` (`:43`) across all of `Accept`
(`:137-148`) including the O(n) `_audio.ToArray()`.

**Recommended fix — this is the single highest-leverage change to the plan.**
- **v1.3: run the spotter once, post-stop, pinned to `ComputeBackend.Cpu`.** After
  `_recorder.Stop(wav)` returns (`RecorderController.cs:210`) the RNNT is already finished
  (`FinishAsync` completed at `:205`), so contention with live captions is *zero*, the full float
  array is in hand for free, and MAJOR-1/MAJOR-2 both evaporate. This also removes the
  utterance-global-normalization problem from BLOCKER-1 (one-shot needs no merge) and the
  auto-detect sequencing problem from BLOCKER-3.
- **Set a latency budget, which the doc currently has none of.** `RecorderController.cs:123`
  `SlowStopMs = 5_000` already fires a user-facing "That took longer than usual" nudge off
  `stopSw` (started `:193`, logged `:259`). A post-stop pass adds directly to that window.
  Pick a number now — e.g. *"the spotter must add < 800 ms p50 and < 2 s p95 to stop-to-delivery
  on the int4/CPU tier for a 60 s dictation"* — and make M2 measure it. If it fails, *then* stream,
  with a re-run of `GpuProbe` under load as the gate.
- Add explicit session lifecycle: load on first use after the toggle is enabled, **dispose on
  disable**. Nothing disposes sessions today, so without this "turn vocabulary off" won't return
  the memory until restart.
- Never put spotter work on `LiveTranscription`'s poll thread — it would add straight into
  `_processingSeconds` (`LiveTranscription.cs:87`) and trip `RealtimeGuard` by construction.

### MAJOR-5 · "Mirror `NemotronModelInstaller` exactly" understates the work by a layer

The *download engine* is genuinely reusable (see §0). But:

- `NemotronModelInstaller.cs` is 51 lines of which ~30 are **hardcoded SHA-256 + exact byte size
  per asset** (`:23-39`), read back from the GitHub release API. So before any installer code
  exists, someone must: obtain/convert the CTC ONNX + tokenizer, cut a new release tag under
  `vineetu/jot-windows` (pattern `jot-model-<name>-v1`, `:19`), upload, and read back digests.
  That is a real non-code prerequisite the doc's "mirror it exactly" hides.
- **The binding layer is hand-enumerated per model, and that's where the day goes.**
  `ModelDownload`'s public ctor is bound to the concrete int4 installer
  (`Services/ModelDownload.cs:24`) with the generic ctor `protected` (`:27`) — a third model needs
  a **new subclass** (as `GpuModelDownload.cs:15` is), a DI line (`App.xaml.cs:2338-2341`), a
  `SettingsViewModel` ctor param + property (`:214`, `:100`, `:105`), a `SettingsPage.xaml.cs:32`
  refresh call, and a **duplicated XAML row block** (`SettingsPage.xaml:218-226` / `:237-245` is
  copy-pasted markup, not an `ItemsControl`).
- **Live bug a third model makes worse:** `SettingsViewModel.cs:473` refreshes only `_download`
  after a data-folder move — `_gpuDownload` is already missed today. Per the standing blast-radius
  rule, the third model is the right moment to convert these to a collection.
- `tests/Jot.Tests/AssetManifestTests.cs:35, :49, :82` assert exact per-manifest asset counts and
  distinct base URLs — a third manifest requires updating them.
- **Do not use `ParakeetModelInstaller` as the template.** `Transcription/ParakeetModelInstaller.cs:12`
  does *not* implement `IModelInstaller`, has its own HTTP loop (`:73-114`), and has **no
  checksums, no resume, no free-space check**. It is the one that superficially looks like "a small
  optional model installer" and it is the wrong pattern.
- Unstated behavior to specify: what happens when vocabulary is enabled and the user dictates while
  the model is still downloading. Required answer: no spotter, no corrections, **no error**, with
  progress on the Settings row.

### MAJOR-6 · Offsets are exact at save time and stale forever after — the doc needs to say how staleness is *detected*

The pre-save story is clean (§0). Post-save, **five** places mutate `RecordingItem.Transcript`,
all auto-persisting via `JsonRecordingStore.cs:97`:

- inline edit — `RecordingDetailViewModel.cs:109` (whole-string replace)
- Find & Replace — `:134` (`Remove`/`Insert`) and `:147` (`Replace`, which shifts **every** later offset)
- ReTranscribe — `:241`
- MediaImporter — `:61` and the error path `:67`

`RecordingItem` has no version, hash, or length field to detect this. The UX doc mandates
strict-only anchor resolution and "hide the row on failure" — but strict resolution alone does not
save you: a Find&Replace that shifts offsets can leave a *matching* word at the shifted position,
which resolves cleanly onto the **wrong occurrence**. That is precisely the iOS bug they deleted.

**Recommended fix.** Persist the exact transcript the proposals were computed against — or a hash
of it — in the `CorrectionProvenance` side-JSON. On load, if the hash doesn't match the current
`Transcript`, hide all rows for that recording. Cheap, and it makes staleness *detectable* rather
than merely *sometimes unresolvable*.

### MAJOR-7 · Two of the three transcript paths never call cleanup, and one never passes the gate at all

- **`MediaImporter`** (`Import/MediaImporter.cs:55-61`): `TranscribeAsync` → `item.Transcript = text`.
  It **never** calls `TextPipeline.Clean` — `RecorderController.cs:226` is the only `Clean` call
  site in the product. So "gate after cleanup" is meaningless there; the gate would run on raw
  engine output. Fine, but it must be a deliberate, documented difference — the two paths gate
  different strings.
- **ReTranscribe** (`ViewModels/RecordingDetailViewModel.cs:230-231`): same, no `Clean`. Also
  re-gates an item that already has committed proposals — old ones must be invalidated.
- **Voice rewrite is the landmine.** `Rewrite/RewriteController.cs:131` does
  `_store.Add(BuildRewrite(...))` and `:133` `Succeeded?.Invoke(result)` →
  `PillController.cs:69` → `OnTranscriptReady`. Rewrite output **never** passes the gate. The UX doc
  catches the pill-chip half (`vocabulary-ux.md:152-155`); the engine doc must also ensure the
  *store* side gets no orphan proposals. And `RewriteController.cs:200-207` transcribes the spoken
  *instruction* through the same live/batch path — **if the spotter is wired at the
  `AudioRecorder`/`LiveTranscription` level rather than at `RecorderController`, it will fire on
  rewrite instructions**, burning compute and producing detections nobody consumes. Wire the
  spotter at `RecorderController`, not lower.

### MAJOR-8 · `vocabulary.json` survives "Erase all data" and is lost by "move data folder"

`JotDataPurge.cs:26-31`:
```
DataSubdirs   = ["models", "recordings", "logs"]
DataFiles     = ["library.json", "aikey.dat", "stats.json"]
ConfigSubdirs = ["tools", "logs"]
ConfigFiles   = ["settings.json", "prompts.json", "migration.json", WipeMarkerFile]
```
`DataFolderMigrator.cs:33`: `MigratedItems = ["models", "recordings", "library.json"]`.

The doc's `<DataDir>\vocabulary.json` (§3.1) and the UX doc's `<DataDir>\Vocabulary\`
(`vocabulary-ux.md:729`) appear in **neither** list. Consequence: Erase-all-data leaves the user's
vocabulary and correction history on disk (a privacy claim violation — the 2026-07-23 blast-radius
work exists precisely to make uninstall/erase total), and moving the data folder silently loses it.

**Recommended fix.** Add `"Vocabulary"` to `JotDataPurge.DataSubdirs` and to
`DataFolderMigrator.MigratedItems`, and extend `JotDataPurgeTests` (`:54-58`) to cover it. Put this
in M1, not M3 — it is a two-line change that is easy to forget once the feature is "done".

---

## MINORS

- **M-1 · Mac facts in §1 are wrong in ways that affect our decisions.**
  - *"Bundled in the app, no separate download."* False for Mac —
    `CtcModelCache.swift:12-14, :24-34, :70-74` downloads ~97.5 MB into
    `~/Library/Application Support/Jot/Models/parakeet-ctc-110m-coreml/`, surfaced as an
    **optional, skippable** wizard step (`SetupWizard/Steps/LanguageStep.swift:35-39, :187-201`).
    iOS is the bundled one. This actually *strengthens* the doc's §6 Q2 recommendation
    (download-on-enable) — cite the Mac correctly and the question answers itself.
  - `applyFromDetections` is `jot-shared/Sources/JotVocabCore/VocabularyGate.swift:**469**`.
    `:270` is the **Mac fork's** copy, whose signature differs (no `commonWordsResource`, concrete
    `CommonWords` instead of the provider). Our port followed the shared one — cite that.
  - The log-sum-exp merge is **13 lines** (`StreamingCtcSpotter.swift:174-186`), not 40; the 40
    counts `processChunk` too. And `overlapFrames` is derived at runtime from FluidAudio's returned
    `frameDuration` (`:158-159`) — there is no constant to port verbatim.
- **M-2 · The tuned constants are hearsay, and must not seed our thresholds.**
  `minSpotterScore -15.0 / minVocabCtcScore -12.0 / cbw 3.0 / minSimilarity 0.52` (§1) appear in
  **no** Swift file in either repo. Every Mac call site passes `minScore: nil`
  (`StreamingCtcSpotter.swift:107, :149`; `VocabularyRescorerHolder.swift:272, :333`) — the app
  takes FluidAudio's defaults. §4.3 already warns not to copy them blind; go further and delete
  them from §1, or label them explicitly as unverified.
- **M-3 · M2's "one genuinely new algorithm" has no local reference.** `CtcKeywordSpotter` /
  `spotKeywordsFromLogProbs` are **not in either repo** — they live in FluidAudio 0.15.4, an
  external SPM dependency (`Package.resolved`, revision `b9d4372`), with no vendored checkout on
  this machine. The source is fetchable from GitHub, but M2's ~2–3 day estimate reads as though a
  reference sits next door. Also unstated: the frame-duration assumption. FastConformer subsamples
  by 8, so CTC frames are ~80 ms; the Mac never hardcodes it (FluidAudio returns `frameDuration`
  per call, `:42, :152`), and `Detection.StartTime/EndTime` feed the proportional placement in
  `VocabularyGate.cs:403-404` directly. Get it wrong and every detection lands on the wrong word.
- **M-4 · `Detection.Aliases` and `enrichedAliases` are engine work that the engine doc omits.**
  `VocabularyGate.Detection` (`:83-88`) carries `Aliases`, and plausibility reads them (`:411`).
  The UX doc flags the space-stripped multi-word form as *"engine work, listed here because it is
  invisible in the UX and will otherwise be forgotten"* (`vocabulary-ux.md:296-303`). It is
  forgotten. Add it to §3.
- **M-5 · Audio fidelity: use `result.Samples`, never the WAV.** `WriteWav16`
  (`AudioRecorder.cs:202-215`) quantizes to 16-bit PCM. A spotter re-reading the WAV would see
  different audio than the RNNT saw. Say so explicitly, since ReTranscribe/MediaImporter *do* go
  through `WavAudio.ReadMono16k`, giving three audio sources at three fidelities.
- **M-6 · Redistribution/attribution.** The plan re-hosts a converted CC-BY-4.0 artifact on our own
  GitHub release. That is redistribution and needs an attribution surface in the app (none is
  named) plus the conversion tooling's own license recorded. Cheap, easy to forget, and the Store
  listing may want it.
- **M-7 · `DescribeProgress` is MB-only** (`AssetManifest.cs:22-27`). Fine for a 130 MB model —
  just noting it is not a general-purpose formatter if a larger multilingual CTC checkpoint ever
  lands.

---

## Recommended revisions to the doc, in priority order

1. **Rewrite §5 M0.** New exit criterion = numeric feature/log-prob parity against a throwaway
   sherpa-onnx .NET oracle harness, not "looks like sensible English". Re-estimate 2–3 days. Make
   it an explicit kill gate.
2. **Add `CtcTokenizer` to §3 and to the estimates**, with a fixture-based conformance test — the
   same discipline that made the gate trustworthy.
3. **Flip §3's architecture diagram to post-stop, CPU-pinned, one-shot for v1.3.** Streaming becomes
   a v1.4 optimization gated on a measured latency budget. This one change retires MAJOR-1,
   MAJOR-2, the streaming half of BLOCKER-1, and the sequencing half of BLOCKER-3.
4. **Add a latency budget** ("< 800 ms p50 added to stop-to-delivery") and make M2 measure it
   against `SlowStopMs = 5_000`.
5. **Cut auto-detect from v1.3.** Vocabulary requires an explicit `en-*` locale. Fix the UX copy.
   File "expose the locale token on both engines" as v1.4 (it benefits `TextPipeline` too).
6. **Fix §3.7 to name `TextPipeline.Clean` at `RecorderController.cs:226`**, note it is conditional
   on `OfflineCleanupEnabled`, and mandate the try/catch + timeout wrapper (BLOCKER-4).
7. **Add `"Vocabulary"` to `JotDataPurge` and `DataFolderMigrator` as an M1 task.**
8. **Correct the Mac facts** in §1/§2 (downloaded not bundled; no language gate on Mac; `:469` not
   `:270`; 13-line merge; constants are hearsay) and the stale §2b/§5b status lines.

---

## Overall

**NEEDS REVISION** — not RETHINK. The architectural bet is correct and is the best available
option: a second small CTC model as a pure acoustic keyword spotter, with placement and safety
handled by a gate that is already ported, already fixture-green against the shipping Swift
behavior, and already fails closed on a missing word list. The insertion point is real, and the
offset guarantee between cleanup and save is genuinely better than what either Apple platform has.

What needs to change is the plan's treatment of what it does not yet know. The single riskiest
item (the feature contract) is scheduled as a half-day with an exit criterion that cannot detect
partial failure and no oracle to check against; a required component (the tokenizer) is missing
from the plan entirely; one user-facing promise cannot be built; and the "stream it during
recording" choice buys latency at the cost of the app's core value on exactly the marginal
machines that can least afford it. Fix those four and the milestones become honest — realistically
**M0 2–3 d, M1 remainder 1–2 d, M2 4–5 d, M3 1–2 d**, roughly double §5.
