# Port spec — NumberNormalizer (Swift → C#, byte-exact)

Reference impl on disk: `projects/jot-mobile/Jot/App/Transcription/NumberNormalizer.swift` (~51 KB) and
`projects/jot-mobile/Jot/Tests/NumberNormalizerTests.swift` (the 51-fixture parity contract). This file
is the distilled build guide; the Swift is the tie-breaker for parser-internal control flow.

**Nature:** pure, **regex-free**, table-driven token walker. Whole-string in / whole-string out. No
`NSRegularExpression`/`NSRange`/`Scanner` anywhere — do **not** reach for `System.Text.RegularExpressions`.
Only Foundation dep is `NumberFormatter` (decimal, forced `,` grouping) → C# `ToString("#,0", InvariantCulture)`.

## API
`public static string Normalize(string text)` — static, pure. Empty/zero-token fast path returns input.
**No internal language gate** — English-only *by construction of the tables*; the caller (our orchestrator)
applies the English-only gate. Runs AFTER filler cleaning (so filler never splits a cardinal).

## Tokenizer (`tokenize`)
Hand-written char walk (NOT `Split(' ')`), preserving exact whitespace so `\n\n` survives:
- Whitespace/newline run → one `.whitespace` token (`raw` = exact substring).
- Else maximal non-ws run → `raw`; strip trailing punct into `trailing` (prepend to keep order); if `raw`
  empties (pure punctuation) → `.other` token; else `kind = isWordish(raw) ? .word : .other`, `core = raw`.
- `isWordish(s)`: non-empty AND every char ∈ {letter, `-`, `'`(U+0027), `’`(U+2019)}. So `twenty-five`,
  `o'clock` are word-ish; `25`, `3.5` are `.other`. Only `.word` tokens open/extend a cardinal run.

`Token` = mutable `{ raw, core, trailing, kind }`. Port as a `class` (synthetic tokens get raw/core reassigned).

## Rule priority (first match wins, per token index `i`)
0. **Phone-shape bail-out (whole-text, before loop):** ≥7 consecutive `.word` tokens whose lower core ∈
   `singleDigitWords` → **return entire input unchanged**. (Non-digit `.word` resets the run to 0;
   whitespace/other skipped without resetting.)
1. **Large-scale pass-through:** `.word` that is/contains(`-`-split) million/billion/trillion → emit verbatim.
2. **Tens-ordinal combiner** (`parseTensOrdinal`, before cardinals): `twenty-third`→`23rd`, `twentieth`→`20th`,
   `twenty third`→`23rd`. Suffix: units 1→st,2→nd,3→rd,else→th. Bare ones-ordinals (`first`..`ninth`) stay words.
3. **Cardinal-open gate** — enter only if `.word` AND `!isOrdinal(core)` AND (first `-`-piece of lower core ∈
   `cardinalWords` OR `isSplittableCardinalWord(core)`). Inside, in order:
   - **3a Year pre-parser** (`isYearContext(prevLower)` + `parseYearShape`): `nineteen NN`→1900+NN,
     `twenty NN`→2000+NN, `two thousand [and] NN`→2000+NN, bare `two thousand`→2000.
   - **3b Address digit-run** (`prevLower ∈ addressContextWords` + `parseAddressDigitRun`): ≥2 single-digit
     words or an "oh" present → literal concatenated digits.
   - **3c Cardinal parse** (`parseCardinalSequence`): if the run contains a large-scale word → emit verbatim;
     else `rewriteSequence(...)` (below). `nil` → emit original tokens verbatim.
4. **Fallthrough:** emit verbatim.

`rewriteSequence` context rules (value from `computeValue`):
- **Money:** next word core (lower, trimmed `.,!?;:`) ∈ {dollars,dollar,bucks,buck} → `$` + `formatThousands(value)`;
  cents extension `and N cents/cent` → `$T.NN` (`%02d`); article-drop `a/one`+idiom via `dropArticleIfPresent`.
  **Cents-only** unit {cents,cent} → `<value>¢` (U+00A2), fires even sub-10 (`eight cents`→`8¢`).
- **Percent:** next unit == `percent` → `<value>%` (+ article-drop).
- **Year fallback:** `isYearContext(prev)` + 1000–2999 + `two thousand …` shape → `String(value)`.
- **Time:** value 1–12, single-token, not containsOh: next ∈ {thirty:30,fifteen:15,forty-five:45} → `H:MM`
  (+ meridiem); `o'clock`/`o’clock` → `H o'clock` (ASCII apostrophe out); sub-10 + `prev ∈ {at,by,around,about}`
  → `<value>` (+ meridiem). Meridiem: next core lower minus `.`, `am|pm` → `AM|PM`.
- **Address (rewrite):** `prev ∈ addressContextWords`: all-single-digit → concat digits (`four oh seven`→`407`);
  else `formatThousands(computeValue)` (`two hundred and three`→`203`); nil → verbatim.
- **Idiom exception** (before ≥10): value ∈ {100,1000,1000000} AND (start ∈ {hundred,thousand,million} preceded
  by bare `a`, OR start == `one`) → verbatim. (Money/percent run FIRST, so `one hundred percent`→`100%` still converts.)
