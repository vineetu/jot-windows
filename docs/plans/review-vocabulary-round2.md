# Round-2 verification review — `vocabulary-ctc-port.md` + `vocabulary-ux.md`

> Convergence check, 2026-07-25, against `src/Jot` at working-tree state. Settled decisions are not
> re-litigated. Every "RESOLVED" below was spot-checked against the actual code, not just against the
> doc's own table.
>
> **Verdict: ONE MORE PASS NEEDED — narrow and mechanical. Do not wait for it to start M0/M1.**
> 15 of the 16 first-round BLOCKER/MAJOR items are genuinely resolved and the fixes are sound. Two
> things must change: (1) every `VocabularyGate.cs` and `AskPolicy.cs` line number in both docs is
> wrong — the files grew ~10 KB *after* the reviews were written and *before* the docs were revised,
> and the revision re-pointed the citations without re-reading them; (2) the M3c section has three
> real design holes (§C). M0, M1, M2, M3a and M3b are buildable today.

---

## 1 · Per-blocker resolution table

### `review-vocabulary-engine.md`

| # | Item | Resolution | Notes |
|---|---|---|---|
| **BLOCKER-1** | Mel gap is a new front-end; M0 can't detect partial failure | **RESOLVED (one gap)** | D2 + `Transcription/Ctc/CtcMelFrontend.cs` is right, and the "don't touch `MelFrontend`" argument is *stronger* than stated (§B). D3 substitutes a **behavioral** oracle (transcript string-equality) for the review's demanded **tensor** oracle (≤1e-3). That is a defensible weakening — the residual is named, and the tensor escalation is written down as the first thing to try. **Gap:** criterion 1's WAV is left as an "if". `src/Jot/Assets/probe.wav` is **202,556 B ≈ 6.33 s, one utterance** — it *is* too short to be discriminating. Settle it: check in a longer multi-sentence WAV in M0, or the whole kill gate rests on a weak oracle. |
| **BLOCKER-2** | No term→CTC-token encoder | **PARTIALLY RESOLVED** | D4 + §3.3 + `CtcTokenizer.cs` in the M2 file list + budgeted. Good. **But the exit criterion is weaker than the review asked for and weaker than the doc believes.** An encode→decode round-trip proves the tokenizer is *self-consistent*; it does **not** prove it agrees with the checkpoint's encoder. A merge sequence that is wrong but self-consistent round-trips perfectly and still yields silent zero recall — the exact failure §3.3 says it is guarding against. Since the sherpa-onnx oracle binary is already in hand for criterion 1, dump reference ids from it and assert **id-for-id**, as the review specified. |
| **BLOCKER-3** | Resolved-language gating not implementable | **RESOLVED** | D5 is clean and verified: `NemotronLocales.Normalize` (real path `Transcription/Nemotron/NemotronLocales.cs:106`) returns `"auto"` for auto and `"en-US"` for unknown/empty, so the one-line gate behaves exactly as §3.6 claims. UX copy rewritten (§6.3), v1.4 locale-token ticket filed with the correct evidence (`NemotronTranscriber.cs:293`, `NemotronFp16Transcriber.cs:316` both verified; fp16 genuinely has no `Tokens`/`Piece`). |
| **BLOCKER-4** | An exception destroys the dictation | **PARTIALLY RESOLVED** | D6 + the try/catch sketch + the two-test requirement are right, and the sketch compiles against reality (`JotLog.Error(string, Exception?)` exists at `Services/JotLog.cs:28`; the `catch` at `RecorderController.cs:267-271` does skip both `_store.Add` and `PasteAtCursor`; the gate is safe before the empty check). **Three exit paths it does not cover — see §C-4.** |
| **MAJOR-1** | `SnapshotSamples` O(n)/poll | **RESOLVED** | D1 (post-stop) deletes the problem entirely. |
| **MAJOR-2** | Second poller races `Stop()`/`Discard()` | **RESOLVED** | Same — D1 removes the second poller. |
| **MAJOR-3** | `MaybeCleanupAsync` doesn't exist; stale §2b/§5b | **RESOLVED** | Verified: zero hits for `MaybeCleanupAsync` in `src/Jot`; `TextPipeline.Clean` at `RecorderController.cs:226` is the sole call site in the product; `TextPipeline`/`ModelArtifactScrubber`/`FillerWordCleaner`/`NumberNormalizer` all exist. *(New minor error introduced in the fix — test count, §B.)* |
| **MAJOR-4** | Two resident ONNX models / DML contention | **RESOLVED, strongest fix in the revision** | D1 CPU-pinned one-shot + the §3.5 budget + dispose-on-disable + "never on the poll thread". `GpuProbe.MaxAvgChunkMs = 150` (`:24`), `RealtimeGuard` defaults, `EngineSelector` CPU default (`:43`), `OnnxSessionFactory.DirectMlOptions()` (`:51-65`), `ComputeBackend.Cpu` — all verified. |
| **MAJOR-5** | "Mirror the installer exactly" understates it | **RESOLVED** | §3.4 enumerates every binding site and each one checks out: `ModelDownload.cs:24/:27`, `GpuModelDownload.cs:15`, `App.xaml.cs:2338-2341`, `SettingsViewModel.cs:214/:100/:105`, duplicated XAML rows (real ranges `:217-226`/`:236-245`), `AssetManifestTests.cs:35/:49/:82`, `NemotronModelInstaller.cs` 51 lines with hardcoded digests at `:23-39`. The **live bug is real**: `SettingsViewModel.cs:473` refreshes only `_download` after a data-folder move; `_gpuDownload` is never `Refresh()`ed from that file. |
| **MAJOR-6** | Anchors stale after save, undetectably | **RESOLVED (with §C-2/§C-3 gaps)** | DECIDE-10's fingerprint is the right answer. Two under-specified pieces below. |
| **MAJOR-7** | Three transcript paths differ | **RESOLVED** | Verified: `MediaImporter.cs:55-61/:67` never calls `Clean`; `ReTranscribe` never calls `Clean` and only runs for `IsPending` (`:218`); `RewriteController.cs:131/:133` → `PillController.cs:69`. The §3.6 / §5.6 tables are accurate and the "wire at `RecorderController`, not lower" rule is correctly justified by `RewriteController.cs:196-207`. |
| **MAJOR-8** | Vocabulary invisible to Erase and to the folder move | **RESOLVED — line-exact** | See §D. |
| M-1 … M-7 | Mac facts, hearsay constants, no local DP reference, `enrichedAliases`, `result.Samples` not the WAV, CC-BY attribution, `DescribeProgress` | **ALL FOLDED IN** | Each is now in the doc with the correct framing. `enrichedAliases` appears in **both** docs, which was the point. |

