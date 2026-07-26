# E8 — a plausibility ceiling that is a function of how well the term was heard

> Status: **MEASURED and SHIPPED.** Run 2026-07-26 on this machine against the shipping v1.2.2
> pipeline, on E5's 1041 FLEURS clips and the 433 detections the shipping CTC spotter produced from
> them. Answers item 1 of
> [`vocabulary-corrector-vs-spotter.md §8`](vocabulary-corrector-vs-spotter.md) — "a
> confidence-conditional plausibility ceiling… worth more than any second checkpoint".
>
> Harness: `tests/Jot.Tests/Vocabulary/VocabEvalHarness.cs`, with a new `calibrate` stage that dumps the
> joint distribution the mapping is derived from, and `-fixed` counterfactual arms so before and after
> are produced in one process from the same detections. Raw rows on `D:\caches\jot-vocab-eval\out\`
> (`detection-calibration.tsv`, `report.txt`), never in git.

---

## 0. Answer

**+4.7 points of end-to-end recall on the realistic list, for zero additional false applies.** E5's
English precision baseline — the spotter's **0 false applies in 22 490 words** — is untouched.

```
                     recall%   applied    TP  FP-absent  FP-overwrote  FP/1k   WER%

### focused-25 (the realistic list, and the arm the decision uses)
spotter, 0.45 fixed     42.4        38    38          0             0   0.00   10.27
spotter, EARNED         47.1        42    42          0             0   0.00   10.25
both,    0.45 fixed     48.2        49    43          6             0   0.27   10.25
both,    EARNED         52.9        53    47          6             0   0.27   10.24

### all-155 (the stress list nobody would type)
spotter, 0.45 fixed     44.4       118   102         14             2   0.71   10.03
spotter, EARNED         45.8       124   108         14             2   0.71   10.01
both,    0.45 fixed     49.5       141   113         26             2   1.24    9.98
both,    EARNED         50.9       147   119         26             2   1.24    9.96
```

Six more correct applications on the stress list, four more on the realistic one, **and not one more
false apply in either**. WER improves in all four arms.

This is the band E5 called "the largest identified pool of unrecovered value": 61 of 214 missed terms
(29 %) sit beyond the fixed 0.45 ceiling, the spotter hears 35 of them, and the gate applied exactly
zero. It is also why the casing fix — worth 32 points of raw detector recall — moved end-to-end recall
by 0.5 points: the detections it unlocked landed in a band the gate refused on principle.

**It is not a general loosening.** The ceiling moves only for a detection carrying a real acoustic
score. The model-free corrector's `Score` field holds a negated string distance on a completely
different scale, so it is flagged non-acoustic and gets the shipped 0.45 exactly as before — verified
by re-running E6's 20-language report, which came back **byte-identical**.

---

## 1. The calibration

Every one of the 433 detections the shipping spotter produced over 1041 clips, with the score the DP
gave it and the smallest distance from any 1- or 2-word window of the transcript to the term. `said` =
the term really was spoken in that clip; `already` = spoken and the engine had already spelled it
right; `absent` = never spoken, so an apply is a false apply.

```
gap band          n    said   already   absent
0.00-0.20        82      69         0       13
0.20-0.30        43      29         0       14
0.30-0.45        62      43         0       19
0.45-0.55        30      16         0       14      <- the band the fixed ceiling refuses
0.55-0.65        25      14         0       11
0.65-0.75        15       5         0       10
> 0.75            0       0         0        0
```

Flatly raising the ceiling to 0.65 would admit **30 right and 25 wrong**. That is the shape of the
problem, and the reason the answer has to be conditional. Now the same rows beyond 0.45, split by how
well the model heard them:

```
score band        n    said   absent
-0.25 .. -0.50    4       4        0
-0.50 .. -0.75    2       2        0
-0.75 .. -1.00    5       5        0
-1.00 .. -1.50    7       6        1
-1.50 .. -2.00   10       4        6      <- the boundary
-2.00 .. -2.50   15       8        7
-2.50 .. -3.00   28       7       21
```

The far band is not one population, it is two, and the acoustic score separates them. Above −1.5 it is
17 right and 1 wrong; below, 19 right and 34 wrong. **The strongest score in the whole corpus belonging
to a detection that was beyond the ceiling AND never spoken is −1.793.**

---

## 2. The mapping, and where every number comes from

```
                     0.45                                      score <= -1.8
effective ceiling =  0.45 + 0.20 * (score + 1.8) / 0.8         -1.8 < score < -1.0
                     0.65                                      score >= -1.0
