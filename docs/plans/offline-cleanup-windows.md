# Offline Default Cleanup — Windows master design

A deterministic, **on-device, always-on** text pipeline every dictation passes through before it is saved
and pasted. **No LLM, no network, no user-visible processing.** Distinct from AI Rewrite (opt-in) and from
the AI-cleanup toggle that was removed. This doc is airtight by design: every rule, constant, fixture, and
integration edit is pinned so implementation is transcription, not decision-making.

## Provenance — two certainty tiers

| Stage | Source of truth | Fixtures |
|---|---|---|
| FillerWordCleaner (English) | **Ported** from `projects/jot-mobile/.../FillerWordCleaner.swift` | real — port all cases from `FillerWordCleanerTests.swift` |
| NumberNormalizer (English) | **Ported** from `.../NumberNormalizer.swift` → `portspec-numbernormalizer.md` | 51, real |
| ParagraphSegmenter | **Ported** (DEFERRED) → `portspec-paragraphsegmenter.md` | real, ported when built |
| FillerWordCleaner (es/de/fr/it/pt) | **Spec-implemented** — no reference impl exists yet | spec-derived (this doc); reconcile w/ `jot-shared` later |
| ModelArtifactScrubber (`<unk>`) | **Spec-implemented** — no reference impl exists yet | spec-derived (this doc); reconcile later |

The multilingual cleaner and the `<unk>` scrubber are **fully specified** (exact token lists, casing rules,
named anchors, precise scrub rules) but have no canonical Swift/fixtures yet — Mac ships English-only today
and the shared package "lands with the next push". They are implementable now from the spec; their fixtures
below are spec-derived and marked for reconciliation when `jot-shared/JotTextPipeline` exists. Canonical
spec: `docs/plans/cleanup-spec.md`.

## Contract (per-language matrix) + pipeline order

| Language | Paragraphs | `<unk>` scrub | Fillers | Numbers |
|---|---|---|---|---|
| English | deferred (needs timings) | ✓ (Nemotron) | full (um/uh/er/uhm/erm) + sentence recap | ✓ NumberNormalizer |
| es, de, fr, it, pt | deferred (needs timings) | ✓ (Nemotron) | hesitation-only, **no recap** | ✗ |
| all others (ar, ko, ja, …) | deferred (needs timings) | ✓ (Nemotron) | **none — strict byte-for-byte no-op** | ✗ |

Order: `ParagraphSegmenter → (Nemotron only) ModelArtifactScrubber → FillerWordCleaner(lang) → (English only) NumberNormalizer`.
Unknown languages pass through **byte-identical**.

## Scope (user-decided)

**v1 ships together:** `<unk>` scrubber + multilingual filler cleaner + English NumberNormalizer + English
recap + the hidden Advanced off-switch + a new test project. **ParagraphSegmenter is deferred** (blocker
below). The user chose "all at once, skip paragraphs".

## Blocker for ParagraphSegmenter (why it's deferred)

The transcription layer produces **no per-word timings** — `ITranscriber.TranscribeAsync` and the streaming
`Accept`/`Finish` return bare strings; the Nemotron decoders collapse tokens to text discarding frame
indices. Pause-based breaks are impossible without decoder surgery. Per the spec's own "no timings → skipped"
rule, v1 emits single-block transcripts (unchanged from today). Enablement is documented in
`portspec-paragraphsegmenter.md` (thread token→time, widen `ITranscriber`, run segmenter first) — a separate,
cross-cutting project, not coupled to v1.

---

## C# module layout

New namespace **`Jot.Text`** under `src/Jot/Text/`. All **static, pure, no I/O, no DI**, invariant culture
everywhere. **Thread-safety invariant:** every table is `static readonly` immutable and every `Regex` is a
`static readonly` (optionally `RegexOptions.Compiled`) instance with no shared mutable buffer — so `Clean`
(recorder continuation thread) and `CleanPartial` (pill Dispatcher thread) are safe to call concurrently.
One file per stage, mirroring the Swift so fixtures transfer 1:1:

