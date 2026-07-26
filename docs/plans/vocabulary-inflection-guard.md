# E7 — can a structural guard stop the corrector overwriting a correctly-inflected word?

> Status: **MEASURED and SHIPPED, with a negative result inside it.** Run 2026-07-26 on this machine
> against the shipping v1.2.2 pipeline, on E6's own 10 000 FLEURS clips in 20 languages plus E5's 1041
> English clips, re-scored by the same code in the same process. Answers item 1 of
> [`vocabulary-brake-per-language.md §6`](vocabulary-brake-per-language.md) — "an inflection guard,
> worth more than any further threshold tuning".
>
> Harness: `tests/Jot.Tests/Vocabulary/VocabEvalMultilingualHarness.cs`, which now scores every
> language **twice**, guard off and guard on, from the same transcripts in the same run. Raw rows on
> `D:\caches\jot-vocab-eval\out\` (`ml-e7-guard.tsv`, `ml-rows.tsv`), never in git.

---

## 0. Answer

**The failure class E6 named cannot be fixed by a structural guard, and saying so is most of this
document. What CAN be fixed is a different, overlapping class, and that is what ships.**

| | guard off | guard on |
|---|---|---|
| false applies, realistic arm, 20 languages | 64 | **58** |
| false applies, adversarial arm | 146 | **135** |
| of which FP-**overwrote** (the class E6 named) | 29 | **24** |
| correct corrections lost | — | **4**, all in Greek |
| languages whose numbers move at all | — | **3** (el, pl, ru) |
| English (E5, 1041 clips, 22 490 words) | 0.27 / 0.22 FP/1k | **0.27 / 0.22 — inert** |

The guard is worth shipping: it removes **17 of 210 false applies for 4 correct corrections**, it takes
Greek's realistic arm from **1.42 to 0.89 FP/1k** — E6's worst realistic number, now inside the 1.0
budget — and it is byte-for-byte inert in the other seventeen languages and in English.

But read the third row honestly. E6 said the worst failure class is *a correctly-transcribed inflected
form flattened into a term's citation form*, and named `Аризона` → `Аризоны`, `Версали` → `Версаче`,
`Fatima` → `Fatimě`. **The guard catches five of those twenty-nine rows, and all five are the same
word — `пирамид`/`piramid` (pyramid) in Russian and Polish.** Every row it does not catch is a proper
noun, and §2 is the measurement of why no threshold reaches them.

---

## 1. What actually distinguishes the two cases — measured, not reasoned

E6 proposed the signal: *a shared stem with a divergent suffix is suspicious in a way a scattered edit
distance is not.* It is true and it is not enough. Over the **1207 applied corrections** E6 produced
across 20 languages (base false-apply rate **17.4 %**):

| predicate on the applied correction | rows | correct | false | false rate | lift |
|---|---|---|---|---|---|
| shared stem ≥ 4, both tails ≤ 2 (the "inflection shape") | 342 | 266 | 76 | 22.2 % | **×1.28** |
| shared stem ≥ 5, both tails ≤ 1 | 128 | 110 | 18 | 14.1 % | ×0.81 |
| span is the term plus a 1–3 character suffix | 13 | 10 | 3 | 23.1 % | ×1.33 |
| divergence in the first half of the word only | 489 | 411 | 78 | 16.0 % | ×0.92 |
| **span is an everyday word minus its final character** | 43 | 20 | 23 | 53.5 % | **×3.07** |
| ↑ and the inflection shape, and ≥ 7 characters *(shipped)* | **21** | **4** | **17** | **81.0 %** | **×4.65** |

Two things fall out.

**The shape is nearly worthless on its own.** Every one of the 29 measured overwrites has it — and so
do 266 correct corrections. A guard that fired on the shape alone would destroy **30 correct
corrections to prevent 33 false ones**, which is not a safety feature, it is a recall cut wearing one.
The fourth row is the same point from the other side: the divergence being early rather than late
excludes *all* 29 overwrites, so lateness is necessary — and 78 false applies are late too.

**The lexical half is where the discrimination is**, and it is the direct repair of what E6 diagnosed:
the brake is a **type** lookup over a 24 000-entry frequency list, so `пирамид` walks straight past it
while `пирамиды` is right there in the list, one character longer. Widening the brake by exactly one
trailing character — "is this span an everyday word with its last letter missing?" — selects a
population that is **half false applies**, three times the base rate.

### 1.1 One character, one direction, seven characters — each of these is a measurement

| variant | rows | correct lost | false caught |
|---|---|---|---|
| also allow the SPAN to be shaved ("span minus a character is a list word") | 63 | 30 | 33 |
| allow two characters instead of one | 146 | 92 | 54 |
| **one character, one direction, ≥ 7 chars (shipped)** | **21** | **4** | **17** |
| the same at ≥ 6 characters | 27 | 8 | 19 |
| the same at ≥ 5 characters | 43 | 20 | 23 |

Shaving the span is the expensive one and the reason is legible in the rows it adds: a mangled rare
name lands on *some* list word's prefix by luck far more often than it lands exactly one character
short of a whole one (`Kanaan` → `kanaal`, `Antoinet`, `Мартеи` → `марте`). It costs Greek fifteen
correct corrections instead of four.

The length floor is the one number chosen from a small sample and it is reported as such: at six it
also blocks `wigili` → `Wigilii` and three more real corrections to catch two more false ones; at eight
it loses the `piramid`/`пирамид` rows, which are the only overwrites the guard catches at all. Seven is
where precision peaks on this corpus, and the difference between six and seven is 4 rows — inside
noise. The reason there is a floor at all is not noise: a five-letter prefix is shared by a large slice
of any inflecting language's dictionary.

---

## 2. The negative result, stated plainly

**A span that is a proper noun in another grammatical case is unreachable, and no threshold changes
that.** The frequency list has no entry for `аризон-`, `верса-`, `fatim-`, `гренланд-`, `filipin-`,
`jaavalai-` at any truncation, so the lexical half is false and the guard stands aside.

That is the right answer, not an omission, and the corpus proves it. E6's Russian rows contain **both**
of these, from the same term, in the same 500 clips:

```
Аризона  → Аризоны     the engine wrote the nominative correctly       FALSE APPLY
Аризоне  → Аризоны     the engine wrote the wrong case                 CORRECT
```

They are the same length, the same shared stem (6), the same tail (1), the same edit distance (0.14),
and the same everyday-word status (neither is in the list). Nothing available to the gate at dictation
time separates them: not the string, not the frequency list, not the acoustic score the textual path
does not have. Closing this needs a lexicon of *inflected proper nouns* per language, which is exactly
the per-language morphology resource this experiment was told not to invent — and the honest cost of
guessing is 30 correct corrections for 33 false ones, measured above.

Two smaller near-misses, recorded so they are decisions:

* **`Mexikó-ból` → `Mexikóból` (hu)** is not an inflection at all — the two skeletons are *identical*
  and only a hyphen differs. It is the apostrophe guard's shape (E5 §6.1) with a different mark.
  Generalising that guard from apostrophes to all internal punctuation would catch it. Not done: one
  measured row, against the real risk of refusing `Nemo-tron` → `Nemotron`, which is a correction users
  want.
* **`gewürzten` → `Gewürzen` (de)** has a listed relative (`gewürze`) but is two characters away from
  it, not one. Two characters was measured (§1.1) and costs 92 correct corrections.

---

## 3. What shipped

**`VocabularyGate.IsInflectionOfCommonWord`** — a fifth `WINDOWS DIVERGENCE` guard on
`ApplyFromDetections`, sitting beside E5's apostrophe guard because it is the same failure with a
different mark. It blocks when **both** hold:

1. span and term share a stem of ≥ 4 scalars and diverge only in a tail of ≤ 3 on either side;
2. the span is ≥ 7 scalars and is an everyday word minus its final character.

Single-word span against single-word term only — a multi-word term never reaches the brake anyway
(`Decide` step 2), and every overwrite E6 measured is one word against one word. A block publishes the
original text and still leaves a reviewable "kept" row, exactly as the other four force-blocks do.

**`CommonWordStems`** — the frequency list minus each word's final character, built lazily once per
language and cached against the common-word set object, so it lives exactly as long as the set
`EmbeddedCommonWordsProvider` already caches and no longer. Built only when a span has already passed
the cheap structural test, so a dictation that proposes nothing never pays for it.

It is **not** in `Decide`, and not on the rescore path. `Decide` is pinned by the shared golden fixture
`vocabulary_gate_decide.json`; `ApplyFromDetections` is the only path Windows runs and is already the
documented divergence zone. All seven jot-shared golden fixtures are byte-identical.

**`ApplyFromDetections` grew an `inflectionGuard` parameter**, default true, which no product code
passes. It exists so the harness can score the guard's own counterfactual on the same transcripts in
the same process — the same reason `VocabularyCorrector.Spot` exposes its distance. E6's report now
emits `ml-e7-guard.tsv` (before/after per language per arm) and sweeps every distance with the guard
both ways.

**Tests**: `DetectionPathInflectedCommonTests` — the two measured blocks, the predicate's four
structural refusals, the brake-absent degradation, and **two pinned limits**: `Аризона` → `Аризоны`
still applies, and a six-character span is below the floor. Those two exist so the limits in §2 are a
decision somebody has to un-make on purpose. 734 tests, 718 passed, 16 skipped, 0 failed.

E5's English report re-runs **byte-identical apart from its two latency lines** — the guard fires zero
times on 1041 English clips, in either arm, which is what "inert in English" means here.

---

## 4. Per-language, before → after

Every language not listed is byte-identical in both arms, both recall and false applies.

| lang | arm | recall off → on | FP/1k off → on | FP-overwrote off → on |
|---|---|---|---|---|
| **el** | realistic | 35.7 → **32.1 %** | 1.42 → **0.89** | 0 → 0 |
| **el** | adversarial | 24.6 → 24.6 % | 0.98 → **0.44** | 0 → 0 |
| **pl** | adversarial | 70.4 → 70.4 % | 0.90 → **0.56** | **3 → 0** |
| **ru** | adversarial | 71.4 → 71.4 % | 1.73 → **1.51** | **4 → 2** |
| en, bg, cs, da, de, es, fi, fr, hu, it, nl, pt, ro, sk, sl, sv, uk, hr | both | unchanged | unchanged | unchanged |

Greek is the only place the guard costs anything, and it is also where it is worth most: 10 blocks on
the realistic arm, 6 of them false applies and 4 of them correct corrections. Greek's frequency list is
inflection-dense (`πολιτική, πολιτικής, πολιτικό, πολιτικές, …`) and its engine runs at 47 % WER, so
more of what it writes lands one character from a listed word — in both directions. Net WER still
improves (47.10 → 46.80 %).

### 4.1 A finding this experiment did not act on

E6 chose six languages' acceptance distances from a sweep the guard did not exist for, and predicted "a
guard would probably let some of them loosen again". Under E6's own rule — *the loosest distance in
{0.30, 0.25, 0.20, 0.15} that brings both arms to ≤ 0.5* — the guard-on sweep says:

```
pl   shipped 0.20 (33.9 % / 66.7 % recall)   →  0.30 qualifies: 0.11 / 0.45,  50.0 % / 66.7 % recall
el   shipped 0.20 (16.1 % / 10.8 % recall)   →  0.25 qualifies: 0.44 / 0.27,  25.9 % / 20.0 % recall
ru   shipped 0.20                            →  no change (0.25 leaves 1.41 adversarial)
```

That is 16 points of Polish recall and 10 of Greek sitting on the table. **Not taken here**: the six
distances are E6's decision, made from a full sweep with a stated rule, and re-opening them is its own
change with its own risk. Recorded so it is a short decision rather than a rediscovery.

---

## 5. Reproducing

```powershell
$env:JOT_VOCAB_EVAL_ML="report"    # ~60 s, both guard arms, no GPU
dotnet test tests\Jot.Tests\Jot.Tests.csproj --filter "FullyQualifiedName~VocabEvalMultilingualHarness" `
  --logger "console;verbosity=detailed"
```

Outputs under `D:\caches\jot-vocab-eval\out\`: `ml-e7-guard.tsv` is §4's table; `ml-rows.tsv` carries
every applied and blocked correction for both guard arms (`{lang}-{arm}` and `{lang}-{arm}-noguard`),
which is what §1 was measured from; `ml-sweep.tsv` is §4.1. Nothing writes to the repo.
