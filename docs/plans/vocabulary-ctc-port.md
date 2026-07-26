# Vocabulary on Windows — port the Mac/iOS CTC word-spotter + gate

> Status: **DESIGN, PARTLY IMPLEMENTED. M0 spike PASSED with caveats (D13).** Written 2026-07-25;
> revised 2026-07-25 after the adversarial review
> ([`review-vocabulary-engine.md`](review-vocabulary-engine.md)) and the owner decisions folded in
> below; revised again 2026-07-25 to fold in the **M0 feature-contract + tokenizer spike**, which
> measured the model, built and tensor-verified the front-end, verified the tokenizer, **retired
> D3's transcript oracle as invalid**, and produced real latency numbers (D10–D13). The **gate half
> is ported and fixture-green in-repo** (`src/Jot/Vocabulary/`); the **spotter front half now
> exists at spike quality** (`src/Jot/Transcription/Ctc/`, not wired into the pipeline); the **DP
> itself is still unbuilt** — the spike deliberately did not touch it. Companion UX design:
> [`vocabulary-ux.md`](vocabulary-ux.md).
>
> **Everything in this revision marked MEASURED was measured on this machine** (Ryzen 7 3700X,
> RX 5700 XT, Release, CPU EP unless stated). Everything still marked *expected* / *modeled* was
> not. Do not promote one to the other without a number.
>
> **This supersedes the L2/L3 half of [`vocabulary-plan.md`](vocabulary-plan.md)** (that plan
> assumed we had to invent RNNT shallow fusion because "the Nemotron checkpoint is RNNT-only,
> so CTC is a NO-GO"). That conclusion was right about *Nemotron* and wrong about the
> *approach*: the Mac never touches Nemotron's decoder either. It runs a **second, small CTC
> model purely as an acoustic keyword spotter** and edits the finished transcript. L1 of the
> old plan (post-decode fuzzy corrector) survives as a fallback layer.
>
> **Citation convention (changed in this revision).** Where a component is already ported, the
> citation is the **C# file** — that is what an implementer has open. Swift citations remain only
> for things not yet ported, or where the Swift is the sole reference. **Symbol names are
> authoritative; line numbers into actively-edited files (`src/Jot/Vocabulary/*.cs` above all) are
> advisory — grep the symbol, don't trust the number.**

---

## 0. Settled decisions — do not re-open

These were decided by the owner on 2026-07-25 after the review. The rest of the doc obeys them.

| # | Decision | One-line rationale |
|---|---|---|
| **D1** | The v1.3 spotter is **post-stop and one-shot**. No streaming during recording. ~~No DirectML.~~ **AMENDED by D10: DirectML is allowed for the post-stop pass.** | It retires the `SnapshotSamples` re-decode cost, the unlocked-buffer race and the chunk-merge-equivalence problem `per_feature` normalization creates — at a stop-latency cost we budget and measure (§3.5). D1's *other* stated fear — DirectML/`RealtimeGuard` contention silently downgrading live captions — is a **recording-time** hazard; post-stop there are no live captions to contend with, and the measured CPU cost (§3.5) makes the GPU lever necessary. |
| **D2** | The CTC mel front-end is a **separate class** (`Transcription/Ctc/CtcMelFrontend.cs`). `Transcription/Nemotron/MelFrontend.cs` is **not to be touched**. | `MelFrontend.NMels` is `public const 128` (`MelFrontend.cs:20`) and is read from **10 sites** in the shipping RNNT engines; re-parameterizing it puts the app's core value on the line to save a duplicated FFT (§3.2). |
| **D3** | ~~M0's oracle is an end-to-end transcript comparison against the prebuilt sherpa-onnx Windows binary.~~ **RETIRED 2026-07-25 — the transcript oracle is INVALID.** M0's oracle is a **tensor comparison of our mel matrix against a NeMo reference**, bar ≤1e-3 max abs. | Two independent disproofs, both reproduced (§5 M0): a ±1 LSB (−90 dBFS, inaudible) dither moves *sherpa's own* transcript **as much as our implementation differs from it** (real-40s: ours-vs-oracle 11.8 %, oracle-vs-itself under dither 14.7 %) — string equality is unachievable, not merely hard; and a **deliberately sabotaged analysis window (Povey↔Hann) left every reference transcript byte-identical** — so it is insufficient as well as unachievable. A tensor oracle is both: measured **max abs delta ≤ 9.34e-4 across 4 clips**, while the same sabotage scores **2.8e-2 – 1.2e-1**, i.e. 30–130× worse. |
| **D4** | Term→token encoding uses a **real SentencePiece/BPE encoder** (`Microsoft.ML.Tokenizers`), with an encode→decode round-trip exit criterion. | A hand-rolled longest-match encoder that disagrees with the model by one merge produces **silent zero recall** — no error, no log line (§3.3). |
| **D5** | v1.3 language gating is **explicit selection only**: vocabulary runs iff the user has explicitly chosen an English locale. **Auto-detect ⇒ vocabulary OFF**, and the UI says so. | Both transcribers discard the `<xx-YY>` locale token and the fp16 engine exposes no token accessor at all, so per-recording resolved language does not exist without new engine work (§6). |
| **D6** | Vocabulary may **never** break a dictation. Any exception in spotter or gate falls back to the raw transcript and still reaches `_store.Add` **and** `PasteAtCursor`. | `StopAndDeliverAsync`'s existing `catch` (`RecorderController.cs:267-271`) skips both — an unguarded throw loses the user's text (§3.6). |
| **D7** | Data lifecycle is in scope, with **one** storage layout: `<DataDir>\Vocabulary\` registered in `JotDataPurge.DataSubdirs` **and** `DataFolderMigrator.MigratedItems`. | Otherwise vocabulary survives "Erase all data" (a privacy-claim violation) and is stranded by a data-folder move (§3.7). |

**Decisions added 2026-07-25 after the M0 spike. Same status: settled, do not re-open.**

| # | Decision | One-line rationale |
|---|---|---|
| **D10** | **Corrections stay in the pasted text — but degrade gracefully.** Four parts: (a) **DirectML is allowed** for the post-stop CTC pass (amends D1); (b) on the D6 deadline the spotter **pastes the RAW transcript and applies corrections to the SAVED recording**, surfacing the chip; (c) above a **120 s** trimmed-speech cutoff the spotter is **skipped entirely**; (d) leading/trailing silence is **trimmed before** the pass. | "Spotter fully async, pasted text never corrected" was rejected outright: the term appearing in *what you paste* is the entire feature. But the measured CPU cost (60 s → **1987 ms**, 180 s → **9662 ms**, super-linear) makes "always in the paste" a promise we cannot keep on long audio, so the honest design is a budget with a stated degradation per tier (§3.5). |
| **D11** | **We ship `tokenizer.model` in our own release asset alongside the ONNX.** That is a **second** CC-BY-4.0 redistribution, so the attribution surface is **REQUIRED, not optional** — a new "Models & licences" `ui:Card` on `Views/AboutPage.xaml`, naming **both** artifacts. | The sherpa-onnx archive contains no tokenizer (`CtcTokenizer`'s own artifact note says so); `tokenizer.model` had to be extracted from the 459 MB `.nemo`. Two files re-hosted from a CC-BY-4.0 checkpoint = attribution is a licence term, and `AboutPage.xaml` has **no attribution section today** (verified — the page is logo / vision / privacy / impact / donate / iPhone / troubleshooting). |
| **D12** | **Term validation is a product requirement.** Reject length-1 terms outright; run every term through `CtcTokenizer.IsSpottable` and **tell the user** when a term can never be spotted. | Measured tokenizer defects: a **single-character input returns an EMPTY id list** (a term with no ids is a guaranteed silent no-op), and this 1024-piece English BPE has **no digits, no hyphen, no accented Latin, no CJK** — `Wi-Fi`, `café`, `3.14`, `Zürich` encode with `<unk>` and can **never** be spotted. Shipping a text box that silently accepts them is shipping a lie; `IsSpottable` exists to make it visible (`vocabulary-ux.md §2.4`). |
| **D13** | **M0 is PASSED with caveats; M2 proceeds.** Revised estimate: front-end **−1 day** (written + tensor-verified), tokenizer **−0.5 day** (done), **latency re-architecture +1 day (new)**, model release + installer binding unchanged. | The spike closed the two kill-gate unknowns (feature contract, tokenizer) and opened one new one (latency), which nets out roughly flat. The one genuinely unbuilt piece is `CtcWordSpotter` — the DP — which the spike deliberately did not touch. |

Streaming-during-recording is deferred to **v1.4**, with an explicit trigger: *if M2 measures the
post-stop spotter missing its §3.5 deadline on **more than 1 in 10** real dictations under 60 s,
after silence-trimming and with DirectML available, revisit streaming.* (The old trigger — "more
than 2 s p95 on the CPU tier for a 60 s dictation" — is **already known to be missed**: the
measured CPU p50 at 60 s is 1987 ms, so p95 is over 2 s by construction. A trigger that has
already fired before the milestone starts is not a trigger; §3.5 replaces it with tiers.)

---

## 1. What the other two platforms actually do (verified against their source)

Both apps share one design.

**Two models run over the same audio:**

| Role | Mac | iOS |
|---|---|---|
| Primary transcript | Parakeet TDT 0.6B v3, or **Nemotron** | Parakeet TDT (v2/v3) |
| Keyword spotting | **Parakeet CTC 110M** | **Parakeet CTC 110M** |

The CTC model is *not* used to transcribe. It is used to produce a **per-frame log-probability
matrix** over its ~1024-token BPE vocabulary, and then a cheap dynamic-programming search asks:
"does the token sequence for the term *Nemotron* appear anywhere in this matrix, and at what
score and time range?" That is NVIDIA's **CTC-based Word Spotter** (arXiv 2406.07096).

**The pipeline (Mac, `Sources/Vocabulary/`):**

1. `CtcModelCache` provides the `parakeet-ctc-110m-coreml` package (~97.5 MB: mel + encoder +
   CTC head + `vocabulary.json` + `tokenizer.json`).
   **CORRECTED:** on **Mac this is downloaded, not bundled** — `CtcModelCache.swift:12-14, :24-34,
   :70-74` fetches into `~/Library/Application Support/Jot/Models/…`, surfaced as an optional,
   skippable wizard step (`SetupWizard/Steps/LanguageStep.swift:35-39, :187-201`). **iOS** is the
   bundled one. This settles our own §6 Q2: download-on-enable *is* the shipping Mac behavior.
2. `VocabularyRescorerHolder.prepare()` tokenizes the user's terms against the CTC vocabulary
   (`loadWithCtcTokens`) and builds a `CtcKeywordSpotter`.
3. `StreamingCtcSpotter` feeds audio to the spotter during recording in 15 s chunks with 2 s
   overlap, merging per-chunk log-probs with a log-sum-exp average. **We do not do this in v1.3
   (D1).** For the record: the merge is **13 lines** (`StreamingCtcSpotter.swift:174-186`), not 40,
   and `overlapFrames` is *derived at runtime* from FluidAudio's returned `frameDuration`
   (`:158-159`) — there is no constant to port verbatim.
4. The detections (`term, score, startTime, endTime`) go to **`VocabularyGate`** — a pure-value
   anti-overcorrection layer — which decides whether each one may actually replace a word.
5. Applied corrections are surfaced to the UI and recorded for a propose→confirm→learn loop
   (`CorrectionStore`, "when I say *Jamie* I mean *Jamy*").

**The part that matters most to us — the Nemotron path.** `VocabularyRescorerHolder.swift:286`
is titled *"No-fork Nemotron entry point"*, and the comment states the problem exactly as we
have it on Windows:

> Nemotron's stream returns a plain `String` with NO per-word timings and NO confidence, so the
> timing-dependent `VocabularyRescorer.ctcTokenRescore` is inert there. Instead we run the CTC
> keyword SPOTTER on the audio — which acoustically detects each vocab term and its audio TIME
> RANGE without needing transcript timings — and place each detected term onto the Nemotron
> transcript ourselves, then apply the SAME `VocabularyGate`.

So the Mac already faced "our ASR gives us bare text" and solved it. **Windows is that case.**

**Placement without timings** — ported and in-repo: `VocabularyGate.ApplyFromDetections`
(`src/Jot/Vocabulary/VocabularyGate.cs:372`; jot-shared original is
`VocabularyGate.swift:469` — **not** `:270`, which is the Mac fork's copy with a different
signature). Split the transcript into words; give each word a fractional position `(i + 0.5) / n`;
map each detection's audio midpoint to a fraction of total duration; for each detection pick the
*nearest unclaimed word that is a plausible near-miss* of the term, one detection per word, then
splice in document order (`ApplyFromDetections`'s placement loop, `:412-413`, is where
`Detection.StartTime/EndTime` feed placement).

**The gate's guards** (why it doesn't destroy good transcripts — this is the whole value). All
five are ported, and all five live in **`VocabularyGate.Decide`** (`:535-613`) — cite the numbered
step, the line number is advisory:

- **Learned override** — `Decide` step (0), the override loop (`:572-584`): a user-confirmed pair
  bypasses the guards for that pair only; a demoted pair (`ov.Net <= -1`) is blocked outright.
- **Plausibility** — `Decide` step (1) (`:591`, via `Plausible` at `:840`): normalized Levenshtein
  / "skeleton" similarity ceiling against the term *or any alias*; far-off swaps rejected.
- **Multi-word terms self-gate** — `Decide` step (2) (`:595-596`): applied silently, not an ask.
- **Confidence ceiling** — `Decide` step (3) (`:600`): never overwrite a word the ASR was ≥0.95
  sure about unless the margin is decisive. *(TDT path only — **inert on our path**, since
  `confidence` is pinned at `LowConfidence = 0.85f` (declared `:43`, pinned at `:555`) when
  `wordConfidence` is empty. Note `:40` is `ConfidenceCeiling = 0.95f` — a collision that has
  misled two revisions of this doc.)*
- **Common-word brake** — `Decide` step (4) (`:607`): a word in the ~24k-entry frequency list is
  *never* auto-replaced ("neutron" must not become "Nemotron"); it is blocked and surfaced. 21
  language lists ship embedded (`Jot.csproj:39`).

**Tuned constants — UNVERIFIED, do not seed thresholds with them.** `minSpotterScore -15.0`,
`minVocabCtcScore -12.0`, `cbw 3.0`, `minSimilarity 0.52` appear in **no** Swift file in either
repo; every Mac call site passes `minScore: nil` (`StreamingCtcSpotter.swift:107, :149`;
`VocabularyRescorerHolder.swift:272, :333`) and takes FluidAudio's defaults. They are hearsay from
FluidAudio's docs and are recorded here only so nobody re-discovers them and treats them as ported
truth. Our thresholds must be derived from our own score distribution in M2.

**The scar tissue worth inheriting:** the Mac's own design doc opens by saying macOS vocabulary
**overcorrected** ("name"→"Jamy", "and"→"Andre") because it applied the rescorer's output
*without* a gate. The gate is the difference between a feature people love and one they turn
off. Do not ship the spotter without it.

---

## 2. Does this port to Windows? Yes — and after M0 the two missing capabilities are built

*(Heading changed 2026-07-25: this section used to read "with two capabilities we do not have yet" —
the mel front-end and the term→id encoder. Both now exist and are verified; what is left is the DP,
the installer binding, and the D10 latency work.)*

| Need | Status on Windows |
|---|---|
| A CTC model in ONNX | **Available and MEASURED.** `sherpa-onnx-nemo-parakeet_tdt_ctc_110m-en-36000-int8.tar.bz2` (**104 MB** archive), converted from `nvidia/parakeet-tdt_ctc-110m`. Contents, opened: **only** `model.int8.onnx` (**131,652,171 B**, a **single graph** — not encoder+decoder), `tokens.txt` (**1025 lines** = 1024 BPE + `<blk>` at id 1024), and two test WAVs. **No tokenizer, no config** — see the tokenizer row. |
| Runtime to host it | **Have it.** `Transcription/Onnx/OnnxSessionFactory.cs` already builds CPU/DirectML sessions. **Per D10 both are allowed** for the post-stop pass; CPU is the measured floor (§3.5), DirectML the (unmeasured) lever. |
| Mel features | **BUILT AND TENSOR-VERIFIED (D2).** `src/Jot/Transcription/Ctc/CtcMelFrontend.cs` exists; `MelFrontend.cs` untouched. The gap turned out to be **three constants plus ~15 lines of normalization**, not a Kaldi-fbank rewrite: 128→**80** mels, `1e-10`→**`2^-24`**, **periodic→symmetric** Hann, then `per_feature`. Everything else (Slaney mel 0–8000 Hz, n_fft 512 / hop 160 / win 400, preemph 0.97) was **already right**. See §3.2. |
| Term → CTC token ids | **BUILT AND VERIFIED (D4).** `src/Jot/Transcription/Ctc/CtcTokenizer.cs` over `Microsoft.ML.Tokenizers` 2.0.0 — **89/89 in-scope terms id-for-id** against the SentencePiece oracle. **But the artifact is not in the sherpa archive**: `tokenizer.model` (SentencePiece BPE, **251,875 B**) had to be extracted from the 459 MB `.nemo`, which is why **D11** puts it in our own release asset. Two pinned defects and one hard vocabulary limit in §3.3. |
| Full audio at stop | **Have it.** `AudioRecorder.Stop()` (`:154-165`) returns `RecordingResult.Samples` — float[], 16 kHz mono — and writes the WAV *from* it. A post-stop spotter gets a clean, already-paid-for buffer for free. |
| Decoder surgery | **None needed.** This is the whole point: we never touch the RNNT greedy loop (`NemotronFp16Transcriber.cs:213-224`, `NemotronTranscriber.cs:176+`). |
| Download engine | **Have it, and it is better than "reusable".** `AssetDownloader.EnsureAsync(manifest, targetDir, …)` (`:31`) has zero Nemotron assumptions; the free-space guard (`:225-243`) is generic; retry / Range-resume / SHA-256 come free. The *binding* layer is the real cost — see §3.4. |

**Licensing (D11) — the attribution surface is now REQUIRED, and it covers TWO artifacts.**
`nvidia/parakeet-tdt_ctc-110m` is **CC-BY-4.0** — attribution only. ~114M params, 1024-token BPE,
16 kHz mono. We re-host **two** derived files on our own GitHub release, and both are redistribution:

| Artifact | Source | Why we host it |
|---|---|---|
| `model.int8.onnx` (131,652,171 B) | sherpa-onnx's converted export | the graph |
| `tokenizer.model` (251,875 B, SentencePiece BPE) | extracted from `parakeet-tdt_ctc-110m.nemo` | **the sherpa archive does not contain it**, and `tokens.txt` alone can only *de*code |

So: a new **"Models & licences"** `ui:Card` on `Views/AboutPage.xaml` (placed after the "Your impact"
card), naming the checkpoint, CC-BY-4.0, the link, and **both** files — plus the conversion tooling's
own licence. **Verified: the page has no attribution section today.** *(Adjacent, flagged not folded:
the same card is the right home for the shipping Nemotron artifacts' attribution; that licence audit
is separate work.)*

**Size:** the int8 graph is **131.7 MB**, plus 252 KB of tokenizer — call it **~132 MB** in UI copy,
not "100–150 MB". We already download ~754 MB for Nemotron, so this rides the existing installer
pattern as a second, optional artifact — **downloaded only when the user turns vocabulary on**, so
nobody pays for a feature they don't use.

**The honest accuracy note, and it cuts our way.** sherpa-onnx feeds this model the **wrong**
front-end — Kaldi fbank: HTK mel, Povey window, per-frame DC removal, 20–7600 Hz — where the
checkpoint's own `model_config.yaml` specifies NeMo `AudioToMelSpectrogramPreprocessor` (§3.2). It
survives because CTC plus per-utterance normalization is forgiving, but it costs accuracy: with
**correct NeMo features the model decodes a 60 s real dictation that sherpa's front-end collapses to
empty**, reproduced on **int8 and fp32** — so it is the front-end, not quantization. Consequence for
this plan: sherpa is a **provenance source for the weights only**. Never a behavioral reference.

**The honest limitation: this model is English-only.** Jot Windows ships 40 locales plus
auto-detect. Per **D5**, v1.3 vocabulary runs **only when the user has explicitly selected an
English locale**, and disables itself visibly otherwise.

> **CORRECTION — the previous revision claimed "the Mac has the same constraint." It does not.**
> `Transcriber.swift:331-343` gates only **Japanese**, and on *model identity*
> (`if modelID == .tdt_0_6b_ja`), not language. The Nemotron path gates on **engine**
> (`DualPipelineTranscriber.swift:80-83`) and hardcodes `language: .english` at `:347` purely to
> pick a common-word list. On the v3 European path the spotter **does** run on non-English, with
> the common-word brake *absent* (`CommonWords.swift:96-100`). So Windows would be the **first**
> platform to build language gating. That is still the right call — the `lista → Lisa` incident
> (`verification.md §1`) is evidence *for* our design, not evidence the Mac already fixed it — but
> we must stop claiming precedent we do not have.

---

## 2b. The gate is already extracted for us — `jot-shared`

Cloned to `C:\Users\vinee\projects\jot-shared` (`git@github.com:vineetu/jot-shared.git`, branch
`main`, **pinned at `5326460`** — which is HEAD, 0 commits behind as of 2026-07-25).

`Sources/JotVocabCore/` — pure Foundation, no ASR/CoreML/UI dependency:
`VocabularyGate.swift` (58 KB — the superset gate, bigger than either app's copy),
`CorrectionStore`, `CorrectionProvenance`, `CorrectionKey`, `AskPolicy`, `VocabTerm`,
`VocabularyFile`, `Seams.swift`.

**`Seams.swift` is the porting spec.** It names the only four things a platform must supply:

| Seam | Swift | Windows — **all four ported** (`src/Jot/Vocabulary/Seams.cs`) |
|---|---|---|
| 1 · engine-neutral rescore input | `RescoreProposal` / `RescoreOutput` / `TokenTiming` | our CTC spotter's detections mapped into `VocabularyGate.Detection` (`VocabularyGate.cs:89-94`) |
| 2 · common-word list loading | `CommonWordsProvider` | `EmbeddedCommonWordsProvider` (`CommonWords.cs`) over `EmbeddedResource` |
| 3 · diagnostics | `DiagnosticsSink` | `IDiagnosticsSink` adapter onto `JotLog` |
| 4 · storage directory | (injected) | `JotPaths.DataDir` → `<DataDir>\Vocabulary\` (D7) |

**Two things that materially shrank the plan:**

1. **The 20-language common-word problem is solved.** `common-words-{bg,cs,da,de,el,es,fi,fr,hu,it,nl,pl,pt,ro,ru,sk,sl,sr,sv,uk}.txt`
   plus English — **21 lists, ~5 MB**, now embedded in our assembly (`Jot.csproj:34-39`). The gate
   is multilingual today. The English-only constraint in §2 applies *only to the CTC spotter model*.
2. **Golden fixtures are a conformance contract that names us.** The jot-shared README: *"a future
   non-Apple (e.g. Windows) port must reproduce the same outputs from the same fixtures."* All 7 are
   vendored at `tests/Jot.Tests/Fixtures/vocab/` and wired at `Jot.Tests.csproj:31-34`:
   `vocabulary_gate_decide.json`, `detections_apply.json`, `correction_store_roundtrip.json`,
   `ask_policy_select.json`, `provenance_verdicts.json`, `correction_key_normalize.json`,
   `pair_key.json`. Port fixture-first: red tests from the JSON, then make them green.

**Loud-failure behavior — note what the port actually does.** `EmbeddedCommonWordsProvider.Load`
(`CommonWords.cs:53-88`): a *named but missing* resource reports once to `IDiagnosticsSink`
(`ReportMissing`, `:90-100`) and returns an empty set; `Words(null)` returns empty *silently and
intentionally* (`:49`) for a language with no list (`ResourceFor`, `:39-45` — only 21 of 40
locales ship one). It **degrades**, it does not disable the gate. The Mac's "#1 shipped bug" (the
list missing from the bundle) **cannot happen here**: `Jot.csproj:39` uses `EmbeddedResource`, not
`Content`, specifically for that reason, and `CommonWordLists_AreEmbeddedAndPopulated`
(`VocabGoldenFixtureTests.cs`) pins every one. That packaging risk is **retired** — do not build a
"required file is missing" surface for it (see `vocabulary-ux.md §6.5`).

**Adjacent item — CORRECTED.** The previous revision said *"our offline-cleanup design is not yet
implemented; it should be a port of `JotTextPipeline` instead."* It **is** implemented and wired:
`src/Jot/Text/TextPipeline.cs` (+ `ModelArtifactScrubber`, `FillerWordCleaner`, `NumberNormalizer`),
called at `RecorderController.cs:226`. The live recommendation is therefore narrower: **reconcile
our shipped `TextPipeline` against jot-shared's `JotTextPipeline` fixtures** to catch Windows drift.
Separate work; flagged, not folded in.

---

## 3. Proposed Windows architecture

```
recording ──► Nemotron RNNT (unchanged, live or batch) ──► transcript (string)
                                                                  │
                            at STOP, after TextPipeline.Clean:    │
   result.Samples (float[], 16 kHz mono) ──► TrimSilence (D10 — correctness, not just latency)
                                                 │  (keep the trim offset: detections are in
                                                 │   TRIMMED time and must be shifted back)
                                             CtcMelFrontend (80-bin, per_feature)
                                                 │
                                          CTC single graph (ONNX, CPU **or DirectML** — D10, ONE SHOT)
                                                 │
                                          per-frame log-probs ──► CtcWordSpotter DP
                                                 │                    detections
                                                 ▼                        │
                                   VocabularyGate.ApplyFromDetections ◄────┘
                                                 │
                                    gated transcript + records
                                                 │
                                  ► _store.Add ► paste ► pill chip
```

**Why post-stop (D1).** After `_recorder.Stop(wav)` returns (`RecorderController.cs:210`), the RNNT
has already finished (`_live.FinishAsync()` completed at `:205`), so GPU/CPU contention with live
captions is **zero**, the full float array is in hand for free, and three separate hazards
evaporate: the `SnapshotSamples()` O(n)-per-poll re-decode, the `Stop()`/`Discard()` unlocked-buffer
race, and the chunk-merge-equivalence problem that utterance-global `per_feature` normalization
creates. The cost is latency at stop, budgeted in §3.5.

**Why DirectML is now allowed there (D10).** The CTC pass runs strictly *after* the RNNT has
produced text — in live mode because `FinishAsync()` has returned, in batch mode because the batch
decode is what produced `text` in the first place. The two never overlap, so the `RealtimeGuard` /
`GpuProbe` contention D1 was protecting against cannot occur on this path. Two constraints survive
verbatim: **never** put spotter work on `LiveTranscription`'s poll thread, and **never** let the CTC
session influence the cached `GpuProbe` verdict (`EngineSelector.cs:39-41`) — it is a consumer of
the tier, never an input to it.

### 3.1 New code, in dependency order

| # | File | Notes |
|---|---|---|
| 1 | `Services/VocabularyStore.cs` | Terms + aliases, persisted to `<DataDir>\Vocabulary\vocabulary.json` (D7). Mirror `Services/PromptCatalog.cs`; resolve the directory **once at construction** from `JotPaths.DataDir(...)` like `JsonRecordingStore.cs:30`, or it disagrees with the library store after a data-folder move. One `SanitizeTerm` choke point used by *every* writer — and per **D12** that choke point is where length-1 rejection and the `IsSpottable` check live. **NEW FILE — not ported.** |
| 2 | `Transcription/Ctc/CtcMelFrontend.cs` | **EXISTS (spike quality), tensor-verified.** Separate class per D2; `MelFrontend.cs` untouched. **Sharing `MelFrontend` is still forbidden**: `NMels` is `public const 128` (`MelFrontend.cs:20`) read from **10 sites** (`NemotronFp16Transcriber.cs:125, 201, 202, 234, 237`; `NemotronTranscriber.cs:182, 187, 211`; `StreamingMel.cs:33, 37`), and `Jot.csproj:29` exists to protect those engines' byte-exactness. M2's job here is productionization (naming, disposal, the D10 silence trim), **not** re-derivation. |
| 3 | `Transcription/Ctc/CtcTokenizer.cs` + `CtcTokens.cs` | **EXIST (spike quality), verified 89/89.** `Microsoft.ML.Tokenizers` 2.0.0 over `tokenizer.model`; `CtcTokens` reads `tokens.txt` (1025 lines, blank at 1024). `IsSpottable(term, tokens)` is already there and is **D12's mechanism**. See §3.3 for the two defects and the vocabulary limit. |
| 4 | `Transcription/Ctc/CtcModel.cs` + `CtcModelInstaller.cs` | **NOT STARTED.** Mirror `NemotronModel` / `NemotronModelInstaller` (resume/retry/free-space/SHA-256). Manifest carries **two** files: `model.int8.onnx` **and** `tokenizer.model` (D11) — a manifest without the tokenizer produces a downloaded model that cannot encode a single term. **Do NOT use `ParakeetModelInstaller.cs` as the template** — it doesn't implement `IModelInstaller`, has its own HTTP loop (`:73-114`), and has no checksums, no resume, no free-space check. See §3.4. |
| 5 | `Transcription/Ctc/CtcEncoder.cs` → `CtcSpotter.cs` | **`CtcEncoder` EXISTS (spike quality); `CtcSpotter` NOT STARTED.** One-shot: samples → trim → mel → single graph → log-probs. Session backend is **CPU or DirectML (D10)**, created on first use after the toggle is enabled and **disposed on disable** — measured as real: **+283 MB working set** after the first `Run`, **276 MB returned on `Dispose`**. Nothing else in the app disposes ONNX sessions, so without this "turn vocabulary off" won't return the memory until restart. |
| 6 | `Vocabulary/CtcWordSpotter.cs` | **The DP. The one genuinely unbuilt piece — the M0 spike deliberately did not touch it.** Reference: NeMo CTC-WS (arXiv 2406.07096) and sherpa-onnx's keyword-spotter machinery. There is **no local reference implementation**: `CtcKeywordSpotter`/`spotKeywordsFromLogProbs` live in FluidAudio 0.15.4 (`Package.resolved`, rev `b9d4372`), an external SPM dep with no checkout on this machine. Note the graph's `logprobs` output is **already log-softmaxed** (metadata-confirmed) — do not apply a second one. |
| 7 | `Vocabulary/VocabularyGate.cs` etc. | **DONE.** See §5b. |
| 8 | `Vocabulary/CorrectionStore.cs`, `Vocabulary/CorrectionProvenance.cs` | **PORTED 2026-07-25**, fixture-green (8 store + 9 provenance cases). `<DataDir>\Vocabulary\` is registered in `JotDataPurge` and `DataFolderMigrator`, verified by tests that run the real purge and the real migrator. **No consumers yet** — nothing in `src/` calls them, so the folder is never created in a shipped build until `VocabularyRunner` lands. |
| 9 | `Vocabulary/VocabularyRunner.cs` | The wire-up seam called from `RecorderController` — language gate, model-ready check, tokenize, spot, gate, the `Outcome == "applied"` filter before `AskPolicy.Select` (§5b), the deadline, and the D6 try/catch. Having one class means one place to test the invariant. |

**Also engine work, and easy to forget: `enrichedAliases`.** Mac `design.md §6 [ADD]` and iOS
`correction-review-implementation.md §11` both record the same bug: *"'Ramaa Nathan' heard as merged
'Ramanathan' → replaced by the SHORTER term 'Ramaa' because the right term never competed."* Fix:
append each multi-word term's space-stripped form as an alias **at feed time only** — the user's
`vocabulary.json` is untouched and the UI never shows the synthetic alias.
`VocabularyGate.Detection` already carries `Aliases` (`:89-94`, the field at `:91`) and
`Decide` step (1) plausibility reads them (`:591` → `Plausible`, `:840-849`), so this is a few
lines in `VocabularyRunner` — but it is invisible in the UX and will otherwise be lost.

**Audio fidelity: use `result.Samples`, never the WAV.** `WriteWav16` (`AudioRecorder.cs:202-215`)
quantizes to 16-bit PCM, so a spotter re-reading the WAV sees different audio than the RNNT saw.
ReTranscribe and MediaImporter *do* go through `WavAudio.ReadMono16k` — three audio sources at three
fidelities, so be explicit about which one each path feeds.

### 3.2 The mel front-end (D2) — RESOLVED by M0, all values measured

**The graph's actual contract** (read from the ONNX, not inferred):

| | |
|---|---|
| Input 1 | `audio_signal`, float32, `[batch, 80, frames]` — **FEATURES, feature-major.** Not samples, and not frame-major. |
| Input 2 | `length`, int64, `[batch]` — the **UNsubsampled** frame count |
| Output | `logprobs`, float32, `[batch, frames/8, 1025]` — **already log-softmaxed** |
| Metadata | `vocab_size=1024`, `subsampling_factor=8`, `normalize_type=per_feature` |
| Frame rate | **80.00 ms/frame after subsampling — MEASURED**, not assumed from 8×10 ms |

**The front-end spec is the checkpoint's own `model_config.yaml`** (extracted from the 459 MB
`.nemo`): NeMo `AudioToMelSpectrogramPreprocessor` — Slaney mel 0–8000 Hz, Hann, n_fft 512 /
hop 160 / win 400, preemph 0.97, log guard `2^-24`, `per_feature` normalization.

| Property | Nemotron `MelFrontend` (shipping) | CTC (`CtcMelFrontend`, **built**) | Delta |
|---|---|---|---|
| Mel bins | 128 (`public const`, **10** read sites) | **80** | changed |
| Log guard | `1e-10` | **`2^-24`** | changed |
| Hann window | **periodic** | **symmetric** | changed |
| Normalization | **none** | **`per_feature`** (per-bin mean/var over the utterance, unbiased `n-1` std) | ~15 new lines |
| n_fft / hop / win | 512 / 160 / 400 | 512 / 160 / 400 | **same** |
| Preemphasis | 0.97 | 0.97 | **same** |
| Filterbank | Slaney, 0–8000 Hz | Slaney, 0–8000 Hz | **same** |

**So the "new front-end" was three constants and a normalization pass — not a Kaldi rewrite.** That
is the single biggest estimate change out of M0 (D13: −1 day). One trap the spike hit and pinned:
frame count is NeMo's `get_seq_len` — `floor(n/hop) + 1` for a `center=True` STFT — **not** kaldi's
`(n + hop/2) / hop`, which sherpa uses and which is off by one on most lengths
(`CtcSpikeTests.FrameCount_MatchesNeMoGetSeqLen`).

`per_feature` being *utterance-global* is precisely why streaming is deferred (D1): a 15 s chunk
normalized against its own statistics is not a slice of the one-shot input, and no log-sum-exp merge
of the *outputs* recovers the one-shot result. One-shot needs no merge and the problem does not
arise.

**And it is why long silence is actively harmful (D10d).** Utterance-global statistics mean leading
silence changes every frame's normalized value. A **total decode collapse was reproduced**: a 48 s
clip decodes; **the same clip padded to 50 s returns nothing at all.** Trimming leading/trailing
silence is therefore a **correctness** fix first and a latency win second. Implementation trap worth
writing down: the trim shifts the time base, so `Detection.StartTime/EndTime` come back in *trimmed*
time and must have the leading-trim offset added back **before** `ApplyFromDetections` maps them to
word positions — otherwise every detection lands early, in proportion to the silence removed.

**Do not expect the ONNX graph to contain the front-end — CONFIRMED, not assumed.** The graph's
first input is an 80-bin feature matrix (above); feeding it raw samples fails at the first `Run`.
sherpa-onnx computes features **externally** (kaldi-native-fbank) — and, per §2, computes the
**wrong ones** for this checkpoint.

### 3.3 The tokenizer (D4) — RESOLVED by M0, with two defects and one hard limit

The whole spotter depends on turning `"Nemotron"` into ids in the CTC model's 1024-token BPE
vocabulary (the Mac's `loadWithCtcTokens`). Before M0 we had only the easy direction.

- **Dependency, shipped:** `Microsoft.ML.Tokenizers` **2.0.0** — fully managed, one transitive dep
  (`Google.Protobuf`), no native library. It therefore **cannot** recreate the APPX1101 duplicate-
  `onnxruntime.dll` collision `Jot.csproj:52-56` documents. `Jot.csproj` gained **exactly one line**.
- **Artifact — and this is the D11 surprise.** `tokenizer.model` (SentencePiece BPE, **251,875 B**)
  is **NOT in the sherpa-onnx archive**; it had to be extracted from `parakeet-tdt_ctc-110m.nemo`.
  The archive's `tokens.txt` (1025 lines) carries pieces + ids but **no merge scores**, so it can
  decode and cannot encode. Both files go in our manifest (§3.4) and both need attribution (D11).
  `tokenizer.model`'s vocabulary is **id-for-id identical to `tokens.txt` for all 1024 ids** —
  verified, so `CtcTokens` and `CtcTokenizer` cannot disagree about the id space.
- **Verification result: 89/89 in-scope terms, id-for-id** against the SentencePiece oracle
  (`spm-oracle-ids.json`). Criterion met. A round-trip was kept only as a secondary check — it
  proves self-consistency, and a wrong-but-self-consistent merge sequence round-trips perfectly
  while giving the DP a sequence the model never emits (silent zero recall, no exception, no log).

**Defect 1 — a single-character input returns an EMPTY id list.** Not an exception, not `<unk>`:
zero ids. A term with no ids is a guaranteed silent no-op in the DP. **D12: reject length-1 terms
at `VocabularyStore.SanitizeTerm`,** and say why in the UI.

**Defect 2 — a run of OOV characters emits one `<unk>` per character.** So an unspottable term does
not merely fail; it produces a long junk id sequence. `IsSpottable` rejects any term whose ids
include `<unk>`, the blank id, or an out-of-range id.

**Hard limit — what this model can never hear.** The 1024-piece English BPE contains **no digits, no
hyphen, no accented Latin, no CJK.** So `Wi-Fi`, `café`, `3.14` and `Zürich` encode with `<unk>` and
**can never be spotted, at any threshold, with any amount of tuning.** This is a property of the
checkpoint, not of our code. Two consequences:

1. **D12 makes it visible.** `CtcTokenizer.IsSpottable(term, tokens)` runs at term-entry time and
   the Vocabulary UI states the result (`vocabulary-ux.md §2.4`).
2. **It narrows what the feature may promise.** "Words Jot should get right — names, products,
   jargon" is honest only for plain unaccented A–Z words. Product names with hyphens or digits —
   a large slice of the obvious use case — are out. Copy everywhere must reflect that, not just the
   error state.

### 3.4 Model install — the binding layer is where the day goes

The download *engine* is free (§2). Everything around it is not:

- `NemotronModelInstaller.cs` is 51 lines of which ~30 are **hardcoded SHA-256 + exact byte size per
  asset** (`:23-39`), read back from the GitHub release API. Non-code prerequisite: publish **both**
  D11 artifacts — `model.int8.onnx` (131,652,171 B) **and** `tokenizer.model` (251,875 B, extracted
  from the `.nemo`; it is *not* in the sherpa archive) — under a `vineetu/jot-windows` release tag
  (pattern `jot-model-<name>-v1`), upload, read back digests. A manifest missing the tokenizer
  downloads a model that cannot encode a single term.
- **The binding is hand-enumerated per model.** `ModelDownload`'s public ctor is bound to the
  concrete int4 installer (`Services/ModelDownload.cs:24`) with the generic ctor `protected` (`:27`),
  so a third model needs: a new subclass (as `GpuModelDownload.cs:15` is), a DI line
  (`App.xaml.cs:2338-2341`), a `SettingsViewModel` ctor param + property (`:214, :100, :105`), a
  `SettingsPage.xaml.cs:32` refresh call, and a **duplicated XAML row block**
  (`SettingsPage.xaml:218-226` / `:237-245` is copy-pasted markup, not an `ItemsControl`).
- **Fix a live bug while you are there (blast-radius rule).** `SettingsViewModel.cs:473` refreshes
  only `_download` after a data-folder move — `_gpuDownload` is already missed **today**. The third
  model is the right moment to convert these to a collection.
- `tests/Jot.Tests/AssetManifestTests.cs:35, :49, :82` assert exact per-manifest asset counts and
  distinct base URLs — a third manifest requires updating them.
- **Specify the mid-download behavior:** vocabulary enabled + user dictates while the model is still
  downloading ⇒ **no spotter, no corrections, no error**, with progress on the Settings row.

### 3.5 Latency — MEASURED, and the D10 degradation ladder

**The old budget ("< 800 ms p50 / < 2 s p95 at 60 s") is deleted. It was never achievable.**
Measured, Ryzen 7 3700X, int8, CPU EP, Release, **p50 of 10 runs after warm-up**:

| Audio | One-shot wall time | Notes |
|---|---|---|
| 10 s | **219 ms** | |
| 40 s | **1139 ms** | |
| 60 s | **1987 ms** | ~2.5× the old p50 budget |
| 180 s | **9662 ms** | ~2× `SlowStopMs` on its own |
| *(one-off)* | session load **1.0–1.1 s** | must be paid at enable/startup, never inside a stop |

**Cost is super-linear: 18× the audio costs 44× the time** (fitted exponent ≈ **1.31**). And the mel
front-end is **not** the problem — it is **143 ms of the 1987 ms (7 %)**; the graph is **1781 ms**.
So "optimize the front-end" is not a lever. **Chunking is not available either**: `per_feature` is
utterance-global (§3.2).

A post-stop pass adds directly to the window `RecorderController.cs:123` `SlowStopMs = 5_000`
already measures — `stopSw` starts at `:193`, is logged at `:259`, and fires a user-facing "That took
longer than usual" nudge at `:260`. That constant, not a wish, is what sets the budget below.

**The D10 ladder. Corrections stay in the pasted text wherever we can pay for them; where we can't,
we say what happens instead.** All tiers are on **trimmed** speech (D10d), CPU int8, which is the
floor — DirectML is expected to move the middle tier up and is measured in M2, not promised here.

| Trimmed speech | CPU int8 cost | Behavior |
|---|---|---|
| **≤ 20 s** | ≤ ~0.5 s (10 s = 219 ms measured) | Corrections **in the pasted text**. The overwhelming majority of dictations. |
| **20–60 s** | 1.14 s @ 40 s, 1.99 s @ 60 s (measured) | Corrections **in the pasted text**. Inside the deadline with headroom. |
| **60–120 s** | ~2–5 s (modeled at exponent 1.31; **not measured** — M2 measures 90 s and 120 s) | **Degrades by design** above the deadline: paste the RAW transcript, apply corrections to the SAVED recording, surface the chip. DirectML is the lever that may pull this band back under the deadline. |
| **> 120 s** | ≥ ~5 s (180 s = 9662 ms measured) | **Spotter skipped entirely.** No pass, no chip, no claim. |

**Deadline: 2.5 s, fixed, on the CTC pass.** Chosen from the measurement, not taste: 60 s of speech
costs 1987 ms p50, so 2.5 s clears the common case with ~500 ms of headroom while keeping the added
stop cost inside `SlowStopMs = 5_000` alongside the stop work that already happens. On expiry the
spotter does **not** throw and does **not** block — it returns the input unchanged (D6), the raw
text is pasted, and the pass finishes against the saved recording.

**Hard cutoff: 120 s of trimmed speech, skip entirely.** At 120 s the modeled CPU cost (~5 s) *is*
`SlowStopMs`, and the measured 180 s cost is 9.66 s — nearly 2× it. Above 120 s the pass would miss
the deadline on essentially every dictation, so running it only buys a guaranteed degrade plus the
power and the 283 MB. Skipping is the honest option. **M2 confirms with a measured 120 s point and
drops the cutoff to 90 s if the measured cost there exceeds 4 s.**

**Session warm-up is mandatory, not an optimisation.** The 1.0–1.1 s load must never land inside a
stop, or the first vocabulary dictation of every session blows the deadline by itself. Create and
warm the session when the toggle is enabled, and at app start when it is already on.

**Silence trimming (D10d) comes first in the pipeline** — correctness before latency (§3.2: a 48 s
clip decodes, the same clip at 50 s collapses to nothing). Remember to shift detection timestamps
back by the leading-trim offset.

**Memory, measured:** **+283 MB** working set after the first `Run`; **276 MB returned on
`Dispose`**, so dispose-on-disable genuinely works. Alongside the RNNT (int4 **690 MB** / fp16
**1.236 GB**) that is **~20–40 % on top** — higher than §4's earlier "~10–20 %" guess, and the
reason the disable path is a requirement rather than hygiene.

**Escalation order if M2 measures worse than the ladder:** (1) **DirectML** (D10a — the new first
lever), (2) cap terms harder (`vocabulary-ux.md` DECIDE-8's 200 is a *latency* budget), (3) tighten
the length cutoff, (4) **then** streaming as a v1.4 optimisation. If streaming is ever revisited,
re-run `GpuProbe` *under load* as its gate: `GpuProbe.MaxAvgChunkMs = 150` (`GpuProbe.cs:24`) is
measured solo (`:49-77`) and cached per adapter+driver (`EngineSelector.cs:39-41`); it says nothing
about "this machine with a second model resident."

**Never put spotter work on `LiveTranscription`'s poll thread** — it would add straight into
`_processingSeconds` (`LiveTranscription.cs:87`) and trip `RealtimeGuard`
(`RealtimeGuard.cs:16-19`) by construction, degrading live captions to batch. D10 relaxes D1's CPU
pin; it does **not** relax this.

### 3.6 Call site + the never-break-a-dictation invariant (D6)

**Where.** `RecorderController.StopAndDeliverAsync`, between the cleanup at `:226` and the save at
`:235`. **CORRECTED:** the previous revision said *"gate after `MaybeCleanupAsync`"* — that symbol
does not exist anywhere in `src/` (the AI cleanup pass was removed 2026-07-20). The real call is:

```csharp
// RecorderController.cs:225-226 — NOTE: conditional.
if (_settings.Current.OfflineCleanupEnabled)
    text = Jot.Text.TextPipeline.Clean(text, _settings.Current.Language, isNemotron: true);
```

This matters because that line is load-bearing for the entire offset story: nothing mutates `text`
between `:226` and `_store.Add` (`:235`) / `PasteAtCursor` (`:244`) / `TranscriptReady` (`:253`), so
`ApplyFromDetections`'s `Proposal.PublishedStart`/`PublishedLength` (`VocabularyGate.cs:77-78`, set
at `:489-490`) are exact against the string that is both saved and pasted.

**Guard test — with exactly one sanctioned exception.** Add a guard test that fails if any text
mutation is inserted between the gate and `_store.Add`. The **ask splice** (`vocabulary-ux.md §4.4`
step 4) *is* such a mutation and is deliberate, so the two sections are reconciled this way:

- The ask splice is the **only** whitelisted mutation. It runs through the same
  `CorrectionProvenance.ShiftAnchors` the transcript editors use (`vocabulary-ux.md §5.5`), so
  every later `PublishedStart` is corrected for the length delta rather than left stale.
- The §5.5 transcript fingerprint is stamped on the **post-splice** string — the exact string
  handed to `_store.Add`. Never on the pre-splice string.
- The guard test therefore asserts: *the only writer to `text` between the gate and `_store.Add` is
  the ask splice, and it goes through `ShiftAnchors`.* Any other mutation fails it.

**HARD INVARIANT (D6): vocabulary never breaks a dictation.** `StopAndDeliverAsync` wraps everything
from `:195` in one `try` whose `catch` at `:267-271` plays an error sound and fires
`Failed?.Invoke(...)` — **and never reaches `_store.Add` or `PasteAtCursor`.** The transcript is
gone. So the gate call site must be independently wrapped:

```csharp
string gated = text;
try
{
    gated = _vocabulary.Run(text, result.Samples, result.Duration);   // includes its own timeout
}
catch (Exception ex)
{
    JotLog.Error("vocabulary failed — delivering ungated text", ex);
}
text = gated;
```

Requirements on that block. **A throw is the easy case; the three that actually bite are a hang, a
live ask card on the error path, and a test spec that asserts nothing:**

- **Wall-clock timeout** inside `Run` — a hung ONNX session must not hold the paste. On timeout,
  return the input unchanged and log. **Per D10 this is the 2.5 s CTC deadline (§3.5), and expiry is
  a designed path, not an error path** — see "the degraded path" below.
- **TWO deadlines, nested — this reconciles D10 with the ask deck.** A single number cannot serve
  both: the CTC pass must expire in **2.5 s** (§3.5), while the ask deck legitimately runs for
  `MaxAsks = 3` cards × 6–10 s each (`vocabulary-ux.md §4.2`). So:
  **inner** = 2.5 s on the spotter; **outer** = spotter deadline + the deck's own bounded maximum +
  a small slack, around the whole block. The outer one exists purely to catch a *hang* (a card that
  never resolves); the inner one is the latency control. Both keep-original-and-deliver on expiry.
- **One deadline around the WHOLE block, not just `Run`.** A non-completing
  `await AskCardWindow.RunAsync(deck)` (`vocabulary-ux.md §4.4` step 3) throws nothing, so neither
  the `catch` nor the `finally` at `RecorderController.cs:272-276` ever runs: `State` stays
  `Transcribing`, `PressToStart` ignores that state (`:143-144`), and **dictation is dead until
  restart, silently**. So the gate *and* the ask deck sit inside one `CancellationTokenSource`
  deadline (a `WhenAny(task, Delay(deadline))` is enough); on expiry, keep-original, deliver,
  log. Every await in this block must be able to complete or be abandoned.
- **`finally`: force-close any live card AND force the paste target.** If steps 1–4 throw while the
  card is up, Jot's own activatable window is foreground, so `PasteAtCursor(text, IntPtr.Zero, …)`
  pastes **into the card** (`RecorderController.cs:243` resolves `IntPtr.Zero` →
  `GetForegroundWindow()` at `TextInjector.cs:107`, and the `IsOwnWindow` guard at `:81` does not
  cover that path). So `vocabulary-ux.md §4.3` requirement 1 is conditioned on the vocabulary block
  being **entered**, not on a deck having completed: `finally { close any live card; }` and force
  `_originWindow` on the error path too.
- **Test requirement, stated so it asserts something real.** `PasteAtCursor` is gated on
  `_settings.Current.AutoPaste` (`:238`), so "assert the original transcript reaches `PasteAtCursor`"
  is vacuous with AutoPaste off. Write it as **two paths per failure mode**:
  - **AutoPaste ON** — the spotter throws / the spotter times out / the deck never completes: each
    asserts `_store.Add` receives the **original transcript** *and* `PasteAtCursor` is called
    **exactly once** with it.
  - **AutoPaste OFF** — same three failure modes: each asserts `_store.Add` receives the original
    transcript, `PasteAtCursor` is **not** called, and `TranscriptReady` still fires.
  Plus, on every failure mode: the state machine returns to `Idle` (the `finally` ran) and no ask
  card is left on screen. These tests are the invariant; they are not optional.
- Placing the gate before the empty check at `:228` is safe — `ApplyFromDetections` returns the
  input unchanged for an empty transcript (`VocabularyGate.cs:384-385`).

**The degraded path (D10b) — specified, because "degrade gracefully" is not a specification.**
When the inner 2.5 s deadline expires, the spotter is **not** cancelled and thrown away; the work is
finished off the stop path:

1. `Run` returns the **input text unchanged**. `_store.Add` and `PasteAtCursor` proceed exactly as
   they do today. This is D6's guarantee and it is unchanged.
2. The pass continues on a background task against the **already-saved** `RecordingItem`. When it
   finishes, the gate is applied to `Item.Transcript` and the proposals are committed to
   `CorrectionProvenance` against **that** string (the fingerprint is stamped on it, `§5.5` of the
   UX doc) — so the review surface is correct even though the pasted text was not corrected.
3. **No ask card on this path.** The ask is a *pre-paste* confirmation; the paste has happened.
4. The chip (`vocabulary-ux.md §3.3`) is surfaced **if the pill is still up**, and silently skipped
   if it is not — `PillState.Success` lingers 4000 ms (`PillController.cs:183`), and a pass that
   overran a 2.5 s deadline can easily land after that. **Never resurrect a dismissed pill**: a
   status pill reappearing seconds after the user moved on is a worse bug than a missing chip.
5. The background task inherits the same D6 rules: it may not throw into the app, and a *second*
   recording starting cancels it (its target recording is still saved and simply has no proposals).

**And the skip path (D10c):** above the 120 s cutoff nothing runs — no pass, no chip, no notice.
Consistent with `vocabulary-ux.md §6.3`'s standing rule that we do not put a "vocabulary was
skipped" notice on the pill; a nag about something the user cannot act on in the moment is worse
than silence.

**Language gate (D5), evaluated before any model work:**

```csharp
// "auto" normalizes to "auto"; unknown/empty normalizes to "en-US" (NemotronLocales.cs:106).
bool vocabLanguageOk =
    NemotronLocales.Normalize(_settings.Current.Language)
        .StartsWith("en", StringComparison.OrdinalIgnoreCase);
```

One line, zero engine change. **Precedent for punting on auto-detect:** `TextPipeline` already does
exactly this — `LanguageCode.cs:38` excludes `"auto"`, so auto-detect dictations today get no filler
removal and no number normalization. Being consistent with shipped behavior is defensible and
honest. The v1.4 unlock is filed in §6.

**Wire at `RecorderController`, not lower.** `RewriteController.cs:200-207` transcribes the spoken
*instruction* through the same live/batch path; a spotter hooked at the `AudioRecorder` or
`LiveTranscription` level would fire on rewrite instructions, burning compute and producing
detections nobody consumes.

**The other three transcript paths, stated as deliberate differences:**

| Path | Cleanup? | Gate in v1.3? |
|---|---|---|
| `RecorderController` dictation | `TextPipeline.Clean` at `:226` (conditional) | **Yes** — the only gated path. |
| `Import/MediaImporter` (`:55-61`) | **Never calls `Clean`** — `:226` is the only `Clean` call site in the product | **No.** Gating raw engine output on a different string is a different feature; scope it out and say so. |
| `RecordingDetailViewModel.ReTranscribe` (`:230-231`) | **Never calls `Clean`** | **No.** It also re-gates an item that may already have committed proposals — those would have to be invalidated first. Scoped out; it only runs for `IsPending` rows (`:218`). |
| `Rewrite/RewriteController` (`:131` `_store.Add`, `:133` `Succeeded`) | n/a | **No, and must stay no.** Rewrite output never passes the gate; ensure the *store* side gets no orphan proposals and the pill gets an **empty** corrections list (`PillController.cs:69`), or a rewrite inherits the previous dictation's chip. |

### 3.7 Data lifecycle (D7) — implementation checklist

**One layout, used by both docs:**

```
<DataDir>\Vocabulary\
    vocabulary.json      terms + aliases           (VocabularyStore)
    corrections.json     learned net per pair      (CorrectionStore)
    provenance.json      per-recording verdicts    (CorrectionProvenance)
```

Checklist — **do this in M1, not M3.** It is a two-line change that is easy to forget once the
feature is "done", and both reviewers found it independently:

- [ ] Add `"Vocabulary"` to `JotDataPurge.DataSubdirs` (`Services/JotDataPurge.cs:26`, currently
      `["models", "recordings", "logs"]`). Without it, "Erase all data" leaves the user's terms and
      correction history on disk — a violation of the privacy claim the 2026-07-23 blast-radius work
      exists to make true.
- [ ] Add `"Vocabulary"` to `DataFolderMigrator.MigratedItems` (`Services/DataFolderMigrator.cs:33`,
      currently `["models", "recordings", "library.json"]`). Without it, a data-folder move strands
      the folder in the old location (`Finish()` `:186-199` deletes only `MigratedItems` from the
      source) and the app shows an empty list on the new drive.
- [ ] Extend `tests/Jot.Tests/JotDataPurgeTests.cs:39-69` (`SeedEverything` +
      `ArtifactPaths_CoverEveryKnownArtifact`) and `DataFolderMigratorTests.cs`. Those tests exist
      precisely so a new artifact cannot be forgotten.
- [ ] `VocabularyStore` resolves its directory **once at construction** (`JsonRecordingStore.cs:30`
      is the pattern).
- [ ] The CTC model itself needs no work — it lands under `models\`, which is already in both lists.

**Divergence worth stating:** `prompts.json`, the app's other user-authored list, lives in
**`ConfigDir`** (`Services/PromptCatalog.cs:16, :43`; `JotDataPurge.ConfigFiles`). Vocabulary goes in
**`DataDir`** on purpose — it is user *data*, it should follow a data-folder move, and it should be
erased by "Erase all data". Reset-settings keeps it.

---

## 4. What I could not verify, and the experiment that resolves each

Each item names the experiment, so none of these can be closed by assertion. **Items 1, 2, 3, 5 and
7 were closed by the M0 spike; their results are kept here so the closure is auditable.**

1. ~~**The exact CTC feature contract.**~~ **CLOSED by M0.** The authority turned out to be the
   checkpoint's own `model_config.yaml` (from the `.nemo`), **not** sherpa's config — sherpa feeds
   this model the wrong front-end (§2). Full measured contract in §3.2; verified by tensor
   comparison against a NeMo reference at **max |Δ| ≤ 9.34e-4** over 4 clips (bar: 1e-3).
2. ~~**Whether `Microsoft.ML.Tokenizers` reproduces this checkpoint's BPE exactly.**~~ **CLOSED by
   M0: 89/89 in-scope terms id-for-id** against the SentencePiece oracle (§3.3). No vendored BPE
   fallback needed. Two defects and one vocabulary limit came out of it (§3.3, D12).
3. ~~**CTC frame duration.**~~ **CLOSED by M0: 80.00 ms/frame, measured** (subsampling factor 8, per
   graph metadata) — derived as `audioSeconds / frameCount`, not hardcoded from 8×10 ms. Still
   derive it at runtime rather than pinning the constant: `Detection.StartTime/EndTime` feed
   proportional placement directly (`ApplyFromDetections`'s placement loop,
   `VocabularyGate.cs:412-413`), so an error puts **every** detection on the wrong word.
4. **Exact DP formulation + score normalization. STILL OPEN — this is the piece M0 did not touch.**
   FluidAudio's constants are hearsay (§1) and tuned to their scale.
   *Experiment:* M2 dumps the score distribution over a planted-term corpus and a decoy corpus;
   thresholds are picked from the separation, not copied.
5. ~~**Real post-stop cost on the CPU tier.**~~ **CLOSED by M0, and it is worse than the old budget
   assumed** — 60 s → 1987 ms, 180 s → 9662 ms, super-linear (§3.5). This is what produced D10.
   *Still open:* the **DirectML** numbers and the 90 s / 120 s CPU points — M2 measures both.
6. **Whether proportional placement lands on the right word in long dictations. STILL OPEN.**
   *Experiment:* M2's verification set must include a **long dictation with repeated near-misses**,
   not just planted single terms.
7. ~~**Memory with two encoders resident.**~~ **CLOSED by M0: +283 MB** working set after the first
   `Run`, **276 MB returned on `Dispose`**. Against the RNNT's 690 MB (int4) / 1.236 GB (fp16) that
   is **~20–40 % on top**, not the ~10–20 % this list previously guessed. Dispose-on-disable is
   demonstrated to work; M2 re-verifies it in the shipping wiring, not in a spike harness.
8. **Whether DirectML actually rescues the 60–120 s tier. ~~OPEN~~ → MEASURED, and it does.**
   D10 relaxed the CPU pin on *reasoning*; the M2 spotter build then measured it end-to-end
   (trim + mel + graph + DP, p50 of 5 after warm-up, Debug, RX 5700 XT):

   | trimmed speech | CPU EP | DirectML |
   |---|---|---|
   | 6.4 s | 181 ms | **129 ms** |
   | 38.0 s | 1493 ms | **801 ms** |
   | 58.9 s | 2421 ms | **1038 ms** |
   | 89.5 s | 4177 ms | **1453 ms** |

   2.3× at 59 s, 2.9× at 90 s — so the 60–120 s tier lands **inside** the 2.5 s deadline on a
   DirectML machine. Two caveats before treating this as settled: these are **Debug** numbers (the
   managed mel is 546 ms of the 1038 and will shrink in Release), and one GPU. Session load is
   1022 ms CPU / 1856 ms DML, which is why `Warm()` exists. *Still open:* the CPU-only fallback
   tier is the binding constraint, and §3.5's cutoff is still derived from the CPU column.
9. **Whether the D10b background pass is worth its complexity. NEW, OPEN.** If M2 finds the 60–120 s
   tier is rare in real use (most dictations are short), the simpler answer is "skip above 60 s".
   *Experiment:* M2's real-dictation corpus reports the duration distribution.

---

## 5. Milestones

Estimates are the review's re-estimate (roughly 2× the original optimistic ones), and the exit
criteria are pass/fail, not judgement calls.

**Re-estimate after M0 (D13), stated as deltas so the reasoning survives:**

| Item | Delta | Why |
|---|---|---|
| CTC mel front-end | **−1 day** | written and tensor-verified in the spike; M2 productionizes it |
| Tokenizer | **−0.5 day** | done and verified 89/89; only the artifact plumbing remains |
| **Latency re-architecture (D10)** | **+1 day (new)** | the tier ladder, the 2.5 s deadline, silence trim + timestamp shift-back, the D10b background pass, session warm-up, DirectML routing |
| Model release + installer binding (§3.4) | unchanged | still the hand-enumerated binding layer, now with **two** assets (D11) |
| **Net** | **≈ −0.5 day**, i.e. flat | M0 closed two kill-gate unknowns and opened one engineering problem |

The one genuinely unbuilt piece remains `CtcWordSpotter` — the DP — which the spike deliberately did
not touch.

### M0 — feature-contract + tokenizer spike · **DONE 2026-07-25 — PASSED with caveats (D13)**

Estimated 2–3 days. The spike ran as a set of `[Fact]`s in the test project rather than a throwaway
console app, because the model-dependent ones no-op cleanly without the assets (below) and are
therefore worth keeping.

#### Why D3's transcript oracle was retired — record this, so nobody rebuilds it

The plan was: run the prebuilt sherpa-onnx Windows binary over a hard multi-sentence WAV, check the
transcript in, and require our stack to reproduce it **string-equal**. Two independent findings
killed it:

1. **The target is unachievable.** An **inaudible ±1 LSB (−90 dBFS) dither** moves *sherpa's own*
   transcript as much as our implementation differs from it — on `real-40s`, ours-vs-oracle **11.8 %**
   vs oracle-vs-itself-under-dither **14.7 %**. The model's argmax is chaotic on quiet audio; string
   equality was never on the table.
2. **The target is also insufficient.** A **deliberately sabotaged analysis window (Povey↔Hann)**
   left **every** reference transcript byte-identical. So even a passing string comparison would
   have proved nothing about the feature matrix.

Both failure modes point the same way: the oracle has to be a **tensor** oracle.

#### The replacement oracle, built and passing

`D:\caches\jot-ctc-spike\nemo_ref.py` (Python 3.12.7 + numpy + librosa) reproduces the NeMo
`AudioToMelSpectrogramPreprocessor` from the checkpoint's own `model_config.yaml` and dumps the
feature matrix. **This script is on this machine and is deliberately NOT in the repo** — it is a
dev-time oracle, exactly as the sherpa binary was meant to be.

**Result: max |Δ| ≤ 9.34e-4 across 4 clips**, against a **≤1e-3** bar. The Povey↔Hann sabotage
scores **2.8e-2 – 1.2e-1** on the same comparison — **30–130× worse** — so the oracle demonstrably
has the discrimination the transcript comparison lacked.

#### Exit criteria — as revised, and their outcomes

| # | Criterion | Outcome |
|---|---|---|
| 1 | ~~A multi-sentence WAV is checked in under `tests/Jot.Tests/Fixtures/ctc/`~~ **REVISED: a spike audio set exists on `D:\caches\jot-ctc-spike\audio\`, out of git** | **MET.** See "test assets" below — one clip contains the user's voice, so checking the set in was never acceptable. |
| 2 | ~~Reference transcript captured from the sherpa binary~~ **REPLACED: NeMo reference feature matrices captured** | **MET** via `nemo_ref.py`. |
| 3 | ~~Our transcript is string-equal to the reference~~ **REPLACED: our mel matrix agrees with the NeMo reference to ≤1e-3 max abs** | **MET — 9.34e-4 worst case over 4 clips**, with a sabotage control at 2.8e-2–1.2e-1. |
| 4 | **Tokenizer agreement, id-for-id (D4)** — against a real SentencePiece oracle, not a round-trip | **MET — 89/89 in-scope terms.** Note the oracle changed too: `tokenizer.model` had to come from the `.nemo`, since the sherpa archive has none (D11). |
| 5 | **Timing recorded** on the CPU tier | **MET, and it forced D10.** 10 s 219 ms · 40 s 1139 ms · 60 s 1987 ms · 180 s 9662 ms (p50 of 10, after warm-up). Folded into §3.5. |
| 6 | *(added during the spike)* **Memory on/off measured** | **MET.** +283 MB after first `Run`; 276 MB returned on `Dispose`. |

**Caveats on the pass (D13):** the DP is untouched (§4 item 4), DirectML is unmeasured (§4 item 8),
and the front-end/tokenizer/encoder code is **spike quality** — verified, but not wired, not
disposed by anything, not covered by the app's own lifecycle.

#### Test assets — where they live and why they are not in git

`D:\caches\jot-ctc-spike\` holds the model archives (`model.tar.bz2` 104 MB, `model-fp32.tar.bz2`
434 MB), `parakeet.nemo` (459 MB), `tokenizer.model`, `spm-oracle-ids.json`, `nemo_ref.py`, the
sherpa Windows binary, and `audio\`. **None of it is in git**, for two reasons: size, and the fact
that the discriminating clips are **recordings of the user's own voice** (`real-10s` … `real-180s`,
`real-concat`, the `seg-*` silence-collapse clips).

**The shareable asset is `tts-terms.wav`** — 1,285,166 B, **40.16 s**, 8 sentences of synthesized
speech with rare proper nouns, **no user voice**. That is the one to hand to a second machine, to
CI, or to a future contributor. It is *not* checked in either (40 s of WAV), but it can be.

The tests gate on environment variables (`JOT_CTC_SPIKE_DIR`, `JOT_CTC_SPIKE_AUDIO`,
`JOT_CTC_SPIKE_FEATS`, `JOT_CTC_SPIKE_ORACLE`, `JOT_CTC_SPIKE_SPM`, …) and **no-op when they are
unset**, so **CI needs no download**. Note the mechanism precisely: they *early-return*, so they
report as **passed**, not skipped — a green suite on a machine without the assets is not evidence
the model-dependent facts ran.

**Do not take a runtime dependency on sherpa-onnx.** `Jot.csproj:52-56` already documents that a
duplicate `onnxruntime.dll` breaks MSIX packaging (APPX1101), and sherpa-onnx ships its own. The
binary is a **dev-time provenance/comparison tool only** — and, per §2, **not** a behavioral
reference; nothing in `src/Jot` may reference it. (`Microsoft.ML.Tokenizers` is safe on this count:
fully managed, only `Google.Protobuf` transitively, no native library to collide.)

### M1 — finish the `JotVocabCore` port (**1–2 days remaining**)

**Already done** (see §5b): `VocabularyGate`, `CommonWords`, `CorrectionKey`, `CorrectionRecord`,
`AskPolicy`, `Seams`, 21 embedded lists, 7/7 jot-shared fixtures green.

**Remaining:**
- `CorrectionStore` + `CorrectionProvenance` ports, driven red→green from
  `correction_store_roundtrip.json` and `provenance_verdicts.json`.
- `VocabularyStore` (new file, not a port) + `SanitizeTerm` choke point + `MaxTermWords = 4`.
- **The §3.7 data-lifecycle checklist** — `JotDataPurge`, `DataFolderMigrator`, both test suites.
- The old plan's L1 fuzzy corrector as the no-model fallback.
- **Ships hidden** (literal `Visibility="Collapsed"`; see `vocabulary-ux.md §7.1` for why the
  binding-to-`AdvancedFeatures` variant is *not* hidden).

*Exit criteria:* 7/7 fixtures green; `JotDataPurgeTests` and `DataFolderMigratorTests` both cover
`Vocabulary\`; zero ML risk touched.

### M2 — spotter (**4–5 days**, unchanged: −1.5 from M0's head start, +1 for D10)

`CtcMelFrontend` / `CtcTokenizer` / `CtcTokens` / `CtcEncoder` **productionized from the spike**
(they exist; they are not wired, disposed or lifecycle-managed), plus: `CtcModel` + installer +
two-asset manifest + release tag (D11), the D10 silence trim, `CtcSpotter`, the **`CtcWordSpotter`
DP — the only from-scratch algorithm left**, and the `VocabularyRunner` wire-up with the D6 wrapper
and the D10 ladder.

*Exit criteria:*
- **Term recall** on a planted-term recording set, with the score distribution dumped and thresholds
  derived from it (not copied from FluidAudio's hearsay constants).
- **Zero insertions on silence.**
- **A decoy set comes back byte-identical.**
- **A long dictation with repeated near-misses** places corrections on the right occurrences.
- **The §3.5 ladder holds, per tier** — not "a budget is met". Specifically: the 2.5 s deadline is
  met on ≤ 60 s dictations; the 60–120 s tier degrades **exactly as §3.6's degraded path specifies**
  (raw paste, background pass, provenance against the saved string, no ask, no resurrected pill);
  above 120 s nothing runs. Measured against `SlowStopMs = 5_000`.
- **The DirectML ladder is measured** (10/40/60/90/120/180 s), and §3.5's cutoff is re-derived from
  whichever backend is worse (§4 item 8). Confirm the 120 s cutoff or drop it to 90 s.
- **Silence trim is verified both ways:** the 50 s collapse clip (`seg-*`) decodes after trimming,
  **and** detection timestamps are shifted back by the leading-trim offset so placement is unmoved.
- **D12 validation is enforced at the store choke point** — length-1 rejected, `IsSpottable` false
  surfaced, with a test per class (digit, hyphen, accent, CJK, single char).
- **The D6 tests pass**, on both AutoPaste paths (§3.6): throwing spotter, timing-out spotter and
  non-completing ask deck each deliver the raw transcript to `_store.Add`, and to `PasteAtCursor`
  exactly once when `AutoPaste` is on / not at all when it is off — with the state machine back at
  `Idle` and no card left on screen in every case.
- Session **warm-up** (no 1.0–1.1 s load inside a stop) and **dispose-on-disable** verified by
  working-set measurement in the shipping wiring, not in the spike harness.
- **The D11 attribution card exists on `AboutPage`** naming both artifacts. Licence terms are not
  a polish item.

### M3 — UX (**1–2 days**)

Settings section, `VocabularyPage`, the pill chip, the review surface, the ask card. Split into
M3a/M3b/M3c in `vocabulary-ux.md §13`. Then calibration (~20–30 real dictations, verdict logs),
**then** unhide.

Default **off** until the calibration pass, exactly as the Mac shipped it.

---

## 5b. Scope settled 2026-07-25 — ships as **v1.3**

Owner decisions (details + consequences in [`vocabulary-ux.md`](vocabulary-ux.md) §0a/§0b):
**full faithful port.** Learn loop IN, ask-before-paste IN, dedicated Vocabulary page.

So M1's scope is the whole of `JotVocabCore`: `CorrectionStore`, `AskPolicy` and
`CorrectionProvenance` come over too, and their golden fixtures join the conformance suite.

**Status — stated precisely (the previous revision overstated it).**

| Component | State | Where |
|---|---|---|
| `VocabularyGate` (incl. `ApplyFromDetections`) | **DONE, fixture-green** | `src/Jot/Vocabulary/VocabularyGate.cs` (**54 KB**) |
| `CommonWords` + 21 embedded lists | **DONE** | `Vocabulary/CommonWords.cs`, `Vocabulary/Resources/common-words*.txt`, `Jot.csproj:39` |
| `CorrectionKey`, `CorrectionRecord`, `Seams` | **DONE** | `src/Jot/Vocabulary/` |
| `AskPolicy` | **DONE, conformance-green** | `Vocabulary/AskPolicy.cs` |
| `CorrectionStore`, `CorrectionProvenance` | **DONE, fixture-green** (8 store + 9 provenance cases; purge + folder-move wiring test-verified). No consumers yet. | `Vocabulary/CorrectionStore.cs` (17 KB), `Vocabulary/CorrectionProvenance.cs` (26 KB) |
| `VocabularyStore`, `VocabTerm`/`VocabularyFile` | **NOT STARTED** | — |
| `CtcMelFrontend` | **EXISTS — spike quality, tensor-verified ≤9.34e-4 vs NeMo.** Not wired into any pipeline. | `src/Jot/Transcription/Ctc/CtcMelFrontend.cs` |
| `CtcEncoder` | **EXISTS — spike quality.** Single-graph ONNX wrapper; no lifecycle owner yet. | `src/Jot/Transcription/Ctc/CtcEncoder.cs` |
| `CtcTokens` | **EXISTS — spike quality.** `tokens.txt` reader (1025 lines, blank at 1024). | `src/Jot/Transcription/Ctc/CtcTokens.cs` |
| `CtcGreedyDecoder` | **EXISTS — spike quality.** Spike instrument only; the product never greedy-decodes. | `src/Jot/Transcription/Ctc/CtcGreedyDecoder.cs` |
| `CtcTokenizer` | **EXISTS — spike quality, 89/89 id-for-id.** Carries `IsSpottable`, which D12 depends on. | `src/Jot/Transcription/Ctc/CtcTokenizer.cs` |
| `CtcSpotter`, `CtcModel`/`CtcModelInstaller`, **`CtcWordSpotter` (the DP)**, `VocabularyRunner` | **NOT STARTED** | — |

**Dependency change — exactly one line.** `Jot.csproj` gained
`<PackageReference Include="Microsoft.ML.Tokenizers" Version="2.0.0" />`. Fully managed; only
transitive dep is `Google.Protobuf`; **no native library**, so it cannot recreate the APPX1101
duplicate-`onnxruntime.dll` collision documented at `Jot.csproj:52-56`. Verified in the csproj.

**Test count — a moving number; re-derive it, never quote it from a review or from this line.** The
"316 tests" figure three revisions ago was **jot-shared's Swift suite**, not ours; "163 across 27"
and "175 across 29" were also stale within days.

**Measured by actually running the suite on 2026-07-25: `Total: 386, Passed: 386, Failed: 0`** —
`dotnet test tests\Jot.Tests\Jot.Tests.csproj --logger trx`, counters read from the TRX. The
attribute count on the same tree is **192 `[Fact]`/`[Theory]` across 36 files**; the gap is
`[InlineData]` expansion. The M0 spike added `tests/Jot.Tests/CtcSpikeTests.cs` (~27 KB).

**Caveat that matters when you re-derive it:** the model-dependent spike facts gate on
`JOT_CTC_SPIKE_*` environment variables and **early-return** when unset — so they count as
**passed**, not skipped. 386 green on a machine without `D:\caches\jot-ctc-spike\` does **not** mean
the tensor-parity and timing facts ran. Set the variables to actually exercise them.

```powershell
# attribute count (fast, approximate — misses [InlineData] expansion)
Get-ChildItem tests\Jot.Tests -Recurse -Filter *.cs |
  ForEach-Object { (Select-String $_.FullName -Pattern '^\s*\[(Fact|Theory)').Count } |
  Measure-Object -Sum

# the real number
dotnet test tests\Jot.Tests\Jot.Tests.csproj --logger trx --results-directory <dir>
```

Per the standing "never claim it works without testing it" rule, quote whatever the second one
prints — and do **not** pass `-p:BaseIntermediateOutputPath` to try to keep out of another build's
way: it makes the WPF temp project glob every `obj\**\*.g.cs` and the build dies with CS0111.

**Ask throttling — RESOLVED (owner, 2026-07-25): ship jot-shared's `AskPolicy` unchanged.** The
inert-signal finding stands (pinned by `DetectionPath_ConfidenceSignalsAreInert` in
`VocabGoldenFixtureTests.cs`) and is not a blocker. **But state the guarantee accurately:** of
`AskPolicy`'s four guarantees, only **two are live on our detection path** — "an answered pair is
never asked again" (`AskPolicy.WorthAsking`'s `keyboardSuppressed` check, `AskPolicy.cs:100`) and
"the payload is capped at `MaxAsks = 3`" (declared `:23`, spent at `:111`). "Always-replace stops
consuming budget" is dead (no always-replace ships in v1.3, so `Granted()` — `:82-91` — is always
false) and "merge-teach is one card ever" is dead (`MergeTeachEligible` requires `Shape == "merge"`,
`:70`, always null on our path). Consequences, including what the ask deck can actually contain, are
worked through in `vocabulary-ux.md §0b-RESOLVED` and §4.

**One consequence belongs here, not only in the UX doc.** `AskPolicy.WorthAsking` ends
`return r.Outcome == "applied" || Prior(r) > 0;` (`:103`), and `Prior` reads `OverrideEntry.Net`. A
**common-word** original stays blocked forever regardless of `Net`, because `Decide`'s override
branch requires `!isCommon` (`VocabularyGate.cs:582`). So once anything pushes such a pair to
`Prior > 0` — the review surface's KEPT pick or the right-click "Add to Vocabulary" gesture, both
`+1` — `WorthAsking` returns **true on every later dictation** and never stops. `VocabularyRunner`
therefore **filters the record list to `Outcome == "applied"` before handing it to
`AskPolicy.Select`**. That is app-side composition, not a policy divergence — `AskPolicy` still
ships unchanged. See `vocabulary-ux.md §4.1`.

## 6. Open questions — status

| # | Question | Status |
|---|---|---|
| 1 | English-only v1 acceptable? | **RESOLVED by D5.** v1.3 runs vocabulary only on an explicitly-selected English locale; auto-detect is off with plain copy. The *gate* stays multilingual (21 lists) — the limit is the spotter model. |
| 1b | How do we consume `jot-shared`? | **RESOLVED.** Vendored copy pinned to `5326460`, recorded in the source comments, with the 7 fixtures vendored into `tests/Jot.Tests/Fixtures/vocab/` so drift shows up as a red test. Re-pin deliberately, never silently. |
| 2 | Bundle or download the CTC model? | **RESOLVED: download-on-enable** — and this is now the *cited* Mac behavior, not just our preference (§1). Size is **measured: 131.7 MB ONNX + 252 KB tokenizer ≈ 132 MB**, two assets (D11), not "100–150 MB". |
| 3 | How faithful a port? | **RESOLVED by §5b:** full port, learn loop in. |
| 4 | Multilingual spotter | **v1.4+.** Different checkpoint, own investigation. |
| 5 | Per-recording resolved language | **v1.4 ticket, filed here.** Surface the leading `<xx-YY>` locale token on `ITranscriber` for **both** engines — today `NemotronTranscriber.cs:293` and `NemotronFp16Transcriber.cs:316` both `continue` past it, and the fp16 engine has neither `Tokens` (`NemotronTranscriber.cs:129-131`) nor `Piece` (`:282-284`), so on the GPU tier the detected language is unrecoverable even for diagnostics. `RecordingItem` would also gain a language field. **Shared value:** `TextPipeline` wants this too (`LanguageCode.cs:38` punts on `"auto"` today), so it is not vocabulary-only cost. |
| 6 | CC-BY-4.0 attribution surface | **RESOLVED by D11 — REQUIRED, not optional, and it is an M2 exit criterion.** We redistribute **two** CC-BY-4.0-derived artifacts (`model.int8.onnx` and `tokenizer.model`), because the sherpa archive ships no tokenizer. Home: a new "Models & licences" `ui:Card` on `Views/AboutPage.xaml` after the "Your impact" card — **verified that no attribution section exists there today**. Name the checkpoint, the licence, the link, both files, and the conversion tooling's licence. |
| 7 | *(new)* Terms this model can never spot | **RESOLVED by D12 — surfaced, not hidden.** No digits, hyphen, accented Latin or CJK in the 1024-piece BPE; length-1 terms encode to nothing. `CtcTokenizer.IsSpottable` gates it at the store choke point and the UI says so (`vocabulary-ux.md §2.4`). It also **narrows what the feature may promise** (§3.3). |
| 8 | *(new)* Does the D10b background pass earn its keep? | **OPEN, decided provisionally.** Built in v1.3 because "the term appears in what you paste" is the feature and a silent skip on a 90 s dictation is not acceptable. If M2's duration distribution shows the 60–120 s tier is rare, collapse it to "skip above 60 s" and delete the background path (§4 item 9). |