- `TextPipeline.cs` — orchestrator.
- `LanguageCode.cs` — display-name → ISO-639.
- `ModelArtifactScrubber.cs` — `<unk>` fixup (spec).
- `FillerWordCleaner.cs` — English (ported) + multilingual (spec).
- `NumberNormalizer.cs` — English (ported; see `portspec-numbernormalizer.md`).
- `ParagraphSegmenter.cs` + `Word` struct — later milestone.

## Public API

```csharp
public static class TextPipeline
{
    // languageName is ISettingsStore.Language ("English", "German", …). isNemotron gates the scrubber.
    public static string Clean(string text, string languageName, bool isNemotron);
    // Cosmetic live-partial pass (scrubber only; idempotent over the growing partial).
    public static string CleanPartial(string partial, bool isNemotron);
}
public static class LanguageCode { public static string ToIso(string? displayName); } // "" when unmapped
public static class ModelArtifactScrubber { public static string Scrub(string text); }
public static class FillerWordCleaner { public static string Clean(string text, string isoLang); }
public static class NumberNormalizer { public static string Normalize(string text); }
```

## Orchestrator — `TextPipeline.Clean` exact algorithm

```
Clean(text, languageName, isNemotron):
    if text is null/empty: return text
    iso = LanguageCode.ToIso(languageName)          // "en","es",…, or "" (unknown)
    s = text
    if isNemotron: s = ModelArtifactScrubber.Scrub(s)   // Nemotron-only; safe no-op on clean text
    fillerRan = false
    if iso is one of {en, es, de, fr, it, pt}:
        s = FillerWordCleaner.Clean(s, iso)             // includes its own trailing space (see below)
        fillerRan = true
    if iso == "en":
        s = NumberNormalizer.Normalize(s)               // English hard gate
        // NumberNormalizer may consume the filler cleaner's trailing space; re-normalize it:
        s = EnsureSingleTrailingSpace(s)
    return s
    // ParagraphSegmenter would run FIRST, before the scrubber, once timings exist — omitted in v1.
```

- **No-op languages** (iso == "" or not in the 6): scrubber runs (Nemotron artifact fix is language-agnostic
  and safe), filler/numbers do **not**, no trailing space added → **byte-identical to the transcript except a
  genuine `<unk>` artifact fix**, which is the intended Nemotron-only exception. (If strict byte-identity is
  required even for `<unk>`, gate the scrubber too — not recommended; `<unk>` is always a defect.)
- **Trailing space** is owned by `FillerWordCleaner` (matches Mac, step 6). `EnsureSingleTrailingSpace` after
  the number pass guarantees exactly one trailing space survives for English; es/de/fr/it/pt get it straight
  from the cleaner. All-filler input → cleaner returns `""` (no lone space) → orchestrator returns `""`.
- **Idempotent:** every stage is a no-op on its own output, so `Clean(Clean(x))==Clean(x)`.

`EnsureSingleTrailingSpace(s)` = `s.Length==0 ? s : s.TrimEnd(' ','\t') + " "` (only when fillerRan; here only
English reaches it). Verify against fixtures; if `NumberNormalizer` preserves the trailing space in practice,
this is a cheap guarantee, not a behavior change.

## LanguageCode — full name→ISO map

Cover every name in `NemotronLanguages` so the map is total; only the six active codes trigger behavior,
the rest return their ISO (harmless — cleaner no-ops) and unknown → `""`.

```
English→en  Spanish→es  German→de  French→fr  Italian→it  Portuguese→pt        // the 6 active
Chinese→zh  Hindi→hi  Arabic→ar  Japanese→ja  Russian→ru  Korean→ko  Dutch→nl
Polish→pl  Turkish→tr  Ukrainian→uk  Romanian→ro  Greek→el  Czech→cs  Hungarian→hu
Swedish→sv  Danish→da  Finnish→fi  Slovak→sk  Croatian→hr  Bulgarian→bg  Lithuanian→lt
Vietnamese→vi  Estonian→et  Latvian→lv  Slovenian→sl  Hebrew→he  Norwegian→nb
(unknown / null / "None") → ""
```
Case-insensitive lookup. (App collapses Portuguese=pt-BR, Chinese=Mandarin, Norwegian=nb — base code suffices.)

---

