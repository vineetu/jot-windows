# E6 — is the over-correction brake strong enough outside English?

> Status: **MEASURED and SHIPPED.** Run 2026-07-26 on this machine against the shipping v1.2.2
> pipeline. Answers experiment **E6**, named in
> [`vocabulary-corrector-vs-spotter.md §8.2`](vocabulary-corrector-vs-spotter.md) as "the one claim in
> this document I would most want checked next".
>
> 10 000 newly transcribed clips of real human speech across 20 languages — the 19 reachable ones plus
> a Croatian probe — in 3½ h of GPU, alongside E5's 1041 English clips re-scored by the same code as
> the baseline. Every number below was produced on this box; nothing is modelled, inferred or quoted.
> Harness:
> `tests/Jot.Tests/Vocabulary/VocabEvalMultilingualHarness.cs` (off unless `JOT_VOCAB_EVAL_ML` names a
> stage). Raw rows on `D:\caches\jot-vocab-eval\out\`, never in git.

---

## 0. Answer

**The brake is materially weaker outside English, exactly as `vocabulary-multilingual-research.md §7.5`
predicted — and it is still good enough in 18 of the 19 reachable languages, 12 of them without any
change at all.**

| | |
|---|---|
| **Ship unchanged (12)** | cs, da, es, fi, fr, hu, it, nl, pt, ro, sk, sv |
| **Ship tightened (6)** | **bg** 0.25, **de** 0.15, **el** 0.20, **pl** 0.20, **ru** 0.20, **uk** 0.20 |
| **Cut (1)** | **sl** (Slovenian) → `Off`, and the UI already says so |
| **Never was reachable** | **sr** (Serbian) — a 24 000-entry list that no setting can select |

Three findings matter more than the verdicts:

1. **The 0.45 plausibility ceiling is not the brake, and on this path it is not anything at all.** Of
   every gate block on the realistic arm across the 19 languages and English — **531** of them —
   **zero** came from the plausibility ceiling and **all 531** from the common-word rule. The
   corrector proposes at ≤ 0.33 and the gate
   refuses above 0.45, so the two never meet. **A per-language `PlausibilityCeiling`, which is what
   §7.5 recommended, would have been a no-op.** The knob that bites is the corrector's own acceptance
   distance, and that is what ships.

2. **§7.5's ranking is wrong where it matters, and its mechanism is right.** It named Finnish and
   Hungarian as the languages to fear. Their brakes *are* the weakest measured — the Finnish list
   covers 60.1 % of the corpus's types against English's 88.8 %, and in Finnish the brake stopped
   **0.00** false applies per 1000 words against English's 1.60 — and they are two of the safest
   languages in the experiment (0.00 and 0.34 false applies per 1000 words). Agglutination cuts both
   ways: it starves the frequency list *and* it makes words long enough that almost nothing collides
   with them. The languages that actually failed are **Slovenian, Russian, Bulgarian, Greek** —
   moderate word length, heavy inflection, and (for Greek) an engine at 47 % WER.

3. **The failure has a shape, and it is inflection.** English's corrector never once overwrote a word
   the engine had already got right (0 in both E5 configurations). Eight other languages did, and the
   rows are all the same thing: `Аризона` → `Аризоны`, `jaavalainen` → `Jaavalaisten`, `Fatima` →
   `Fatimě`, `gewürzten` → `Gewürzen`. The corrector flattens a grammatical case ending into the term's
   citation form. The gate has a guard for the English shape of this (the possessive apostrophe, E5
   §6.1) and no guard at all for the suffix shape. **That, not the ceiling, is the next experiment.**

And the single most quotable row: in Russian, with an ordinary 25-term list, the corrector turned
**`Версали` (Versailles) into `Версаче` (Versace)** — three times, once per speaker. That is
`lista → Lisa` in a language where the frequency list never saw the inflected form.

---

## 1. What is actually reachable

`VocabularyRunner.ModeFor` put a language in **Textual** mode when a frequency list existed for it.
Twenty-one lists ship. The set a user can actually select is the intersection with Nemotron's 40
locales, and that is **19 languages, not the 20 the ship note claims**:

| | |
|---|---|
| **Acoustic** | en-US, en-GB |
| **Textual, before E6 (19)** | bg cs da de el es fi fr hu it nl pl pt ro ru sk sl sv uk |
| **Unreachable** | **sr** — `common-words-sr.txt` ships (217 KB, embedded, 24 000 entries) and nothing can select it. Serbian is not one of Nemotron's 40 locales, and `NemotronLocales.Normalize` folds anything else to `en-US`, so a hand-edited `"sr-RS"` in settings.json does not get Serbian-without-a-ceiling — it gets English |
| **Off** | hr, tr, et, lt, lv, he, mt, nb, nn, ar, hi, vi + CJK/Thai (no list, and `SplitWords` splits on a space) |

Pinned by `VocabularyLimitsTests.SerbianCannotBeSelectedAtAll`, which fails the day Nemotron gains
Serbian — because on that day a language would silently start running an unmeasured corrector.

---

## 2. Method — E5's, run per language

Same corpus family, same pipeline, same scoring code. E5's text measures were extracted to
`VocabEvalScoring` and both harnesses now share them, so "0.27 in English" and "1.73 in Russian" are
the same measurement and not two similar ones. E5's report was re-run after the extraction and is
byte-identical apart from its two latency lines.

**Corpus.** FLEURS (`google/fleurs`, **CC-BY-4.0**, ungated), dev split then test, **500 clips per
language** — 10 000 clips, 7 400–12 300 reference words each, against English's 22 490. Read-aloud
Wikipedia prose, ~2 speakers per sentence. On `D:\caches\jot-vocab-eval\ml\`, never in git. Real human
speech only; no TTS, per the owner's standing objection that one voice is one pronunciation.

**Transcription.** The shipping engine — Nemotron 3.5 fp16 on DirectML — with the language prompt slot
set to that locale, then `TextPipeline.Clean(text, locale, isNemotron: true)`, i.e. exactly the string
`RecorderController` hands to vocabulary. 3 h 20 min of GPU, one pass, no crashes.

**Terms, mechanically from the references**, exactly as E5 did: capitalised, purely alphabetic, 4+
characters, not sentence-initial, and **not in that language's frequency list**. Nothing hand-picked,
in any language. Three arms, two of them at **25 terms** so list size is constant everywhere:

* **realistic-25** — the 25 terms this engine got wrong most often. E5's shape, and the arm the
  English baseline of **0.27 false applies / 1000 words** belongs to.
* **adversarial-25** — the 25 terms sitting closest to an everyday word *actually spoken in this
  corpus*. The `lista`/`Lisa` shape, picked by arithmetic. This arm exists because the realistic one
  cannot settle the question on its own: in a language whose rare words are long compounds it reports
  a reassuring 0.00 that is a property of the corpus, not of the brake. English scores **0.22** here.
* **all-terms** — the whole pool as a stress list. Different size per language (German 304, French 67),
  so it is context, not a comparison.

**Attribution.** Every blocked proposal is charged to the guard that stopped it, in the gate's own
order and with the gate's own predicates — `VocabularyGate.Gap` for the ceiling and
`VocabularyGate.IsCommonSpan`, extracted from `Decide` for exactly this purpose, for the brake — then
classified counterfactually: *would applying it have been a false apply?*

**And the counterfactual arm.** Every language is scored a second time with the frequency list replaced
by nothing at all. The difference is damage the brake — not the ceiling, not the corrector's own
threshold — is preventing.

---

## 3. Results

Rates are **false applies per 1000 reference words**. "brake share" is the fraction of would-be false
applies the frequency list stopped on the adversarial arm — the direct answer to *is the list carrying
the weight it does in English?*

| lang | words | WER | list token/type | realistic recall / FP·1k | adversarial recall / FP·1k | brake share | verdict |
|---|---|---|---|---|---|---|---|
| **en** *(baseline)* | 22 490 | 10.4 | 93.7 / **88.8** | 34.1 % / **0.27** | 27.3 % / **0.22** | **98 %** | — |
| fr | 11 782 | 11.3 | 94.8 / 87.1 | 32.8 % / 0.00 | 38.1 % / 0.08 | 99 % | ship |
| it | 11 214 | 6.6 | 93.4 / 85.6 | 41.0 % / 0.09 | 36.0 % / 0.09 | 98 % | ship |
| pt | 11 227 | 9.3 | 93.6 / 86.0 | 43.7 % / 0.00 | 37.0 % / 0.09 | 99 % | ship |
| nl | 11 259 | 14.6 | 92.6 / 80.2 | 40.7 % / 0.00 | 28.6 % / 0.09 | 99 % | ship |
| sk | 9 259 | 25.5 | 82.4 / 70.6 | 55.6 % / 0.32 | 50.0 % / 0.11 | 93 % | ship |
| es | 12 345 | 6.5 | 93.4 / 85.0 | 60.0 % / 0.00 | 60.5 % / 0.32 | 97 % | ship |
| fi | 7 392 | 22.4 | 71.5 / **60.1** | 60.0 % / 0.00 | 61.5 % / 0.41 | **63 %** | ship |
| hu | 8 792 | 32.3 | 79.2 / 66.0 | 46.5 % / 0.34 | 34.2 % / 0.57 | 84 % | ship |
| sv | 10 218 | 25.5 | 88.4 / 75.8 | 43.1 % / 0.29 | 38.1 % / 0.69 | 91 % | ship |
| da | 10 069 | 28.8 | 89.5 / 78.5 | 42.3 % / 0.10 | 34.5 % / 0.79 | 83 % | ship |
| ro | 11 078 | 33.9 | 90.8 / 81.2 | 50.6 % / 0.09 | 59.5 % / 0.81 | 79 % | ship |
| cs | 9 078 | 24.9 | 83.3 / 72.0 | 47.5 % / 0.55 | 53.1 % / 0.88 | 84 % | ship *(closest pass)* |
| **pl** | 8 858 | 18.3 | 82.1 / 71.4 | 51.8 % / 0.23 | 70.4 % / **0.90** | **62 %** | tighten → 0.20 |
| **de** | 10 400 | 10.6 | 88.7 / 76.3 | 54.3 % / 0.10 | 62.5 % / **0.96** | 92 % | tighten → 0.15 |
| **uk** | 9 047 | 17.5 | 80.4 / 68.7 | 59.0 % / 0.66 | 45.0 % / **0.99** | 75 % | tighten → 0.20 |
| **el** | 11 256 | **47.1** | 72.4 / **56.9** | 35.7 % / **1.42** | 24.6 % / 0.98 | 90 % | tighten → 0.20 |
| **bg** | 10 241 | 23.1 | 88.8 / 78.1 | 50.7 % / 0.49 | 44.4 % / **1.27** | 71 % | tighten → 0.25 |
| **sl** | 9 279 | **58.6** | 84.2 / 72.3 | 37.0 % / 0.54 | 42.1 % / **1.72** | **57 %** | **cut** |
| **ru** | 9 248 | 12.6 | 81.6 / 69.6 | 60.3 % / 0.65 | 71.4 % / **1.73** | **67 %** | tighten → 0.20 |

Net effect on the transcript is **positive in every language measured**: WER falls in all 20 (e.g. ru
12.59 → 12.25, el 47.10 → 46.77, es 6.54 → 6.17). The feature is not trading accuracy for terms; it is
recovering 33–70 % of the terms the engine got wrong, and the argument is entirely about the tail.

### 3.1 What the tightened languages cost

The loosest measured distance that brings **both** arms to ≤ 0.5:

| lang | at ship (realistic / adversarial) | tightened to | after | recall kept |
|---|---|---|---|---|
| bg | 0.49 / 1.27 | **0.25** | 0.10 / 0.20 | 50.7 → 43.8 % (86 %) |
| de | 0.10 / 0.96 | **0.15** | 0.00 / 0.19 | 54.3 → 31.4 % (58 %) |
| el | 1.42 / 0.98 | **0.20** | 0.27 / 0.09 | 35.7 → 18.8 % (53 %) |
| pl | 0.23 / 0.90 | **0.20** | 0.11 / 0.45 | 51.8 → 33.9 % (65 %) |
| ru | 0.65 / 1.73 | **0.20** | 0.32 / 0.43 | 60.3 → 56.9 % (94 %) |
| uk | 0.66 / 0.99 | **0.20** | 0.33 / 0.22 | 59.0 → 47.5 % (81 %) |

German's 0.15 looks brutal and is not: German terms average **8.9 characters** (French 7.3, Bulgarian
6.4), so 0.15 still buys an edit on most of them, and it is the only setting that closes German's
adversarial arm — 0.25 leaves 0.67 and 0.20 leaves 0.58, because German's false applies are compound
and case forms sitting one edit from the term, which no distance can separate from a real near-miss.
Russian is the opposite shape: 0.20 costs it **3 points of recall** and removes three quarters of its
false applies.

Slovenian is the one that could not be saved. The only cap that brings it under (0.20) leaves 16.0 %
recall — 43 % of what it recovers uncapped — so it recovers one missed term in six while still
corrupting at 0.43. The engine is also at **58.6 % WER** there, which means the feature's premise (a
mostly-correct transcript with a few mis-spelled terms) does not hold in the first place.

### 3.2 The attribution, said plainly

```
                       gate blocks by guard          brake stopped, per 1000 words
                    ceiling   brake   other      realistic     adversarial     of what would have hit
