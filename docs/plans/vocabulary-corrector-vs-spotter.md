# E5 — how much does the acoustic spotter add over a model-free corrector?

> Status: **MEASURED and SHIPPED.** Run 2026-07-25 on this machine against the shipping v1.2.2
> pipeline. Answers experiment **E5** in
> [`vocabulary-multilingual-research.md §10`](vocabulary-multilingual-research.md), which that
> document calls "the most decision-relevant experiment" and the gate on the whole multilingual
> programme.
>
> Everything below is a number produced on this box. Nothing is modelled, inferred or quoted.
> The harness is `tests/Jot.Tests/Vocabulary/VocabEvalHarness.cs` (off unless `JOT_VOCAB_EVAL` names a
> stage); the raw rows are on `D:\caches\jot-vocab-eval\out\`, never in git.

---

## 0. Answer

**The spotter's marginal value is real and it is bigger than the research doc assumed — but so is the
corrector's, and they live in different places.** On 1041 clips of real human speech:

| | recovers terms the engine got wrong | false applies / 1000 words |
|---|---|---|
| baseline (no vocabulary) | 0 % | 0 |
| **model-free corrector** | **36.9 %** | 0.93 |
| **CTC spotter** (132 MB, English) | **44.4 %** | 0.71 |
| both | 49.5 % | 1.24 |

*(155-term stress list. On a realistic 25-term list: corrector 34.1 % / 0.27, spotter 42.4 % / 0.00,
both 48.2 % / 0.27.)*

So the recommendation in `vocabulary-multilingual-research.md §8` — "if the margin is small, stop, ship
the model-free path everywhere and close this line of work" — **does not fire**. The margin is not
small: the spotter wins on recall *and* on precision in the language it covers.

What changed anyway, and why it is still the big win: **the corrector recovers 34–37 % of missed terms
in the 20 gate languages that will never have a spotter, at 0.27 false applies per 1000 words, for
zero bytes.** Today those languages recover 0 %. That is what shipped.

**And the single most useful number in the whole experiment is neither of the above:** 61 of 214
missed terms (29 %) sit further than 0.45 from anything the engine wrote. The spotter *hears* 35 of
them. The gate applies **zero**. No checkpoint, in any language, changes that — it is our own
plausibility ceiling, and it is now the largest identified pool of unrecovered value.

> **Which spotter build.** The headline table is the spotter *after* commit `904b4df` (the casing
> variants fix). It was re-run for exactly that reason. The fix is worth **+0.5 points** here
> (43.9 → 44.4 %) despite being worth 32 points of raw DP recall on Title-Case terms, because the
> extra detections it unlocks land mostly in the far band the gate refuses anyway — which is the
> best available independent evidence that the ceiling, not detection, is now the binding constraint.

---

## 1. The corpus

**FLEURS `en_us`, dev + test splits** (`google/fleurs` on Hugging Face).

| | |
|---|---|
| Licence | **CC-BY-4.0** — redistributable, no click-through, no token |
| Clips | **1041** (647 test + 394 dev), all downloaded, none discarded |
| Audio | **2.82 h**, 16 kHz mono |
| Reference words | 22 490 |
| Distinct sentences | **500** — each read by ~2 different speakers |
| On disk | `D:\caches\jot-vocab-eval\fleurs\` (tarballs + extracted wav + TSV) |

**Why FLEURS and not Common Voice.** Common Voice is CC0 and would have been fine, but the Hugging
Face repos (`mozilla-foundation/common_voice_*`) are **gated** — they need an accepted licence
agreement and a user token, which is not something to wire into a reproducible harness. FLEURS is
ungated, is read-aloud **Wikipedia** prose (so it is dense in exactly the rare proper nouns custom
vocabulary exists for), and its two-readers-per-sentence structure gives the multi-speaker coverage
that was the whole objection to the previous single-TTS-clip evidence.

**Speaker coverage.** 214 recovery chances arise across 1041 independent renditions; every sentence
that carries a target term is read by at least two speakers, and 18 terms occur in 2–3 different
sentences (so 4–6 renditions). This is not one pronunciation of one voice.

### Term selection — mechanical, from the references, not hand-picked

A word enters the term list when it is, in a **reference** transcript:

1. not sentence-initial (initial capitalisation says nothing about the word),
2. capitalised and purely alphabetic, 4+ characters,
3. **not** in the shipped 24 059-entry English frequency list,
4. free of internal punctuation.

That yields **155 terms** — `Nyiragongo`, `Balasubramanian`, `Tendulkar`, `Sundarbans`, `Geospiza`,
`Uppsala`, `TogiNet`, `SANParks`… — of which the CTC checkpoint's BPE can represent **150** (the 5 it
cannot are accented: `Asunción`, `Chişinău`, `Erdoğan`, `Klöcker`, `Cañitas`).

Rule 4 is deliberate: possessives and hyphenated forms were excluded because a user types `Martelly`,
not `Martelly's`, and keeping them would have handed the corrector free wins on term classes the
checkpoint cannot encode at all — a difference already documented, not one this corpus needs to prove.

