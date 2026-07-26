# Custom vocabulary beyond English — checkpoint survey, cost model, and a recommendation

> Status: **RESEARCH ONLY. No code was written, no model was downloaded, nothing under `src/` was
> touched.** Written 2026-07-25. Answers the v1.4 question filed as open question **#4** in
> [`vocabulary-ctc-port.md §6`](vocabulary-ctc-port.md) ("Multilingual spotter — different
> checkpoint, own investigation") and the language-honesty half of
> [`vocabulary-ux.md §6`](vocabulary-ux.md).
>
> **Evidence convention, enforced throughout.**
> **[V]** = I fetched the model card / repo / script / source file and read the claim there; the URL
> is given. **[I]** = inferred (arithmetic, architecture identity, or a second-hand snippet), stated
> as inference and never promoted. **[?]** = could not verify; the experiment that would resolve it
> is named in §10.
>
> **Nothing in this document is a measurement on this machine.** Every latency and memory figure for
> a *new* checkpoint is modelled from the v1.3 measurements in `vocabulary-ctc-port.md §3.5`. §10
> lists what has to be measured before any of it is believed.

---

## 0. Bottom line up front

The multilingual spotter problem is **not** "find a bigger, more multilingual model". It is three
separate problems that the framing "maybe you can use a different model for different language"
correctly separates but which have very different answers:

1. **Is there a checkpoint?** For 11 of Jot's 36 languages, yes — and it is a *drop-in*: NVIDIA's
   `stt_XX_fastconformer_hybrid_large_pc` family is the **same architecture, same size, same mel
   front-end, same 80 ms frame rate, same CC-BY-4.0 licence** as the English model we already ship,
   with a real CTC head and a per-language tokenizer that **does** contain accented Latin and
   Cyrillic. Cost is essentially unchanged. For 17 more languages there is **nothing shippable at
   any price**, and for the rest the only options are 5× too expensive or non-commercially licensed.
2. **Does our gate transfer?** Partly. It transfers cleanly to space-delimited Latin/Cyrillic/Greek.
   It is **architecturally broken for Chinese, Japanese and Thai** — `SplitWords` splits on a literal
   space (`VocabularyGate.cs:1301` **[V]**), so a CJK transcript is one "word" and both plausibility
   and placement collapse. That is a blocker *independent of any model*, and no checkpoint fixes it.
3. **Is the acoustic spotter even the right lever outside English?** We have never measured how much
   the CTC spotter adds over the model-free L1 fuzzy corrector that `vocabulary-plan.md` already
   specifies. That measurement can be made **today, in English, with no new model, in about a day**,
   and it determines whether per-language checkpoints are worth 132 MB × N plus a calibration
   programme we cannot staff.

**Recommendation: run the L1-vs-spotter experiment first (E5), then ship the model-free path in all
21 gate languages, and add per-language CTC checkpoints only for the Tier-A languages, starting with
German and Portuguese because their int8 CTC ONNX already exists.** Full reasoning in §8; costs in
§9.

---

## 1. What a candidate actually has to satisfy

Six hard gates, in the order that kills candidates fastest. These come straight out of the shipped
v1.3 design; none is negotiable without re-opening a settled decision.

| # | Gate | Where it comes from | Kills |
|---|---|---|---|
| **G1** | A **CTC head** (or any head emitting per-frame token log-probs on a monotonic time axis) | The spotter *is* a Viterbi DP over `logprobs[frame][token]`. `vocabulary-ctc-port.md §3.2` | Whisper, Canary, all transducer/TDT-only checkpoints |
| **G2** | **Redistributable licence.** We re-host derived artifacts on our own GitHub release and ship a Store product. CC-BY-4.0 / Apache-2.0 / MIT pass. NC, "research only", and permission-gated fail | D11; `vocabulary-ctc-port.md §2` | MMS, canary-1b, `*_pc_nc`, SenseVoice, TeleSpeech, GigaAM v1 |
| **G3** | **Fits the latency ladder.** 60 s of trimmed speech inside a 2.5 s deadline; 120 s hard cutoff; must not blow `SlowStopMs = 5_000` | `vocabulary-ctc-port.md §3.5` (D10) | every 0.6B checkpoint; every wav2vec2-class 300M+ raw-waveform model |
| **G4** | **Tokenizer contains the user's characters.** A term that encodes to `<unk>` is *silent zero recall* | D12; `CtcTokenizer.IsSpottable` | Korean syllable-truncated vocabs; rare-kanji names |
| **G5** | **ONNX, or a documented export path** — and no runtime `onnxruntime.dll` collision | `Jot.csproj:52-56` (APPX1101) | fairseq2-only models (omniASR) |
| **G6** | **The gate can operate on the language at all** — space-delimited words, a common-word list, a defensible plausibility ceiling | `VocabularyGate.cs:1301`, `CommonWords.cs` | zh, ja, th regardless of model |

G6 is the one that is easy to forget and expensive to discover late. **A perfect Chinese CTC model
buys us nothing today**, because `ApplyFromDetections` cannot place a detection onto a transcript it
cannot split into words.

---

## 2. Candidate comparison table

Sizes are the **int8 ONNX on disk** where a real artifact exists, otherwise an estimate marked [I].
"Cost" is relative to our measured English baseline (114M FastConformer, 8× subsampling, 80 ms
frames, 60 s → **1987 ms** CPU p50 / **~1038 ms** DirectML, **+283 MB** working set).

| Candidate | Langs | Params | CTC head | Licence | int8 size | Vocab / tokenizer | Cost vs baseline | Verdict |
|---|---|---|---|---|---|---|---|---|
| **`nvidia/stt_XX_fastconformer_hybrid_large_pc`** [V] | 18 separate checkpoints (en de es fr it nl pl pt ru ua hr be ka hy uz ar fa kk-ru) | **~115M** | **YES** — *"a hybrid model trained on two losses: Transducer (default) and CTC"* [V] | **CC-BY-4.0** [V] | **131–132 MB** (de, pt artifacts read) [V] | SentencePiece **Unigram**, size varies per language: pt **128**, hr **256**, ua **512**, de/es/ru/nl/ar **1024** [V] | **≈1.0×** [I] — same encoder shape, same front-end, same frame rate | **PRIMARY CANDIDATE** |
| `nvidia/stt_multilingual_fastconformer_hybrid_large_pc` [V, NGC] | 10 (be de en es fr hr it pl ru uk) | 114M | YES (joint RNNT-CTC loss) [V] | **NGC Terms of Use** stated on the NGC page; **not on Hugging Face at all** (HF search returns nothing) [V] → CC-BY-4.0 **unconfirmed** [?] | ~132 MB [I] | **Aggregate** SentencePiece, **256/language, 2560 total** [V]; ids are **namespaced by per-language offset** [V] | ≈1.0× [I] | Viable but licence-blocked and needs offset code — see §8(a) |
| `nvidia/stt_XX_conformer_ctc_large` (en de es fr it ru hr be ca rw eo) [V] | 11 | ~120M | **Pure CTC** (`EncDecCTCModelBPE`) [V] | CC-BY-4.0 [V] | ~130 MB [I] | SentencePiece BPE [V] | **>1.0×** — Conformer, **4× subsampling → 2× the frames** [I] | Fallback only; adds no language Tier A lacks |
| `nvidia/parakeet-tdt-0.6b-v3` [V] | **25 European** | 600M | **NO — "FastConformer-TDT"**, TDT/transducer only [V] | CC-BY-4.0 [V] | — | unified SentencePiece **8192** [V] | — | **DISQUALIFIED on G1.** But see §3.3 — it was *initialized from* an unreleased multilingual CTC checkpoint |
| `nvidia/parakeet-tdt_ctc-0.6b-ja` [V] | ja | 600M | YES [V] | CC-BY-4.0 [V] | **656 MB** (sherpa export, read) [V] | SentencePiece **3072** [V] | **≈5.3×** [I] → 60 s ≈ **10.5 s** CPU | Fails G3 **and** G6 |
| `nvidia/parakeet-ctc-0.6b-Vietnamese` [V] | vi (+en code-switch) | 600M | Pure CTC [V] | **NVIDIA Open Model License** (not CC-BY-4.0) [V] | ~640 MB [I] | not disclosed [?] | ≈5.3× [I] | Fails G3; separate licence review |
| Chinese Citrinet (`csukuangfj/sherpa-onnx-nemo-ctc-zh-citrinet-512`) [V] | zh | ~140M (1024 variant) | YES — *"uses CTC loss/decoding instead of Transducer"* [V] | upstream **CC-BY-4.0** [V] (mirror mis-tags apache-2.0 — the CC-BY obligation still binds) | **40.7 MB** (512) / **147 MB** (1024) [V] | **character-level, ~5k hanzi** [V] | likely **<1.0×** (fully convolutional, no attention) [I] | Model is excellent; **blocked by G6** |
| WenetSpeech-Yue u2pp Conformer CTC [V] | yue + zh + en | 130M | YES (CTC branch usable standalone) [V] | **Apache-2.0** upstream [V] | **135 MB** [V] | character, 85 kB tokens.txt [V] | ~1.0× [I] | Blocked by G6 |
| GigaAM **v3** CTC (Russian) [V] | ru | 220–600M | YES [V] | **MIT** (LICENSE file read) [V] | **225 MB** [V] | **character-level, 34 tokens** [V] | >1.0× [I] | Works, but NeMo `stt_ru` (115M, 132 MB) beats it on every axis |
| GigaAM **v1** export (`…-russian-2024-10-24`) | ru | — | YES | **NON-COMMERCIAL** — sherpa docs say so explicitly [V] | 262 MB | — | — | **TRAP. Do not ship.** |
| `nvidia/stt_ar_fastconformer_hybrid_large_pcd_v1.0` [V] | ar | 115M | YES [V] | CC-BY-4.0 [V] | no ONNX published; ~120 MB after export [I] | SentencePiece Unigram **1024** [V] | ≈1.0× [I] | Tier A, but **output is fully diacritised** — see §6.4 |
| `openai/whisper-*` [V] | 99 | 39M–1.5B | **NO — encoder + attention decoder** [V] | Apache-2.0 / MIT [V] | — | — | — | **DISQUALIFIED on G1.** See §4 |
| `nvidia/canary-*` [V] | 4–25 | 182M–1B | **NO — attention encoder-decoder** [V] | CC-BY-4.0, **except `canary-1b` = CC-BY-NC-4.0** [V] | — | SentencePiece 16384 (v2) [V] | — | **DISQUALIFIED on G1** |
| `facebook/mms-1b-all`, `mms-1b-fl102`, `mms-300m` [V] | 1162 | 300M–1B | YES (wav2vec2 CTC + adapters) | **CC-BY-NC-4.0** [V] | — | — | — | **DISQUALIFIED on G2.** NC derivatives stay NC |
| `facebook/omniASR-CTC-300M` [V] | **1600+** | **325,494,996** [V] | YES (wav2vec2-style enc + CTC head) [V] | **Apache-2.0** [V] | no ONNX; **fairseq2** runtime [V] | 3 tokenizers, type not documented [?] | **≫1.0×** — 2.85× params **and** ~4× the frames (20 ms) [I] | Best licence+coverage in the survey; **fails G3 and G5** |
| `jonatasgrosman/wav2vec2-large-xlsr-53-XX` (16 langs incl. **fi, hu, el**) [V] | 16 | ~317M | YES (`Wav2Vec2ForCTC`) [V] | **Apache-2.0** [V] | ~320 MB [I] | **character-level**, 41 entries for es — *includes hyphen and apostrophe* [V] | **≥11×** [I] — 2.8× params × 4× frames, attention quadratic | Fails G3; also needs a **second front-end** (raw waveform, no mel) |
| `kresnik/wav2vec2-large-xlsr-korean` [V] | ko | ~317M | YES [V] | Apache-2.0 [V] | no ONNX (~315 MB) [I] | **1,205 precomposed Hangul syllables** out of ~2,300 in real text [V] | ≥11× [I] | **Fails G3 and G4.** Korean has no clean answer |
| `FunAudioLLM/SenseVoiceSmall` [V] | zh yue ja ko en | ~234M [I] | YES (SANM enc + CTC) [I] | **bespoke "FunASR Model Open Source License"**, text not even hosted with the model, says *"provided for reference and learning purposes only"*, unilaterally terminable [V] | 239 MB [V] | BPE, 316 kB tokens [V] | ~2× [I] | **DISQUALIFIED on G2.** Technically the best CJK+ko fit — worth one email, not worth shipping |
| `Tele-AI/TeleSpeech-ASR1.0` [V] | zh dialects | 0.3B | YES | **permission-gated commercial use** (written approval required) [V] | 341 MB [V] | 66.6 kB | — | **DISQUALIFIED on G2** |
| `ai4bharat/indic-conformer-600m-multilingual` [V] | **22 Indic** | 600M | YES (*"Hybrid CTC + RNNT"*, `model(wav,"hi","ctc")`) [V] | **MIT** [V] | repo is **gated**, sizes unreadable [?] | unverified [?] | ≈5.3× [I] | Best licence for Indic; fails G3, and a gated repo cannot be scripted in CI |
| `ai4bharat/indicwav2vec-hindi` [V] | hi (+8 siblings) | ~315M | YES [V] | Apache-2.0 [V] | no ONNX (~315 MB) [I] | **character-level ~70 entries** — full Devanagari, zero OOV [I] | ≥11× [I] | Fails G3 |
| `sherpa-onnx-kws-zipformer-*` [V] | zh, en only | **3M** | **NO — transducer** [V] | **no licence statement anywhere** [V] | tiny | BPE | — | Wrong architecture, and silence is not permission |
| icefall / Zipformer-CTC non-English [V] | **zh only** | — | YES | Apache-2.0 tooling | 350 MB (zh) | byte-BPE | — | No de/fr/es checkpoints exist. Training them ourselves is a different project |

---

## 2b. Coverage against Jot's actual 40 locales

Jot ships **40 locales spanning 36 distinct languages** (`NemotronLocales.All` **[V]**). Mapping the
survey onto that list is the number that matters for a roadmap:

| Tier | Meaning | Languages | Locales covered |
|---|---|---|---|
| **Already shipping** | v1.3 English spotter | en | en-US, en-GB (**2**) |
| **A — drop-in** | CC-BY-4.0, ~115M FastConformer hybrid CTC, same cost, same front-end, tokenizer verified to carry the language's accents/script | **de, es, fr, it, nl, pl, pt, ru, uk, hr, ar** (11) | de-DE, es-ES, es-US, fr-FR, fr-CA, it-IT, nl-NL, pl-PL, pt-BR, pt-PT, ru-RU, uk-UA, hr-HR, ar-AR (**14**) |
| **B — exists, but blocked** | a checkpoint exists and is licensable, but fails G3 (cost) or G6 (gate) | ja (0.6B, 656 MB), vi (0.6B, NVIDIA OML), zh (model is great, **gate can't segment**), hi + Indic (0.6B MIT but gated repo) | ja-JP, vi-VN, zh-CN, hi-IN (**4**) |
| **C — heavy fallback only** | community wav2vec2/XLS-R, Apache-2.0 or CC-BY-4.0, character-level (no tokenizer trap) but **≥11× our cost** and a second raw-waveform front-end | fi, hu, el, tr | fi-FI, hu-HU, el-GR, tr-TR (**4**) |
| **D — nothing shippable found** | no CTC checkpoint at any acceptable size/licence | ko, sv, cs, nb, nn, da, bg, sk, ro, et, he, lt, sl, lv, mt, th | 16 locales |

**Read that as: the acoustic spotter can realistically reach 16 of 40 locales (Tier A + English),
and will never reach 20 of them.** The 21 embedded common-word lists already cover **bg, cs, da, de,
el, es, fi, fr, hu, it, nl, pl, pt, ro, ru, sk, sl, sr, sv, uk + en** — i.e. the model-free path in
option (c)/(d1) reaches **more locales than the acoustic path ever will**, including six Tier-D
languages (sv, cs, da, bg, sk, ro) and two Tier-C ones (fi, hu). That asymmetry is the single
strongest argument in §8's recommendation.

Note the two mismatches worth remembering: **Tier A includes Croatian and Arabic, which the gate has
no common-word list for** (`CommonWords.Words(null)` returns empty *silently and intentionally* —
the gate degrades rather than disabling, `CommonWords.cs:49`), so those two would run the spotter
with the **brake absent** — exactly the configuration `vocabulary-ctc-port.md §2` flags as the Mac's
unfixed v3-European behaviour. Do not enable a Tier-A language that has no frequency list until
either the list exists or the ceiling is tightened for it.

---

## 3. The primary candidate, in detail

### 3.1 Why `stt_XX_fastconformer_hybrid_large_pc` is a drop-in and not a port

Every constant our v1.3 spotter hard-codes comes from the FastConformer recipe, and this family is
that recipe. From NeMo's own `examples/asr/conf/fastconformer/fast-conformer_ctc_bpe.yaml` **[V]**:

| Our `CtcMelFrontend` constant (M0-verified) | FastConformer recipe | Match |
|---|---|---|
| 80 mel bins | `features: 80` | ✅ |
| `n_fft` 512 | `n_fft: 512` | ✅ |
| win 400 / hop 160 | `window_size: 0.025` / `window_stride: 0.01` @ 16 kHz | ✅ |
| Hann | `window: "hann"` | ✅ |
| `per_feature` normalization | `normalize: "per_feature"` | ✅ |
| 16 kHz mono | `sample_rate: 16000` | ✅ |
| 8× subsampling → 80.00 ms/frame | `subsampling_factor: 8` | ✅ |
| — | `d_model: 512`, `n_layers: 18`, ~120M params | same class as our 114M |

So `CtcMelFrontend`, `CtcEncoder`, `CtcTokens` and the (still-unbuilt) `CtcWordSpotter` DP are
**reusable unchanged**. The graph contract is the same shape: features in, `logprobs [B, T/8, V]`
out. This is the single most important fact in this document — it is why option (b) costs
engineering days rather than a re-architecture. **[I], with the identity established from the shared
recipe rather than measured on an artifact — E1 in §10 closes it for ~2 hours of work.**

### 3.2 The export path is documented, scripted, and has already been run on this exact family

sherpa-onnx ships `scripts/nemo/fast-conformer-hybrid-transducer-ctc/` **[V]** containing
`export-onnx-ctc-non-streaming.py`, which:

- loads `nemo_asr.models.ASRModel.from_pretrained(...)` (an `EncDecHybridRNNTCTCBPEModel`),
- selects the CTC branch with **`change_decoding_strategy(decoder_type="ctc")`** followed by
  **`set_export_config({"decoder_type": "ctc"})`**,
- writes ONNX metadata keys **`vocab_size`, `normalize_type`, `subsampling_factor`, `model_type`,
  `version`, `model_author`, `url`, `comment`, `doc`** — the same keys our M0 spike reads,
- emits `tokens.txt` from `asr_model.joint.vocabulary` plus a trailing blank,
- quantizes with `quantize_dynamic(..., QuantType.QUInt8)` to `model.int8.onnx`. **[V]**

And `run-ctc-non-streaming-2.sh` **[V]** runs exactly that over
`nvidia/stt_pt_fastconformer_hybrid_large_pc` and `nvidia/stt_de_fastconformer_hybrid_large_pc`. The
resulting artifacts are public:

| Artifact | `model.int8.onnx` | `tokens.txt` | Source |
|---|---|---|---|
| `csukuangfj/sherpa-onnx-nemo-stt_de_fastconformer_hybrid_large_pc-int8` | **132 MB** [V] | **10.7 kB, 1025 lines** [V] | HF |
| `csukuangfj/sherpa-onnx-nemo-stt_pt_fastconformer_hybrid_large_pc-int8` | **131 MB** [V] | **795 B, 129 lines** [V] | HF |

Compare our shipping English artifact: **131,652,171 B** (`vocabulary-ctc-port.md §2`). Same size,
same two files, same layout.

**The `tokenizer.model` chore repeats per language.** Neither export contains a SentencePiece
`tokenizer.model` **[V]** — same gap that forced D11's extraction from the 459 MB `.nemo`. So every
language costs: one `.nemo` download, one extraction, **two** release assets, **two** attribution
lines.

### 3.3 The single-model answer that does not exist (yet)

`nvidia/parakeet-tdt-0.6b-v3`'s card states it *"was initialized from a **CTC multilingual
checkpoint** pretrained on the Granary dataset"* **[V]**. That ancestor — one CTC model covering 25
European languages — would be exactly option (a) done properly. **It is not published**: no
`nvidia/parakeet-ctc-*-v3`, nothing in NVIDIA's HF CTC listing, nothing in the sherpa-onnx
catalogue **[V]**. At 0.6B it would fail G3 anyway, but a distilled 110M version would end this
whole discussion. **Action: one email to NVIDIA / one HF discussion post. Cost: 20 minutes.**

---

## 4. Whisper — the plain verdict, because it will be asked again

Whisper is an **encoder + autoregressive attention decoder** (*"We chose an encoder-decoder
Transformer"*; the model card calls it a *sequence-to-sequence* model) **[V]**. It cannot serve as a
CTC word spotter, and the reason is structural, not a matter of effort:

| | Our CTC head | Whisper cross-attention / DTW alignment |
|---|---|---|
| Matrix contents | calibrated **log P(token \| frame)** over the whole vocabulary | **attention weights** for a token the decoder already chose to emit |
| Softmax axis | over the **vocabulary**, per frame | over **time**, per emitted token |
| Availability | every token, every frame, unconditionally, one forward pass | only for tokens that were actually decoded |

The spotter's question is *"what is the score of a term the transcriber did **not** write?"*
Whisper's attention matrix has **no row for a token it never emitted** — so the question is not
merely hard to answer, it is undefined. WhisperX makes the point for us: it does its forced
alignment with a **separate wav2vec2 CTC model**, not with Whisper's own attention **[V]**.

`WhisperForCTC` does not exist in `transformers` (open feature request only) **[V]**.
CrisperWhisper is CC-BY-NC-4.0 *and* is still DTW-over-cross-attention **[V]**. distil-whisper and
`whisper-large-v3-turbo` are still AED. **Verdict: unusable, at any size, for this purpose.** The
same reasoning disqualifies the entire Canary family.

---

## 5. Cost — does a bigger model blow the budget? Yes, and by how much

Baseline, measured (`vocabulary-ctc-port.md §3.5`): 114M FastConformer, int8, 8× subsampling,
80 ms frames, 60 s of speech → **1987 ms** CPU p50, **~1038 ms** DirectML, **+283 MB** working set,
**131.7 MB** on disk. Budget: **2.5 s deadline**, **120 s hard cutoff**, must fit inside
`SlowStopMs = 5_000` alongside the stop work that already happens.

> *Discrepancy to reconcile: the DirectML figure above comes from the brief.
> `vocabulary-ctc-port.md §4` item 8 still lists the DirectML ladder as **unmeasured and open**. One
> of the two is stale — fix it before quoting either.*

**The two cost drivers are parameters and frames**, and they compose badly because self-attention is
quadratic in frames:

| Candidate class | Param ratio | Frame ratio | Modelled 60 s CPU | Fits the 2.5 s deadline? |
|---|---|---|---|---|
| 115M FastConformer, 8× subsampling (**Tier A**) | 1.0× | 1.0× (80 ms) | **~2.0 s** | **Yes** — identical to today |
| 120M Conformer, 4× subsampling | ~1.05× | **2×** | ~4–6 s [I] | No |
| **0.6B** FastConformer (ja, vi, Indic) | **~5.3×** (d_model 1024 vs 512 = 4× per layer, ×24/18 layers) | 1.0× | **~10.5 s** [I]; **~5.5 s** on DirectML [I] | **No.** Also ≈ 2× `SlowStopMs` |
| 317M wav2vec2 / XLS-R | 2.8× | **4×** (20 ms frames) — linear terms ×4, attention ×16 | **≥22 s** [I] | **No, by an order of magnitude** |
| 325M omniASR-CTC-300M | 2.85× | ~4× | same class as above; also fairseq2-only, ~2 GB VRAM [V] | **No** |

**So: a 0.6B model blows the budget.** Concretely — at ~10.5 s CPU for 60 s of speech, a 0.6B
spotter would miss the 2.5 s deadline on **every dictation longer than about 15 seconds**, meaning
the D10 promise ("the term appears in what you paste") would essentially never hold. The D10b
background tier would still work, but at that point we are shipping a feature whose headline
behaviour never fires. The 120 s cutoff would have to drop to ~30 s, and `vocabulary-ux.md §3.3b`'s
"delete this tier if it is rare" note becomes "delete the feature".

**Memory is the second wall.** The 656 MB Japanese int8 artifact **[V]** implies a working set well
above our measured +283 MB — plausibly +1.2–1.6 GB [I]. Alongside the fp16 RNNT (1.236 GB) that is
a 2.5 GB resident footprint for a dictation app. Tier A's +283 MB is already called out as
"~20–40 % on top" and the reason dispose-on-disable is a requirement rather than hygiene.

**Disk, per-language.** 132 MB per language, downloaded on enable. A user with English + German +
Spanish pays **~396 MB** on top of Nemotron's ~754 MB. That is real but not disqualifying, provided
the UI is honest and old checkpoints are evicted when a language is deselected.

---

## 6. The tokenizer trap, per language — treated as a first-class criterion

This is where a candidate silently produces zero recall, so it gets its own section. The test is:
**does `CtcTokenizer.IsSpottable` return true for the terms a real user of that language would
type?**

### 6.1 What we verified by reading actual token inventories

**German** (`sherpa-onnx-nemo-stt_de_…-int8/tokens.txt`, 1025 lines) **[V]**:

- `ü` = 49, `ä` = 52, `ö` = 88, `ß` = 110 — **accented Latin is present as ordinary tokens.**
- **Uppercase letters are present as individual tokens, ids 527–1019**, including `Ä` and `Ö`.
- **No digits. No hyphen.**

**Portuguese** (`…stt_pt_…-int8/tokens.txt`, **129 lines** — a near-character vocabulary) **[V]**:
the entire alphabet plus `ã á ç é ê í ó õ ú ô â à ü` and their uppercase forms `À Á Â É Í Ó Ô Ú`,
plus `.` `,` `?`. **No digits. No hyphen.**

**English** (our shipping model, M0-verified in `vocabulary-ctc-port.md §3.3`): 1024-piece BPE,
**no digits, no hyphen, no accented Latin, no CJK, and no uppercase** — the model emits lowercase,
unpunctuated text.

### 6.2 Three conclusions, and one is a new trap

1. **D12's "no numbers, no hyphens" copy is universal, not English-specific.** Every NeMo tokenizer
   we inspected lacks both. Good news: the shipped UX copy in `vocabulary-ux.md §2.4` needs no
   per-language variant for those two classes.
2. **D12's "no accented Latin" copy is English-specific and must become conditional.** `café` is
   unspottable in the English model and perfectly spottable in the French/Portuguese/German ones.
   Hard-coding the current message into a shared string would be a lie in 11 languages.
3. **NEW TRAP — case sensitivity.** Our English model is lowercase-only; every `_pc` model emits
   **punctuation and capitalisation**, and has separate uppercase tokens. So `Zürich` and `zürich`
   are **different id sequences**, and the DP searches for exactly what it is given. If we tokenize
   the user's typed casing and the model emitted the other, recall is zero — silently. This is
   exactly the D4/D12 failure mode, in a new place, and it does not exist in the English path we
   validated. **Mitigation: feed both the typed form and a canonically-cased form as feed-time term
   variants** (the `enrichedAliases` mechanism in `vocabulary-ctc-port.md §3.1` is the right hook).
   **Experiment E2.**

### 6.3 Vocabulary size varies wildly, and small is fine — arguably better

pt **128**, hr **256**, ua **512**, de/es/ru/nl/ar **1024** **[V]**. A 128-piece vocabulary is
near-character-level: every letter of the language is a token, so **coverage is total and
`IsSpottable` will essentially never fire**. The cost is longer id sequences per term, which makes
the DP proportionally longer — but the DP is a rounding error next to the encoder (the English mel
front-end is 7 % of the pass; the graph is the other 93 %). Longer, more specific token sequences are
also *more* discriminative, not less. **Small vocabularies are a feature here, not a defect.**

### 6.4 Where the trap actually bites

| Language | Trap | Severity |
|---|---|---|
| **Arabic** | The checkpoint is `_pcd` — it *"transcribes text in Arabic with **diacritical marks**"* **[V]**. Users type undiacritised Arabic. The model's token sequence will carry harakat the user's term does not. | **High — likely near-total recall loss without normalisation.** Needs a strip-harakat pass on both sides, and that changes the DP's target sequence. E9. |
| **Japanese** | 3,072 SentencePiece pieces cannot cover 2,136 jōyō kanji + kana + Latin + multi-char pieces. Rare kanji in **names and place names** — the exact vocabulary use case — will be OOV **[I]**. | High, but moot: ja fails G3 and G6 anyway |
| **Korean** | The only Apache-2.0 CTC option has **1,205 precomposed syllables** of the ~2,300 that occur in real text **[V]**. Arbitrary Korean names cannot be represented. | **Fatal.** A jamo-level vocabulary would be fine; a truncated syllable vocabulary is not |
| **Chinese** | ~5k character vocab is genuinely good **[V]** — rare surname characters are the only gap | Low (but blocked by G6) |
| **German / Finnish / Hungarian** | Tokenizer is fine. The problem is **compounding**, and it lands on the *gate*, not the tokenizer — §7.4 | Medium |
| **All P&C languages** | Casing (§6.2 item 3) | **Medium-high, and new** |

---

## 7. Does the safety gate transfer?

The gate is *already* multilingual in the sense that matters least (21 embedded frequency lists,
**24,000 entries each**, 24,059 for English — counted on disk **[V]**) and *not* multilingual in
three ways that matter more.

### 7.1 The skeleton handles diacritics correctly — this was already fixed

`Skeleton` (`VocabularyGate.cs:1140`) NFC-precomposes, lowercases, and keeps Unicode categories
**L\*, M\* and N\*** as `Rune`s **[V]**. Two consequences:

- For **Latin-1 / Latin Extended** (es, fr, de, pt, it, pl, hr, cs, tr, hu, ro…) every accented
  letter has a precomposed NFC form, so it is **one rune**, and the ceiling behaves exactly as it
  does in English. `café` vs `cafe` = 1/4 = 0.25, comfortably inside 0.45. **Transfers cleanly.**
- For **Cyrillic and Greek** the mapping is 1:1 with Latin behaviour. **Transfers cleanly.**

The already-shipped fix (Rune-based, `M*` included, NFC first) is what makes this analysable at all;
the previous char-based filter dropped combining marks and made the brake looser than intended.

### 7.2 Where the skeleton gets *looser* than intended — scripts without precomposition

Devanagari, Thai and Hebrew vowel points have **no precomposed forms**. NFC leaves them as separate
combining marks, and `IsAlphanumericScalar` deliberately counts them. So a Hindi word's skeleton is
longer than its perceived length, the denominator `max(a.Length, b.Length)` grows, and the same
number of real edits yields a **smaller** normalized distance. Direction of error: **more permissive
→ more overcorrection**, which is the failure mode the gate exists to prevent. **[I]** — the
arithmetic is certain; whether it matters in practice depends on real term/word pairs we do not
have. If Indic or Thai vocabulary is ever pursued, the ceiling needs a per-script value or a
mark-folding skeleton variant.

### 7.3 Where the gate is architecturally broken — CJK and Thai

`private static string[] SplitWords(string s) => s.Split(' ', …)` (`VocabularyGate.cs:1301`) **[V]**.

For Chinese, Japanese and Thai a transcript has no spaces, so:

- the whole transcript is **one "word"**,
- `ApplyFromDetections`'s fractional placement `(i + 0.5) / n` degenerates to a single position,
- `Plausible` compares the term against the **entire transcript**, which will exceed 0.45 for any
  transcript longer than about twice the term,
- the common-word brake, which looks up whole words, never matches anything.

**This is a blocker no checkpoint fixes**, and it is why the excellent 40.7 MB Chinese Citrinet is
listed as unusable. Making CJK work means a segmentation step and a different placement model — a
separate project, not a model swap. **State it in the UX and never partially enable it** (the
`lista → Lisa` precedent applies with more force here, not less).

### 7.4 Agglutination and compounding — the recall hole in de / fi / hu / et

Plausibility is whole-word normalized Levenshtein. When the ASR writes a **compound** containing the
term (`Förderungsantrag` for a term `Förderung`), the distance is roughly
`(compound_len − term_len) / compound_len`, which crosses 0.45 as soon as the compound is about
**1.8×** the term's length. So a term embedded in a compound is **silently un-appliable**. **[I],
arithmetic.**

Note the existing Windows divergence — the contiguous N-word **window** added for
`cloud code → Claude Code` — solves only the **space-separated** case. German, Finnish and Hungarian
compounds have no spaces, so the window never helps. Fixing this needs a prefix/substring-anchored
plausibility variant for compounding languages, or an explicit, documented recall loss.

### 7.5 Is 0.45 defensible outside English? Only where the common-word brake is strong

The load-bearing observation: **the ceiling did not stop `lista → Lisa`; the brake did.**
`skeleton("lista")` vs `skeleton("lisa")` is 1/5 = **0.20**, well inside 0.45. The only thing
between that detection and a corrupted transcript is `Decide` step (4), which requires `lista` to be
in the Spanish frequency list.

So the brake's strength = **how much of running text 24,000 surface forms cover in that language**,
and that number varies enormously:

| Language class | 24k-form coverage of running text | Brake strength | 0.45 ceiling defensible? |
|---|---|---|---|
| Analytic / lightly inflected: **en, nl, es, it, pt, fr, sv, da, nb** | high | strong | **Yes** [I] |
| Moderately inflected: **de, el, ro, bg** | medium-high | adequate | Probably; measure |
| Heavily inflected / agglutinative: **fi, hu, et, lt, lv, tr, sl, sk, cs, pl, ru, uk, hr, sr** | **low** — one Finnish noun has thousands of forms; 24k forms is a small fraction of the surface inventory | **weak** | **No — tighten it, or accept overcorrection** |

Recommendation: make `PlausibilityCeiling` a **per-language value** with 0.45 kept only where the
brake is strong, and a tighter default (0.33 is the value that would have blocked `lista → Lisa` on
its own [I]) elsewhere until measured. **Experiment E6 quantifies which bucket each language is in,
needs no audio and no model, and costs about half a day.** It is the cheapest safety work available
and it should be done regardless of which architecture option is chosen.

### 7.6 What already transfers, unchanged

Learned overrides (step 0), the multi-word self-gate (step 2), `CorrectionStore`, `AskPolicy`,
`CorrectionProvenance`, the D6 never-break-a-dictation wrapper, the D7 storage layout. None of these
is language-aware. The 7 jot-shared golden fixtures stay valid.

---

## 8. Architecture options

### (a) One multilingual CTC model for everything

**The only candidate at our size class is `stt_multilingual_fastconformer_hybrid_large_pc`** — 114M,
joint RNNT-CTC, 10 languages (be de en es fr hr it pl ru uk), and sherpa-onnx has already exported
it (`sherpa-onnx-nemo-fast-conformer-ctc-be-de-en-es-fr-hr-it-pl-ru-uk-20k-int8`) **[V]**.

| For | Against |
|---|---|
| One artifact, one download, one manifest, one attribution line | **Licence unconfirmed.** It is **not on Hugging Face at all** — NGC-only, and the NGC page states only "NGC Terms of Use" **[V]**. Every per-language sibling is explicitly CC-BY-4.0; this one we cannot quote |
| Same cost as today (114M, 8× subsampling) | **Aggregate tokenizer**: 256 pieces per language, ids **namespaced by per-language offset** **[V]**. We must ship each language's sub-tokenizer *and* its offset, and encode terms into the right id range. That is genuinely new code the per-language path does not need |
| No language-switch download UX | Covers **10 of Jot's 36 languages** — it does not solve the problem, it solves a fifth of it |
| | Its English is a 256-piece aggregate sub-vocab; our dedicated English model has 1024 BPE pieces. Swapping would almost certainly **regress the one language that works today** [I] |

**Verdict: no.** The genuinely universal options — omniASR (1600+ languages, Apache-2.0) and MMS —
fail on cost and licence respectively. There is no one-model answer available in 2026-07.

### (b) Per-language checkpoints, downloaded on demand — owner-sanctioned

**What it costs, honestly:**

- **Download UX.** A second "Vocabulary model" row that changes meaning when the user changes
  dictation language. New states nobody has designed: *the user has German downloaded and switches
  to Spanish* (offer? auto-download? silently disable?), *the user has three languages* (a list, not
  a row), *disk pressure* (evict on deselect, or accumulate?). `SettingsPage.xaml:218-226` is
  **copy-pasted markup, not an `ItemsControl`** — this option forces that refactor, which is a good
  thing (it also fixes the live `_gpuDownload` refresh bug at `SettingsViewModel.cs:473`).
- **Disk.** 132 MB per language. Three languages ≈ 396 MB.
- **Code.** `ModelDownload`'s ctor is bound to a concrete installer and the binding is hand-enumerated
  per model (`vocabulary-ctc-port.md §3.4`). N languages makes hand-enumeration untenable — this must
  become **table-driven**, which is 2 days of the estimate and pays for itself.
- **Release engineering.** Per language: extract `tokenizer.model` from a ~460 MB `.nemo`, run the
  sherpa export script, publish **2** assets, read back SHA-256 + exact byte size, add a manifest,
  update `AssetManifestTests.cs`'s exact-count assertions.
- **Attribution.** D11's About card must name **2 × N** artifacts, not 2.
- **Calibration — the real cost.** M2's exit criteria (recall on planted terms, zero insertions on
  silence, decoy set byte-identical, thresholds derived from a measured score distribution) are
  **per language**, and we have no native-speaker audio for any of them. This is the item most likely
  to be underestimated.

**What it buys:** the 11 Tier-A languages get the *same* feature English gets, at the *same* cost,
under a licence we already handle.

### (c) English-only forever; everything else served by the model-free L1 corrector

`vocabulary-plan.md`'s **L1** is already fully specified and needs **no model, no download, no
licence, and no GPU**: NFC + casefold matching that keeps diacritics, punctuation-stripped candidate
windows, digit↔word normalisation, word n-grams up to term-length + 1, character-length windows for
spelled-out acronyms, acceptance at Damerau-Levenshtein ≤ ⌈len/5⌉ with exact match required for
len ≤ 3, blocked on the active locale's common-word list, inflection preserved on splice, and
explicitly **skipped for CJK**.

**What L1 catches:**

- The near-miss band — roughly normalized distance ≤ 0.2, i.e. `Nemotron` written as `Nemotron`
  with one or two wrong letters.
- **Merge and split**: `nemo tron → Nemotron` and `Ramanathan → Ramaa Nathan` fall out of the n-gram
  windows plus whitespace collapse, at distance ≈ 0.
- Casing-only fixes for mixed/all-caps terms.
- Spelled-out acronyms (`W A S A P I → WASAPI`).
- **Every language with a common-word list — all 21 — with no new artifact.**

**What L1 cannot catch, and this is the honest limit:**

- The far-miss band the spotter exists for. Our own measured example: the spotter found `Parakeet`
  while the engine had written **`Herrakit`** — skeleton gap **0.50**. Damerau-Levenshtein
  `herrakit → parakeet` is 4 edits on a length-8 term against a ⌈8/5⌉ = 2 threshold, so **L1 rejects
  it**. That is precisely the case a purely textual corrector must refuse, because accepting it
  would also accept a hundred wrong things.
- Anything where the engine wrote a real common word (`lista` for `Lisa`) — but note the **spotter
  cannot apply that either**, because the brake blocks it. No loss.
- Terms the engine dropped entirely (no anchor text to correct).

**So the honest statement is: L1 covers the near band and the merge/split band in all 21 languages
for free; the CTC spotter's entire marginal value is the far band.** *We have never measured how big
the far band is.* That is E5 in §10 and it is the most decision-relevant experiment in this document.

### (d) Options the brief did not name

- **(d1) Alias-first, model-free — the cheapest multilingual vocabulary we can ship.** The
  right-click **"Add to Vocabulary…"** gesture (`vocabulary-ux.md §2.7`) captures the *exact string
  the engine wrote*. That converts a fuzzy acoustic problem into an **exact string replacement**,
  which needs no model, no threshold, no calibration, and works in **all 40 locales including CJK**
  (exact replacement does not need word boundaries). Combined with L1 it makes vocabulary a real
  feature everywhere, and it is already ~90 % designed. **This should ship before any second
  checkpoint is downloaded.**
- **(d2) Run the *English* spotter on non-English dictations, on the theory that product names are
  English-ish. Reject — and record why**, so it is not re-proposed: the acoustic model is trained on
  English phonotactics, the transcript context is not English, and the `lista → Lisa` incident is
  exactly this failure mode with the safety net removed. Partial enablement is worse than off.
- **(d3) Pilot on the two languages whose artifacts already exist.** German and Portuguese have
  published int8 CTC exports **[V]**. Wiring those two costs no release engineering at all and
  produces the calibration data that tells us whether option (b) generalises. If the pilot fails, we
  spent days, not weeks.
- **(d4) Ask NVIDIA for the Granary multilingual CTC checkpoint** (§3.3). 20 minutes; potentially
  ends the whole discussion.
- **(d5) Decouple "vocabulary works" from "the spotter runs."** Today D5 makes vocabulary *entirely*
  off outside English. With L1 + aliases, the honest gate becomes three-state: **acoustic + textual**
  (English, later Tier A) / **textual only** (the other 18 gate languages) / **off** (CJK, Thai,
  auto-detect). That is a strictly better product than today at zero model cost.

### Recommendation

**Sequence, not a single choice: (c)+(d1) as the universal floor, then (b) for Tier A, gated on one
experiment.**

1. **Run E5 first.** Measure the CTC spotter's marginal recall over L1 alone, in English, on the M2
   corpus. ~1 day, no new artifact. If the margin is small, **stop** — ship (c)+(d1) everywhere and
   close this line of work permanently.
2. **Ship (c)+(d1) in all 21 gate languages** regardless of E5's outcome. It is the largest
   product improvement per engineering day available here, and it makes `vocabulary-ux.md §6`'s
   language-honesty copy something better than an apology.
3. **Then (b), Tier A only, starting with de + pt** (d3). Never (a). Never CJK/Thai. Never a 0.6B
   checkpoint. Never an NC licence.

---

## 9. What a v1.4 realistically costs

Assumes v1.3 has shipped (the DP, the installer, the UX all exist). Estimates are engineering days,
in the same "roughly 2× the optimistic number" spirit as `vocabulary-ctc-port.md §5`.

| Phase | Work | Days |
|---|---|---|
| **0** | **E5** (L1-vs-spotter marginal recall) + **E6** (common-word coverage by language) + **E1** (read the de export's ONNX metadata; confirm the contract) | **2** |
| **1a** | Build L1 (`Services/VocabularyCorrector`) from `vocabulary-plan.md`'s spec, with the store choke point, common-word brake and CJK skip | 3 |
| **1b** | Three-state language gate (d5), per-language `PlausibilityCeiling` from E6, UX copy rewrite across `§2.4 / §2.7 / §6`, alias-first flow polish (d1) | 2 |
| **2a** | Table-driven model registry: convert `ModelDownload` / `SettingsViewModel` / `SettingsPage.xaml`'s duplicated rows to a collection; fix the `_gpuDownload` refresh bug; update `AssetManifestTests` | 2 |
| **2b** | Per-language routing in `VocabularyRunner`: model selection, session cache, dispose-on-language-change, casing variants (E2), warm-up per language | 1.5 |
| **2c** | Release pipeline for the **first** language: `.nemo` → CTC ONNX → int8 → tokens.txt → extract `tokenizer.model` → publish 2 assets → SHA/size → manifest → D11 attribution | 2 |
| **2d** | Calibration for the first language (score distribution, thresholds, decoy set, planted-term recall) — **assumes audio exists** | 1.5 |
| | **Subtotal: first non-English acoustic language, end to end** | **~14** |
| **+** | Each **additional** Tier-A language: release pipeline ~0.5 + calibration ~1 + copy/QA ~0.5 | **~2 each** |

**Two costs that are not days and will bite anyway:**

- **Audio we do not have.** Every calibration exit criterion needs native-speaker dictations with
  planted terms. The M0 shareable asset (`tts-terms.wav`, 40 s, synthesized) is English. Synthesized
  audio may be acceptable for recall smoke-testing but is *not* acceptable for threshold calibration
  — thresholds tuned on TTS will not transfer to real microphones. Budget procurement, or accept
  shipping each language behind an "Experimental" badge with default-off and no calibration claim.
- **Licence surface growth.** 2 artifacts × N languages on the About card, plus a per-language
  `_nc` check every single time (see §11 trap 1).

**Realistic v1.4 shape:** Phase 0 + Phase 1 = **~7 days** and delivers multilingual vocabulary to 21
languages. Adding German and Portuguese acoustics on top = **~+9 days**. Everything else is
incremental at ~2 days per language.

---

## 10. Open questions and the experiments that close them

| # | Question | Experiment | Cost | Kill-gate? |
|---|---|---|---|---|
| **E1** | Does the hybrid CTC export match our graph contract — input `[B,80,T]`, `length` int64, `logprobs [B,T/8,V]` already log-softmaxed, metadata `normalize_type=per_feature` / `subsampling_factor=8`? | Download `sherpa-onnx-nemo-stt_de_…-int8` (132 MB), read ONNX metadata, run one clip through the existing `CtcEncoder` + `CtcGreedyDecoder` | **~2 h** | **YES.** Cheapest, highest-value item in the list |
| **E2** | **Casing.** P&C models emit mixed case; our English model does not. Does a term have to be tokenized in the cased form the model emits? | Encode `Zürich` / `zürich` against the de tokenizer, compare id sequences; run the DP both ways on one German clip | ~0.5 d | Yes — silent zero recall if wrong |
| **E3** | Is the front-end **identical** for every per-language checkpoint, or does some language differ? | Extract `model_config.yaml` from each `.nemo`, diff the preprocessor block against `CtcMelFrontend`'s constants; **and** assert `normalize_type` from ONNX metadata at load time | ~1 h/language, scriptable | Yes, per language |
| **E4** | Real latency and memory parity for a Tier-A checkpoint | Re-run the M0 ladder (10/40/60/90/120/180 s) on the de export, CPU **and** DirectML | ~0.5 d | Confirms §5's central claim |
| **E5** | **How much does the acoustic spotter add over L1 alone?** | On the M2 English corpus, run (i) L1 only, (ii) spotter+gate only, (iii) both; report recall, false-apply rate, and the score/distance distribution of the terms only the spotter caught | **~1 d** | **YES — this decides the whole programme** |
| **E6** | Is the 0.45 ceiling defensible per language? Equivalently: what fraction of running text do our 24k lists cover in fi/hu/et/tr vs en/es? | Tokenise a public corpus per language, compute list-hit rate; bucket languages; propose per-language ceilings | ~0.5 d, **no audio, no model** | No, but it is the cheapest safety work available |
| **E7** | Does the compound problem (§7.4) actually cost recall in de/fi/hu? | Offline: take a term list and a compound word list, compute how often the whole-word ceiling blocks a valid embedding | ~0.5 d | No |
| **E8** | Arabic: does the `_pcd` diacritised output make undiacritised user terms unspottable? | Export the ar checkpoint, encode a diacritised vs undiacritised name, inspect the DP score on real audio | ~1 d + audio | Yes, for Arabic only |
| **E9** | Is the multilingual NGC checkpoint actually CC-BY-4.0? | Read the NGC model card's licence section end-to-end; if ambiguous, ask NVIDIA | ~1 h | Only if option (a) is revived |
| **E10** | Does NVIDIA's unreleased Granary multilingual CTC checkpoint exist in a shippable form? | One HF discussion post / email | ~20 min | Could end the discussion |
| **E11** | Are per-language calibration thresholds transferable, or must each language be tuned? | Compare the score distributions of two Tier-A languages on comparable planted-term sets | ~1 d + audio | Determines the per-language cost in §9 |

**Explicitly unanswerable without an experiment** — do not let anyone assert these: whether the
hybrid CTC branch is as *accurate* as our dedicated English CTC model (the CTC head of a
jointly-trained hybrid is generally the weaker of its two heads), and whether the 2.5 s deadline
holds for a language whose tokenizer produces 3× longer id sequences (pt at 128 pieces). Both are
covered by E4 + E11.

---

## 11. Traps to write down before anyone starts

1. **`_nc` means non-commercial.** `nvidia/stt_es_fastconformer_hybrid_large_pc_nc` is
   **CC BY-NC 4.0** — *"The model weights are distributed under a research-friendly non-commercial
   CC BY-NC 4.0 license"* **[V]** — sitting one character away from the CC-BY-4.0 model we want.
   **Check the licence tag on every single checkpoint, every time.** Same trap: `canary-1b` is NC
   while the rest of the Canary family is CC-BY-4.0 **[V]**.
2. **GigaAM v1's sherpa export is explicitly non-commercial** and the sherpa docs say so **[V]**;
   v2/v3 are MIT. The artifact names differ only by date (`…-russian-2024-10-24` is the NC one).
3. **Downstream mirrors relabel licences.** csukuangfj's Citrinet mirrors carry an `apache-2.0` tag
   over CC-BY-4.0 NVIDIA weights **[V]**. A downstream tag cannot relax an upstream obligation — the
   CC-BY attribution still binds us.
4. **No sherpa export contains a SentencePiece `tokenizer.model`** — verified for de and pt **[V]**,
   same as the English gap D11 already documents. Every language repeats the `.nemo` extraction.
5. **`tokens.txt` can *decode* but never *encode*** (no merge scores). D4's whole argument repeats
   per language.
6. **Never take a runtime dependency on sherpa-onnx** — `Jot.csproj:52-56` (APPX1101, duplicate
   `onnxruntime.dll`). Its export scripts are a build-time tool only.
7. **`sherpa-onnx-kws-*` is a transducer, and its weights carry no licence statement at all** **[V]**.
   It is not a shortcut.

---

## 12. Bottom line

The multilingual spotter is available, cheap, and narrower than it looks: NVIDIA's
`stt_XX_fastconformer_hybrid_large_pc` family gives us a **real CTC head, ~115M parameters, CC-BY-4.0,
131–132 MB int8, and the exact same 80-bin `per_feature` mel front-end, 8× subsampling and 80 ms
frame rate our v1.3 code already implements** — for 11 of Jot's 36 languages, at essentially zero
change to the measured 1987 ms / +283 MB budget, with a scripted export path that has already been
run on two of them (German and Portuguese int8 artifacts are public today). Everything larger fails:
a 0.6B checkpoint is ~5.3× the compute (≈10.5 s for 60 s of speech) and would miss the 2.5 s deadline
on any dictation over ~15 s, wav2vec2/omniASR are an order of magnitude worse because they add 4×
the frames on top of 2.8× the parameters, MMS and SenseVoice and TeleSpeech are licence-disqualified,
and Whisper and Canary cannot do this at all because an attention decoder has no row in its matrix
for a token it never emitted. But the checkpoint is not the binding constraint — **our own gate is**:
`SplitWords` splits on a space, so Chinese, Japanese and Thai are architecturally out no matter what
model we download, and the 0.45 plausibility ceiling is only as safe as the common-word brake behind
it, which is materially weaker in Finnish, Hungarian, Turkish and the Slavic languages than in
English. So the recommendation is a sequence, not a purchase: **spend one day measuring how much the
acoustic spotter actually adds over the model-free L1 corrector we already designed (E5), then ship
L1 plus the alias-capture gesture to all 21 gate languages — which needs no model, no download and no
new licence and turns "vocabulary is English-only" into "vocabulary works everywhere, acoustically
assisted in English" — and only then add per-language CTC checkpoints for the Tier-A languages,
piloting on German and Portuguese because their artifacts already exist.** First non-English acoustic
language is ~14 engineering days end to end and ~2 per language after that, plus native-speaker
calibration audio we do not currently have; the model-free multilingual path is ~7 days and helps
twice as many users.
