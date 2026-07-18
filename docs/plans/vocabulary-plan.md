# Vocabulary (custom terms) — scope & design

Status: v2 (2026-07-17) — revised after a 3-reviewer loop (ASR-algorithm, codebase-fit,
product/scope). Round-2 verification pending at the end of this doc's history. Not yet implemented.
Related: `fixit-worklist.md` (B1), `store-submission.md`.

## Context

Settings has a hidden "Vocabulary" panel (SettingsPage.xaml): terms are in-memory only — never
persisted, never applied. The engine is Nemotron 3.5 ASR streaming 0.6B (int4 ONNX): cache-aware
FastConformer encoder + **vanilla RNNT** decoded by our own C# greedy loop
(`NemotronTranscriber.ProcessChunk`: joint logits → argmax → advance LSTM on non-blank; joint
output width 13 088 = 13 087 SentencePiece pieces + blank as the LAST id; no duration head —
**revisit L2 if a future export switches to TDT**). The SP vocab is shared across all 40 locales.

⚠️ Ground truth on "CTC": the official model card (`nvidia/nemotron-3.5-asr-streaming-0.6b`)
says **"FastConformer-CacheAware-RNNT with Prompt" — RNNT-only, no hybrid CTC head**. Third-party
posts claiming CTC decoding are wrong. This demotes the CTC-WS idea (L3) to a
verify-first spike with a likely-negative outcome and a fallback (below).

Prior art: sherpa-onnx "hotwords" (Aho-Corasick + boost, requires beam search — which is why our
greedy-loop variant must stay conservative), NeMo CTC-WS (arXiv 2406.07096).

## Goals

1. Typed custom terms measurably increase exact recognition (spelling; casing where stated below).
2. Coverage: **verified for space-delimited scripts** (Latin, Cyrillic, Greek, …). Token machinery
   is script-agnostic, but CJK effectiveness is UNKNOWN in v1 and is not claimed — see L2 notes.
3. Zero perceptible latency on the streaming path.
4. A term can never make transcription worse elsewhere (bounded boosts, insertion guards,
   common-word protection, guard suite below).
5. Persisted; UI unhidden only after end-to-end verification (house rule).

## Non-goals (v1)

No model fine-tuning; no pronunciation/phoneme entry (⇒ UI hints that "@handle", digit-terms like
"GPT-5", and spoken-letter acronyms may not fire — partial mitigations in L1); no per-term boost
sliders (schema reserves a weight field, not surfaced); no semantic expansion.

## Design — three layers

### L1 — Post-decode corrector (ships first, hidden)

A small injectable service (`Services/VocabularyCorrector`) applied to **final** transcripts only:

- Call sites: `RecorderController.StopAndDeliverAsync` — **AFTER `MaybeCleanupAsync`** (the LLM
  cleanup replaces the text and would undo corrections; running before as well is optional but the
  post-cleanup pass is the one that must exist), `RecordingDetailViewModel.ReTranscribe`, and
  `MediaImporter` (imported-file transcripts).
- Matching: Unicode NFC + simple casefold on both sides (keep diacritics — "José" must not match
  "Jose" at distance 0); strip leading/trailing punctuation from each candidate window and restore
  it after replacement; digit↔word normalization ("5"↔"five"); candidate windows are word n-grams
  up to term word count + 1, and additionally **character-length-based windows for all-caps/short
  terms** so spelled-out acronyms ("W A S A P I" → "WASAPI") can match after whitespace collapse.
- Accept when Damerau-Levenshtein ≤ ⌈len/5⌉ (exact required for len ≤ 3) AND no other term is
  closer AND **the source window is not a common word of the active locale** (small bundled
  frequency list; protects "neutron" from a "Nemotron" rewrite — DL=2!). Frequency lists ship for
  EN + the verify-suite locales in v1; locales without a list run **exact-match-only** (no fuzzy),
  so the safety guard never silently vanishes.
- Casing-only correction ONLY for terms with non-trivial casing (mixed/all-caps) or len ≥ 4, never
  for sentence-initial capitalization mismatches, and suppressed when the term casefolds to a
  common word ("Jot" must not rewrite "jot down a note").
- Preserve trailing inflection from the source span (`'s`, `s'`, plural s) when splicing.
- Multi-word phrases ARE supported (windows above cover them).
- Scope: space-delimited scripts only; skipped for CJK (no word boundaries).
- The corrected final text is also pushed to the pill via the existing `TranscriptReady` path so
  the last visible caption matches what is pasted.

### L2 — Decode-time shallow-fusion boosting (the core; conservative by design)

Char-trie over normalized terms; greedy loop maintains active matches; boost continuation tokens.
Safety rules from review (these are the design, not options):

- **Never flip blank**: boost applies to token t only if unboosted logit(t) > unboosted
  logit(blank). Blank remains the sole loop exit at its natural score — no hallucinated
  insertions from silence.
- **Margin gate on opening**: a new match may only open (and receive boost) when the candidate
  word-initial piece is within margin δ of the unboosted argmax — boosts decide near-ties, never
  drag the path from far behind (mitigates partial-term corruption; greedy cannot rewind).
- **Repetition guard**: same-token/same-frame emission guard in the ≤10-symbols inner loop.
- λ initial ≈ 1.0–2.0 logits, tuned against the paired guard (below); note λ=2.0 ≈ 7.4× posterior
  ratio — err low.
- Matching mechanics: normalize pieces and terms through the SAME function; advance the trie by
  the piece's **normalized string** (never raw char counts — casefold is not length-preserving).
  A piece whose normalized text passes THROUGH a leaf and continues (term "Nemo", piece
  "▁Nemotron") counts as completing the term at the leaf; the remainder is ordinary text. A final
  piece that covers the term tail plus trailing text (e.g. glued "'s") is accepted when the
  remaining trie path is a prefix of the piece.