**Decoys are not a separate set.** Every clip is scored against the *whole* term list, so the 800-odd
clips containing none of the terms are the decoy set, and a term is a decoy for every clip that does
not contain it. That is the realistic exposure: a user has a list, and almost every dictation contains
none of it.

---

## 2. Method

Three stages, each cached, so scoring could be re-derived without re-decoding 2.8 h of audio.

1. **Transcribe.** Every clip through the *shipping* engine — Nemotron 3.5 fp16 on DirectML — then
   `TextPipeline.Clean(text, "en-US", isNemotron: true)`, i.e. exactly what `RecorderController` hands
   to vocabulary. Baseline WER **10.44 %**.
2. **Spot.** The shipping `CtcVocabularySpotter` over the same clips with the same 155 terms.
3. **Score.** Each arm's detections through the real `VocabularyGate.ApplyFromDetections`, with the
   real embedded English common-word list.

### Definitions

* **Opportunity** — the reference contains the term and the baseline transcript does not. 214 of them.
  This is the only thing vocabulary can improve; a term the engine already spelled right is not a win
  available to anyone.
* **Recovered** — the term appears in the arm's output text.
* **TP** — an applied correction whose term really was spoken, replacing a span that is *not* itself a
  word of the reference.
* **FP-absent** — applied a term that was never said. **FP-overwrote** — the term *was* said, but we
  replaced text the engine had already got right. The second is the failure the gate exists to
  prevent and is counted separately for that reason.

The "both" arm merges with **acoustic-wins-per-term**: a transcript can legitimately host the same
term twice, and letting two sources place it independently splices it twice.

---

## 3. Head to head

```
arm          WER%  opportunities  recovered  recall%   spottable-recall%   applied    TP  FP-absent  FP-overwrote  FP/1k-words

### all-155 terms (150 spottable by the CTC checkpoint's BPE)
baseline    10.44            214          0     0.0                0.0         0     0          0             0        0.00
corrector   10.08            214         79    36.9               36.9       104    83         21             0        0.93
spotter     10.03            214         95    44.4               46.8       118   102         14             2        0.71
both         9.98            214        106    49.5               50.2       141   113         26             2        1.24

### focused-25 terms (24 spottable)
baseline    10.44             85          0     0.0                0.0         0     0          0             0        0.00
corrector   10.30             85         29    34.1               32.9        36    30          6             0        0.27
spotter     10.27             85         36    42.4               43.9        38    38          0             0        0.00
both        10.25             85         41    48.2               47.6        49    43          6             0        0.27
```

`focused-25` is the realistic shape — a user adds terms they actually say — and is selected from the
corpus (the 25 terms the engine got wrong most often), not by hand. Acoustic detections are subset to
the smaller list rather than re-run; the DP scores each term independently, so the only effect of
dropping a query is that its own hits disappear.

**Precision, said plainly.** At realistic list size the spotter made **zero** false applies in 22 490
words. The corrector made **six**, all of the "term was never spoken" kind — `Terry` → `Jerry`,
`Maria` → `Marie`, `Raku` → `Raju`, `Cotro` → `Potro`, `Cosman` → `Norman`, `civitas` → `Cañitas`.
Every one is a genuine near-neighbour *name*, which is exactly where a purely textual rule cannot tell
a hit from a collision. Note the corrector's **FP-overwrote is 0 in both configurations**: it never
replaced a word the engine had already got right.

---

## 4. Marginal value, by band

The decisive table. `nearest gap` = the smallest normalized skeleton distance from any 1- or 2-word
window of the baseline transcript to the term — i.e. how far off the engine actually was.

| band | opportunities | corrector detects | spotter detects | **corrector recovers** | **spotter recovers** | both recovers |
|---|---|---|---|---|---|---|
| **A** ≤ 0.20 | 66 | 63 | 61 | **47** | 43 | 46 |
| **B** 0.20–0.30 | 35 | 30 | 27 | **25** | 22 | 29 |
| **C** 0.30–0.45 | 51 | 8 | 37 | 7 | **30** | 31 |
| **D** > 0.45 | 61 | 0 | 35 | **0** | **0** | 0 |
| — no anchor | 1 | 0 | — | 0 | 0 | 0 |