## ModelArtifactScrubber (spec-implemented)

Runs before filler cleaning, **only on Nemotron output** (and on live partials). Case-exact `<unk>` only;
idempotent. Rules, in order, on the whole string:
0. **Fast-path (REQUIRED for byte-identity):** `if (!text.Contains("<unk>")) return text;` — case-exact. This
   is not an optimization: rule 4 mutates spacing, and the recorder always calls with `isNemotron: true`, so
   without this guard a no-op-language transcript (ar/ko/ja) with double/edge spaces would be altered,
   breaking the matrix's "strict byte-for-byte no-op". Rules 1–4 run only when a `<unk>` is actually present.
1. **`digit<unk>` → `digit%`:** an ASCII digit `[0-9]`, then optional spaces/tabs (no newlines), then literal
   `<unk>` → the digit(s) + `%`. `25<unk>`→`25%`, `25 <unk>`→`25%`. **ASCII digits only** — after Arabic-Indic
   digits, drop the `<unk>` (rule 3) rather than inserting `%`.
2. **Never cross newlines:** the digit-to-`<unk>` match must not span `\n` — `25\n\n<unk>` keeps its break
   (rule 1 fails; rule 3 turns the stray `<unk>` into a space, trimmed at the boundary).
3. **Any remaining `<unk>` → single space:** `hola<unk>mundo` → `hola mundo` (never glued).
4. **Tidy:** collapse runs of spaces/tabs to one (not newlines), trim edges.

Implementation note: rule 1 needs a small regex `([0-9]+)[ \t]*<unk>` → `$1%` (ASCII digit class, no `\n` in
`[ \t]`); rule 3 `Replace("<unk>", " ")`; rule 4 `[ \t]{2,}`→`" "` + edge trim that preserves interior `\n\n`.
This is the one stage that legitimately uses `System.Text.RegularExpressions`.