en                        0      50       0           1.60           10.45              98 %
es                        0      86       0           6.16            9.23              97 %
el                        0      71       0           3.02            8.97              90 %
cs                        0      20       0           1.54            4.63              84 %
bg                        0      11       0           0.49            3.03              71 %
uk                        0      11       0           0.66            2.98              75 %
pl                        0       5       0           0.23            1.47              62 %
fi                        0       8       0           0.00            0.68              63 %
ru                        0       0       0           0.00            3.46              67 %
```

Two things to take from it. **The ceiling never fires** — not once, in any language, in any arm. And
**Russian's brake blocked nothing at all** on a realistic list while that list produced six false
applies: every span it should have protected was an inflected form the 24 000-entry list has never
seen. That is §7.5's prediction, landed, with a number.

### 3.3 Caveats I would not want discovered later

* **500 clips is 7 400–12 300 words, a third of the English corpus.** A language's false applies are
  1–16 events; the 95 % Poisson interval on 10 events is roughly ±0.6/1000 words. The budget below is
  a statement about *order of magnitude*, and the difference between cs at 0.88 and pl at 0.90 is
  noise, not ranking. Both were treated by the same rule; neither number should be quoted alone.
* **The term rule reads different things in different languages.** German capitalises every noun, so
  its mechanical pool is 304 terms of ordinary vocabulary where French's is 67 proper nouns. That is
  the same rule reading a different language — which is why the decision uses the two fixed-size arms
  and not the pool.
* **The adversarial arm is not equally adversarial everywhere.** Its mean gap to a spoken everyday word
  is 0.169 in Greek and 0.392 in Finnish: Greek's terms *can* be made far more collision-prone than
  Finnish's. Finnish's 0.41 is partly a low ceiling on how bad the arm could get, and Greek's 0.98 is
  partly the opposite. Both are reported; neither is corrected for.
* **Spanish and Portuguese were measured on es_419 and pt_br** (the only FLEURS variants), scored
  against the per-*language* list that es-ES and pt-PT also get.
* **English's own corrector could be tightened too.** At 0.30 its realistic false applies fall 0.27 →
  0.09 for 2.3 points of recall. It is deliberately left alone: it is the fallback path only, and that
  0.27 is quoted in three documents.
* **`sl` is cut but its 24 000-entry list still ships**, as does the unreachable `sr` — 440 KB of
  embedded resource that nothing reads. Left in place on purpose (deleting them is a change with no
  measurement behind it), and flagged here so it is a decision rather than an oversight.

### 3.4 A probe, not a proposal: Croatian

Croatian is one of Nemotron's 40 locales and is `Off` today because no `common-words-hr` exists. The
unreachable Serbian list is **Latin-script BCMS** and covers Croatian running text at 81.6 % of tokens
/ 65.0 % of types — inside the range of languages that do ship. Measured on the same 500 clips:

```
hr (with the SERBIAN list)   WER 31.3 %   realistic 41.2 % / 0.00   adversarial 37.0 % / 0.97
                             at 0.20:                  0.00                        0.22, recall 27.9 %