Read it in three lines:

* **Near band (A+B, 101 chances): the corrector wins**, 72 recoveries to the spotter's 65. A textual
  rule is simply better than acoustics at `Sandarbans` → `Sundarbans` and `Upsala` → `Uppsala`.
* **Mid band (C, 51 chances): this is the spotter's entire marginal value**, 30 recoveries to 7. This
  is `Gowry` → `Gourley`, `Jucklin` → `Jelinek`, `Toky Net` → `TogiNet` — too far for a textual
  threshold that is safe, close enough for the gate to allow once acoustics vouch for it.
* **Far band (D, 61 chances — 29 % of everything): dead for both.** The spotter hears 35 of them; the
  gate's 0.45 plausibility ceiling refuses every one, so they surface only as `spot-unplaced` log
  lines. `Sun Darwins` → `Sundarbans`, `Lylone` → `Slalom`, `Jo Spisa` → `Geospiza`.

Note the "both" arm is 1 recovery *below* the sum implies, and A is where it loses: acoustic-wins-per-
term means an extra acoustic detection can displace a textual one that the gate would have placed
better. Merging two detection sources is not free even when both are right.

The far band is the finding worth carrying forward. **It is larger than the spotter's entire marginal
band, it is already being detected, and unlocking it costs no model at all** — it needs the ceiling to
become conditional on detection confidence rather than fixed. That is a much cheaper experiment than a
second checkpoint and it should be run before one.

### Could a looser corrector reach band C?

Measured, by moving `CharactersPerEdit` (one edit allowed per N term characters):

| budget | recovered | applied | false applies |
|---|---|---|---|
| ⌈len/5⌉ (shipped) | 79 | 104 | **21** |
| ⌈len/4⌉ | 87 | 127 | **35** |
| ⌈len/3⌉ | 105 | 175 | **63** |

+26 recoveries for +42 false applies. **No.** Band C is not reachable textually at an acceptable
price, which is the cleanest possible statement of what the acoustic model actually buys.

---

## 5. Cost

| | corrector | spotter |
|---|---|---|
| Latency, 155 terms | **p50 0.50 ms**, p95 0.78, max 11.1 | **p50 415 ms**, p95 510, max 719 |
| Latency, 25 terms | **p50 0.06 ms**, p95 0.10 | (unchanged — dominated by the encoder) |
| Memory | none beyond the transcript | +283 MB CPU / +116 MB DirectML working set |
| Disk | 0 | 131.7 MB, downloaded on enable |
| Languages reachable | **21** (every common-word list) | 2 today, ≤ 16 of 40 ever |

The corrector is ~1000× cheaper and takes no part of the 4 s vocabulary deadline.

---

## 6. Two precision bugs the corpus found, and both are fixed

Neither was hypothesised; both fell out of reading the false applies.

1. **The gate silently rewrote possessives, on the SHIPPING acoustic path.** `SkeletonOf` drops the
   apostrophe, so `Mariana's` measures **0.00** against the term `Marianas` and was applied,
   deleting the inflection. `PreservingEdgePunctuation` cannot save it (the trailing `s` is
   alphanumeric) and narrowing the span would publish `Marianas's`. **5 of the spotter's 20 false
   applies were this one shape** (`Mariana's` → `Marianas`, `USOC's` → `USOC`, `Shayam's` → `Shyam`,
   `Falkland's` → `Falkland`). Fixed by a fourth WINDOWS-DIVERGENCE guard in `ApplyFromDetections`
   that blocks — leaving a reviewable row — when the span carries an apostrophe the term and its
   aliases do not. One-directional: a term that *has* an apostrophe still corrects a span that lost
   it. This moved the shipping spotter from 0.89 to **0.67** false applies per 1000 words (measured
   on the pre-`904b4df` spotter build; the post-casing-fix build sits at 0.71 with the guard in).
   Cover: `tests/Jot.Tests/Vocabulary/DetectionPathInflectionTests.cs`.

2. **The corrector let a wider window eat an adjacent token.** `George W` won the two-word slot for
   the term `George` at 0.14, because the one-word exact match was merely *skipped* rather than
   *claimed*, and the splice deleted the initial. Same shape for `John F`, `25 Dunlap`, `A Giza`,
   `Plata a`. Fixed by claiming identity matches first: a word the engine already spelled exactly as a
   term is settled and no other candidate may consume it.