**Spec-derived fixtures** (reconcile with `jot-shared`'s 11 when it lands):
`25<unk>`→`25%`; `25 <unk>`→`25%`; `veinticinco 25<unk> por ciento` type → `…25%…`; `hola<unk>mundo`→`hola mundo`;
`25\n\n<unk>`→`25\n\n` (stray dropped, break kept); `no artifact here`→unchanged (no-op); `<unk>`→`` (→ single
space → trimmed to empty); idempotency: `Scrub(Scrub(x))==Scrub(x)`.

---

## FillerWordCleaner — English (ported) + multilingual (spec)

`Clean(text, isoLang)`: `iso` comes from `LanguageCode.ToIso(_settings.Current.Language)`, a base code with no
region subtag (the app stores a display name like "Portuguese", never `pt-BR`, so there is nothing to strip on
Windows). Unknown iso = **strict byte-for-byte no-op**; `en` = English path; `es/de/fr/it/pt` = multilingual path.

### English path — port `FillerWordCleaner.swift` verbatim
Tokens `um(m+)? | uh(h+)? | er(r+)? | uhm | erm`, matched case-insensitively at `\b` word boundaries; the
surrounding pattern consumes adjacent commas + `[ \t]` (NOT `\n`, so `\n\n` breaks survive). Steps:
1. Strip filler → single space. 2. Collapse `[ \t]{2,}`→" ". 2.5 Trim space adjacent to `\n\n` (both sides).
3. Remove orphan `" [,.?!]"`→"". 4. Strip leading `.,?! \t`. Trim trailing ` \t`. 5. Recapitalize sentences.
6. Append one trailing space iff non-empty.

Recap (`recapitalizeSentences`, verbatim): flag `capitalizeNext=true` initially; a `. ! ?` sets it; a
letter/number clears it (uppercasing the letter if the flag was set); whitespace/newline does NOT reset the
flag (it "waits"). **No abbreviation guard exists in the reference** — ship the plain recap for v1 (the English
fixtures don't exercise abbreviations). The spec's "abbreviation-aware (Dr./e.g./U.S.)" is a future
enhancement with no defined list; add it only when `jot-shared` defines the list. Flagged, not invented.

**English fixtures (ported from `FillerWordCleanerTests.swift` — trailing space is intentional):**
`"Um, I think"`→`"I think "`; `"I, uh, mean"`→`"I mean "`; `"Ummmm yes"`→`"Yes "`; `"umbrella"`→`"umbrella "`;
`"Um. Uh."`→`""`; `""`→`""`; `"Hello world."`→`"Hello world. "`; `"Hello.\n\num New paragraph."`→
`"Hello.\n\nNew paragraph. "`; `"Hello.\n\num world."`→`"Hello.\n\nWorld. "`; `"yeah uh okay"`→`"Yeah okay "`;
`"hello um world"`→`"Hello world "`; `"this is uh really fast"`→`"This is really fast "`;
`"yeah uh um okay"`→`"Yeah okay "`; non-empty output ends in exactly one space; empty output has no space.

### Multilingual path (spec-implemented, es/de/fr/it/pt)
Same strip→tidy skeleton as English, with three differences: language-specific tokens only, **exposed-word
capitalization instead of blanket recap**, and Spanish inverted punctuation.

**Token regexes (case-insensitive, `\b`-anchored, one per token; elongate the final char like English `um(m+)?`):**
| iso | regexes |
|---|---|
| es | `eh(h+)?`, `em(m+)?` |
| de | `äh(h+)?`, `ähm(m+)?`, `öh(h+)?`, `hm(m+)?` |
| fr | `euh(h+)?` |
| it | `ehm(m+)?`, `m{3,}` (3+ m's, so "mm" millimetres survives) |
| pt | `hum(m+)?` |
Assemble exactly like English: `[ \t]*,?[ \t]*\b(?:<alt>)\b[ \t]*,?[ \t]*` → single space (never consumes
`\n`). These are non-lexical hesitation sounds only. **Never** strip lexical/discourse fillers (es
este/pues/o sea, de halt/genau, fr ben/du coup, pt tipo/né, it cioè/allora) — the `\b` + exact token set is
the safety property. The German anchors "ähnlich", "er kommt um drei Uhr" are safe because none of
"ähnlich"/"er"/"um" is a token.

**Casing — capitalize ONLY the exposed sentence-initial word (deterministic, no blanket recap):**
1. Before replacing, for each match record whether it is **sentence-initial**: the preceding text, with
   trailing `[ \t\r\n]` skipped, is empty (string start) OR ends in `.`/`!`/`?` — or, for es, an opening `¡`/`¿`.
2. Replace all matches with a single space and run English steps 1–4 (collapse `[ \t]{2,}`, trim around `\n\n`,
   drop orphan `" [,.?!]"`, strip leading punctuation/space).
3. For each recorded sentence-initial position, uppercase the first alphabetic char of the word now at that
   position. **No other casing change** — the model's own casing stands. (This is *why* there is no blanket
   recap: it would wrongly re-case after "z. B." / "M." using English abbreviation logic.)
4. Append one trailing space iff non-empty (English step 6).

**Spanish inverted punctuation:** before the general strip, delete a fully-wrapped interjection wholesale —
`¡[ \t]*(?:<es-token>)[ \t]*!` and `¿[ \t]*(?:<es-token>)[ \t]*\?` → "" (so `"¡eh!"`→`""`). Inside a sentence
the token strips normally and the enclosing `¿…?`/`¡…!` survives.

**Spec-derived fixtures (single unambiguous expected values; reconcile with `jot-shared`'s 53 later):**
| iso | input | expected |
|---|---|---|
| de | `"Ähm, hallo"` | `"Hallo "` |
| de | `"das ist ähnlich"` | `"das ist ähnlich "` (untouched, no recap) |
| de | `"er kommt um drei Uhr"` | `"er kommt um drei Uhr "` (untouched) |
| de | `"ich ähm weiß"` | `"ich weiß "` (mid-sentence; "ich" stays lowercase) |
| fr | `"euh bonjour"` | `"Bonjour "` (exposed at start) |
| fr | `"je euh pense"` | `"je pense "` (mid; no recap) |
| fr | `"eh bien"` | `"eh bien "` (fr token is euh, not eh → untouched) |
| it | `"ehm sì"` | `"Sì "` |
| it | `"due mm tre"` | `"due mm tre "` ("mm" survives) |
| it | `"ehm, mmm"` | `""` (both stripped) |
| es | `"em hola"` | `"Hola "` |
| es | `"hola eh mundo"` | `"hola mundo "` (mid; "hola" stays as the model wrote it) |
| es | `"¿Verdad, eh?"` | `"¿Verdad?"` |
| es | `"¡eh!"` | `""` |
| es | `"pues bien"` | `"pues bien "` ("pues" is discourse → untouched) |
| pt | `"hum certo"` | `"Certo "` |
| pt | `"tipo assim"` | `"tipo assim "` ("tipo" untouched) |

The token-safety anchors are certain; the exposed-word capitalization is fully specified above. Only the exact
casing of "exposed sentence-initial word" cases is a reconciliation point when `jot-shared` ships its fixtures.

---

## NumberNormalizer (English, ported)

Full build guide in **`portspec-numbernormalizer.md`** (control flow, all constant tables, 51 fixtures,
Swift→C# traps). Key facts for integration: pure regex-free table walker; whole-string in/out; **no internal
language gate** — the orchestrator applies the English-only gate (`iso == "en"`). Invariant culture; forced
`,` grouping via `ToString("#,0", InvariantCulture)`; `¢`=U+00A2; phone-shape (≥7 single-digit words) bails
out the whole string. Port `NumberNormalizer.swift` 1:1 and port all 51 `NumberNormalizerTests.swift` cases.

**Spec caveat (do NOT "fix" the port to match it):** `cleanup-spec.md` gives `"thirty second timeout"` →
`"30-second timeout"`, but the reference Swift has **no hyphenated `30-second` behavior** — it treats "thirty
second" as a tens-ordinal → `"32nd timeout"`. The port follows the Swift + `NumberNormalizerTests.swift`; the
spec's example is the wrong one.

---

## Integration diffs (exact)

### 1. `Recording/RecorderController.cs` — final transcript
Insert one line after the `final text len` log (`~164`), before the `IsNullOrWhiteSpace` gate (`~166`), so
all-filler still routes to `NothingTranscribed`:
```csharp
Log($"final text len={text.Length}");
if (_settings.Current.OfflineCleanupEnabled)
    text = Jot.Text.TextPipeline.Clean(text, _settings.Current.Language, isNemotron: true);
if (string.IsNullOrWhiteSpace(text)) { ... NothingTranscribed ... }
else { _store.Add(BuildRecording(result, text)); ... PasteAtCursor(text, ...); TranscriptReady?.Invoke(text); }
```
`isNemotron: true` — the wired `ITranscriber` is always Nemotron (`App.xaml.cs` DI); pass a real flag so it
stays correct if Parakeet is ever wired. No DI change (pipeline is static). `TitleFrom(text)` already trims
for the Recents title, so the trailing space isn't shown.

### 2. `Services/PillController.cs` — live-partial `<unk>` scrub (cosmetic)
At `OnPartial` (`~111`): `if (_recorder.State == RecorderState.Recording) _pill?.SetLiveText(
_settings.Current.OfflineCleanupEnabled ? Jot.Text.TextPipeline.CleanPartial(text, isNemotron: true) : text);`
(`PillController` already has `_settings` injected). Partials grow (full string re-emitted), so
`CleanPartial` = scrubber only, idempotent; the authoritative clean is the final pass in §1.

### 3. Settings — the hidden Advanced off-switch (default ON)
Exact inverse of the removed `CleanupEnabled`:
- `Services/Abstractions/ISettingsStore.cs`, on the **`JotSettings` POCO** (not the interface — interface
  members can't carry `= true`): `public bool OfflineCleanupEnabled { get; set; } = true;` No `JsonSettingsStore`
  change: `Reset()` copies every property by reflection and `System.Text.Json` serializes all public props, so
  the default-true field round-trips and resets for free.
- `ViewModels/SettingsViewModel.cs`: `[ObservableProperty] private bool _offlineCleanupEnabled = true;` +
  seed `_offlineCleanupEnabled = S.OfflineCleanupEnabled;` + `partial void OnOfflineCleanupEnabledChanged(bool v)
  { S.OfflineCleanupEnabled = v; Save(); }`
- `Views/SettingsPage.xaml`, inside `AdvancedPanel`: a `SettingRow` "Clean up transcripts" / "Fix filler words,
  casing, and numbers on-device — no AI, no network." with `<ui:ToggleSwitch IsChecked="{Binding
  OfflineCleanupEnabled, Mode=TwoWay}" />`.

---

## Resolved edge cases

- **Trailing space:** owned by `FillerWordCleaner`; orchestrator re-ensures exactly one after the English
  number pass; no-op languages get none (byte-identical). `PasteAtCursor` adds no spacing of its own (verified),
  so no doubles.
- **All-filler → nothing:** cleanup runs before the whitespace gate → `""` → `NothingTranscribed`.
- **Off-switch:** gates both the final pass and the partial scrub → raw transcript delivered verbatim.
- **Idempotency + no-op-language byte-identity:** property-tested (below).
- **`isNemotron`:** always `true` today; explicit flag keeps the scrubber correct under a future engine.
- **One stored string:** Recents keeps the delivered (cleaned, or raw if disabled) string — no raw/cleaned split.

## Test project

Add **`tests/Jot.Tests/Jot.Tests.csproj`** (xUnit, `net10.0-windows10.0.26100.0`, `<Platforms>x64</Platforms>`,
`ProjectReference` → `src/Jot/Jot.csproj`). There is **no `.sln` in the repo today** — create `Jot.sln` at the
repo root referencing both `src/Jot/Jot.csproj` and `tests/Jot.Tests/Jot.Tests.csproj`, and run the suite with
`dotnet test tests/Jot.Tests/Jot.Tests.csproj -c Release`. Test classes mirror the stages:
- `FillerWordCleanerTests` — every English case from `FillerWordCleanerTests.swift` + the spec-derived multilingual anchors.
- `ModelArtifactScrubberTests` — the spec-derived scrubber fixtures.
- `NumberNormalizerTests` — all 51 ported fixtures (the parity gate).
- `TextPipelineTests` — order, English gate, off-switch, all-filler→empty, and two **property tests**:
  (a) idempotency `Clean(Clean(x))==Clean(x)` over a corpus; (b) **no-op-language byte-identity** — for a
  non-{en,es,de,fr,it,pt} language, `Clean(x, lang, isNemotron:true) == x` for `x` without `<unk>` (uses
  `isNemotron:true` deliberately, to exercise the scrubber's rule-0 fast-path — the production path).
- `ParagraphSegmenterTests` — ported but `[Trait("Category","deferred")]`/skipped until the milestone.

## Milestones, effort, risk

- **v1** (this doc, minus paragraphs): LanguageCode + scrubber + filler (en+multilingual) + NumberNormalizer +
  orchestrator + settings + `Jot.Tests`. Wire into RecorderController + PillController. ~4–6 focused sessions;
  the NumberNormalizer port + 51-fixture parity is the long pole.
- **Later**: ParagraphSegmenter, after decoder timing plumbing (`portspec-paragraphsegmenter.md`).

**Risks & mitigations:** (1) NumberNormalizer parity — port 1:1, run all 51 fixtures, then an adversarial
review pass comparing C# vs Swift on edge inputs before shipping. (2) Multilingual/scrubber are spec-derived
— ship with the spec anchors as tests, and reconcile with `jot-shared/JotTextPipeline` fixtures when it lands
(track as a follow-up). (3) Grapheme vs UTF-16 tokenization — English transcripts make a `char`/rune walk
safe; documented divergence.

## Reference files
- `docs/plans/cleanup-spec.md` — the canonical feature spec (fetched).
- `docs/plans/portspec-numbernormalizer.md` — full NumberNormalizer build guide + 51 fixtures.
- `docs/plans/portspec-paragraphsegmenter.md` — full ParagraphSegmenter guide + timing shape (deferred).
- On disk: `projects/jot-mobile/Jot/App/Transcription/{FillerWordCleaner,NumberNormalizer,ParagraphSegmenter}.swift`
  + `projects/jot-mobile/Jot/Tests/*Tests.swift` — the reference implementations and canonical fixtures.