- Openers: pieces starting a word (`▁`-prefixed) OR following any non-letter character (covers
  "anti-Nemotron", "(WASAPI"). For terms in non-spacing scripts (CJK), any position may open a
  match at the root — flagged EXPERIMENTAL, unverified, not claimed in user-facing copy.
- `<…>` special/lang-tag pieces are **skipped without dropping the active match** (the decoder
  emits them mid-stream under lang auto-detect; Detokenize already strips them).
- Concurrency: the trie is **immutable**; `SetVocabulary` publishes a new instance via a volatile
  reference; each `Session` snapshots the reference in its ctor (the fp16 transcriber's `_langMask`
  snapshot is the precedent — NOT the int4 `_langId` bare-field pattern, which is itself a latent
  mid-utterance race worth fixing while there). Lazy per-node piece caches are safe because all
  decode serializes under `_inferenceGate`; per-step cost is O(active matches ≤ ~3) — noise next
  to an ONNX joint run (codebase review confirmed the budget).
- The greedy loop records the **emitting encoder-frame index per token** from M2 onward (trivial
  now, prerequisite for any future spotter/splicer).

### L3 — "CTC bundle" (spike, verify-first, likely NO-GO as originally imagined)

Step 0 (before any other work): confirm whether the released checkpoint contains CTC decoder
weights. The model card says RNNT-only, so the expected outcome is **no**. If no:
- Fallback A (preferred v2 direction): optional **small beam** (k = 2–4) with sherpa-onnx-style
  Aho-Corasick hotword boosting — the textbook home for shallow fusion; costs k× joint/decoder
  runs only while vocab is non-empty; still realtime within budget.
- Fallback B (rejected for now): a separate tiny CTC model as spotter — a second model download
  and a second inference path for one feature; not worth it.
If CTC weights DO surface (future checkpoint): export head, CTC-WS spotter, splice using the
recorded per-token frame indices **with an RNNT emission-lag offset calibration step** (RNNT emits
late vs frame-synchronous CTC; NeMo applies exactly such an offset).

## Persistence & plumbing

- `Services/VocabularyStore.cs` — pattern copied from `PromptCatalog` (ctor Load, best-effort
  Save) + `Changed` event; file at **`<DataDir>\vocabulary.json`** (deliberate choice, differs
  from prompts.json's %LOCALAPPDATA% — vocab is user data and belongs with recordings/library).
  Limits: ≤200 terms, 2–64 chars, case-insensitive dedupe.
- **Wiring lives in `App.OnStartup`** next to the existing `ApplyLanguage` call (static
  `ApplyVocabulary(ITranscriber, terms)` mirror) — NOT in SettingsViewModel, which is lazily
  constructed only when Settings opens.
- `OnEraseData` gains `TryDeleteFile(vocabulary.json)`; "Reset settings" keeps vocab (it is user
  data, like recordings) — stated here so it's a decision, not an accident.
- `AddVocabTerm` splits pasted multi-line/comma/semicolon input into terms ("added N, skipped M")
  — bulk import for ~30 min of work.
- Feedback loop: L1 replacements + L2 completed matches recorded as per-recording metadata;
  History detail shows "Vocabulary applied: …". Doubles as field diagnostics.

## Verification (before the UI unhides)

1. Unit tests: trie (metaspace boundaries, leaf-overshoot, opener rules, normalization), corrector
   (diacritics, casing rules, punctuation restore, inflection preserve, common-word protection,
   acronym windows), CJK skip.
2. `--vocabtest <wav> <terms.txt>` dev hook: before/after + which layer fired.
3. **Paired guard** (replaces the v1 "byte-identical" gate, which review showed forces λ→useless):
   (a) decoy-vocab WER delta ≤ +0.1 % absolute on vocab-free clips, measured **through L1 too**;
   (b) planted-term recall above target on A/B clips; (c) zero insertions on silence/noise clips;
   (d) substitution check on phonetically-near negatives ("memo", "neutron").
4. Multilingual without fake accents: **neural TTS (Windows/Edge hi-IN, ja-JP voices) routed
   through a virtual audio cable** selected as Jot's mic — exercises the full real pipeline
   (WASAPI capture → streaming → pill → L1/L2 → paste). FLEURS/Common Voice clips via
   `--vocabtest` for real speech. Human live-mic step: EN only.
5. Then unhide (AdvancedFeatures-gated, "Experimental" badge for one release; plan to promote out
   of Advanced later — custom dictionaries are a mainstream dictation feature).

## Milestones

- **M1** (~1 day): VocabularyStore + App wiring + bulk paste import + L1 corrector service (all
  call sites, post-cleanup ordering) + erase/reset handling + unit tests + `--vocabtest`. Hidden.
- **M2** (~2 days): L2 trie boosting in both transcribers (immutable snapshot), frame-index
  recording, λ tuning against the paired guard, feedback metadata, pill final-caption push,
  TTS-cable multilingual verify → **unhide UI**.
- **M3** (timeboxed ½ day): CTC-weights existence check (expect NO) → if no, file Fallback A
  (small-beam hotwords) as the v2 backlog item and close; if yes, spike per L3 prereqs.

## Resolved questions (from v1 review)

- Partials: corrections are **display-only** on the committed prefix at most; decode-state
  correction happens only through L2's in-loop boosts. Final pill caption gets the corrected text.
- λ: single global, tuned by the paired guard; per-term weight is schema-reserved only.
- Lang-tag tokens: skip without dropping matches (above).
- Joint output is raw logits pre-softmax (argmax space) — boosts add in the same space.
