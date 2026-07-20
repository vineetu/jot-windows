# Port spec — ParagraphSegmenter (Swift → C#) — DEFERRED milestone

Reference impl on disk: `projects/jot-mobile/Jot/App/Transcription/ParagraphSegmenter.swift` +
`projects/jot-mobile/Jot/Tests/ParagraphSegmenterTests.swift`. **Deferred on Windows** — it requires
per-word timings the transcription layer does not produce yet (see the master design's "Blocker"). This
file captures the full contract so the future enablement is mechanical.

**What it does:** inserts `\n\n` paragraph breaks into already-finalized text, driven by inter-word pauses.
Batch-only (streaming never calls it). Safety-first: on any degenerate/ambiguous/drift input it returns the
input text UNCHANGED — segmentation can never regress the transcript.

## Required input shape (what a transcriber must supply)
```
readonly record struct Word(string Text, double Start, double End);   // Start/End in SECONDS
```
`Text` includes trailing punctuation (`"Hello."`, `"said.\""`); punctuation may also be its own word (`"."`).
Pause between words i and i+1 = `words[i+1].Start - words[i].End`. Both Start AND End per word are required.
(The Swift top entry reassembles `Word[]` from BPE `TokenTiming{token, startTime, endTime}` — replicate
`ReassembleWords` if fed raw tokens.)

## API
- `Segment(string rescoredText, IReadOnlyList<Word> words)` — top level.
- `Apply(IReadOnlyList<Word> words, string text)` — break engine (visible for tests).
- `ReassembleWords(IReadOnlyList<TokenTiming>)` → `Word[]` (visible for tests), if porting the BPE path.

## Constants (verbatim)
```
paragraphPauseThreshold = 1.4 s        // primary
discourseMarkerPauseThreshold = 1.0 s  // fast path
minWordsBeforeFirstBreak = 10          // reject break-after-index i < 9
minWordsBetweenBreaks = 8              // reject if i - lastAccepted < 8
sentenceEnders = { . ! ? }
discourseMarkers = [ so, okay, alright, now, next, however, anyway, but, "and then" ]
trailingTrimSet          = " ' ) ] } »(U+00BB) ’(U+2019) ”(U+201D)          // sentence-final detection
normalizedDiscourseToken uses SUPERSET = , . ! ? ; :  plus the 8 above     // markers carry trailing commas
wordStartMarkers = [ ▁(U+2581), space ]
```

## Algorithm
**ReassembleWords:** seed start/end from tokens[0]; per token: strip a single leading `▁`/space; if stripped
empty → skip; `isWordStart = index==0 || original token's first char ∈ {▁, space}`; word-start flushes the
accumulated Word and starts a new one; continuation appends text and extends End. Flush final.

**Apply:** guard `words.Count > 1` else return text.
1. *Candidates:* for i in 0..<count-1: `gap = words[i+1].Start - words[i].End`; trim `words[i].Text` trailing
   by `trailingTrimSet`; require last char ∈ sentenceEnders; `primary = gap > 1.4`; `discourse = gap > 1.0 &&
   nextIsDiscourseMarker(i)`; if either → candidate i ("break AFTER word i"). Empty → return text. (Strict `>`.)
   `nextIsDiscourseMarker`: normalize next word (superset-trim + lower); if ∈ markers → true; else join with the
   word after (`"and then"`) → check membership.
2. *Safety caps* over `candidates.Sorted()`: reject i < 9 (cap 1); reject `i - lastAccepted < 8` (cap 2, updated
   only on acceptance). Empty → return text.
3. *Drift guard:* `rescoredTokens = text.Split(whitespace/newline, RemoveEmptyEntries)`;
   `tolerance = Max(1, (int)(words.Count * 0.05))` (truncate toward zero); if `|rescoredTokens.Count - words.Count|
   > tolerance` → return text.
4. *Reassemble:* join rescoredTokens with " ", but after token i (not last) insert `\n\n` iff `breakAfterWordIndex
   contains i` AND the rescored token i (trailing-trimmed) ends in a sentenceEnder. (Index-safety guard: the
   rescored token must ALSO be sentence-final — BPE emits `"."` as its own word while whitespace-split glues it,
   so a raw break index can map mid-sentence; err toward dropping.)
5. *Collapse:* `while (result.Contains("\n\n\n\n")) result = result.Replace("\n\n\n\n","\n\n");`

## Fixtures (input → expected) — parity gate when built
- Period + long pause → break; short pause → none; long pause without sentence-final punct → none.
- `said.\"` (trailing quote trimmed → ends `.`) + long pause → break.
- Empty words / single word / word-count drift beyond tolerance → text unchanged.
- Mid-sentence break dropped when the rescored token at that index isn't sentence-final.
- ReassembleWords: `▁Hel`+`lo.`+`▁World.` → `Word("Hello.",0.0,0.5)`, `Word("World.",2.5,3.0)`; empty → `[]`.
- Discourse fast path (gap 1.1): `So`, `However,` (comma trimmed), `and then` (pair), case-insensitive `OKAY`; a
  non-marker (`the`) → no break.
- Caps: break-after-index 3 suppressed (<9); of candidates {10,14,22} accept 10, reject 14 (close), accept 22.
Full fixtures live in `ParagraphSegmenterTests.swift` — port them verbatim when the milestone lands.

## Swift→C# traps
`TimeInterval`=seconds Double (not ms); `Int(Double)` truncates (use `(int)`); `Split(whereSeparator:)` omits
empties (`RemoveEmptyEntries`); two DIFFERENT trim sets; `isWordStart` uses ORIGINAL token's first char but the
skip uses STRIPPED text; `Set<Int>` sorted explicitly for determinism; keep the `while` collapse loop (not one
Replace); break token is exactly `"\n\n"` (never `Environment.NewLine`); `Word` = `readonly record struct`.

## Enablement (the prerequisite work, out of v1)
1. Thread `(tokenId, frameIndex)` out of the Nemotron decoders (they currently collapse to a string, discarding
   frame indices). 2. Convert frame index → seconds via the model's frame stride/hop. 3. Widen `ITranscriber`
   (or add a parallel API) to return `Word[]` alongside the string. 4. Run `ParagraphSegmenter.Segment` FIRST in
   the pipeline. Until then the segmenter is simply not invoked (spec's "no timings → skipped" path).