- **Cardinal ≥10:** if containsOh → verbatim; else drop leading `a`, emit `formatThousands(value)`.
- **Cardinal 1–9:** no money/percent/time/cents/address context → verbatim.

`parseCardinalSequence` grammar (`canExtend`): hundred after 1–19; thousand/million after `1..<scale`; after a
scale any `≥1`; after tens only ones 1–9; `and` connector ONLY when last piece is a scale word + next is a
non-ordinal cardinal that canExtend; trailing punct on any token ends the run; interior whitespace consumed.

## Constant tables (verbatim)
```
trailingPunct: , . ! ? ; :  "  '  ) ] }  ”(U+201D) ’(U+2019) »(U+00BB)
cardinalWords: zero/oh=0, one..nine=1..9, ten..nineteen=10..19, twenty..ninety=20..90 (by tens),
               hundred=100, thousand=1000, million=1_000_000   (billion/trillion NOT here)
singleDigitWords: zero, oh, one..nine
ordinalWords: first..twelfth, thirteenth..nineteenth, twentieth, thirtieth..ninetieth, hundredth, thousandth, millionth
addressContextWords: apartment, apt, room, suite, floor, building, unit, office
yearContextWords: in, since, back, year, from, until, before, after
monthWords: january..december
timePrecedingWords: at, by, around, about
largeScaleWords: million, billion, trillion
onesOrdinalValues: first=1..ninth=9
tensOrdinalValues: twentieth=20, thirtieth=30, fortieth=40, fiftieth=50, sixtieth=60, seventieth=70, eightieth=80, ninetieth=90
phoneShapeThreshold = 7
```
Inline literals: dollars {dollars,dollar,bucks,buck}; cents {cents,cent}; percent; meridiem {am,pm}(minus `.`);
compound-minutes {thirty:30,fifteen:15,forty-five:45}; o'clock {o'clock, o’clock}; unit-trim set `.,!?;:`.

## The 51 fixtures (input → expected) — the parity gate
Positive: `fifteen pages`→`15 pages`; `twenty-five percent`→`25%`; `fifty dollars`→`$50`;
`twenty-five thousand dollars`→`$25,000`; `five thirty PM`→`5:30 PM`; `at four`→`at 4`; `at four thirty`→`at 4:30`;
`by five`→`by 5`; `in nineteen ninety-eight`→`in 1998`; `in twenty twenty-six`→`in 2026`;
`in two thousand twenty-six`→`in 2026`; `apartment four oh seven`→`apartment 407`;
`apartment two hundred and three`→`apartment 203`; `two hundred and thirty units`→`230 units`.
Idiom: `a thousand times`, `a million times`, `a hundred drafts` → unchanged; `one hundred percent`→`100%`;
`a thousand dollars`→`$1,000`.
Article-drop: `a thousand and twenty things`→`1,020 things`; `one hundred and fifty users`→`150 users`;
`a million and one ways`→unchanged.
Large-scale pass-through: `300 million`, `two million users`, `twenty-five million dollars`, `fifteen million`,
`two billion`, `million`, `three trillion stars`, `one billion dollars` → all unchanged.
Tens-ordinal: `twenty third street`→`23rd street`; `twenty-first floor`→`21st floor`;
`thirty second avenue`→`32nd avenue`; `ninety ninth percentile`→`99th percentile`; `twentieth century`→`20th century`;
`thirtieth birthday`→`30th birthday`; `twenty-first street`→`21st street`.
Skip/preserve: `my son turned eight`→unchanged; `almost twelve`→`almost 12`;
`Twenty five new sign-ups today`→`25 new sign-ups today`; `I made twenty-five`→`I made 25`;
`eight hundred five five five one two three four`→unchanged (phone shape);
`Looking back over the past ten years`→`...past 10 years`;
`I read fifteen pages and reviewed three pull requests`→`I read 15 pages and reviewed three pull requests`.
Paragraph/punct: `abc.\n\nfifteen things`→`abc.\n\n15 things`; `fifteen, twenty, twenty-five`→`15, 20, 25`;
`I have eight cents`→`I have 8¢`.
Negative: `` → ``; `The quick brown fox jumps over the lazy dog.`→unchanged; `First and second and third.`→unchanged;
`I have two cats and three dogs.`→unchanged.
(`couple/few/dozen` are not in any table → preserved by omission; no fixture.)

## Swift→C# traps
1. Tokenizer walks graphemes; .NET strings are UTF-16 — for English transcripts a `char`/rune walk is fine
   (document the divergence). 2. Invariant culture for ALL casing (`ToLowerInvariant`/`ToUpperInvariant`) —
   Turkish-I would break `"AM".ToLower()`. 3. `formatThousands` forces `,` grouping → `ToString("#,0", InvariantCulture)`,
   NOT current culture. 4. `%02d` → `ToString("D2")`. 5. `¢`=U+00A2; o'clock output always ASCII `'`. 6. `split(separator:"-")`
   drops empties → `Split('-', RemoveEmptyEntries)`. 7. Backward scans in `isStandaloneIdiom`/`articleIndexBeforeIdiom`
   return on the FIRST non-whitespace token (don't keep scanning). 8. `computeValue`: use `long`. 9. `dropArticleIfPresent`
   does `RemoveRange(i, count-i)` on the out-list only when the tail non-ws word core == `"a"`.