### `review-vocabulary-ux.md`

| # | Item | Resolution | Notes |
|---|---|---|---|
| **B1** | The ask can never fire on the case drawn | **PARTIALLY RESOLVED** | Option (a) taken correctly — the card is redrawn around an applied correction and the block-flavoured copy, "Stop asking", and the `suppressedBlocks` UI are all cut. **But the doc then over-generalises to an absolute that is false. See §C-1 — this is the one substantive new error.** |
| **B2** | Paste target isn't the origin window | **RESOLVED** | Verified line-exact: `RecorderController.cs:243`, `ISettingsStore.cs:44` (`ReturnToOrigin` with no initializer ⇒ `false`), `TextInjector.cs:107` (`restore ? restoreTo : GetForegroundWindow()`), `:81` (`IsOwnWindow` guards only the `restoreTo` argument, exactly as claimed), `ForceForeground` at `:433-456` with the 40 ms settle at `:86`. Requirement 1 is correct. *(Residual on the failure path — §C-4.)* |
| **B3** | Data lifecycle | **RESOLVED — line-exact** | §D. The `<DataDir>\vocabulary.json` vs `<DataDir>\Vocabulary\` contradiction is gone; both docs now carry the same block. |
| **B4** | `VocabularyPage` has no way back | **RESOLVED** | `MainWindow.xaml:23 IsBackButtonVisible="Collapsed"` confirmed; `RecordingDetailPage.xaml:19-21` + `RecordingDetailViewModel.cs:94-95` are exactly the cited shape. |
| **M1** | Pill-focus framing misdirects | **RESOLVED, cleanly** | D9 verified in full: `PromptPickerWindow.xaml.cs` class comment `:12-17`, `Deactivated` `:40`, focus `:61-67`, key handling `:99-149`, Esc `:135-138`, `SetWindowLong(... WS_EX_TOOLWINDOW)` at `:193` with **no `WS_EX_NOACTIVATE` anywhere in the file**. `PillWindow.xaml.cs:360` does set both flags. Every line in §4.3 is right. |
| **M2** | `AskPolicy` guarantees overstated | **RESOLVED** | The 2-live/3-dead table is accurate against the code (only the line numbers are wrong, §B), the honest worst case is stated plainly, and §4.2's contradictory "two Windows-only additions" is gone. |
| **M3** | Three writers invalidate anchors | **RESOLVED (with §C-3)** | The rejection of option (a) is **correct — judged in §C-1a.** |
| **M4** | "Flash the span" isn't buildable | **RESOLVED** | `Select(start,length) + Focus()` on the read-only `TextBox` (`RecordingDetailPage.xaml:210-214`), code-behind precedent at `.xaml.cs:29-53`. Right answer. |
| **M5** | Hidden vs `AdvancedFeatures` | **RESOLVED — line-exact** | `SettingsPage.xaml.cs:196-198` and `:135` verified verbatim; `SettingsPage.xaml:300` is a **literal** `Visibility="Collapsed"` and its comment does say "Rebind Visibility to AdvancedFeatures once implemented"; the block really does span `:298-349`; `ISettingsStore.cs:18` and `WizardViewModel.cs:188` both confirm Advanced is genuinely off. |
| **M6** | Fail-closed targets a trap we don't have | **RESOLVED** | `Jot.csproj:39` `EmbeddedResource` verified; `CommonWordLists_AreEmbeddedAndPopulated` verified at **exactly** `VocabGoldenFixtureTests.cs:292-301`; `Words(null)` returns empty silently at `CommonWords.cs:49`; `ResourceFor` at `:39-45`. The retirement is real and the "Reinstall Jot" copy is correctly deleted. |
| **M7** | Ported-core list stale in both directions | **RESOLVED in substance, FAILED in execution** | The right files are listed and `VocabularyStore`/`CorrectionStore`/`CorrectionProvenance` are correctly marked NEW (all three confirmed absent from `src/Jot/`). **But M7's actual ask — re-point the ~15 Swift citations at the C# file — was done with stale line numbers, which is the same class of error M7 existed to fix.** §B. |
| m1 … m9 | `FitTail`, chip column, context menu, path, copy, missing states, a11y, ReTranscribe, M3a scope | **ALL FOLDED IN** | `FitTail` is genuinely called only from `SetLiveText:66`; `OneLine` is at `:347-352` with the 60-char flat truncation; `Capsule.Width = double.NaN` at `:156`; `PillWindow.xaml:11/:22-40/:35/:48` all correct. §2.5b's new states table, the keyboard story, and the DECIDE-8 subtitle are all present. |

---

## 2 · NEW factual errors the revision introduced

### A · The one that matters: every `VocabularyGate.cs` and `AskPolicy.cs` line number is wrong

The revision's new "cite the C# file, that's what the implementer has open" convention is the right
call. It was executed by copying line numbers out of the first-round reviews. Timeline from disk:

```
11:34  review-vocabulary-ux.md written
11:44  review-vocabulary-engine.md written
11:51  VocabularyGate.cs, AskPolicy.cs, CorrectionRecord.cs edited   ← files grew ~10 KB
11:58  both design docs revised (citations copied from the 11:34/11:44 reviews)
```

`VocabularyGate.cs` is **1126 lines / 54 KB**, not the "44 KB" both docs state. Drift is **+9** in the
`ApplyFromDetections` region and **+29** in the `Decide` region. Corrections:

| Cited in the docs | Actual |
|---|---|
| `:363` `ApplyFromDetections` | **`:372`** (`:363` is the word "PROPORTIONAL" in a comment) |
| `:40` `LowConfidence = 0.85f` | **`:43`** — and `:40` is `ConfidenceCeiling = 0.95f`, an actively misleading collision |
| `:83` `Detection` record / `:83-88` `Aliases` | **`:89-94`**, `Aliases` at **`:91`** |
| `:59` / `:71` / `:76` `Proposal`/`Result`/`PublishedStart` | `Alternate` `:59`; `Proposal` **`:62-80`**; `Result` **`:82-86`**; `PublishedStart` **`:77`**, `PublishedLength` **`:78`** |
| `:480-481` published offsets set | **`:489-490`** |
| `:375-376` empty-transcript early return | **`:384-385`** |
| `:403-404` proportional placement | **`:412-413`** |
| `:461` offsets vs the returned string | **`:470`** |
| `:482` `Alternates: []` | **`:491`** |
| `:509-584` `Decide` | **`:535-613`** |
| `:529` confidence pinned / `:534` `measured` null | **`:555`** / **`:550`** (and they are in the opposite order in code) |
| `:543-555` learned-override loop | **`:572-584`** |
| `:548` "net ≤ 0 disarms" | **`:577`, and the predicate is `ov.Net <= -1`, not `<= 0`** — `vocabulary-ux.md §5.4`'s wording is a content error, not just a line error |
| `:553` arming (`!isCommon && Net >= 1`) | **`:582`** — the `!isCommon` requirement itself is **correct** |
| `:562` plausibility | **`:591`** |
| `:566` multi-word self-gate | **`:595-596`** |
| `:571` confidence ceiling | **`:600`** |
| `:578` common-word brake / `:579` block sets `AskCandidate` | **`:607`** / **`:608`** |
| `:583` apply sets `AskCandidate` | **`:612`** |

`AskPolicy.cs` (135 lines) — drift **+5** below line 70:

| Cited | Actual |
|---|---|
| `:93-99` `WorthAsking` — **quoted in both docs as the implementation ticket's anchor** | decl **`:98`**, body **`:99-104`**. *(The quoted code itself is byte-exact — only the citation is wrong.)* |
| `:95` `keyboardSuppressed` check | **`:100`** |
| `:106` `MaxAsks` use | **`:111`** |
| `:77-86` `Granted()` | **`:82-91`** (`o.AlwaysReplace` at `:88`) |
| `:123-127` `Selection.AltTerm`/`AltFind` | **`:131-132`** |
| `:23` `MaxAsks = 3` | **correct** |
| `:70` `MergeTeachEligible` requires `Shape == "merge"` | **correct** |

Also: `CommonWords.cs` "report-once at `:53-93`" — `Load` is `:53-88` and the report-once dedupe is in
`ReportMissing` **`:90-100`**; the cited span truncates the very method being cited.
`GetManifestResourceStream` is on **`:62`**, not `:61-67`.

*Fix: one mechanical sweep. Do not hand-patch — re-grep each symbol.*

### B · Smaller new errors

| Claim | Reality |
|---|---|
| "**163** `[Fact]`/`[Theory]` across **27** files" (§5b, presented as a correction of the old "316") | **164 across 28.** The correction was itself slightly wrong. |
| "`NMels` … read from **9 sites**" (D2, §3.1, §3.2) | The doc lists **10** locations and **all 10 are real read sites** (`NemotronFp16Transcriber.cs:125,201,202,234,237`; `NemotronTranscriber.cs:182,187,211`; `StreamingMel.cs:33,37`). The argument for D2 is stronger than stated; just fix the count. |
| "`VocabularyGate.cs` (44 KB)" | **54 KB / 1126 lines.** |
| `MelFrontend.cs:17-23` for the consts | **Correct** (the *review*'s `:16-23` was the wrong one — the revision fixed it). |
| `RealtimeGuard.cs:16-19` for the defaults | Defaults are on the primary constructor at **`:13-14`**; `:16-19` is `ShouldDegrade`'s body. Values correct. |
| `EngineSelector.cs:39-41` "cached per adapter+driver" | The causal claim is right, the file is wrong: the key is built at `Platform/GpuInfo.cs:19` and compared at `App.xaml.cs:2356`. `EngineSelector` only mentions it in a comment. |
| `AudioRecorder.Stop()` `(:154-165)` | `Stop` is **`:135-166`**; the cited range is its tail. Harmless as an anchor. |
| `GpuProbe.Run` "solo fp16 pass, `:49-77`" | `Run` is **`:34-92`**; `:49-77` omits the sanity check and the verdict. Harmless. |
| `Transcription/NemotronLocales.cs`, `Transcription/NemotronModelInstaller.cs` | Both are under **`Transcription/Nemotron/`**. |
| `AssetManifest.cs:22` for `TotalBytes` | `TotalBytes` is **`:16`**; `:22` is `DescribeProgress`. |
| "`scripts/` holds four files — PowerShell + two static HTML pages" | Count is right (4); the fourth is `sie-test-site/test.txt`, unaccounted for. Cosmetic. |

### C · Corrections the revision made that are RIGHT (verified, don't touch)

`TextPipeline.Clean` at `RecorderController.cs:226`, conditional on `OfflineCleanupEnabled`, sole call
site · `MaybeCleanupAsync` zero hits · `FitTail`/`OneLine`/`Capsule.Width` · `SettingsPage.xaml:300`
literal `Collapsed` and the search-indexer dependency · `ISettingsStore.cs:44` · `SettingsViewModel.cs:473`
· `ParakeetModelInstaller` as the wrong template · `AssetManifestTests:35/:49/:82` · `Jot.csproj:52-56`
does document the duplicate-`onnxruntime.dll`/APPX1101 collision, so "dev-time oracle only" is
well-founded · `NemotronLocales.Normalize` semantics · the whole `PromptPickerWindow` recipe ·
**21 common-word lists** (the UX review's "22" was the wrong one) · **24,059** entries with `jamie`/
`sarah`/`lisa` absent and `mark`/`rose`/`grace`/`april`/`may`/`will` present · 7 fixtures, wired at
`Jot.Tests.csproj:33-34` · `CommonWordLists_AreEmbeddedAndPopulated` at `:292-301` exactly.

---

## 3 · The four items flagged for deep probing

### C-1 · DECIDE-10 vs the review's "hide when `IsEdited`" — **the rejection is right, the replacement is 80 % specified**

**(a) The rejection is correct, and for a second reason the doc doesn't give.** §2.7's right-click add
does set `IsEdited = true` — but so do `ReplaceNext` (`RecordingDetailViewModel.cs:135`) and
`ReplaceAll` (`:148`), both verified. So option (a) would delete the review surface on *any* Find &
Replace, not just on the feature's own gesture. `IsEdited` is a one-way latch
(`RecordingItem.cs:36`, never cleared) — once set, the block would be gone forever. **Rejecting (a)
was the right call; say so with the stronger evidence.**

**(b) The replacement is buildable except for two things:**

1. **`ShiftAnchors(recordingId, at, delta)` cannot express `ReplaceAll`.** `ReplaceAll` is
   `Item.Transcript.Replace(FindText, ReplaceText, cmp)` (`:145`) — N sites, N deltas, and the app
   never computes the indices. The doc's own writer table calls it a "recomputable pass" and then
   routes all three writers through one `(at, delta)` call. An implementer will guess.
   *Fix (pick one, in writing): recompute matches back-to-front and call `ShiftAnchors` per site, or
   treat `ReplaceAll` as `AnchorsStale` in v1.3.* The second is cheaper and consistent with `SaveEdit`.
2. **The fingerprint's stamping moment is not pinned down** relative to the ask splice — see C-2.

Everything else (SHA-256 + length, drop-overlapped, re-stamp, `AnchorsStale` degrade copy, no
nearest-match fallback, the three tests) is specified well enough to build.

### C-2 · "Blocked proposals get no ask, ever, in v1.3" — **verified false, and the docs are not self-consistent**

`AskPolicy.WorthAsking` (real lines `:98-104`) is quoted **byte-exactly** in both docs, and the
first-encounter analysis is right: a common-word block has `Outcome == "kept"`, `Shape == null`,
`Prior == 0` and falls through every branch to `false`. **On first encounter, D8 holds.**

It stops holding the moment the user teaches the pair:

- `vocabulary-ux.md §5.4`: a `KEPT` row resolved by picking the term records **+1**.
- `vocabulary-ux.md §2.7` step 3: the flagship right-click gesture does `CorrectionStore.Adjust(heard → term, +1)` — the pair starts at net 1.
- `Prior(r)` reads `OverrideEntry.Net`, so that pair now has `Prior == 1`.
- `VocabularyGate.Decide`'s override branch requires **`!isCommon`** (verified, real line `:582`), so a
  common-word original **stays blocked forever** while carrying `Prior >= 1`.
- Next dictation: `Outcome == "kept"`, `Prior(r) > 0` ⇒ `WorthAsking` returns **`true`**. The deck
  contains a block.

The card spec has no copy for that record: §4.2's headline is `Jot wrote {term}.`, which is a lie for a
`KEPT` row — Jot kept the original. This is exactly the `lista → Lisa` card B1 said was impossible.

The docs already contradict themselves here. `§4.1` bullet 1 says the deck can contain
*"any pair the user has already pushed to `Prior > 0`"*; bullet 2 immediately says
*"Blocked proposals get no ask, ever, in v1.3."* D8, §3.5 ("the review surface is the only place in the
entire product where a blocked near-miss is visible"), §9's cut row and §10 item 5 all restate the
absolute. Only §9's parenthetical (*"cannot select a `kept` record with `Prior == 0`"*) is precise.

**Fix (one sentence + one line of code, and it does not touch `AskPolicy`):** filter the *caller's* use
of `AskPolicy.Select` to `Outcome == "applied"` for v1.3. That is app-side composition, not a
Windows-specific policy divergence, so §0b-RESOLVED is respected. Then D8's absolute becomes true by
construction. Alternatively spec a `KEPT` card variant — but that reopens a decision, so take the
filter. Either way, delete the "ever" or make it earned.

### C-3 · D6 against the real `StopAndDeliverAsync` — **holds for throws, not for hangs, and leaks on the error path**

Verified against the real control flow (`RecorderController.cs:188-277`): the placement between `:226`
and `:235` is correct, the `catch` at `:267-271` really does skip both `_store.Add` and
`PasteAtCursor`, the gate is safe ahead of the empty check at `:228`, and `JotLog.Error(string,
Exception?)` exists (`Services/JotLog.cs:28`) so the sketch compiles. **Three exit paths it does not
cover:**

1. **A hang is not a throw.** D6 mandates a wall-clock timeout *inside* `Run` (the spotter). The UX
   doc's §4.4 step 3 — `await AskCardWindow.RunAsync(deck)` — sits in the same wrapper with no
   wall-clock backstop, only per-card countdowns and the "new recording force-resolves" rule. If
   `RunAsync` never completes, no exception fires, the `catch` never runs, **and the `finally` at
   `:272-276` never runs either** — so `State` stays `Transcribing`, and `PressToStart` ignores that
   state (`:143-144`). Dictation is dead until restart, silently. *Fix: one deadline around the whole
   gate + ask block, not just inside `Run`.*
2. **A live ask card is not closed on the failure path.** If steps 1–4 throw while the card is up, the
   catch logs, `text` reverts, and the paste proceeds — but the `Topmost`, activatable card is still
   the foreground window, and with `ReturnToOrigin == false` (default) `PasteAtCursor` gets
   `IntPtr.Zero` and resolves `GetForegroundWindow()` (`TextInjector.cs:107`) = **the ask card**. That
   is B2's failure re-entering through the error path that B2's own fix doesn't cover, because §4.3
   requirement 1 is conditioned on *"when a deck ran"*. *Fix: `finally { force-close any live card; }`
   and force `_originWindow` whenever the vocabulary block was **entered**, not only when a deck
   completed.*
3. **`PasteAtCursor` is conditional on `_settings.Current.AutoPaste` (`:238`).** D6's test spec —
   "assert the original transcript is what reaches `_store.Add` *and* `PasteAtCursor`" — is vacuous
   with AutoPaste off. Add the qualifier so the test is written correctly the first time.

There is also a **contradiction between D6's guard test and the ask**: §3.6 and §1b both mandate a test
that *"fails if any text mutation is ever inserted between the gate and `_store.Add`"* — and §4.4 step 4
mandates exactly such a mutation (*"splice answers into `text`"*), which changes lengths
("nemo tron" → "Nemotron" is −1) and therefore shifts every later `PublishedStart`. The docs never
reconcile these. *Fix: name the ask splice as the one sanctioned mutation; route it through the same
`ShiftAnchors`; stamp the §5.5 fingerprint on the **post-splice** string that goes to `_store.Add`;
whitelist it in the guard test.*

### C-4 · D7 against the real `JotDataPurge` / `DataFolderMigrator` — **correct, line-exact, do it as written**

Every instruction in the checklist checks out:

| Instruction | Verified |
|---|---|
| `"Vocabulary"` → `JotDataPurge.DataSubdirs`, `Services/JotDataPurge.cs:26`, currently `["models","recordings","logs"]` | **Exact.** And `ArtifactPaths` (`:37-43`) and `PurgeAll` (`:48-57`) both derive from that one array, so the single edit is genuinely sufficient. |
| `"Vocabulary"` → `DataFolderMigrator.MigratedItems`, `Services/DataFolderMigrator.cs:33`, currently `["models","recordings","library.json"]` | **Exact.** |
| `Finish()` `:186-199` deletes only `MigratedItems` from the source | **Exact.** |
| Extend `tests/Jot.Tests/JotDataPurgeTests.cs:39-69` (`SeedEverything` + `ArtifactPaths_CoverEveryKnownArtifact`) | **Exact** — `SeedEverything` is `:39-51`, the test is `:53-69`. One `Write(...)` line + one `Assert.Contains` covers it, and `PurgeAll_RemovesEverything_OnAnyDrive` (`:71+`) then covers it for free. |
| `DataFolderMigratorTests.cs` exists | **Yes.** |
| `VocabularyStore` resolves its dir once at construction, `JsonRecordingStore.cs:30` as the pattern | **Exact** (`_dir = JotPaths.DataDir(settings.Current);`). |
| The CTC model needs no work — lands under `models\` | **Correct.** |
| Divergence note: `prompts.json` lives in `ConfigDir` (`PromptCatalog.cs:16, :43`) | **Exact.** |

One thing worth adding, because it is what makes the fix *complete* rather than half: the migrator's
**`EnumerateFiles` (`:218-238`) also iterates `MigratedItems`**, so the single array edit covers both
the copy and the delete side. Say it, so nobody adds a second list.

---

## 4 · Implementability, milestone by milestone

| Milestone | Can you start from the doc alone? | The single biggest thing still missing |
|---|---|---|
| **M0 — spotter spike / kill gate** | **Yes.** The oracle, the four exit criteria, the "no runtime sherpa dependency" rule (well-founded — `Jot.csproj:52-56` verified), the model tarball name, and the frame-duration derivation are all concrete. | **The test WAV.** `probe.wav` is 6.33 s / one utterance, and D3's entire strength is "a hard, multi-sentence WAV". Stop hedging: commit to checking in a longer one. Second: the tokenizer criterion is self-consistency, not agreement with the checkpoint (§BLOCKER-2). |
| **M1 — remaining core** | **Yes.** D7 is line-exact, the fixtures exist and are wired, the not-yet-ported list is accurate. | **`CorrectionProvenance`'s Windows schema.** M1 says port it from `provenance_verdicts.json`, but §5.5 adds fields the fixture does not have — transcript fingerprint, length, `AnchorsStale`, per-anchor `PublishedStart`. Define the Windows superset **in M1**, or M3c will discover the M1 port has the wrong shape. (Secondary: "the old plan's L1 fuzzy corrector as the no-model fallback" is one line with no design and no exit criterion — and it is the *only* thing that ships if M0 kills.) |
| **M2 — spotter + wiring** | **Yes, after M0.** Thresholds-from-distribution, latency budget, dispose-on-disable, `enrichedAliases`, `result.Samples`-not-the-WAV, the installer binding checklist — all concrete. The DP having no local reference is stated honestly. | **`VocabularyRunner`'s contract.** It is described as "the class that owns the D6 invariant" and then given three different implied return types: `string` (the §3.6 sketch), `(text, records)` (§4.4 step 1), and a corrections payload for the pill + provenance (§3.3, §5.5). Pin the signature — this is the one class every other piece calls. |
| **M3a — management UI** | **Yes.** The strongest-specified section in either doc: PromptsPage idiom verified line-for-line, §2.5b states table, a11y names, full copy deck, Back button. | Nothing blocking. (Nice-to-have: `RecordingDetailPage.xaml:43-57` is an in-repo `ContextMenu` idiom with `InputGestureText` + `Visibility` bindings worth copying for §2.7.) |
| **M3b — signal (chip)** | **Yes.** `OneLine` 60→46, column 3, folded automation name, `PillController.cs:69/:178` — all verified. | Nothing blocking. |
| **M3c — learn + ask** | **No — not without §C-1/C-2/C-3.** | The ask deck's true membership (C-2), the splice-vs-guard-test contradiction (C-3), and `ShiftAnchors` for `ReplaceAll` (C-1b). Also unsettled: the per-card timeout is "6–10 s", not a number. |

---

## 5 · Verdict

**ONE MORE PASS NEEDED — scoped to two things. Start M0 and M1 today regardless; neither is blocked.**

The architecture is converged. Nothing in either doc needs re-designing: D1 (post-stop CPU one-shot)
retires four separate hazards at once and is the best decision in the revision; D5, D7 and D9 are
clean, verified, and cheaper than the alternatives; the rejection of "hide the review block on
`IsEdited`" is correct and under-argued in the doc's own favour. The revision also fixed nearly every
factual error the first round found, and fixed the "22 lists" error the *review* got wrong.

Must change before M3c is written:

1. **Re-verify every `VocabularyGate.cs` and `AskPolicy.cs` citation** (§2A). All ~28 are wrong; the
   `LowConfidence :40` → `ConfidenceCeiling :40` collision is the dangerous one. Also `44 KB` → `54 KB`,
   `163/27 tests` → `164/28`, `9 NMels sites` → `10`.
2. **Fix the ask's membership claim** (§C-2). Either filter the caller to `Outcome == "applied"` — one
   line, no `AskPolicy` divergence — or spec a `KEPT` card. Then D8's "ever" becomes true instead of
   aspirational, and §3.5/§9/§10/§2.4 stop asserting something the code contradicts.
3. **Reconcile the ask splice with the "no mutation between gate and `_store.Add`" guard test**
   (§C-3), and pin where the anchor fingerprint is stamped.
4. **Close D6's three uncovered exit paths** (§C-3): one wall-clock deadline around the whole
   gate + ask block; a `finally` that force-closes a live card and forces `_originWindow`; and the
   `AutoPaste` qualifier on the test spec.
5. **Settle M0's test WAV** and strengthen the tokenizer criterion to id-for-id against the oracle
   already required for criterion 1.

Items 1 and 5 are a morning. Items 2–4 are three paragraphs in `vocabulary-ux.md §4` and §5.5. None of
them touches a decision, a milestone estimate, or the architecture. After that: build it.