```

| number | why it is that number |
|---|---|
| **0.45** floor | The shipped constant, and the one the golden fixtures pin. Nothing gets *less* than today. |
| **−1.8** ramp start | The strongest score among detections beyond the ceiling whose term was never spoken is **−1.793**. Below and at this score the ramp grants nothing at all. |
| **0.65** cap | The widest gap at which the spotter was RIGHT beyond the fixed ceiling is **0.615** (`blitweis lakes` → `Plitvice`). 0.65 is the first round step above it, and past 0.65 the far band turns: 5 right to 10 wrong. |
| **−1.0** full headroom | 0.8 nats clear of the false-apply boundary, and the score at which the cap covers that widest true detection (−1.012 at 0.615). |

**It is a plateau, not a fitted point.** Scored against the calibration rows, admitted-right /
admitted-wrong beyond 0.45:

| (ramp start, full, cap) | all-155 | focused-25 |
|---|---|---|
| **(−1.8, −1.0, 0.65)** *(shipped)* | **14 / 0** | **6 / 0** |
| (−2.0, −1.0, 0.65) | 14 / 0 | 6 / 0 |
| (−1.6, −1.0, 0.65) | 14 / 0 | 6 / 0 |
| (−1.8, −0.75, 0.65) | 13 / 0 | 6 / 0 |
| (−1.8, −1.0, 0.60) | 13 / 0 | 6 / 0 |
| (−1.8, −1.0, 0.75) | 16 / 1 | 6 / 0 |
| (−2.2, −1.0, 0.65) | 15 / 3 | 7 / 0 |
| flat 0.65, unconditional | 30 / 25 | — |

Every neighbour in {−2.0, −1.8, −1.6} × {−1.0, −0.75} × {0.60, 0.65} lands on **zero** false applies.
The two settings that break it are the ones that reach past a measured boundary: a ramp starting at
−2.2 admits the −1.793 row and four like it, and a 0.75 cap reaches into the band where the spotter's
own far tail turns.

### What a ramp buys over a step

A step at −1.5 → 0.65 scores 14 / 0 too, and was rejected: it hands a detection at −1.49 a quarter more
string distance than one at −1.51, on a score axis whose calibration comes from one checkpoint and 433
observations. The ramp degrades continuously if that distribution moves, and it is the shape the claim
actually has — string similarity and acoustic evidence are substitutes, not a threshold pair.

---

## 3. What shipped

* **`VocabularyGate.Detection` gained `Acoustic`**, defaulting to **false**. The `Score` field has
  always carried two incompatible things — the spotter's mean log-prob per token, and the corrector's
  negated string distance, which says so in its own comment — and nothing had ever needed to tell them
  apart. Now something does. Set true in exactly one place, `CtcWordSpotter`, where the number is
  produced.
* **`VocabularyGate.EffectiveCeiling`** — §2, and the only place the mapping exists. A **sixth WINDOWS
  DIVERGENCE**: Swift's `applyFromDetections` compares every gap against the one constant.
* **`ApplyFromDetections` resolves it once per detection** and uses it for placement, for the
  `spot-unplaced` diagnostic (which used to print `ceiling=0.45` for a row refused at 0.62), and for
  `Decide` — which gained a `plausibilityCeiling` parameter defaulting to the constant. That last one
  is not tidiness: a detection admitted at 0.60 by placement and then refused at 0.45 inside `Decide`
  would vanish with no proposal, no review row and no log, which is the same silent-drop class the
  `spot-unplaced` diagnostic was added to end.
* **`Plausible(_:_:_:)` is gone.** It could only ever compare against one constant, and leaving it
  would give a future caller a way to bypass the earned ceiling by accident.
* **Nothing else moves.** The common-word brake, the apostrophe guard, E7's inflection guard, the
  identity no-op, alignment and dedup all run unchanged on the admitted rows — headroom is granted
  against the *string*, never against the brake, and there is a test that says so.

**Golden fixtures byte-identical.** `detections_apply.json` includes `spot-implausible-skips`
("vikram" vs "Sriram", gap 0.50), which is precisely the row this would flip — and does not, because
fixture detections carry no acoustic flag. The fixture is a contract about the DEFAULT, and the default
is unchanged.

**Tests**: `EarnedCeilingTests` — the mapping at both anchors and between them, monotonic and bounded
across the whole score range, the `Herrakit` → `Parakeet` row end to end (applies when heard at −0.313,
refused at −2.4, refused without the flag), the diagnostic reporting the *earned* ceiling, the
common-word brake still stopping a strongly-heard term, and two tests pinning that the corrector never
claims an acoustic score. `DetectionPathDedupTests.UnplacedDetection_IsReportedWithItsScoreAndNearestMiss`
keeps every assertion it had — its detection is unflagged, so it is still unplaced — and its prose was
updated to say why. **No existing assertion was changed.**

749 tests, 733 passed, 16 skipped, 0 failed.

---

## 4. What this does not do

* **It cannot help a language without a checkpoint.** The ceiling only moves for acoustic evidence, and
  there is one English checkpoint. For the 18 textual languages this is a no-op by construction, and
  E6's report re-runs byte-identical to prove it.
* **It recovers 3 more distinct terms on the stress list, not 14.** Fourteen more detections are
  admitted; six of them become applied corrections; three of those are terms nothing else had already
  recovered. Admitting a detection is not the same as recovering a term, and the recall column is the
  honest one.
* **The remaining far band is still 26 opportunities.** Of E5's 61 beyond-0.45 chances, the spotter
  hears 35 and this now applies a share of them; the rest are either unheard or heard weakly, and
  weakly-heard is exactly what §1 says not to trust. Closing more of it is a detector problem, not a
  gate problem.

---

## 5. Reproducing

```powershell
$env:JOT_VOCAB_EVAL="calibrate"   # ~1 s, needs transcripts.jsonl + detections.jsonl
$env:JOT_VOCAB_EVAL="report"      # ~2 s, prints the fixed and earned arms side by side
dotnet test tests\Jot.Tests\Jot.Tests.csproj --filter "FullyQualifiedName~VocabEvalHarness" `
  --logger "console;verbosity=detailed"
```

`detection-calibration.tsv` is §1: one row per detection with its score, its nearest transcript span
and the truth. `report.txt` is §0. Nothing writes to the repo.