```

So Croatian would qualify under exactly the rule everything else was judged by, with a 0.20 cap — the
same treatment as Polish and Ukrainian. **It is not enabled here.** Enabling a language on the strength
of "another language's list is close enough" is the kind of claim E6 exists to stop being made without
a decision; the numbers are recorded so the decision can be a short one.

---

## 4. The budget, and why that number

**≤ 1.0 false applies per 1000 reference words, on both the realistic and the adversarial arm.**
A language at or above **0.9** — within one measured event of the line — must tighten or be cut.

Four things justify it, in the order they matter:

1. **It is anchored to the English measurement, not invented.** English's corrector runs at
   0.27 / 0.22. The budget is ~4× that. A language shipping at 4× English's corruption rate is a real
   statement, and a defensible one only because the alternative for these languages is *no vocabulary
   at all* — they recover 33–70 % of the terms the engine got wrong, from zero.
2. **It is the tightest line this corpus can resolve.** At 10 000 words a single false apply is
   0.10/1000. A budget of 0.5 would put five languages inside the noise band of their own measurement,
   and a number we cannot tell apart from the baseline is not a criterion.
3. **It is defensible in user time.** 1.0/1000 is at most one corrupted word per ~7 minutes of
   continuous dictation at 150 wpm, in an experimental, off-by-default feature the user opted into by
   typing the terms themselves.
4. **A hard side condition: net WER must improve.** It does, in all 20 languages. A feature that
   recovered terms while making the transcript worse overall would fail regardless of this table.

Then, per language:

* **passes** (both arms ≤ 0.9) → ships **exactly as English does**. Deliberately no per-language value:
  a knob fitted to ±1 event in 10 000 words is fitting noise, and the recall it costs is paid by users
  who never had a problem.
* **fails** → the **loosest** distance in {0.30, 0.25, 0.20, 0.15} that brings **both** arms to ≤ 0.5.
  Half the budget, on purpose: a language that has already shown it can exceed should not be parked on
  the line.
* **cut** → when no distance reaches ≤ 0.5 while keeping at least half the uncapped recall.

---

## 5. What shipped

* **`VocabularyLimits`** — the measured table: which languages the corrector serves, and the acceptance
  distance for each. One source of truth; `VocabularyRunner.ModeFor` and the corrector both read it,
  and so does the tour card's language count.
* **`VocabularyCorrector.Spot` takes the language**, resolves its distance, and refuses a match beyond
  it. The cap is applied in the corrector, **not** as a per-language `PlausibilityCeiling` — §3.2 is
  why: the gate's ceiling never fires on this path, so tightening it would have changed nothing.
* **`VocabularyGate.IsCommonSpan`** — the brake predicate, extracted from `Decide` so the experiment
  measures the shipped rule instead of a copy of it. No behaviour change; `Decide` calls it.
* **Slovenian is `Off`, with the right reason.** The blocked-language InfoBar used to have two
  branches — Auto detect, and "Jot has no everyday-word list for X". For Slovenian the second is now
  false: there IS a list, and it is the measurement that says it cannot protect anyone. That is
  exactly the shape of false that gets "fixed" later by someone shipping the list we already ship, so
  `SettingsViewModel.BlockedMessage` grew a third branch and became static so it can be tested at all
  (WPF-UI's `InfoBar` exposes neither Title nor Message to UI Automation — checked against all 182
  elements of the live window).
* **Copy, all four surfaces that said English-only**: the Help tour card (now derives its count from
  the table, so it cannot drift), `store-assets/store-listing.md`, `store-assets/store-listing-sie.md`,
  and `docs/features.md`. The Settings badge and Vocabulary page subtitle already said "spelling
  matching" per language and needed no change.
* **Tests**: `VocabularyLimitsTests` (19 rows of the shipped table, the Serbian unreachability pin, the
  count the copy quotes, and the cap actually gating the corrector), `sl-SI → Off` in
  `VocabularyModeTests`. `VocabularyTourTests.VocabularyTour_StatesBothHonestLimits` was **changed** —
  it asserted the card says "English only", which E6 made false; it now asserts the card states the
  count from the table and the spelling-only distinction.

722 tests, 706 passed, 16 skipped, 0 failed. The seven jot-shared golden fixtures are byte-identical,
and E5's report re-runs unchanged after the scoring extraction.

**Verified in the running app**, not only in tests, by walking the live Settings window with UI
Automation:

* `sl-SI` → badge "Experimental", description "…Your current language isn't covered — see above."
  (mode `Off` — the cut is real in the shipping UI, not just in a table).
* `ru-RU` → badge "Experimental · spelling matching", description "Outside English, Jot fixes
  near-miss spellings of your terms; it can't listen for them." (mode `Textual` — tightening a
  language did not accidentally disable it).

---

## 6. What I would do next, in order

1. **An inflection guard (§0.3).** Every FP-overwrote row in every language is a case ending flattened
   into the term's citation form — `Аризона` → `Аризоны`, `jaavalainen` → `Jaavalaisten`. The gate
   already blocks the English shape of this (the possessive apostrophe) and has nothing for the suffix
   shape. This is worth more than any further threshold tuning: it is the whole of the *worst* failure
   class, in the six languages that needed tightening, and a guard would probably let some of them
   loosen again. Every artifact needed to score it is on `D:` and re-scoring is 40 seconds.
2. **Croatian (§3.4)**, if the "Serbian list serves Croatian" claim is acceptable. It is measured, it
   passes at 0.20, and it turns 217 KB of unreachable resource into a 19th textual language.
3. **Re-measure the six tightened languages on the remaining clips** (500+ more each, ~1 h GPU; the
   corpus is already on disk, only `JOT_VOCAB_EVAL_MAXCLIPS` changes). Their caps were chosen from arms
   with 8–16 events; the direction is not in doubt but the exact step between 0.25 and 0.20 is within
   noise for pl and uk.

---

## 7. Reproducing

```powershell
$env:JOT_VOCAB_EVAL_ML="transcribe"   # ~3.4 h, resumable (the DML session takes the test host down)
$env:JOT_VOCAB_EVAL_ML="report"       # ~40 s
dotnet test tests\Jot.Tests\Jot.Tests.csproj --filter "FullyQualifiedName~VocabEvalMultilingualHarness" `
  --logger "console;verbosity=detailed"
```

`JOT_VOCAB_EVAL_MAXCLIPS` caps clips per language (500 here); `JOT_VOCAB_EVAL_LANG` restricts to a
comma-separated set. Corpus fetch: `D:\caches\jot-vocab-eval\fetch-ml.ps1` and `fetch-ml-test.ps1`
(FLEURS, CC-BY-4.0). Outputs under `D:\caches\jot-vocab-eval\out\`: `ml-report.txt`, `ml-summary.tsv`,
`ml-sweep.tsv` (the whole distance sweep, every language), and `ml-rows.tsv` — every applied and
blocked correction with its class, which is what §0.3 was read off. Nothing in this pipeline writes to
the repo.
