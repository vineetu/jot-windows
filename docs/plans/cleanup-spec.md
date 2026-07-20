# Jot — Offline Default Cleanup (on-device, no LLM)

Every transcript passes through a **deterministic pipeline** before delivery — all **on-device, no
network, no model**. This is what runs by default on *every* dictation (the optional AI "Transform" is a
separate LLM pass documented elsewhere).

> **Updated 2026-07-20 — multilingual support shipped.** The pipeline is no longer English-only: filler
> cleaning now covers es/de/fr/it/pt, and a new artifact scrubber fixes Nemotron `<unk>` leaks. This page
> reflects the current implementation. Canonical reference: the `JotTextPipeline` Swift package in
> `jot-shared` (the multilingual additions land there with the next push; fixtures pin every behavior —
> 100+ English, 53 multilingual, 11 scrubber cases).

**Pipeline order:** ParagraphSegmenter → *(Nemotron outputs only:)* ModelArtifactScrubber →
FillerWordCleaner → *(English only:)* NumberNormalizer.

## Per-language behavior matrix

| Language | Paragraphs | `<unk>` scrub | Fillers | Numbers |
|---|---|---|---|---|
| English | ✓ | ✓ (Nemotron) | full (um/uh/er/uhm/erm) + recap | ✓ NumberNormalizer |
| es, de, fr, it, pt | ✓ | ✓ (Nemotron) | hesitation-only, **no recap** | ✗ (model-native or none — see notes) |
| all others (ar, ko, ja, …) | ✓ | ✓ (Nemotron) | none (strict no-op) | ✗ |

---

## 1. ParagraphSegmenter — pause-based paragraph breaks (language-agnostic)

Needs per-word timings. Inserts `\n\n` when:
- **Primary rule:** pause **> 1.4 s** between adjacent words AND the previous word ends in `.` `!` `?`
  (after trimming trailing quotes/brackets).
- **Discourse-marker fast path:** pause **> 1.0 s** AND previous word sentence-final AND next word (or
  "and then") is a known discourse marker.
- **Safety caps:** no break before word 10; no break within 8 words of the previous break; `\n\n\n\n`
  collapses to `\n\n`.

No timings (streaming paths) → skipped; the other passes still run.

---

## 2. ModelArtifactScrubber — `<unk>` token cleanup (Nemotron outputs only)

**Why it exists (measured):** the Nemotron tokenizer has **no `%`, `€`, or `$` in its vocabulary**, so
its inline Spanish ITN emits the literal string `25<unk>` for "veinticinco por ciento" — which used to
reach pasted text. Rules (case-exact `<unk>` only; idempotent; guaranteed no-op on text without it):

- ASCII digit + optional spaces/tabs + `<unk>` → digit + `%` — e.g. `25<unk>` → `25%`, `25 <unk>` → `25%`.
  (**ASCII digits only** — after Arabic-Indic digits the `<unk>` is just dropped, since Arabic typography
  uses `٪` and the %-evidence was only measured on the latin ship.)
- The matcher **never crosses newlines** — `25\n\n<unk>` keeps its paragraph break.
- Any remaining `<unk>` → replaced with a single space (so `hola<unk>mundo` → `hola mundo`, never glued),
  then doubled spaces collapse and edges trim.

Run this **before** filler cleaning, and **only** on Nemotron-family outputs (v3 doesn't emit `<unk>`).
Also run it on **live streaming partials** so the preview UI never flashes `<unk>` mid-dictation.

---

## 3. FillerWordCleaner — hesitation strip (language-aware)

API shape: `clean(text, language:)` where language is a lowercase code; region subtags are stripped
(`fr-FR`/`pt_BR` → `fr`/`pt`); unknown codes are a **strict byte-for-byte no-op**; `nil`/`en` = English.

**Token lists (exhaustive — do not add to these):**

| Lang | Tokens (plus natural elongations, e.g. `ummm`, `euhh`) |
|---|---|
| en | um, uh, er, uhm, erm |
| es | eh, em |
| de | äh, ähm, öh, hm |
| fr | euh |
| it | ehm, mmm (3+ m's — so "mm" millimetres survives) |
| pt | hum |

Every token is a **non-lexical hesitation sound** — never a real word in its language. That's the safety
property. **Never** strip lexical/discourse fillers (es *este/pues/o sea*, de *halt/genau*, fr *ben/du
coup*, pt *tipo/né*, it *cioè/allora*) — they're homographs of content words; deleting them corrupts
meaning. Critical anchors (all fixture-pinned): German "ähnlich" and "er kommt um drei Uhr" untouched
("er"/"um" are German words — proof lists can't be shared across languages); French "eh bien" untouched;
"mm" untouched.

**Capitalization differs by language — this matters:**
- **English:** blanket sentence recap (first word + after `.` `!` `?`), abbreviation-aware (Dr., e.g.,
  vs., U.S., …) — unchanged legacy behavior for under-cased models.
- **Non-English:** **NO blanket recap.** The model's own casing is authoritative (these models emit
  correctly-cased text, and the abbreviation list is English — it would wrongly capitalize after German
  "z. B." / "ca." or French "M."). Capitalize ONLY a word directly exposed by stripping a
  sentence-initial filler ("Ähm, hallo" → "Hallo").
- **Spanish paired punctuation:** `¡` `¿` are handled — "¿Verdad, eh?" → "¿Verdad?" (sentence enders
  survive filler removal; a fully-wrapped "¡eh!" drops wholesale as a self-contained interjection).

---

## 4. NumberNormalizer — spelled numbers → digits (**English ONLY — hard gate**)

Deterministic, context-aware (money/percent/year/time/address/decimal/cardinal-≥10 rules; phone-shape
bail-out; colloquial "a couple" preserved). Tuned against 2,711 real English recordings, 0 false
positives. Examples: "fifty percent"→`50%`, "five dollars and fifty cents"→`$5.50`, "two thirty"→`2:30`,
"twenty twenty six"→`2026`, "thirty second timeout"→`30-second timeout`.

> **The gate is not optional.** Its spelled-cardinal rules are English-hardcoded — French "six cents"
> (= 600) would become "6¢". Run it **only** when the transcript language is English (`nil`/unknown counts
> as English for the legacy path). Japanese/Chinese are never touched.

**Why other languages don't need it (measured, 2026-07):** Parakeet v3 emits digits natively for
es/de/fr; Nemotron-latin emits digits for Spanish. The one real gap — **Nemotron French spells numbers
out** — is deliberately unfixed: the only battle-tested multilingual ITN library turned out to be
English-only in its ITN direction (it mangles "six cents euros" into "$0.06 euros"). Verbatim words are
correct output; do NOT attempt a hand-rolled French normalizer.

---

## Summary for the Windows port

1. Pull `JotTextPipeline` from `jot-shared` as the source of truth (token lists, abbreviation set, the
   full rule tables, and every fixture live there).
2. Order: paragraphs → scrub (Nemotron only, partials included) → fillers (language-aware) →
   numbers (English only).
3. The per-language matrix above is the complete contract — unknown languages must pass through
   byte-identical.
4. This offline pass always runs; the LLM "Transform" is separate and opt-in.