Together these took the corrector from 38 false applies to 21, and **FP-overwrote from 10 to 0**.

---

## 7. What shipped

`VocabularyRunner` now has a three-state language gate:

| mode | languages | source |
|---|---|---|
| **Acoustic** | en-US, en-GB | CTC spotter; corrector deliberately NOT stacked on it |
| **Textual** | ~~the other 20 with a common-word list~~ → **18**, per language, since E6 (bg cs da de el es fi fr hu it nl pl pt ro ru sk sv uk; six of them at a tighter acceptance distance) | `VocabularyCorrector` |
| **Off** | everything else, and Auto detect | nothing |

Three deliberate choices, each with the measurement behind it:

* **English does not stack the two.** Adding the corrector to the spotter bought +5.8 points of recall
  for +6 false applies — roughly one recovery per one corrupted word. D2 says precision wins that.
  It is one line to change if the learning loop is later shown to decay those false applies.
* **English with the checkpoint absent now falls back to the corrector** instead of doing nothing at
  all. Same gate, same silence, no error — the terms just start working before 132 MB arrives.
* **Off means off.** A language with no frequency list would run the gate with its over-correction
  brake *absent*, which is the shipped `lista` → `Lisa` incident with the safety net removed. CJK and
  Thai are additionally out because `SplitWords` splits on a space.

The runner needs an injected `ITextVocabularySpotter` to serve a Textual language, so a runner built
without one reports `Off` — which is what keeps `ShouldRun` honest and every pre-existing test true.

UI, verified in the running app by UI Automation (badge, description, InfoBar, subtitle):

* en-US → "Experimental · sound-alike matching"
* es-ES → "Experimental · spelling matching"; Vocabulary page subtitle "· spelling matching only"; the
  "Vocabulary unavailable — needs an extra 130 MB model" bar is suppressed, because that model would
  do nothing for them
* ja-JP / Auto → "Experimental" + the blocking InfoBar

---

## 8. What I would do next, in order

1. **A confidence-conditional plausibility ceiling (band D).** 61 of 214 chances, 34 already detected,
   0 applied. Let a detection well above the spotter's accept threshold buy ceiling headroom — say
   0.60 instead of 0.45 — and measure the false-apply cost on this exact corpus. It is a half-day, it
   needs no model, and it is worth more than any second checkpoint. Every artifact needed to score it
   is already on `D:`.
2. ~~**E6 — per-language plausibility ceilings.**~~ **DONE**, 2026-07-26 —
   [`vocabulary-brake-per-language.md`](vocabulary-brake-per-language.md). 10 000 more clips, 19
   languages. The brake IS materially weaker outside English (Finnish covers 60.1 % of its corpus's
   types against English's 88.8 %) and 18 of the 19 still clear a 1.0-false-applies-per-1000-words
   budget, 12 without any change; Slovenian is cut. Two things this document got wrong: §7.5's *fix*
   was a per-language `PlausibilityCeiling`, and the ceiling turns out to fire **zero** times on the
   textual path in any language — the knob that bites is the corrector's own acceptance distance. And
   §7.5's *ranking* was wrong: Finnish and Hungarian, the two it named, are among the safest, because
   agglutination starves the frequency list and lengthens the words past collision range at the same
   time.
3. **Inflection-preserving splice.** The guard in §6.1 blocks rather than fixes. `Neumotron's` →
   `Nemotron's` is the correct answer and the gate has no way to express it today.
4. **Only then** the Tier-A checkpoints (de, pt). The spotter is worth 51 opportunities' worth of
   mid-band recovery per language — real, but smaller than items 1 and 2, and 132 MB × N more
   expensive.

---

## 9. Reproducing

```powershell
$env:JOT_VOCAB_EVAL="transcribe"   # ~22 min, resumable (the DML session takes the test host down)
$env:JOT_VOCAB_EVAL="spot"         # ~8 min
$env:JOT_VOCAB_EVAL="report"       # ~2 s
dotnet test tests\Jot.Tests\Jot.Tests.csproj --filter "FullyQualifiedName~VocabEvalHarness" `
  --logger "console;verbosity=detailed"
```

Outputs under `D:\caches\jot-vocab-eval\out\`: `transcripts.jsonl`, `detections.jsonl`,
`spottability.tsv`, `report.txt`, and the two auditable row dumps `corrections.tsv` (every applied
correction with its TP/FP class) and `opportunities.tsv` (every missed term with its gap and which
side found it). Nothing in this pipeline writes to the repo.
