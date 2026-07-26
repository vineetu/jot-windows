# Custom Vocabulary on Windows — UX design (v1.3)

> Status: **DESIGN.** Written 2026-07-25; revised 2026-07-25 to fold in the adversarial review
> ([`review-vocabulary-ux.md`](review-vocabulary-ux.md)) and the owner decisions in §0a2; revised
> again 2026-07-25 for the **M0 engine spike**, which added three UX-visible decisions — **D10**
> (some dictations get their corrections *after* the paste, and some get none at all), **D11** (a
> licence attribution surface is now required), and **D12** (a whole class of terms can **never**
> work, and the UI must say so). The
> **gate half is ported and fixture-green in-repo**; every UI surface below is unbuilt.
> Downstream of [`vocabulary-ctc-port.md`](vocabulary-ctc-port.md) (engine architecture) and
> [`vocabulary-plan.md`](vocabulary-plan.md) (the storage/L1 half). Mined from the **shipping**
> Mac and iOS implementations rather than invented:
> `JOT-Transcribe/docs/vocabulary-gate/{design,feedback-ux,ask-ux,review-ux,verification}.md`,
> `JOT-Transcribe/Sources/Vocabulary/*`, `jot-mobile/docs/keyboard-*`,
> `jot-shared/Sources/JotVocabCore/*`.
>
> Every recommendation below is either (a) copied from a shipping surface, (b) copied
> from a documented failure on those platforms, or (c) an explicit deviation with a
> stated Windows-specific reason.
>
> **Citation convention (changed in this revision).** Where a component is already ported, the
> citation is the **C# file in `src/Jot/`** — that is what the implementer has open. Swift
> citations remain only for things not yet ported. **Symbol names are authoritative; line numbers
> into actively-edited files (`src/Jot/Vocabulary/*.cs` above all) are advisory — grep the symbol,
> don't trust the number.**

---

## 0a. OWNER DECISIONS — settled 2026-07-25 (these override the [DECIDE] markers below)

1. **DECIDE-4 · Learn loop: IN for v1.3.** Port `CorrectionStore` + the review surface under the
   transcript. The correction chip therefore also ships (a signal with recourse).
2. **DECIDE-3 · Ask-before-paste: BUILD IT IN v1.3.** Overrides this doc's original
   recommendation to defer. `AskPolicy`, `blockedKeeps` and `suppressedBlocks` are all IN scope.
   §4 is rewritten around what that actually gets us.
3. **DECIDE-2 · Dedicated `VocabularyPage`**, built on the PromptsPage pattern. Not an inline
   Settings block.

## 0a2. Post-review decisions — settled 2026-07-25, do not re-open

| # | Decision | One-line rationale |
|---|---|---|
| **D5** | **Language gating is explicit selection only.** Vocabulary runs iff the user has explicitly chosen an English locale. **Auto-detect ⇒ vocabulary OFF**, stated plainly in the UI. | Both transcribers discard the `<xx-YY>` locale token and the fp16 engine exposes no accessor at all, so per-recording resolved language does not exist today (§6). |
| **D6** | **Vocabulary may never break a dictation.** Any spotter/gate exception falls back to the raw transcript and still reaches `_store.Add` **and** `PasteAtCursor`. | `StopAndDeliverAsync`'s `catch` (`RecorderController.cs:267-271`) skips both. Engine-side detail in `vocabulary-ctc-port.md §3.6`. |
| **D7** | **One storage layout: `<DataDir>\Vocabulary\`**, registered in `JotDataPurge.DataSubdirs` **and** `DataFolderMigrator.MigratedItems`. | Otherwise vocabulary survives Erase-all-data and is stranded by a data-folder move (§5.5). |
| **D8** | **The ask card is redrawn around an APPLIED correction, and the caller filters the deck to `Outcome == "applied"` before `AskPolicy.Select`.** Blocked proposals therefore get no ask; they are reviewable only on the transcript surface (§5). | `AskPolicy.WorthAsking` (`AskPolicy.cs:98-104`) selects `Outcome == "applied"` **or `Prior > 0`**. A first-encounter common-word block falls through every branch — but once the user teaches the pair (`Prior ≥ 1`) it would be asked on *every* later dictation, forever, because `Decide`'s override branch requires `!isCommon` (`VocabularyGate.cs:582`) so it stays blocked. The one-line caller-side filter is what makes D8 true rather than aspirational (§4.1). |
| **D9** | **Build the ask card by cloning `PromptPickerWindow`. Do not touch `PillWindow`.** | The app already ships an activatable, keyboard-driven overlay on the paste path; the real hazard is the paste *target*, not `WS_EX_NOACTIVATE` (§4.3). |

## 0a3. M0-spike decisions — settled 2026-07-25, do not re-open

Engine detail in [`vocabulary-ctc-port.md`](vocabulary-ctc-port.md) §3.5, §3.3, §2. What each one
costs *this* doc:

| # | Decision | What it changes here |
|---|---|---|
| **D10** | **Corrections stay in the pasted text — but degrade gracefully.** Measured cost is **1987 ms for 60 s of speech** on CPU (super-linear: 180 s = 9662 ms). So: a **2.5 s deadline**; past it the **raw** transcript is pasted and corrections are applied to the **saved recording** instead; above **120 s** of trimmed speech the spotter is **skipped entirely**. | A third delivery mode nobody designed: *corrected after the fact*. §3.3b specifies it — the chip only appears if the pill is still up (`PillState.Success` lingers **4000 ms**), it is **never** resurrected, and there is **no ask card** on that path (§4.6). Also settles DECIDE-8's term cap as a real latency budget with real numbers. |
| **D11** | **We redistribute two CC-BY-4.0 artifacts**, so an **attribution surface is required** — a "Models & licences" `ui:Card` on `Views/AboutPage.xaml`. | One new card, in M2 (engine milestone), not M3. Verified: `AboutPage.xaml` has **no** attribution section today. The Vocabulary UI itself carries none of this — About is the right home. |
| **D12** | **Term validation is a product requirement.** Length-1 terms encode to **nothing**; the model's 1024-piece English BPE has **no digits, no hyphen, no accented Latin, no CJK**, so `Wi-Fi`, `café`, `3.14`, `Zürich` can **never** be spotted. `CtcTokenizer.IsSpottable` detects it and the UI must say so. | A **new, previously undesigned state** on every term-entry surface — the page form (§2.3), the inline warning table (§2.4), bulk import (§2.5), and the right-click dialog (§2.7). It also **narrows the promise**: the headline copy "names, products, jargon" is only honest for plain unaccented A–Z words (§2.1). |

## 0b-RESOLVED (owner, 2026-07-25) — ship jot-shared's `AskPolicy` as-is

> "When it for sure knows a particular word, it doesn't ask, but maybe if it asks, it's fine.
> But once the user says, it should not ask again if it is not confused… Just do what we do in
> jot-shared for now. We can do any updates later."

**Decision: port `AskPolicy` unchanged and ship it. Do NOT design a Windows-specific score gate
now.** Ported + conformance-green 2026-07-25 (`AskPolicy.cs`, `CorrectionRecord.cs`, 10 golden
cases from `ask_policy_select.json`; mutation-checked).

**CORRECTED — what `AskPolicy` actually gives us on this path.** The previous revision sold four
guarantees. **Two are live; two are dead code on the detection path:**

| Guarantee | Status | Evidence |
|---|---|---|
| An answered pair lands in `keyboardSuppressed` and is **never asked again** | **LIVE** | `AskPolicy.WorthAsking`'s first line (`AskPolicy.cs:100`) |
| The whole payload is capped at **`MaxAsks = 3`** | **LIVE** | `AskPolicy.cs:23` (declared), `:111` (`.Take(MaxAsks)`) |
| "Always replace" grant stops consuming ask budget | **DEAD** | `Granted()` reads `OverrideEntry.AlwaysReplace` (`AskPolicy.cs:82-91`, the field read at `:88`); DECIDE-9 ships no always-replace in v1.3 ⇒ always `false` |
| Merge-teach phrases get exactly one card ever | **DEAD** | `MergeTeachEligible` requires `r.Shape == "merge"` (`AskPolicy.cs:70`); `ApplyFromDetections` never sets a shape (§1) |
| *(consequence)* the card can offer an alternate suggestion | **DEAD** | `Selection.AltTerm`/`AltFind` (declared `AskPolicy.cs:27-31`, populated at `:131-132`) read `r.Alternates`, always `[]` on this path (`VocabularyGate.cs:491`) |

**So the honest worst case is not "3 asks on the first dictation, then quiet."** It is: **up to 3
asks per dictation, for every newly-applied, still-unanswered pair, on every dictation containing
one** — because nothing is written to `keyboardSuppressed` until the user *answers*. Ignoring a card
is not answering it (§4.5). That is still acceptable to ship — the user converges by answering, and
the cap is hard — but it must be written down truthfully so nobody is surprised in calibration.

**Revisit only if real use shows it asking too much.** §0b's option (a) — calibrate on the spotter's
own CTC score — remains the first thing to try, and M2's score distribution makes it cheap.

## 0b. (superseded — kept for the reasoning) The ask's throttling signals are inert here

This doc's §1 establishes (and `Jot.Tests` pins in `DetectionPath_ConfidenceSignalsAreInert`,
`VocabGoldenFixtureTests.cs`) that on the Windows engine path: `Confidence` ≡ 0.85, `Margin` ≡ 0,
`Unsure` ≡ false, and **`AskCandidate` is true for every single-word apply and every plausible
common-word block.** The options weighed at the time were (a) use the spotter's CTC score as a
confidence proxy, (b) per-pair suppression only, (c) a hard cap per dictation, (d) ask only on
blocks. The owner chose "ship `AskPolicy` unchanged", which is (b) + (c) as implemented — and, per
D8, (d) is **out**: `AskPolicy` cannot select a *first-encounter* block, and the caller filters the
deck to `Outcome == "applied"` so a taught one can't leak in either (§4.1).

**Second prerequisite from the original risk list — RETIRED.** It said the pill is
`WS_EX_NOACTIVATE`, so a keyboard-driven card sits on the paste hot path, and budgeted a spike. That
framing was wrong and pointed at the wrong risk; see §4.3 (D9).

---

## 0. TL;DR — the six shapes

| Area | Shape chosen |
|---|---|
| 1 · Managing terms | Settings → **Vocabulary** section (toggle + model status + language honesty + "Manage terms…") that navigates to a **dedicated `VocabularyPage`** built on the PromptsPage list pattern, **with its own Back button**. Aliases are first-class, not Advanced-gated. Add-from-transcript via a right-click **"Add to Vocabulary…"** on the recording detail transcript. |
| 2 · Moment of correction | **Silent + a chip on the existing `PillState.Success`.** No new pill state, no extra beat, no blocking. Detail lives in the pill's existing expand panel. One opt-out toggle. **On a long dictation the correction may arrive after the paste (D10) — then the chip appears only if the pill is still up, and never comes back if it isn't (§3.3b).** |
| 3 · The "did you mean?" ask | **BUILT in v1.3** (owner decision), as a **clone of `PromptPickerWindow`** — never a change to `PillWindow` — and **drawn around an APPLIED correction**, because that is the only record `AskPolicy` can select. Blocked near-misses get **no ask**; they live on the review surface only. |
| 4 · Review + learn | A **`ui:CardExpander` review block on `RecordingDetailPage`**: "Jot guessed on N words" → per-occurrence rows → pick the word you meant → transcript edits + `CorrectionStore` delta. Ported copy from the Mac. |
| 5 · Language honesty | Spotter is English-only, **and v1.3 requires an explicitly-selected English locale**. In any other language *and in auto-detect*, the Vocabulary section shows a persistent `ui:InfoBar` and vocabulary does nothing. The list stays editable. Never partially enable — the Spanish `lista→Lisa` incident is why. **Plus a second honesty axis (D12): even in English, a term with a digit, a hyphen, an accent or CJK can never be spotted — said per-term, at entry (§2.4).** |
| 6 · First-run / discovery | **No proactive nudge, no first-run tour.** Ships **visible** inside Advanced features, master toggle **default off**, with an "Experimental" badge — see §7.1 (superseded 2026-07-25: hidden *and* default-off meant nobody would ever use it, so no real-speech calibration data could arrive). Discovery = the right-click entry point + a `TourCatalog` entry + a Help card. |

**Top 3 risks:** (1) the Mac's three anti-annoyance signals (`unsure`, `notable`, the narrow ask
band) are **all constant on our engine path** — inheriting them buys nothing and hides the real
problem; (2) English-only spotter vs 40 locales *and* an auto-detect mode — **now compounded by
D12's character limit, which bites even inside English**; (3) placement without word
timings can land a correction on the wrong occurrence, so review rows must never claim more precision
than the anchor has.

**Added by the M0 spike (risk 4):** the feature's promise — *the term appears in what you paste* —
holds only under ~60 s of speech. Between 60 s and 120 s it degrades to "corrected on the recording";
above 120 s it does nothing (D10). The UI must never claim otherwise, and §3.3b is where that
honesty lives.

---

## 1. What the Windows engine can actually tell the UI

This section exists because the Mac's UX docs are built on signals that **do not vary on our path**.
Getting this wrong would produce a design that looks tuned and is not.

Windows runs Nemotron RNNT → bare text, no per-word confidence. Vocabulary therefore comes from
**`VocabularyGate.ApplyFromDetections` (`src/Jot/Vocabulary/VocabularyGate.cs:372`** — ported;
jot-shared original `VocabularyGate.swift:469`), the same entry point the Mac uses for its own
Nemotron path. Traced through `Decide(...)` (`VocabularyGate.cs:535-613`):

| Signal | On the TDT/rescore path (Mac default) | **On our detection path** |
|---|---|---|
| `Confidence` | real per-token softmax | **always exactly `LowConfidence` (0.85f)** — declared `:43`, pinned at `:555` because `wordConfidence` is empty. *(`:40` is `ConfidenceCeiling = 0.95f` — do not confuse them.)* |
| `Margin` | real CTC score delta | **always 0** |
| `Unsure` | `measured ∈ [0.85, 0.95)` | **always `false`** (`measured` is null, `:550`) |
| Step (3) confidence-ceiling block | fires (the "0.998 name" protector) | **never fires** (`:600`, 0.85 < 0.95) |
| `AskCandidate` | narrow | **`true` for every single-word APPLY (`Decide` step (5), `:612`) and every common-word BLOCK (step (4), `:608`)** |
| `Alternates` / `Shape` | populated | **always `[]` / `null`** (`:491-492`) — no merge lane, no 3-option ask |

Three direct consequences the rest of this doc obeys:

1. **Do not implement the Mac's `notable` filter.** Its definition (`feedback-ux.md §3`) is
   `term.contains(" ") || margin >= 4.0 || confidence <= 0.85 || decision == .override`. With
   confidence pinned at exactly 0.85, `notable` is **true for 100% of our applied corrections**.
   Building it would be dead code that *looks* like an anti-annoyance control. The Mac already admits
   it "rarely subtracts anything" even on its own path and that "the real v1 throttle is the Settings
   opt-out" (`feedback-ux.md §6 rule 2`). On Windows: ship the opt-out, skip `notable`.
2. **Do not trigger an ask off `AskCandidate` alone.** It is true for essentially every correction we
   will ever make. The ask is gated by `AskPolicy`'s *history* signals instead (§4).
3. **The guards that survive are the ones that matter**: `Decide` step (1) plausibility (`:591`),
   step (4) the common-word brake (`:607`), and step (0) learned overrides (`:572-584`). Our whole
   safety story is those three.

### 1b. Two things Windows has that Mac/iOS do not — and they are gifts

**(a) There is no post-gate transform chain.** `RecorderController.StopAndDeliverAsync` runs, in
order: `live/batch transcribe → TextPipeline.Clean (:226, conditional on OfflineCleanupEnabled) →
_store.Add (:235) → PasteAtCursor (:244) → TranscriptReady?.Invoke(text) (:253)`. The AI cleanup pass
was removed 2026-07-20. So if we insert the gate **after** `TextPipeline.Clean` and **before**
`_store.Add`, the gate's `Proposal.PublishedStart`/`PublishedLength` (`VocabularyGate.cs:77-78`,
set in `ApplyFromDetections` at `:489-490`) are valid against the exact string that is saved *and*
pasted.

This deletes the single largest source of bugs on the other two platforms. iOS's
`correction-review-implementation.md §11` records anchors that were *"stale FROM BIRTH"* with a
nearest-match fallback *"routinely resolving — sometimes onto the wrong occurrence"*, which they had
to delete outright. **We can ship strict-offset resolution with no reconcile pass at birth.** Say it
loudly in the implementation ticket, and add a guard test that fails if a text mutation is inserted
between the gate and `_store.Add` — with **exactly one whitelisted exception, the §4.4 ask splice**,
which must route through `CorrectionProvenance.ShiftAnchors` and re-stamp the §5.5 fingerprint on
the post-splice string. Nothing else may write `text` there.

**Note the boundary precisely:** offsets are exact *at save time* and stale forever after. §5.5
designs what happens when the user later edits the transcript.

**(b) There is one channel, not two.** The Mac's `feedback-ux.md` burned an entire review round on
discovering its success pill is delivery-driven, not result-driven, so the corrections payload had to
be re-plumbed onto `DeliveryEvent`. On Windows there is exactly one call site:
`TranscriptReady?.Invoke(text)` at `RecorderController.cs:253`, consumed by
`PillController.OnTranscriptReady` (`Services/PillController.cs:178`). Widening it to
`TranscriptReady?.Invoke(text, corrections)` is a two-file change.

**One trap inherited from the Mac (`feedback-ux.md §9`):** `PillController.Attach()` also routes
`_rewrite.Succeeded → OnTranscriptReady` (`PillController.cs:69`). A voice-rewrite result never
passes through the gate. It must pass an **empty** corrections list explicitly, or a rewrite will
inherit a stale chip from the previous dictation.

### 1c. Surfaces that already exist (do not invent controls)

| Need | Existing thing | Path |
|---|---|---|
| Settings row | `Jot.Controls.SettingRow` (`Title` / `Description` / `RowContent`) | `Controls/SettingRow.xaml.cs:17-23` |
| Section header | `TextBlock Style="{StaticResource SectionHeader}" **Tag="section"**` | `Views/SettingsPage.xaml:27` |
| Vocabulary block (visible since 2026-07-25 — §7.1) | plain `StackPanel`, inherits `AdvancedPanel` visibility | `SettingsPage.xaml:298` |
| In-memory VM stubs (replace these) | `VocabularyTerms`, `AddVocabTermCommand`, … | `ViewModels/SettingsViewModel.cs:554-571` |
| List CRUD pattern | search box + `ListView` of card `Border` rows + `ui:CardExpander` add/edit form | `Views/PromptsPage.xaml:21-23,25,91-98,106-123`; `ViewModels/PromptsViewModel.cs:20-31,71-109` |
| **Back button on a sidebar-less page** | `ui:Button` + `BackCommand` → `_navigator.GoBack()` | `Views/RecordingDetailPage.xaml:19-21`; `ViewModels/RecordingDetailViewModel.cs:94-95` |
| Banner | `ui:InfoBar Severity="…" IsClosable="False"` | `SettingsPage.xaml:57` |
| Empty state | 36px tertiary `ui:SymbolIcon` → `SubtitleTextBlockStyle` → secondary line, `MaxWidth="360"` | `Views/RecentsPage.xaml:185-194` |
| Chips | `Border CornerRadius="12" Padding="8,3"` + `ui:Button Icon="{ui:SymbolIcon Dismiss12}"` | `RecordingDetailPage.xaml:151-160` |
| Selectable transcript | read-only borderless `TextBox Style="{x:Null}"` | `RecordingDetailPage.xaml:210-214` |
| **Activatable, keyboard-driven overlay on the paste path** | `PromptPickerWindow` — `WS_EX_TOOLWINDOW` only, no `NOACTIVATE`; Enter commits, Esc cancels | `Controls/PromptPickerWindow.xaml.cs:12-17, :61-67, :99-149, :188-194` |
| Origin-window capture + restore-then-paste | `_origin = TextInjector.CaptureForegroundWindow()` → `PasteAtCursor(result, _origin, …)` → `ForceForeground` | `Rewrite/RewriteController.cs:77, :129`; `Delivery/TextInjector.cs:83-86, :433-456` |
| Programmatic navigation | `INavigator.Navigate(typeof(Page), parameter)` one-shot `Parameter` | `Services/Navigation/Navigator.cs:11-31` |
| Page scrolling | central; `FindScrollViewer` deliberately skips a search box's content host | `Controls/PageScrolling.cs:62-71` — `VocabularyPage` needs **no** scroll plumbing |
| Tours | one `Tour` record in `TourCatalog.All` + one Help button | `Controls/TourCatalog.cs:114, :124` |
| Model download status | `ModelDownload` bound to a Settings row; `DataFolderMigrator.IsMigrating/Progress/StatusText` | `Services/ModelDownload.cs`; `Services/DataFolderMigrator.cs:45-51` |

**Already-ported vocabulary core — CORRECTED (the previous revision listed only two files).**
Present in `src/Jot/Vocabulary/`, pinned to `jot-shared@5326460`:

| File | Notes |
|---|---|
| `VocabularyGate.cs` | **54 KB**, **fully ported**: `ApplyFromDetections` (`:372`), `Decide` (`:535-613`), `LowConfidence = 0.85f` (`:43` — **`:40` is `ConfidenceCeiling = 0.95f`**), `Detection` record (`:89-94`), `Alternate` (`:59`), `Proposal` (`:62-80`, `PublishedStart` `:77`, `PublishedLength` `:78`), `Result` (`:82-86`) |
| `AskPolicy.cs` | `Select` (`:42`) / `WorthAsking` (`:98-104`), `MaxAsks = 3` (`:23`, spent `:111`) |
| `CorrectionRecord.cs`, `CorrectionKey.cs`, `Seams.cs` | |
| `CommonWords.cs` + `Vocabulary/Resources/common-words*.txt` | **21 lists**, `EmbeddedResource` (`Jot.csproj:39`) |
| `tests/Jot.Tests/Vocabulary/{VocabGoldenFixtureTests,VocabPortFidelityTests}.cs` | 7/7 jot-shared golden fixtures, vendored at `tests/Jot.Tests/Fixtures/vocab/` |

**PORTED 2026-07-25 (after this doc's first draft):** `CorrectionStore.cs` and
`CorrectionProvenance.cs` are now in `src/Jot/Vocabulary/`, fixture-green (8 store + 9 provenance
cases), with `<DataDir>\Vocabulary\` registered in `JotDataPurge` and `DataFolderMigrator` and
verified by tests that run the real purge and the real migrator. **They have no consumers yet**, so
the learn loop does nothing until the UI and `VocabularyRunner` land.

**Partly built since, by the M0 spike (engine side, spike quality, not wired):**
`src/Jot/Transcription/Ctc/` now holds `CtcMelFrontend`, `CtcEncoder`, `CtcTokens`,
`CtcGreedyDecoder` and `CtcTokenizer`. The one this doc depends on directly is
**`CtcTokenizer.IsSpottable`**, which is D12's mechanism (§2.4). Full status table in
`vocabulary-ctc-port.md §5b`.

**Still NOT built — new files, not ports in place:** `VocabularyStore`, `VocabularyTerm` (model),
the **`CtcWordSpotter` DP** and the rest of the spotter wiring, and every surface in this doc.
§13's ticket lists say so explicitly.

**⚠ There is no info-popover control in this app.** A repo-wide grep for
`Popup|Flyout|ToolTipService|InfoBadge|Learn more` returns one `ui:HyperlinkButton`
(`SettingsPage.xaml:87`). The app's actual "explain this" vocabulary is `SettingRow.Description`
(inline, 12px secondary) + `ui:InfoBar` (section banner) + `ui:CardExpander` (click-to-reveal).

**Recommendation: do not build a popover for this feature.** Use `SettingRow.Description` for the
one-liner and a Help card for the long form. If the owner wants popovers app-wide, that is a separate
control and a separate ticket. **[DECIDE-1]**

---

## 2. Area 1 — Managing terms

### 2.1 Where it lives

**Settings → Vocabulary** (a section inside `AdvancedPanel`, header carries `Tag="section"`) holds
only what belongs in Settings — four `ctl:SettingRow`s and, when relevant, one `ui:InfoBar`:

```
Vocabulary                                    [Experimental]
┌───────────────────────────────────────────────────────────┐
│ ⓘ  Custom vocabulary works in English only right now.     │   ← §6, conditional
│    Your language is set to Spanish — Español.             │
└───────────────────────────────────────────────────────────┘
│ Custom vocabulary                                   [ ● ] │
│ Words Jot should get right — names, products, jargon.     │
│ Runs entirely on this PC.                                 │
├───────────────────────────────────────────────────────────┤
│ Your terms                            12 terms  [Manage…] │
├───────────────────────────────────────────────────────────┤
│ Vocabulary model                              [Download]  │
│ About 130 MB, downloaded once. Needed before your terms   │
│ can change anything.                                      │
├───────────────────────────────────────────────────────────┤
│ Tell me when a term is used                         [ ● ] │
│ Shows a small mark on the pill that appears when a        │
│ dictation finishes.                                       │
└───────────────────────────────────────────────────────────┘
```

The model row binds to a `CtcModelDownload` (a `ModelDownload` subclass) exactly as the speech-model
rows do, with states `notDownloaded / downloading / ready / failed(reason)` — the Mac's
`BoostModelStatus` shape, whose headline is deliberately *"Vocabulary unavailable — <reason>"* not
*"Download failed"*, because a failure can also come from a tokenizer error on an
already-downloaded bundle.

**"About 130 MB", not "One-time 130 MB download".** Now backed by a measurement rather than a guess:
**131,652,171 B of ONNX + 251,875 B of `tokenizer.model` ≈ 132 MB**, two assets (D11). Still bind the
number to `AssetManifest.TotalBytes` if it is cheap, and keep the copy soft — a hard number that
ships wrong is an overpromise on a metered connection.

**Headline copy, narrowed by D12.** *"Words Jot should get right — names, products, jargon"* is only
honest for plain unaccented A–Z words: the spotter model has **no digits, no hyphen, no accented
Latin, no CJK** in its vocabulary, so a large slice of the obvious use case (`Wi-Fi`, `GPT-4`,
`café`) is out. We do **not** put that caveat in the Settings row — a row that opens with a
limitation reads as a broken feature. It is said **per term, at the moment the user types one**
(§2.4), which is where it is actionable. What the Settings description must not do is *contradict*
it, so the second line stays factual (*"Runs entirely on this PC."*) rather than aspirational.

**Manage… navigates to a dedicated `Views/VocabularyPage.xaml`**, reached programmatically via
`INavigator.Navigate(typeof(VocabularyPage))` and **not** added to the nav sidebar — exactly how
`RecordingDetailPage` already works.

*Why a page rather than the existing chip block:* the current hidden block is a `WrapPanel` of chips.
Chips cannot render a term *with its aliases* (which is the whole "when I say X I mean Y" model), and
the plan caps the list at 200 terms — 200 chips inside the one long Settings `ScrollViewer` is
unusable, and a nested `ListView` inside that `ScrollViewer` is a known WPF scroll trap. The
PromptsPage shape already solves exactly this problem in this codebase. **[DECIDE-2]**

### 2.2 The VocabularyPage

Structure = `PromptsPage.xaml` with the strings changed, **but Row 0 takes `RecordingDetailPage`'s
header shape, not PromptsPage's.**

- **Row 0 — Back button + title + subtitle**, a 3-column grid copied from
  `RecordingDetailPage.xaml:19-21`:
  ```xml
  <ui:Button Grid.Column="0" Appearance="Secondary" Command="{Binding BackCommand}"
             Icon="{ui:SymbolIcon ArrowLeft24}" ToolTip="Back" AutomationProperties.Name="Back" />
  ```
  backed by `[RelayCommand] private void Back() => _navigator.GoBack();`
  **This is not optional.** `MainWindow.xaml:23` sets `IsBackButtonVisible="Collapsed"` and §9 gives
  Vocabulary no sidebar entry, so without it the user lands on a page with no exit.
  Title `TitleTextBlockStyle` "Vocabulary"; subtitle:
  *"Terms Jot should prefer when it transcribes. Kept on this PC. Works best under about 100 terms."*
  (the second sentence is DECIDE-8's "advise 100" — it has to actually appear somewhere).
- **Row 1** — `ui:TextBox PlaceholderText="Search terms" Icon="{ui:SymbolIcon Search24}"`. Shown only
  above ~15 terms so a 3-term list isn't fronted by a search box.
- **Row 2** — the `ListView`. One card `Border` per term:

```
┌──────────────────────────────────────────────────────────────┐
│ Nemotron                                          [✎]  [🗑]  │
│ also heard as: nemo tron · neutron                           │
└──────────────────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────┐
│ UJET                                        ⚠   [✎]  [🗑]   │
│ Add a sounds-like spelling — Jot rarely hears short           │
│ acronyms correctly on its own.                                │
└──────────────────────────────────────────────────────────────┘
```

  Title = the term (SemiBold). Subtitle = `also heard as: ` + aliases joined by ` · `,
  `TextTrimming="CharacterEllipsis"`; when there are none, the subtitle is the warning (if any) or
  nothing. Right side = `ui:Button Appearance="Transparent" Icon="{ui:SymbolIcon Edit24}"` /
  `Delete24`, bound via `RelativeSource={RelativeSource AncestorType=ListView}` — the exact
  PromptsPage idiom. Icon-only buttons must carry
  `AutomationProperties.Name="Edit {term}"` / `"Delete {term}"` — WPF does **not** expose `ToolTip`
  as an automation name (PromptsPage has the same latent gap at `PromptsPage.xaml:94, :98`; fix it
  here, and file the PromptsPage one). `Delete` key on a selected row deletes it, with the same
  confirm as the button.
- **Row 3** — `ui:CardExpander Icon="{ui:SymbolIcon Add24}"`, header **"Add a term"**, doing double
  duty as the edit form (`IsEditing` → header "Edit term", button text "Save changes" vs "Add term").
- **Model state on this page.** When the model is missing or downloading, a one-line tertiary note
  sits under the subtitle: `Vocabulary model downloading — {N}%` / `Vocabulary model not downloaded —
  your terms are saved and will apply once it is.` Editing is never blocked on the model.

### 2.3 The add/edit form

```
Add a term
┌──────────────────────────────────────────────────────────────┐
│ Term                                                          │
│ [ Nemotron                                                  ] │
│ How you want it spelled.                                      │
│                                                               │
│ When Jot hears  (optional)                                    │
│ [ nemo tron ][×]  [ neutron ][×]                              │
│ [ Add a sounds-like spelling            ] [+]                 │
│ The wrong spellings Jot writes instead. Each one makes Jot     │
│ likelier to fix it.                                           │
│                                                               │
│                                   [ Cancel ]  [ Add term ]    │
└──────────────────────────────────────────────────────────────┘
```

- **Term** field: `ui:TextBox`, `FontFamily="{StaticResource JotMonoFont}"` (the Mac uses a monospaced
  term field so it reads as data, not prose — `VocabRow.swift:33`).
- **Aliases**: chip list + inline add field, `KeyBinding Key="Return"` to commit. Copy **"When Jot
  hears"** is lifted verbatim from `VocabRow.swift:76` and `TranscriptReader.swift:325`, so the three
  Windows entry points read identically.
- Enter in the Term field commits the whole form.
- **D12 validation is live, per field, as the user types.** Length-1 in the Term field disables
  **Add term**. If neither the term nor any alias is spottable, the §2.4 can't-work line appears
  under the alias field — *under the alias field, not the term field*, because adding a plain-letter
  alias is the fix. The moment one spottable alias exists the line disappears. Saving is never
  blocked by the can't-work state (only by length-1).

**Deviation from Mac — aliases are not behind Advanced.** The Mac gates its alias editor on the global
Advanced flag (`VocabRow.swift:23`) and iOS ships **none at all**, which its own `features.md §8.4`
calls a documented gap. We should not repeat that, for a Windows-specific reason: with no per-word
confidence, the gate's *only* remaining plausibility lever is string similarity against the term **and
its aliases** (`Decide` step (1) → `Plausible`, `VocabularyGate.cs:591`, `:840-849`; an alias is
literally "the user telling us this pair is
plausible"). It is the single most effective thing a user can do to make vocabulary work on our
engine. Hiding it would be hiding the feature's main control.

**Also port `enrichedAliases` (feed-time merged form).** Mac's `design.md §6 [ADD]` and iOS's
`correction-review-implementation.md §11` both record the same bug: *"'Ramaa Nathan' heard as merged
'Ramanathan' → replaced by the SHORTER term 'Ramaa' because the right term never competed."* Fix is to
append each multi-word term's space-stripped form as an alias **at feed time only** — the user's
`vocabulary.json` is untouched, and the UI never shows the synthetic alias. Engine work; also listed
in `vocabulary-ctc-port.md §3.1` so it can't fall between the two docs.

### 2.4 Inline warnings — and, new in this revision, one hard block (D12)

Same shape as `VocabRow.warningMessage` — a `ui:SymbolIcon Warning24` in `JotWarningBrush` with a
`ToolTip`, plus the subtitle line. **Two severities now, and the distinction is the point:**

- **Advisory** (the original four) — *"this may not work well."* Never blocks saving; the user is
  trusted.
- **Hard block / can't-work** (D12) — *"this can never work, at any threshold, ever."* Different
  copy, different tone, and for the length-1 case a **disabled Add button**, because saving it would
  be saving a guaranteed no-op.

| Condition | Severity | Copy |
|---|---|---|
| **Exactly 1 char after trim** | **BLOCK — Add disabled** | `Single letters can't be matched. Use at least two characters.` |
| 2 chars after trim | advisory | `Too short — terms under 3 characters are skipped to avoid false replacements.` |
| **`IsSpottable` false — digits** | **can't-work** | `Jot can't listen for numbers. Try the spelled-out form — "GPT four" instead of "GPT-4".` |
| **`IsSpottable` false — hyphen** | **can't-work** | `Jot can't listen for hyphens. Add "Wi Fi" as a spelling instead, and Jot will still write "Wi-Fi".` |
| **`IsSpottable` false — accent / non-English letters** | **can't-work** | `Jot can't listen for "é". Add an unaccented spelling like "cafe" — Jot will still write "café".` |
| **`IsSpottable` false — anything else (CJK, symbols)** | **can't-work** | `Jot can't listen for these characters. Add a spelling using plain English letters.` |
| Term is in the loaded common-word list | advisory | `Common word — Jot won't swap this on its own. You can confirm it on the recording.` |
| ≤4 chars, no aliases | advisory | `Add a sounds-like spelling — Jot rarely hears short acronyms correctly on its own.` |
| >4 words | advisory | `Use a single word or short phrase (max 4 words).` |

**Why the can't-work states are still SAVEABLE, and why that is not a contradiction.** A term is two
things: the **spelling Jot writes** and the **sound Jot listens for**. D12 only breaks the second.
`Wi-Fi` with the alias `Wi Fi` works perfectly — the alias is what gets tokenized and spotted, and
`Wi-Fi` is what gets written. So the copy above is written as a **recipe, not a rejection**: every
can't-work line names the fix (add a plain-letter spelling under *When Jot hears*). Once a spottable
alias exists, the marker downgrades from can't-work to nothing.

**The row marker.** On the `VocabularyPage` list, a can't-work term with **no spottable alias**
carries a distinct badge rather than the generic ⚠ — a secondary capsule reading **`NOT HEARD`**,
same `Border CornerRadius="12" Padding="8,3"` recipe as the `KEPT` badge (§5.3), with the fix line
as the subtitle. It is a state the user can leave in place deliberately (they may just want the
spelling recorded), so it is a marker, not an error.

```
┌──────────────────────────────────────────────────────────────┐
│ Wi-Fi                                 NOT HEARD   [✎]  [🗑]  │
│ Jot can't listen for hyphens. Add "Wi Fi" as a spelling       │
│ instead, and Jot will still write "Wi-Fi".                    │
└──────────────────────────────────────────────────────────────┘
```

**Where the check runs.** `CtcTokenizer.IsSpottable(term, tokens)` — engine side, at the
`VocabularyStore.SanitizeTerm` choke point (`vocabulary-ctc-port.md` D12), **not** re-implemented in
the ViewModel. The check needs the tokenizer, so **before the model is downloaded it cannot run**:
in that state show no can't-work markers at all (do not guess with a regex — a regex would be a
second, drifting implementation of the model's vocabulary), and re-evaluate the whole list once the
model becomes ready. Length-1 is the exception: it needs no model and blocks immediately.

**The common-word copy changed in this revision.** It used to say *"Jot will ask before using this"* —
**factually wrong as shipped**: per D8 the ask deck is filtered to applied corrections (§4.1), so no
block ever reaches a card and nothing will ask. The
truthful version says what actually happens: the gate blocks it (`Decide` step (4),
`VocabularyGate.cs:607`) and surfaces
it on the recording for review.

**Deviation from Mac (improvement):** `VocabRow.swift:178-188` hard-codes a ~90-word English watchlist
with an apologetic comment (*"a bigger list belongs in a bundled frequency file in a future phase"*).
We already bundle the real 24k list for the gate. Check against that.

**The names carve-out is already correct — risk RETIRED.** iOS regenerated `common-words.txt` with
*"306 popular given names removed (SSA peak-share ≥ 0.005), minus a curated dual-meaning allowlist so
may/will/mark/rose/grace/april stay protected."* Verified in our copy
(`src/Jot/Vocabulary/Resources/common-words.txt`): 24,059 entries; `jamie`, `sarah`, `lisa` **absent**;
`mark`, `rose`, `grace`, `april`, `may`, `will` **present**. Nothing to do.

### 2.5 Bulk paste import

`vocabulary-plan.md` already specifies this ("added N, skipped M"). UX:

The Term field is `AcceptsReturn="False"`, but paste is intercepted: if the pasted content contains a
newline, comma, or semicolon **and** yields ≥2 candidates, the form switches to import mode rather than
dumping a paragraph into the field.

```
Add a term
┌──────────────────────────────────────────────────────────────┐
│ Looks like a list — 14 terms found.                          │
│ [ Nemotron, UJET, Ramanathan, Ideaflow, …                  ] │
│                                                               │
│              [ Add as one term ]  [ Add 14 terms ]            │
└──────────────────────────────────────────────────────────────┘
```

Result reported in a `ui:InfoBar Severity="Success" IsClosable="True"` above the list:
`Added 11 terms. Skipped 3 — already in your list, or under 2 characters.`
Never a modal, never a blocking error. If **every** candidate is skipped:
`Nothing added — those 3 terms are already in your list.`

**Bulk import and D12.** Only the length-1 rule skips on import (it is a hard block). Can't-work
terms are **imported normally** and simply land in the list carrying their `NOT HEARD` marker —
pasting 40 product names and having 12 silently vanish would be worse than importing them and
showing which need a spelling. A second line is appended when any land that way:
`12 of these need a sounds-like spelling before Jot can hear them.`

Aliases cannot be bulk-imported in v1 (the `Term: alias1, alias2` file format is the escape hatch, and
the file lives at **`<DataDir>\Vocabulary\vocabulary.json`** — D7, §5.5).

### 2.5b States the previous revision did not design

| State | Rule |
|---|---|
| **Duplicate term on single add** | Never create a second row. If the new term matches an existing one (after `SanitizeTerm` + case-fold), **merge**: append any new aliases to the existing term, scroll to it, flash it, and show `Already in your list — added 2 new spellings.` (or `Already in your list.` if nothing changed). |
| **Term cap (200) reached** | The Add button disables with `You've reached 200 terms. Remove one to add another.` Bulk import fills up to the cap and reports `Added 6 terms. Skipped 8 — the list is full at 200.` The cap is a **latency budget** (every term is a spotter query), so confirm the number against M2's timings before shipping. |
| **Same alias claimed by two terms** | Last write wins, loudly: the alias moves to the term being saved and the form shows `"neutron" was listed under Nemotron — moved it here.` An alias belonging to two terms makes the gate's plausibility lever ambiguous, so we never allow the duplicate to persist. |
| **Renaming a term that has correction history** | Renaming `Nemotron` → `NeMotron` orphans every `CorrectionStore` net keyed on the old pair (`CorrectionRecord.MappingKey`, `CorrectionRecord.cs:47`). v1.3 rule: **rename = delete + create**, and say so — `Renaming clears what Jot learned about this term.` (inline, on the edit form, only when history exists). Migrating nets across a rename is v1.4; doing it wrong silently transfers a learned override onto a different word. |
| **Sample data** | `JotSettings.ShowSampleData` seeds demo rows. The review block (§5) must **not** render on them. |

### 2.6 Empty state

Exactly the `RecentsPage.xaml:185-194` recipe. Copy adapted from the Mac
(`VocabularyPane.swift:283-286`):

```
                        [ 💬  Chat24, 36px, tertiary ]

                        No terms yet

     Add names, products, and jargon you want Jot to spell your way.
     Right-click a wrong word in any transcript to add it in one step.
```

The second sentence is the **only** discovery hook we ship in v1 (§7). No button in the empty state —
the add form is already on screen directly below it.

### 2.7 Adding a term from outside Settings

**The right-click on the recording-detail transcript.** This is the single most important non-Settings
entry point, because it is the only one that appears at the moment the user is annoyed. Mac shipped it
(`TranscriptReader.swift:155-172`) and iOS shipped it after explicitly cutting it and reversing that
cut (`adaptive-vocabulary-correction.md §0h`).

`RecordingDetailPage.xaml:210-214`'s transcript is a read-only borderless `TextBox`, which already
supports drag-select and today shows the default WPF context menu. Replace that with an explicit
`ContextMenu`: `Copy` · `Select All` · separator · **`Add to Vocabulary…`**.

**Replacing the menu costs the built-ins — re-wire them.** `Copy` and `Select All` must be
`ApplicationCommands.Copy` / `ApplicationCommands.SelectAll` with `CommandTarget` bound to the
TextBox. The menu goes on the **read-only** box only (`:210`), **not** the edit-mode `ui:TextBox`
(`:217`). Selection offsets live on the control, so `Add to Vocabulary…` needs code-behind —
precedent at `RecordingDetailPage.xaml.cs:29-53`.

Gating (mirrors Mac + iOS): the menu item is enabled only when the selection sanitizes to a plausible
term — **≤4 words** (`VocabularyStore.MaxTermWords = 4`), ≤60 chars, contains letters, edge punctuation
trimmed, and is not already a term. Otherwise it is present but `IsEnabled="False"` — a missing item
reads as a bug, a disabled one reads as a rule.

The dialog is a small centered modal (there is no popover control), ported nearly verbatim from
`TranscriptReader.swift:319-372`:

```
Add to Vocabulary
─────────────────────────────────────────────
When Jot hears
[ nemo tron                                ]

Spell it as
[ Nemotron                                 ]

Future dictations will prefer this spelling.

                          [ Cancel ]  [ Add ]
```

Modal mechanics, copied from `PromptPickerWindow` (`:61-67` initial focus, `:135-138` Esc): initial
focus on the **Spell it as** field, Esc cancels, Enter is the default button.

On **Add**, three things happen, all of which the Mac and iOS do:
1. The selected span in *this* transcript is replaced with the canonical term and persisted
   (`Item.Transcript`, `IsEdited = true`) — the user's immediate annoyance is fixed. **This is a
   transcript mutation and therefore shifts correction anchors — see §5.5, which handles it.**
2. `VocabularyStore.AddMapping(heard, term)` — the term is created (or the alias appended to an
   existing term), so **future** dictations improve.
3. `CorrectionStore.Adjust(heard → term, +1)` — the pair starts at net 1, which for a rare/OOV original
   arms the learned override immediately (`Decide` step (0), `VocabularyGate.cs:582`). One gesture,
   three effects. **Note what this does *not* do for a common-word original:** step (0) requires
   `!isCommon`, so such a pair stays blocked forever while now carrying `Prior ≥ 1` — which is why
   the ask deck is filtered to `Outcome == "applied"` (§4.1), or this gesture would arm a permanent
   ask on every later dictation.

Guard rails, both from iOS scar tissue:
- **If every word of the typed term is a common word, fix the text but create no term.** iOS: *"that's
  the 'what is this?' test — ordinary rewording isn't vocabulary."* Show
  `Fixed here. "the list" is an everyday phrase, so it wasn't added to your vocabulary.`
- **Sanitize both fields at the store choke point**, not at the UI. iOS's R3 review found typed terms
  containing `:` `,` or a leading `#` **corrupted the vocabulary file format**, and separately that
  Settings' free-text editing bypassed the same sanitizer. One `VocabularyStore.SanitizeTerm` used by
  every writer.

Status line variants (replaces the "Future dictations…" line):

| Condition | Copy |
|---|---|
| Default | `Future dictations will prefer this spelling.` |
| Vocabulary toggle off | `Custom vocabulary is off — turn it on in Settings to use this.` (warning brush) |
| Model not downloaded | `Saved. Jot downloads the vocabulary model (about 130 MB) the first time you use it.` |
| Model downloading | `Saved. The vocabulary model is still downloading.` |
| Language is not English | `Saved. Vocabulary only applies when your language is set to English.` (warning brush) |
| Language is Auto detect | `Saved. Vocabulary needs your language set to English — it's on Auto detect.` (warning brush) |
| Selection >4 words | `Select just the word — up to 4 words.` (error brush, Add disabled) |
| **"When Jot hears" is 1 char (D12)** | `Single letters can't be matched. Use at least two characters.` (error brush, Add disabled) |
| **"When Jot hears" is unspottable (D12)** | `Saved the spelling, but Jot can't listen for "{chars}". Edit this term in Settings to add a plain-letter spelling.` (warning brush) |

**Note which field D12 applies to, because this dialog has two — and the rule is the same one §2.4
states.** The spotter listens for the **term and every alias**; a term is only unreachable when
**none** of them is spottable. This dialog always supplies an alias (*When Jot hears*, taken from
the transcript), so the *Spell it as* field is free to contain hyphens and accents — that is the
`Wi Fi` → `Wi-Fi` case working exactly as designed, and blocking it would block the feature's whole
point. So the check here runs on **When Jot hears** only.

**Not in v1:** the Find-&-Replace offer. When a user replaces the same one/two-word non-common term in
≥2 places and saves, iOS shows *"a gentle one-tap offer to add the corrected term to your vocabulary…
dismissible and never blocks"* (`jot-mobile/Jot/features.md §3.10`). `RecordingDetailPage` already has
Find & Replace, so this is cheap and it is the highest-value discovery hook we are leaving on the
table. Flagged for v1.4, deliberately.

---

## 3. Area 2 — The moment of correction

### 3.1 The constraint

`TranscriptReady?.Invoke(text)` fires at `RecorderController.cs:253`, **after** `PasteAtCursor`. The
text is already in the user's document. Nothing here can be an action; it can only be information.
`PillState.Success` lingers 4000 ms (`PillController.cs:183`) and auto-sizes.

### 3.2 Options weighed

| Option | Verdict |
|---|---|
| **Silent** | Rejected as the *only* behavior. Mac's `feedback-ux.md §1`: a user who just set up vocabulary has "no confirmation it's working… the first few wins are exactly when reassurance matters most." We ship default-off pending calibration — silent means nobody can tell whether calibration worked. |
| **Chip on the existing Success pill** | **CHOSEN.** No new `PillState` (the enum's comment demands every consumer switch exhaustively). Adds zero screen-time. Degrades to today's exact look when nothing was corrected. |
| **Second pill after Success** | Rejected, same reasoning as Mac's Option B: *"adds a second status beat to every corrected dictation… directly fights the anti-annoyance goal."* |
| **Ask before paste** | **Built in v1.3, but it is a different surface** — a separate window before the paste, not a change to this pill (§4). |

### 3.3 The chip

```
one correction:
┌──────────────────────────────────────────────────────┐
│ ●  Met with Ramanathan about the launch    ✦ Ramanathan│
└──────────────────────────────────────────────────────┘

two or more:
┌──────────────────────────────────────────────────────┐
│ ●  Nemotron and UJET both shipped              ✦ 2    │
└──────────────────────────────────────────────────────┘
```

- Lives in `LineRow`, right-aligned, after `LineText`. **Not** in `ExpandPanel` — the
  `ux-audit-2026-07-20.md` P1-5 trap is that `ExpandPanel` only opens when there is transcript text
  (`PillWindow.xaml.cs:293`, `:309`), so anything the user must see cannot live there alone.
- **Column placement (name the choice):** `LineRow` is a fixed 4-column grid — dot / waveform /
  caption (`*`) / elapsed (`PillWindow.xaml:22-40`), and `Elapsed` (column 3) is collapsed outside
  Recording. **Reuse column 3** — no layout change, and the chip and the timer can never both be
  visible.
- Glyph: `ui:SymbolIcon Sparkle24` (or `WandSparkle24`), ~11px, `#8CFFFFFF` — quieter than the green
  `JotSuccessBrush` dot. Mac's tone note: *"'noticed,' not 'alert.'"*
- 1 correction → the **resulting** term, tail-truncated at 14 chars. ≥2 → the count. Never a list
  inline. De-duped by `(originalWord, term)` — a term applied 3× is **1**.
- **CORRECTED — the `FitTail` claim in the previous revision was wrong.** `FitTail` runs only from
  `SetLiveText` (`PillWindow.xaml.cs:66`), i.e. during Recording. `PillState.Success` uses
  `OneLine(text)` — a flat 60-char truncation (`:195`, `:347-352`) — and `Capsule.Width` is reset to
  `double.NaN` (auto) for every non-Recording state (`:156`). There is no fixed slot to measure.
  **What to do instead:** shorten `OneLine`'s budget from 60 to ~46 chars when a chip is present, so
  the auto-sized capsule (`SizeToContent="WidthAndHeight"`, `PillWindow.xaml:11`) doesn't outgrow
  `LineText`'s `MaxWidth="320"` (`PillWindow.xaml:35`).
- **Automation:** the pill sets `AutomationProperties.Name` on the **Window**
  (`PillWindow.xaml.cs:198`), so the chip must be folded into **that one string** —
  `"Transcription ready. Vocabulary used Ramanathan."` — not added as a separate peer, or Narrator
  announces it twice.
- The chip is **not** clickable in v1. Reserve the affordance: keep the glyph identical so a later
  "click → open this recording's corrections" deep-link is not a visual change. A 4-second click target
  is a bad target.

### 3.3b The late-correction path (D10) — a third delivery mode, previously undesigned

The engine's measured cost (60 s of speech = **1987 ms** on CPU, super-linear) means "the term is in
what you paste" is a promise we can keep on short dictations and not on long ones. D10's ladder
(`vocabulary-ctc-port.md §3.5`) therefore produces **three** outcomes, and this doc has to design
all three rather than the one:

| Tier | What the user experiences |
|---|---|
| **≤ 60 s of speech** | Today's design, unchanged: pasted text is corrected, chip on the pill, ask card if there is one. |
| **60–120 s** | The **raw** transcript is pasted; the spotter finishes in the background against the **saved recording**. Corrections appear in the library/recording detail, not in what was pasted. |
| **> 120 s** | Nothing runs. No corrections, no chip, no notice. |

**Rules for the middle tier, all of them "don't make it weird":**

1. **The chip appears only if the pill is still up.** `PillState.Success` lingers **4000 ms**
   (`PillController.cs:183`); a pass that overran a 2.5 s deadline can easily land after that.
2. **Never resurrect a dismissed pill.** A status pill popping back seconds after the user moved on
   is a worse bug than a missing chip. If the pill is gone, the corrections are simply on the
   recording — which is where §5 already sends people.
3. **No ask card on this path.** The ask is a *pre-paste* confirmation (§4.4); the paste happened.
   §4.6 states the consequence for the ask design.
4. **No "vocabulary is still working" indicator.** Consistent with §6.3's standing rule and §7.3's
   "a banner is itself a nag": we do not narrate background work the user cannot act on.
5. **The review surface is correct either way.** The background pass commits its proposals against
   the string it actually gated — the saved transcript — and stamps the §5.5 fingerprint on that
   string, so §5's rows and anchors are valid exactly as on the fast path.
6. **A second recording cancels it.** The target recording stays saved and simply has no proposals.

**What we never do: retract or re-paste.** iOS's *"the month-long problem we refuse to re-open"*
(§4.5) applies with full force here — a late correction must never touch text already in the user's
document.

**The honest consequence, stated once:** on a long dictation this feature does not do the thing it
is for. That is the price of not blocking the paste for five seconds, and it is the right trade —
but it is the reason `vocabulary-ctc-port.md §4` item 9 keeps "collapse this tier to a plain skip"
open. If M2 shows the 60–120 s band is rare, delete this whole path rather than ship a mode nobody
hits.

### 3.4 Detail

The pill's `ExpandPanel` (click-to-expand, already exists) appends the correction list under the
transcript, above the Copy/Stop row:

```
Vocabulary
  nemo tron → Nemotron
  you jet   → UJET
```

Cap at 5 rows + `+3 more`. This costs one `ItemsControl` and no new interaction model.

### 3.5 Blocked corrections are not shown here

A blocked common-word near-miss ("did you mean Lisa?") is exactly the case where the user might want to
act — and the paste already happened, so the pill cannot help. It belongs on the review surface (§5),
where it renders as a `KEPT` row. Mac scopes blocks out of the pill for the same reason.

**Consequence of D8, stated plainly:** the ask deck is filtered to applied corrections (§4.1), so no
block reaches it either — **the review surface is the only place in the entire product where a
blocked near-miss is visible.** That makes §5 load-bearing, not optional, and it makes the §4.1
filter load-bearing too: drop the filter and the same block becomes a card that never stops asking.

### 3.6 The opt-out

One `ctl:SettingRow` under Vocabulary:

> **Tell me when a term is used** — *Shows a small mark on the pill that appears when a dictation
> finishes.* Default **on**.

Mac ships the same toggle and calls it *"cheap insurance"*. It is our **only** real throttle on this
surface, because `notable` is inert on our path (§1). Do **not** build frequency damping ("show at most
once per N minutes per term") — Mac explicitly recommends against it for v1 and we have no usage data
to tune it with.

---

## 4. Area 3 — The "did you mean?" ask (**IN v1.3**)

### 4.1 What the ask can actually be about (D8)

The owner decided to build the ask and to ship `AskPolicy` unchanged. Those two decisions together
determine the card's content, and it is **not** what the previous revision drew.

```csharp
// src/Jot/Vocabulary/AskPolicy.cs — `AskPolicy.Select`'s local `WorthAsking` (decl :98, body
// :99-104). Quote this in the implementation ticket.
bool WorthAsking(CorrectionRecord r)
{
    if (keyboardSuppressed.Contains(PairKey(r))) return false;
    if (Granted(r)) return false;                                  // dead: no always-replace in v1.3
    if (r.Shape == "merge" && r.Outcome == "kept") return MergeTeachEligible(r);  // dead: Shape ≡ null
    return r.Outcome == "applied" || Prior(r) > 0;
}
```

A blocked common-word near-miss (`lista → Lisa`) has `Outcome == "kept"`, `Shape == null`, and
`Prior == 0` **on first encounter**. It falls through every branch to `false`.

**But "on first encounter" is the whole story, and the previous revision generalised it into an
absolute that is false.** Two shipping gestures push a pair to `Prior > 0`:

- §5.4 — resolving a `KEPT` review row by picking the term records **+1**;
- §2.7 step 3 — the flagship right-click "Add to Vocabulary" does `CorrectionStore.Adjust(+1)`.

`Prior(r)` reads `OverrideEntry.Net`, so the pair now has `Prior ≥ 1`. And `VocabularyGate.Decide`'s
override branch requires **`!isCommon`** (`VocabularyGate.cs:582`), so a **common-word original stays
BLOCKED forever** while carrying that net. Result: `Outcome == "kept"` and `Prior(r) > 0` ⇒
`WorthAsking` returns **`true` on every later dictation, indefinitely** — an ask that never stops, on
exactly the `lista → Lisa` case D8 declared impossible. The owner's requirement is *"once the user
answers, don't ask again"*, so this must not ship.

**The fix, and it does not touch `AskPolicy` (§0b-RESOLVED stands): filter caller-side.**
`VocabularyRunner` passes `AskPolicy.Select` only records with `Outcome == "applied"`:

```csharp
// One line, app-side composition — not a Windows-specific policy divergence.
var deck = AskPolicy.Select(
    unresolved.Where(r => r.Outcome == "applied").ToList(),
    overrides, keyboardSuppressed, mergeAsked);
```

**With that filter in place, and only with it, these hold as settled design facts:**

- **The deck contains APPLIED corrections and nothing else.** The `Prior > 0` lane in `WorthAsking`
  still exists and still ranks the deck — it just can no longer *admit* a `kept` record.
- **Blocked proposals get no ask in v1.3.** They are reviewable **only** on the transcript surface
  (§5). Any copy that says "Jot will ask about this" is wrong and has been removed (§2.4).
- **"Stop asking" as a block-suppression control is gone from v1.3.** `suppressedBlocks` /
  `blockedKeeps` have no reachable writer on this path. Keep the ported fields (they are conformance
  surface) but do not build UI for them, and do not claim they do anything.
- The card can never show a third option: `Alternates` is always `[]` (`VocabularyGate.cs:491`), so
  `Selection.AltTerm`/`AltFind` are always null.

**Test the filter, not the belief.** A regression test must feed a `kept` record with `Prior = 1` and
assert the deck comes back empty — that is the exact record the unfiltered policy asks about forever.

If the owner ever wants the block-ask, it needs a `KEPT` card variant (§4.2's copy is wrong for one)
*and* a suppression path for records the gate will never apply. That is a **Windows-specific change
to `AskPolicy`** (`WorthAsking` would need `|| (r.Outcome == "kept" && isCommonWordOriginal(r))`),
which §0b-RESOLVED forbids. Escalate; do not quietly diverge.

### 4.2 The card, redrawn around an applied correction

```
┌────────────────────────────────────────────────────────────┐
│ …met with ramanathan about the launch…         ◔     1 of 2│
│ Jot wrote Ramanathan.                                       │
│ [ ramanathan ]   [ Ramanathan ✓ ]                Keep both? │
└────────────────────────────────────────────────────────────┘
```

Reading, top to bottom:

- **Context line first**, italic, ±24 chars each side, the corrected word underlined. iOS puts it
  first *"so when several rows share a word, the owner can tell WHICH occurrence each row is about"* —
  doubly important for us, since our placement is proportional and can pick the wrong occurrence
  (§11 risk 3).
- **One-line statement of what happened**, past tense, because it has: `Jot wrote {term}.` The ask is
  a **confirmation of an edit already made in the pending text**, not a request for permission to make
  one — which is the honest framing given the deck only ever holds applies.
  **This copy is only true for an `applied` record.** On a `KEPT` row Jot wrote the *original*, so the
  headline would be a lie — which is a second reason the §4.1 filter is mandatory rather than
  optional. If a `KEPT` card is ever specced, it needs its own headline (`Jot kept {original}.`) and
  its own chip marking; do not reuse this one.
- **Two chips, original first, the applied one marked.** Never yes/no. The whole Mac + iOS review model
  is *"pick the word you meant."* Picking the original reverts that occurrence; picking the term
  confirms it. `lineLimit(1)` + shrink-to-fit — *"never wrap a word mid-word."*
- **Countdown ring**, no digits. Static full ring if the user has reduced motion on
  (`SystemParameters.ClientAreaAnimation`), but the timeout still fires. 6–10 s.
- **`N of M`** counter, monospaced.
- **No "Stop asking" control** (D8 — no block reaches the deck, so there is nothing to suppress).
  Answering *is* the suppression: the pair goes to `keyboardSuppressed` and is never asked again
  (`WorthAsking`'s first line, `AskPolicy.cs:100`).

### 4.3 How it is built (D9) — clone `PromptPickerWindow`, never touch `PillWindow`

**The previous revision's framing was wrong.** It said the pill is `WS_EX_NOACTIVATE` so a
keyboard-driven card requires either removing that flag or installing a global low-level keyboard hook
that swallows Enter/Esc from the focused app. Both are bad and **neither is necessary**: the app
already ships an activatable, keyboard-driven overlay on the paste path.

```csharp
// src/Jot/Controls/PromptPickerWindow.xaml.cs:12-17 (class comment)
/// Unlike the status pill it is *activatable* — it takes keyboard focus so the
/// user can type to filter. ... Enter commits, Esc / click-away cancels.

// :188-194 — Tool window so the palette stays out of Alt+Tab; still activatable (no NOACTIVATE).
SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
```

**Build `Jot.Controls.AskCardWindow` as a sibling of `PromptPickerWindow`, copying its window recipe
verbatim:**

- `WindowStyle=None`, `AllowsTransparency`, `Topmost`, `ShowInTaskbar=False`, same dark capsule look.
- `OnSourceInitialized` sets **`WS_EX_TOOLWINDOW` only** — *no* `WS_EX_NOACTIVATE` (`:188-194`). That
  is the entire trick: the pill keeps its `NOACTIVATE` forever, and a *different* window takes focus.
  No low-level hook, no change to the paste path's focus model, no risk to the 2026-07-23 fix.
- `OnLoaded` → focus the first chip (`:61-67`).
- `OnPreviewKeyDown` → ←/→ between chips, Enter commits, Esc = keep-original-and-advance (`:99-149`).
- Position: reuse the **pill's** bottom-center-on-anchor-monitor math (`PillWindow.xaml.cs:238-257`),
  not the picker's centered math, so the card appears where the user is already looking.

**The real hazard is the paste target, not the focus flag.**

```csharp
// src/Jot/Recording/RecorderController.cs:243
IntPtr target = s.ReturnToOrigin ? _originWindow : IntPtr.Zero;
```

`ReturnToOrigin` defaults to **false** (`Services/Abstractions/ISettingsStore.cs:44` — no
initializer), so by default `PasteAtCursor` gets `IntPtr.Zero` and resolves the target as
`GetForegroundWindow()` **at paste time** (`Delivery/TextInjector.cs:107`). That is safe today *only*
because the pill never activates — the comment at `RecorderController.cs:242` says exactly that. The
instant an activatable card exists, the foreground window at paste time is **Jot's own**, and the
`IsOwnWindow` guard at `TextInjector.cs:81` does not cover the `IntPtr.Zero` path. The transcript
would be pasted into the ask card, or nowhere.

**Requirement 1 — force the paste target whenever the vocabulary block was ENTERED, not only when a
deck completed.** Pass `_originWindow` unconditionally at `:243`, ignoring `ReturnToOrigin`.
`PasteAtCursor` then does `ForceForeground(_originWindow)` + a 40 ms settle
(`TextInjector.cs:83-86, :433-456`) — the identical restore the shipping rewrite path relies on
(`RewriteController.cs:129`).

**The condition matters.** If it is scoped to *"a deck ran to completion"*, the error path re-opens
the exact hazard this requirement exists to close: an exception thrown while the card is still up
leaves Jot's own activatable window foreground, `PasteAtCursor` gets `IntPtr.Zero`, resolves
`GetForegroundWindow()` (`TextInjector.cs:107`), and **pastes the transcript into the ask card** —
the `IsOwnWindow` guard at `:81` only covers the `restoreTo` argument. So the flag is set on entry
into the vocabulary block and never cleared, and a `finally` force-closes any live card before the
paste (§4.4). Tests: assert the paste target on the happy path **and** on the throw-while-card-is-up
path.

**Requirement 2 — `Deactivated` resolves, it does not close.** `PromptPickerWindow` does
`Deactivated += (_,_) => { if (CloseOnDeactivate) Close(); }` (`:40`). For the ask, deactivation means
"the user clicked back into their document" — that must **resolve-to-default and still deliver**. This
is exactly the Mac round-1 blocker M4/M5: reusing the ordinary dismiss timer *"sets .hidden WITHOUT
delivering → the held paste silently never lands."* Model the whole deck as a single
`TaskCompletionSource<Answers>` that **every** exit path completes, with the paste in a `finally`.

**Requirement 3 — a new recording force-resolves any live deck.** `Toggle` / `PressToStart` are global
and reachable while the card is up (`RecorderController.cs:61, :128`). `Start()` must force-resolve the
live deck (keep-original, deliver) **before** doing anything else. Note `DisarmStopHotkey` has already
run by this point (`:190`), so Esc is free for the card to consume.

### 4.4 Sequencing in `StopAndDeliverAsync`

Keep the existing shape and insert the ask **between** the gate and `_store.Add`, so §1b's "what is
saved is what is pasted" simplification survives:

1. gate the cleaned text → `(text, records)`;
2. filter to `Outcome == "applied"` (§4.1), then `AskPolicy.Select(...)` → **if empty, nothing changes
   at all.** This keeps zero-ask dictations byte-identical to today, and it will be the overwhelming
   majority;
3. if non-empty: `await AskCardWindow.RunAsync(deck)` on the dispatcher. It owns its own resolution and
   **always** completes — timeout, Esc, click-away, and a new recording all resolve to keep-original;
4. splice answers into `text` — **the one sanctioned mutation between the gate and `_store.Add`**
   (§1b). It runs through `CorrectionProvenance.ShiftAnchors` so every later `PublishedStart` is
   corrected for the length delta ("nemo tron" → "Nemotron" is −1), and the §5.5 fingerprint is
   stamped on the **post-splice** string. The §1b guard test whitelists this writer and only this one;
5. `_store.Add(BuildRecording(result, text))`;
6. paste, with the forced target (§4.3 requirement 1).

**This whole block lives inside the D6 wrapper — and a `catch` alone is not enough.** Three exit
paths the wrapper must cover, all of them silent failures if it doesn't:

- **A hang is not a throw.** If `RunAsync` never completes, no exception fires, so neither the
  `catch` nor the `finally` at `RecorderController.cs:272-276` runs: `State` stays `Transcribing`,
  `PressToStart` ignores that state (`:143-144`), and **dictation is dead until restart**. So steps
  1–4 sit under **one wall-clock deadline** (a `CancellationTokenSource` + `WhenAny` around the
  whole block, not just inside the spotter's `Run`). On expiry: keep-original, deliver, log.
- **`finally { close any live card; }`.** An exception in steps 1–4 leaves a `Topmost`, activatable
  window foreground; the paste then lands in it (§4.3 requirement 1). Close the card and force
  `_originWindow` on the failure path too.
- **`PasteAtCursor` is conditional on `_settings.Current.AutoPaste` (`:238`).** Any test that says
  "the original transcript reaches `PasteAtCursor`" is vacuous with AutoPaste off — write both paths
  (see the D6 test spec in `vocabulary-ctc-port.md §3.6`).

An exception anywhere in steps 1–4 falls back to the ungated, un-asked text and still reaches steps
5 and 6.

**Cost:** one new window (~250 lines, structurally a copy of `PromptPickerWindow`), one
`TaskCompletionSource` handshake, one deadline, one changed line at `:243`. Fully testable with no
microphone: feed a synthetic deck and assert, **with AutoPaste on**, that `_store.Add` and
`PasteAtCursor` are each called **exactly once** on every exit path — answer / Esc / timeout /
deactivate / new-recording-interrupt / spotter-throws / **deck-never-completes** — and, **with
AutoPaste off**, that `_store.Add` fires exactly once and `PasteAtCursor` not at all. On every path,
also assert the state machine returns to `Idle` and no card is left on screen.

### 4.5 Resolution rules

- Answer → splice → paste **once**. Never speculative-paste-then-retract: iOS rejected that outright as
  *"the month-long problem we refuse to re-open."*
- **Never splice blind.** Locate the word by whole-word, punctuation-trimmed, case-insensitive match
  anchored at the recorded offset; `PublishedLength` is **not** used to size the splice. If it does not
  resolve exactly → skip that edit and paste the default. iOS: *"corrupting the pasted text is worse
  than leaving the default."* (Their strict length+equality guard is what dropped a `Rama→Ramaa`
  replacement when the original carried a trailing period — hence punctuation-trimmed.)
- **Timeout branches on engagement** (iOS UX-Q2, resolved): card 1 with **zero** interaction → **skip
  the whole deck**, paste all defaults immediately (*"don't march the user through 3×10s they're
  ignoring"*). After ≥1 interaction → skip only the current card, advance. **No banner on skip-all** —
  *"a banner is itself a nag."*
- **Ignoring is not rejecting.** Timeout/dismiss writes nothing — including nothing to
  `keyboardSuppressed`, which is why §0b-RESOLVED's honest worst case is "asks again next dictation".
  Only an explicit revert in the review surface demotes a pair.
- The live resolution must **stamp the per-occurrence verdict** so §5 shows it already resolved — **no
  double-ask** across the two surfaces.

### 4.6 The ask and the D10 ladder — when there is no card at all

The ask is a **pre-paste** confirmation. Two of D10's three tiers therefore have none:

| Tier | Ask? |
|---|---|
| ≤ 60 s | Yes — §4.1–4.5 as written. |
| 60–120 s (late corrections, §3.3b) | **No.** The paste already happened; a card asking to confirm an edit that is not in the pasted text would be asking about something the user cannot see. |
| > 120 s | **No** — nothing ran. |

**Consequence for the deadlines.** The ask deck's own budget (`MaxAsks = 3` × 6–10 s) is **not**
inside the 2.5 s spotter deadline — they are nested, not shared. `vocabulary-ctc-port.md §3.6`
specifies the two-level shape: **inner** 2.5 s on the spotter (a latency control, whose expiry is
the designed §3.3b path), **outer** around the whole gate+ask block (a hang-catcher, because a card
that never resolves throws nothing and would leave `State` stuck at `Transcribing`).

**Consequence for calibration.** Ask frequency will look lower in real use than §0b-RESOLVED's
worst case predicts, purely because long dictations skip the card. Do not read that as the throttle
working.

---

## 5. Area 4 — Review + learn

### 5.1 Where

`RecordingDetailPage`, a `ui:CardExpander` placed **directly under the transcript card**, rendered only
when this recording has corrections. This mirrors the Mac exactly (`review-ux.md §1`) and uses a
control the app already has on two other pages.

### 5.2 Collapsed header

```
[✨] Jot guessed on 2 words                                        ⌄
     Pick the word you meant.
```
When everything is resolved:
```
[✓] All reviewed                                                   ⌄
```

Copy is verbatim from `CorrectionReviewSection.swift:46-54`. Note the Mac says *"Pick the word you
meant"* (mouse-appropriate) where iOS says *"Tap"* — we take the Mac's.

### 5.3 Expanded — one row per occurrence

```
…met with lista about the launch…
[ KEPT ]   Original "lista"
 ( lista  IN TEXT )   ( Lisa )
────────────────────────────────────────────────────────────
…and nemo tron shipped it…
[ CHANGED ]   Original "nemo tron"
 ( nemo tron )   ( Nemotron  IN TEXT )
```

Resolved rows collapse to a single line with an Undo:
```
✓  Nemotron confirmed.                                       Undo
```

Atoms, ported from `CorrectionReviewSection.swift`:
- **Context line** — italic, secondary before/after, primary + dashed underline on the gated word,
  2 lines max. Windows has no serif body font convention; use the page font, italic.
- **Badge** — `CHANGED` (accent capsule) / `KEPT` (secondary capsule with a hairline border), 9px bold,
  letter-spaced. Reuse the existing chip `Border` recipe.
- **Chips** — original **first**, then the term; the one currently in the text carries an `IN TEXT`
  tag. Side by side; stack vertically if they don't fit rather than wrap a word.
- **Resolved copy** — `CorrectionCopy.resolvedParts` verbatim: bold lead + secondary rest.
  `{term} confirmed.` / `{term} applied here.` / `{original} restored.` / `{original} kept.`

**Keyboard story (the previous revision had none).** The block is a `ui:CardExpander`, so Space/Enter
toggles it. Inside: Tab moves between rows; within a row, ←/→ move between the two chips and Enter
picks; after a pick, focus lands on that row's **Undo**; after an Undo, focus returns to the row's
first chip. Each chip carries `AutomationProperties.Name="Use {word} — {CHANGED|KEPT} occurrence {n}"`.

**Per-occurrence, never grouped.** iOS's owner rejected grouping outright: *"the same surface word can
legitimately differ by location — 'cloud code' may really be *cloud code* in one spot and *claude code*
in another."*

### 5.4 What a pick does

```
pick(record, choice)
  → edit RecordingItem.Transcript (replace the span), IsEdited = true, persist
  → shift every later anchor by the length delta (§5.5)
  → flash the changed span
  → CorrectionProvenance.SetVerdict(recordingId, occurrenceKey, choice) → delta
  → CorrectionStore.Adjust(pair, delta)
  → refresh
```

**The flash — CORRECTED.** The previous revision asked for a "brief accent wash" over the changed span,
which §9 elsewhere says is impossible: the transcript is a read-only borderless `TextBox`
(`RecordingDetailPage.xaml:210-214`) and WPF's `TextBox` cannot do styled runs at all. **Implement it as
`TranscriptBox.Select(start, length)` + `Focus()`** — the WPF selection highlight *is* the accent wash,
it scrolls the span into view for free, and it costs one code-behind method (precedent for VM-adjacent
code-behind on this page: `RecordingDetailPage.xaml.cs:29-53`). Honour reduced motion by simply not
animating; the selection is static anyway.

Undo reverses symmetrically. Two directions, both single-gesture:
- **Overcorrection** (`CHANGED` → pick original): reverts the text **and** records −1. Two effects,
  and they are different thresholds — say both: at **net ≤ −1** the pair is actively blocked by
  `Decide` step (0) (`VocabularyGate.cs:577`, the predicate is `ov.Net <= -1`, **not** `<= 0`); at
  net 0 it is merely no longer armed, since arming needs `Net >= 1` (`:582`). "Revert + stop doing
  that" is one click.
- **Missed term** (`KEPT` → pick term): applies it **and** records +1, arming the override for a
  rare/OOV original immediately (`Decide` step (0), `VocabularyGate.cs:582`). The canonical "teach
  the missed term." **For a common-word original it arms nothing** (step (0) requires `!isCommon`) —
  it only sets `Prior ≥ 1`, which is why the ask deck is filtered to applied records (§4.1).

**De-duplicate per transcript**: one recording contributes at most ±1 per mapping, so resolving three
`nemo tron → Nemotron` rows cannot inflate net to 3 (`CorrectionStore.swift:151-154`).

**A common-word original never auto-applies from net alone.** `Decide` step (0)
(`VocabularyGate.cs:582`) requires `!isCommon`, permanently. So the UI must **never** promise
otherwise. iOS §v2-D: *"Never show 'n of 3,
not automatic yet' while the gate already auto-applies."* In v1.3 we show no automation state at all,
which sidesteps the whole class.

### 5.5 Storage, and the transcript-anchor problem

**Storage (D7) — one layout, shared with `vocabulary-ctc-port.md §3.7`:**

```
<DataDir>\Vocabulary\
    vocabulary.json      terms + aliases           (VocabularyStore)
    corrections.json     learned net per pair      (CorrectionStore)
    provenance.json      per-recording verdicts    (CorrectionProvenance)
```

`CorrectionProvenance` is keyed by `RecordingItem.Id` (a `Guid`, stable and persisted —
`Models/RecordingItem.cs:18`, round-tripped at `JsonRecordingStore.cs:125, :131`). **Do not** add
correction data to `RecordingItem` itself — Mac's `review-ux.md §0` calls the equivalent (a SwiftData
migration) the tripwire and rejects it. Our analogue is the library JSON schema; keep it out.

**Registration is mandatory and belongs in M1, not M3:**
- [ ] `"Vocabulary"` → `JotDataPurge.DataSubdirs` (`Services/JotDataPurge.cs:26`). Without it, "Erase
      all data" leaves terms and correction history on disk.
- [ ] `"Vocabulary"` → `DataFolderMigrator.MigratedItems` (`Services/DataFolderMigrator.cs:33`).
      Without it, a data-folder move strands the folder in the old location (`Finish()` `:186-199`
      deletes only `MigratedItems`) and the app shows an empty list on the new drive.
- [ ] Extend `tests/Jot.Tests/JotDataPurgeTests.cs:39-69` and `DataFolderMigratorTests.cs`. Those tests
      exist precisely so a new artifact cannot be forgotten.
- [ ] `VocabularyStore` resolves its directory **once at construction** from `JotPaths.DataDir(...)`,
      like `JsonRecordingStore.cs:30`, or it disagrees with the library store after a move.

**Divergence worth stating:** `prompts.json`, the app's other user-authored list, lives in
**`ConfigDir`** (`Services/PromptCatalog.cs:16, :43`). Vocabulary goes in **`DataDir`** on purpose — it
is user *data*, it should follow a data-folder move, and it should be erased by "Erase all data".
Reset-settings keeps it (**DECIDE-7**).

**Wiring points, mirroring `review-ux.md §0`:**
1. **Commit** immediately after `_store.Add(BuildRecording(...))` in `StopAndDeliverAsync`.
2. **Clear pending** at the start of every transcribe, so a rewrite or an aborted run cannot leak
   proposals into the next real recording.
3. **Discard on delete** — hook `IRecordingStore.Items.CollectionChanged` (the same seam
   `JsonRecordingStore.cs:69-76` uses), **not** `RecordingDetailViewModel.Delete` (`:193-198`), because
   Recents can delete too. Otherwise provenance leaks and grows unbounded.
4. **ReTranscribe and MediaImporter are scoped OUT of v1.3** — see §5.6.

#### The anchor problem, and what we do about it

Offsets are exact at save time (§1b) and stale forever after. **Four** writers mutate
`RecordingItem.Transcript`, all auto-persisting via `JsonRecordingStore.cs:90-106` with no undo:

| Writer | Geometry known? |
|---|---|
| `SaveEdit` — replaces the **whole** string (`RecordingDetailViewModel.cs:105-114`) | **No** |
| `ReplaceNext` — splices at `IndexOf(FindText)` (`:127-138`, the splice at `:134`) | **Yes** — index, find length, replace length |
| `ReplaceAll` — `string.Replace` over the whole transcript (`:140-151`, the replace at `:145`) | **No** — N sites, N deltas, and the app never computes the indices |
| §2.7 "Add to Vocabulary…" — replaces the selected span | **Yes** — this feature's own writer |

**"Hide the row on failure" does not survive contact with these.** Strict resolution alone silently
deletes most of the user's review list the first time they fix a typo — *including immediately after
they use this feature's own right-click entry point*, which sets `IsEdited = true` by design. And a
`ReplaceAll` that shifts offsets can leave a *matching* word at the shifted position, which resolves
cleanly onto the **wrong occurrence** — precisely the iOS bug they deleted.

**Design (v1.3), three parts:**

1. **Persist a transcript fingerprint.** `CorrectionProvenance` stores the SHA-256 (or a cheap
   64-bit hash) **and the length** of the exact transcript the proposals were computed against. On
   load, hash the current `Transcript`; if it does not match, the anchors are stale — **detectable**
   rather than merely sometimes-unresolvable. This is the piece that makes everything else safe.
2. **Shift anchors for the two edits whose geometry we know.** `ReplaceNext`, the §2.7 splice, and
   the §4.4 ask splice all call one method —
   `CorrectionProvenance.ShiftAnchors(recordingId, at, delta)` — which adds `(replaceLen - findLen)`
   to every stored `PublishedStart >= at`, drops any anchor whose own span was overwritten, and
   **re-stamps the fingerprint**. Review survives the ordinary case, including this feature's own
   entry point.
3. **`ReplaceAll` sets `AnchorsStale` in v1.3 — decided, not left to the implementer.**
   `ShiftAnchors(recordingId, at, delta)` **cannot express it**: `ReplaceAll` is one
   `Item.Transcript.Replace(FindText, ReplaceText, cmp)` (`RecordingDetailViewModel.cs:145`) — N
   sites, N deltas, and the app never computes the indices. The two honest options were (i) recompute
   the matches back-to-front and call `ShiftAnchors` once per site, or (ii) mark the whole recording
   `AnchorsStale`. **We take (ii):** it is one line, it needs no second matching implementation to
   stay in sync with `string.Replace`'s comparison semantics, and it is consistent with `SaveEdit`.
   A `ReplaceAll` that shifted anchors wrongly is worse than one that stops offering picks — a
   *matching* word can sit at the shifted position and resolve cleanly onto the **wrong occurrence**,
   which is precisely the iOS bug they deleted. Option (i) is a v1.4 refinement if review rows turn
   out to disappear often in practice.
4. **Free-form `SaveEdit` sets `AnchorsStale`.** No shifting is possible. The review block collapses to
   one tertiary line and stops offering picks:
   `You edited this transcript, so Jot can't line up its guesses any more.`
   Resolved rows and the learned `CorrectionStore` nets are **unaffected** — the learning is keyed on
   the pair, not the offset, so nothing the user already taught is lost. Same copy and same degrade
   for the `ReplaceAll` case above.

**Deviation, stated:** the review's cheapest option was "hide the whole review block when
`Item.IsEdited` is true." Rejected, and the evidence is stronger than "our own gesture trips it":
**`IsEdited = true` is set by §2.7's right-click add *and* by `ReplaceNext` (`:135`) *and* by
`ReplaceAll` (`:148`)** — so that rule would delete the review surface on *any* Find & Replace, not
just on the feature's flagship gesture. And `IsEdited` is a **one-way latch** (`RecordingItem.cs:36`,
never cleared anywhere), so once tripped the block would be gone forever, on that recording, for
every future correction. Parts 1–4 cost one hash field and one shift method and keep the two surfaces
coherent. Do not reopen this.

**Still true: no nearest-match fallback, ever.** iOS deleted theirs after it *"routinely resolv[ed] —
sometimes onto the wrong occurrence."* If an anchor fails strict resolution after shifting, hide that
row. A missing review row is a non-event; a review row that edits the wrong word is a data-loss bug.

**Tests:** (a) the §1b guard test — fail if any text mutation is inserted between the gate and
`_store.Add` **other than the whitelisted §4.4 ask splice**, and assert that splice goes through
`ShiftAnchors` and re-stamps the fingerprint on the post-splice string; (b) `ReplaceNext` shifts
anchors so the review rows still point at the right words; (c) `SaveEdit` **and `ReplaceAll`** each
set `AnchorsStale` and the block degrades instead of vanishing.

### 5.6 Paths deliberately not gated in v1.3

| Path | Decision |
|---|---|
| `Import/MediaImporter` (`:55-61`) | **Out.** It never calls `TextPipeline.Clean` (`RecorderController.cs:226` is the only `Clean` call site in the product), so it would gate a different string than the dictation path. Deliberate difference, documented. |
| `RecordingDetailViewModel.ReTranscribe` (`:215-237`) | **Out.** Also never calls `Clean`, and re-gating an item that already has committed proposals means invalidating them first. It only runs for `IsPending` rows (`:218`). Routing the gate through it means adding *both* the pipeline and the gate — v1.4 if wanted. |
| `Rewrite/RewriteController` | **Out, and must stay out.** Rewrite output never passes the gate (`:131` `_store.Add`, `:133` `Succeeded` → `PillController.cs:69`); it must pass an **empty** corrections list, and the store side must get no orphan proposals. |

---

## 6. Area 5 — Language honesty

### 6.1 The facts

- The CTC spotter checkpoint (`parakeet-tdt_ctc-110m`) is **English-only**
  (`vocabulary-ctc-port.md §2`). Jot Windows ships **40 locales** plus an **auto-detect** option
  (`LanguagePicker` / `NemotronLocales`).
- The **gate** is multilingual — 21 embedded common-word lists. But the gate is downstream of the
  spotter; with no detections there is nothing to gate.
- Running the English spotter on non-English audio is **actively harmful**, not merely useless.
  `verification.md §1` records vocab `["Lisa"]` clobbering the real Spanish word **`lista → Lisa`** at
  margin −8.2. And confidence is language-sensitive (EN ~1.0, ES/JA ~0.25–0.88), so thresholds do not
  transfer.
- **English-only is not the only limit — D12 is the second one, and it bites inside English.** The
  checkpoint's 1024-piece BPE has **no digits, no hyphen, no accented Latin, no CJK**, so `Wi-Fi`,
  `GPT-4`, `café` and `Zürich` are unspottable *even with the language set to English*. The two
  honesty surfaces are deliberately different: language is a **section-level** `ui:InfoBar` (it
  applies to everything at once), D12 is a **per-term** marker (§2.4). Do not merge them — a banner
  saying "some of your terms can't be heard" without saying **which** is a nag, not information.
- **We are the first platform to gate on language.** The Mac does **not** — `Transcriber.swift:331-343`
  gates only Japanese, on *model identity*; on the v3 European path the spotter runs on non-English
  with the common-word brake absent. The `lista → Lisa` incident is evidence *for* our design, not
  evidence anyone has already solved it.

### 6.2 D5 — explicit English selection only

**The previous revision promised behavior we cannot deliver.** It said auto-detect would run "only when
it detects English", gated on the *resolved* language of the recording. That is not buildable today:
language is a global setting applied once (`App.xaml.cs:389` → `SettingsViewModel.ApplyLanguage` →
`SettingsViewModel.cs:268-275`, snapshotted at session open); auto-detect is just another slot
(`NemotronLocales.cs:21-22`); the model *does* emit a `<xx-YY>` locale token but **both** transcribers
throw it away on the identical line (`NemotronTranscriber.cs:293`, `NemotronFp16Transcriber.cs:316`);
the only reader is the dev-only `--langprobe`; and `NemotronFp16Transcriber` has neither `Tokens` nor
`Piece`, so on the GPU tier the detected language is unrecoverable even for diagnostics. `RecordingItem`
records no language either.

**So: vocabulary runs iff `NemotronLocales.Normalize(Settings.Language)` starts with `en`.** Auto-detect
normalizes to `"auto"` and is therefore **off**. One line, zero engine change.

**Precedent:** `TextPipeline` already punts the same way — `LanguageCode.cs:38` excludes `"auto"`, so
auto-detect dictations today get no filler removal and no number normalization. Being consistent with
shipped behavior is defensible and honest.

### 6.3 What the user sees

A persistent, non-closable `ui:InfoBar Severity="Informational"` at the top of the Vocabulary section.

**Named non-English language:**

> **Custom vocabulary works in English only right now.**
> Your language is set to **Spanish — Español**, so Jot won't apply your terms. Your list is saved and
> will work as soon as you switch to English.

**Auto detect — new copy, replacing the promise we can't keep:**

> **Custom vocabulary needs your language set to English.**
> Your language is set to **Auto detect**, so Jot can't tell which language you're speaking. Set it to
> English in Settings → Language to use your terms. Your list is saved either way.

The master toggle stays **enabled and honest**: it can be turned on, it just does nothing right now, and
the InfoBar says so. The term list stays fully editable — people switch languages, and silently locking
their data behind a language setting is worse than telling them the truth. This is the Mac's own pattern
for its Japanese path (`VocabularyPane.headerSubtext`): explain the difference, don't hide the surface.

**We do not put a "vocabulary was skipped" notice on the pill.** A notice on every non-English dictation
is a nag about a feature the user cannot fix in the moment.

Where the user *does* find out at the moment of the question: the recording-detail review block, when
the user has ≥1 term and the language is not English, shows the same message as a one-line tertiary note
instead of a review section — so "why didn't it fix my name?" is answered where it is asked.

### 6.4 Elsewhere

- `VocabularyPage` header subtitle appends *" · English only"* when the selected language is not
  English, so the state is visible from the management surface too.
- The **Add to Vocabulary** dialog's status line switches (§2.7 table) — separate copy for
  non-English and for Auto detect.
- The Language dropdown in Settings gets **no** vocabulary warning. It is a transcription control used
  by everyone; hanging a feature caveat off it is scope creep.

### 6.5 Failure states — CORRECTED: the packaging trap is retired

The previous revision named *"the `Content` / `CopyToOutputDirectory` csproj trap"* as our analogue of
the Mac's missing-`common-words.txt` bug, and specified a "Vocabulary is turned off — a required file is
missing / Reinstall Jot" surface. **That trap does not exist here, and that copy is deleted.**

```xml
<!-- src/Jot/Jot.csproj:39 — EmbeddedResource, not Content, on purpose. -->
<EmbeddedResource Include="Vocabulary\Resources\common-words*.txt" />
```

The port loads them via `GetManifestResourceStream` (`EmbeddedCommonWordsProvider.Load`,
`Vocabulary/CommonWords.cs:62`) and `CommonWordLists_AreEmbeddedAndPopulated`
(`VocabGoldenFixtureTests.cs`) pins every one. An embedded resource cannot go missing without failing
the build. Worse, the "disable vocabulary and force the toggle off" contract **contradicts the ported
code**: on a missing *named* resource `Load` (`CommonWords.cs:53-88`) reports once to
`IDiagnosticsSink` via `ReportMissing` (`:90-100`) and returns an empty set, and `Words(null)`
returns empty *silently and intentionally* for a language with no list (`:49`, `ResourceFor` `:39-45` —
only 21 of 40 locales ship one). It is designed to degrade, not to fail closed. Do not layer a second
detection path on top of it; if a last-resort assert is wanted, hang it off the existing
`IDiagnosticsSink` report.

**What the fail-closed InfoBar is actually for: the spotter model.** That is a genuine runtime condition
with a real user action.

> **Vocabulary unavailable — the model couldn't be prepared.**
> Try downloading it again. Your terms are safe.

`Severity="Warning"`, in the Vocabulary section, with `[Download]` / `[Retry]` as the action and the
`notDownloaded / downloading / ready / failed(reason)` state model behind it. Note the Mac's headline is
deliberately *"Boost unavailable — <reason>"* not *"Download failed"*, because a failure can also come
from a tokenizer error on an already-downloaded bundle. Recourse copy must not say "Reinstall Jot" — for
a Store user that is not actionable; point at Help → Send feedback (`Views/HelpPage.xaml:12`), the app's
existing recourse.

---

## 7. Area 6 — First-run and discovery

### 7.1 v1.3 ships VISIBLE and default-off

> **SUPERSEDED 2026-07-25 (owner).** This section originally required a literal
> `Visibility="Collapsed"`. It is now **removed**: the section is visible inside Advanced features with
> the master toggle default off. Rationale — **default-off is the safety; hiding on top of it only
> guaranteed nobody would turn it on**, so no real-speech data would ever reach the accept threshold,
> which is still derived from a single clean TTS clip. The "Experimental" badge stays.
>
> **The re-hide condition is the MODEL RELEASE, not calibration.** `CtcModelInstaller.ReleaseTag`
> (`jot-model-parakeet-ctc-110m-v1`) is not cut, and nothing in the app calls the installer, so a user
> who switches the toggle on today reaches a dead end. **Before any Store submission: confirm the
> release is live and a fetch path exists, or put `Visibility="Collapsed"` back on the StackPanel.** The
> "Vocabulary unavailable" InfoBar carries that story in the meantime and must stay honest.
>
> The rest of this section is kept because the search-indexer coupling it documents is still real — it
> now works the other way round: with the literal gone, searching "vocab" *does* surface these rows.

**These are two different things, and the previous revision asserted both.** The house rule in this repo
was a **literal** `Visibility="Collapsed"` with no binding, and that is not cosmetic — the Settings search
indexer *depends* on it:

```csharp
// src/Jot/Views/SettingsPage.xaml.cs:196-198
if (fe.Visibility == Visibility.Collapsed
    && fe.GetBindingExpression(VisibilityProperty) is null)
    continue;   // statically hidden by design — don't descend

// :135 — search force-reveals the entire advanced pane while typing
if (searching) ForceVisible(AdvancedPanel); else RestoreVisibility(AdvancedPanel);
```

So binding the Vocabulary section to `AdvancedFeatures` would mean (a) every Advanced user sees it
immediately, and (b) **any** user who types "vocab" in Settings search surfaces the whole section even
with Advanced off. That is not hidden. (`AdvancedFeatures` itself is genuinely off for most users — no
default initializer, `ISettingsStore.cs:18` → `false`, and the wizard deliberately does not flip it on,
`WizardViewModel.cs:188` — but the search path defeats it.)

**Decision as shipped (2026-07-25):**
- **No `Visibility` attribute at all** on the Vocabulary `StackPanel`. It inherits `AdvancedPanel`'s
  visibility (`SettingsPage.xaml:164`), so Advanced features still gates it, and Settings search now
  reaches it — accepted.
- **Re-hide trigger:** the model release, not calibration. See the superseded note above.
- Keep the section inside `AdvancedPanel` either way, as §2.1 says.

Default **off**, "Experimental" badge. Mac shipped the identical way
(`design.md §6 Q4 [DECIDED]`), and its stated plan is the one to copy: default-off → log every
APPLY/BLOCK/OVERRIDE verdict over ~20–30 real dictations → eyeball false blocks/applies → enable.

**Consequence: there is still no first-run moment in v1.3.** A tour for a default-off feature whose model
cannot be downloaded is a tour nobody can act on. Build it with the model release.

Two things that must happen the moment it unhides, both one-liners:
- **`docs/features.md` §9.5** (`:376-379`) — currently reads *"Built-but-hidden: … non-functional."*
  Rewriting it is mandatory under ARCHITECTURE.md's "any user-facing change" rule.
- **One `Tour` in `TourCatalog.All`** (id `"vocabulary"`) + one button on `HelpPage`. Three cards,
  verb-first, ≤15 words each:
  1. `Add24` — **Teach Jot your words** — *"Add names, products, and jargon in Settings → Vocabulary."*
  2. `Cursor24` — **Fix it where you find it** — *"Right-click a wrong word in any transcript to add it."*
  3. `CheckmarkCircle24` — **It learns** — *"Confirm a guess on a recording and Jot remembers it next time."*

  `TourCatalog.MarkShown` appends the id to `JotSettings.ShownTours` — no new bool, no migration. The
  existing guard *"a tour must never appear mid-dictation"* applies free.

### 7.2 Discovery, once visible

Ranked by value, and deliberately short:

1. **Right-click → "Add to Vocabulary…"** (§2.7). Contextual, appears at the moment of annoyance, zero
   chrome when unused. This is the whole discovery strategy.
2. **The empty-state second line** on `VocabularyPage` names the right-click path (§2.6).
3. **The chip** (§3.3) — the reassurance that it is working, which is what a default-off feature needs
   most in its first week.
4. **Help card** under Basics, with the Settings row's `Description` as the short form.

### 7.3 What we explicitly do not do

- **No proactive "you seem to be correcting the same word — add it?" nudge.** Good idea (iOS ships it on
  Find & Replace), and it is v1.4. Shipping it alongside a feature whose false-positive rate is
  uncalibrated is how you train users to dismiss it.
- **No contact/name import.** iOS rejected it explicitly — owner: *"there will be 1000"* — because it
  floods the gate with names that collide with common words.
- **No banner, toast, or badge announcing the feature exists.** iOS's rule: *"a banner is itself a nag."*

---

## 8. Copy deck

Everything user-visible, in one place. House style, inherited from iOS: **sentence case, no exclamation
marks, em dashes fine, no emoji.**

**Settings → Vocabulary**
```
Vocabulary                                     [Experimental]
Custom vocabulary
Words Jot should get right — names, products, jargon. Runs entirely on this PC.
Your terms
{N} term / {N} terms
Manage…
Vocabulary model
About 130 MB, downloaded once. Needed before your terms can change anything.
Downloading vocabulary model…
Vocabulary model ready
Vocabulary unavailable — {reason}
Download        Retry
Tell me when a term is used
Shows a small mark on the pill that appears when a dictation finishes.
```

**VocabularyPage**
```
Back
Vocabulary
Terms Jot should prefer when it transcribes. Kept on this PC. Works best under about 100 terms.
 · English only                                            (appended when language isn't English)
Vocabulary model downloading — {N}%
Vocabulary model not downloaded — your terms are saved and will apply once it is.
Search terms
also heard as: {a} · {b}
Add a term          Edit term
Term
How you want it spelled.
When Jot hears  (optional)
The wrong spellings Jot writes instead. Each one makes Jot likelier to fix it.
Add a sounds-like spelling
Add term            Save changes            Cancel
Edit {term}         Delete {term}                          (automation names)
No terms yet
Add names, products, and jargon you want Jot to spell your way.
Right-click a wrong word in any transcript to add it in one step.
No terms match your search.
Already in your list.
Already in your list — added {N} new spellings.
"{alias}" was listed under {other term} — moved it here.
You've reached 200 terms. Remove one to add another.
Renaming clears what Jot learned about this term.
NOT HEARD                                                  (row badge, D12)
```

**Inline warnings** *(advisory)*
```
Too short — terms under 3 characters are skipped to avoid false replacements.
Common word — Jot won't swap this on its own. You can confirm it on the recording.
Add a sounds-like spelling — Jot rarely hears short acronyms correctly on its own.
Use a single word or short phrase (max 4 words).
```

**Can't-work states (D12)** — *these describe a permanent limit of the model, so every line names
the fix rather than just refusing*
```
Single letters can't be matched. Use at least two characters.                    (hard block)
Jot can't listen for numbers. Try the spelled-out form — "GPT four" instead of "GPT-4".
Jot can't listen for hyphens. Add "Wi Fi" as a spelling instead, and Jot will still write "Wi-Fi".
Jot can't listen for "é". Add an unaccented spelling like "cafe" — Jot will still write "café".
Jot can't listen for these characters. Add a spelling using plain English letters.
```

**Bulk import**
```
Looks like a list — {N} terms found.
Add as one term          Add {N} terms
Added {N} terms. Skipped {M} — already in your list, or under 2 characters.
Added {N} terms. Skipped {M} — the list is full at 200.
Nothing added — those {N} terms are already in your list.
{N} of these need a sounds-like spelling before Jot can hear them.
```

**Add to Vocabulary dialog (right-click)**
```
Add to Vocabulary
When Jot hears
Spell it as
Future dictations will prefer this spelling.
Custom vocabulary is off — turn it on in Settings to use this.
Saved. Jot downloads the vocabulary model (about 130 MB) the first time you use it.
Saved. The vocabulary model is still downloading.
Saved. Vocabulary only applies when your language is set to English.
Saved. Vocabulary needs your language set to English — it's on Auto detect.
Select just the word — up to 4 words.
Fixed here. "{phrase}" is an everyday phrase, so it wasn't added to your vocabulary.
Saved the spelling, but Jot can't listen for "{chars}". Edit this term in Settings to add a
  plain-letter spelling.
Cancel          Add
```

**About → Models & licences (D11 — new card, engine milestone M2)**
```
Models & licences
Speech recognition and custom vocabulary run on models from NVIDIA, redistributed under
CC BY 4.0.
Parakeet TDT-CTC 110M — model.int8.onnx, tokenizer.model
```

**Pill**
```
✦ {term}          ✦ {N}
Vocabulary
{original} → {term}
+{N} more
Transcription ready. Vocabulary used {term}.                    (automation)
Transcription ready. Vocabulary used {N} terms.                 (automation)
```

**Ask card (v1.3, applied corrections only)**
```
Jot wrote {term}.
{N} of {M}
Use {word} — occurrence {n}                                     (automation, per chip)
```

**Review block (recording detail)**
```
Jot guessed on {N} word / Jot guessed on {N} words
Pick the word you meant.
All reviewed
CHANGED     KEPT
Original "{originalWord}"
IN TEXT
{term} confirmed.
{term} applied here.
{original} restored.
{original} kept.
Undo
You edited this transcript, so Jot can't line up its guesses any more.
```

**Language honesty**
```
Custom vocabulary works in English only right now.
Your language is set to {Language}, so Jot won't apply your terms. Your list is saved and
will work as soon as you switch to English.

Custom vocabulary needs your language set to English.
Your language is set to Auto detect, so Jot can't tell which language you're speaking. Set it
to English in Settings → Language to use your terms. Your list is saved either way.
```

**Model unavailable**
```
Vocabulary unavailable — the model couldn't be prepared.
Try downloading it again. Your terms are safe.
```

---

## 9. What I would NOT build in v1.3

Scope discipline, with the reason each one is cut.

| Cut | Why |
|---|---|
| **The block-ask** ("did you mean Lisa?") | **D8.** `AskPolicy.WorthAsking` (`AskPolicy.cs:98-104`) cannot select a `kept` record with `Prior == 0`, and the §4.1 caller-side filter (`Outcome == "applied"`) keeps out the `Prior ≥ 1` ones the review/right-click gestures create — which the unfiltered policy would otherwise ask about on **every** later dictation, forever, since a common-word original never clears `Decide` step (0) (`VocabularyGate.cs:582`). Blocks live on the review surface only. |
| **"Stop asking" / `suppressedBlocks` / `blockedKeeps` UI** | Same reason — no reachable writer on this path. The ported fields stay (conformance surface); no UI, no claims. |
| **The `notable` filter** | §1. Constant-true on our engine path. Dead code dressed as a control. |
| **Alternates / the 3-option ask** | `ApplyFromDetections` emits `Alternates: []` unconditionally (`VocabularyGate.cs:491`). There is nothing to show. |
| **Merge-shape one-shot teach lane** | Same — `Shape` is null on our path. The underlying problem ("sri ram" → Sriram) is real and is solved *engine-side* by `enrichedAliases` (§2.3), plus the user can type the merged form as an alias today. |
| **"Always replace" grant** | The `alwaysReplace` escape hatch for common-word originals. iOS shipped it; **Mac did not** (zero hits in `JOT-Transcribe/Sources`). It is the most dangerous control in the system — it re-enables silent rewriting of an everyday word. Ship it only if users ask, and only after calibration. |
| **Vocabulary on auto-detect language** | **D5.** Per-recording resolved language does not exist on either engine today (§6.2). v1.4, and it benefits `TextPipeline` too. |
| **Gating ReTranscribe / MediaImporter / rewrite** | §5.6. Different strings, different invalidation rules. |
| **Migrating learned nets across a term rename** | §2.5b. Doing it wrong silently transfers a learned override onto a different word. |
| **Inline marked transcript** (blue/dashed underlines, click-a-word → popover) | Mac scoped it to "Later" and shipped the accordion alone. WPF's read-only `TextBox` cannot do styled runs — this would mean a `RichTextBox`/`FlowDocument` rewrite of the transcript surface. |
| **Clickable pill chip → deep-link** | Reserve the affordance; a 4-second click target is a bad target. Cheap to add later. |
| **Proactive discovery nudges** (Find & Replace offer, "add this?" banners) | §7.3. High value, but not before the false-positive rate is calibrated. |
| **Per-term weights / boost sliders** | `vocabulary-plan.md` non-goals: schema reserves a weight field, not surfaced. |
| **Multilingual spotter** | Different checkpoint, own investigation (`vocabulary-ctc-port.md §2`). |
| **A Vocabulary nav-sidebar entry** | Advanced-gated and default-off. `RecordingDetailPage` sets the precedent for a page reached programmatically — **with a Back button** (§2.2). |
| **A "what Jot has learned" ledger view** | Deferred on both other platforms. The review block already shows learning where it is legible: on the recording. |
| **Alias bulk import** | Terms only. The JSON file is the escape hatch. |
| **A new `InfoTip` popover control** | §1c. Would make vocabulary the only screen in the app with one. |
| **A "required file is missing" fail-closed surface** | §6.5. The packaging trap is retired by `EmbeddedResource` + a green test; the copy would describe a state that cannot occur. |
| **A "vocabulary is still working" indicator** for the §3.3b background pass | D10. Narrating work the user cannot act on is §7.3's "a banner is itself a nag" in a different costume. The corrections either make the 2.5 s deadline or show up on the recording. |
| **A regex/charset spottability check in the ViewModel** | D12. `CtcTokenizer.IsSpottable` is the one source of truth; a regex would be a second, silently drifting model of the checkpoint's BPE vocabulary. Before the model is downloaded, show **no** can't-work markers rather than guessing (§2.4). |
| **A section-level "some terms can't be heard" banner** | D12 is per-term by design (§6.1). A banner that says *some* without saying *which* is a nag with no action attached. |
| **UI-string localization** | The app has none; this feature does not start it. |

---

## 10. Where this deviates from Mac/iOS, and why

1. **No `notable`, no `unsure`-driven anything.** Those signals are constant on the detection path.
   *(Neither Mac doc states this, because Mac's Nemotron path never got a UX.)*
2. **Aliases are a base feature, not Advanced-gated.** Mac hides them behind Advanced; iOS has none and
   calls it a gap. With no per-word confidence, aliases are our main plausibility lever (§2.3).
3. **A dedicated page, not a chip block or a Settings-embedded form** — and, unlike PromptsPage, it
   carries its own Back button because it has no sidebar entry.
4. **The common-word warning checks the real 24k list** (already the SSA-name-stripped build), and its
   copy states what the gate actually does — blocks, and does **not** ask (D8 + the §4.1 filter).
5. **The ask is an applied-correction confirmation, not a block prompt.** `AskPolicy` ships unchanged
   and the *caller* filters the deck to `Outcome == "applied"` (§4.1) — without that filter a taught
   common-word pair (`Prior ≥ 1`, permanently blocked) would be asked about forever. Mac and iOS both
   had richer signals and could ask about blocks; we cannot.
6. **The ask is a separate activatable window, not a pill state.** `PromptPickerWindow` is the in-repo
   precedent; the pill keeps `WS_EX_NOACTIVATE` untouched (D9).
7. **Language gating exists at all.** Mac gates only Japanese, on model identity, and runs its spotter on
   non-English with the brake absent. We are the first platform to build this — and v1.3 requires an
   *explicitly selected* English locale, which is stricter than anything either Apple platform does.
8. **Anchors are shifted, not abandoned.** iOS deleted their nearest-match fallback; we replace it with
   a fingerprint + geometry-known shifting, because our own flagship gesture edits the transcript
   (§5.5).
9. **"Pick the word you meant", not "Tap".** Mouse-first wording.
10. **We tell the user which terms the model can never hear (D12).** Neither Mac nor iOS surfaces
    this — both ship the same 1024-piece English BPE and both silently accept `Wi-Fi`. We have their
    scar tissue and can do better cheaply, since `CtcTokenizer.IsSpottable` already exists.
11. **We have a third delivery mode (D10/§3.3b): corrected-after-the-paste.** Mac and iOS do not,
    because Apple silicon runs this checkpoint fast enough not to need one. On a Ryzen 7 3700X, 60 s
    of speech is 1987 ms — so the tiering is a Windows-hardware fact, not a design preference.

---

## 11. Risks

**1 — The Mac's anti-annoyance machinery is inert on our engine, and it looks like it isn't.** `Unsure`
is always false, `Margin` always 0, `Confidence` pinned at 0.85, `AskCandidate` always true, `notable`
always true. A reviewer skimming the Mac docs will "port the filters" and ship something with no
throttle at all while believing it is tuned. *Mitigation:* §1 is the first section of this doc for that
reason; the live throttles in v1.3 are the opt-out toggle, `MaxAsks = 3`, and per-pair suppression on
answer — and §0b-RESOLVED now states the honest worst case rather than the flattering one.

**2 — Language.** English-only spotter, 40 locales, plus an auto-detect mode. The failure mode is not
"nothing happens" — it is `lista → Lisa`, a correct foreign word destroyed. *Mitigation:* D5 — run only
on an explicitly-selected English locale; never partially enable; the answer is discoverable in both the
management surface and the recording detail.

**3 — Placement without timings can pick the wrong occurrence.** `ApplyFromDetections` maps a
detection's audio midpoint to a fractional word index (`ApplyFromDetections`'s placement loop,
`VocabularyGate.cs:412-413`) and takes the nearest
plausible unclaimed word. `vocabulary-ctc-port.md §4` lists this as unverified, and also flags that
getting the CTC **frame duration** wrong puts *every* detection on the wrong word. *Mitigation:* the
review row's context line exists precisely to disambiguate; strict anchors so a wrong-occurrence row is
hidden rather than mis-edited; Undo on every resolved row; and M2's verification must include a
long-dictation, repeated-near-miss fixture, not just planted single terms.

**4 — The feature does its headline job only under ~60 s (D10).** Measured: 60 s of speech costs
1987 ms on CPU and 180 s costs 9662 ms, super-linearly. So above the 2.5 s deadline the pasted text
is **not** corrected (§3.3b), and above 120 s nothing runs. A user who dictates long-form will
conclude the feature is broken. *Mitigation:* the tiers are designed, not accidental; the review
surface still shows the corrections; DirectML is the lever M2 measures; and if the middle tier turns
out to be rare, `vocabulary-ctc-port.md §4` item 9 collapses it to a plain skip rather than shipping
a half-mode. *Not mitigated:* we do not tell the user it happened, on purpose — see §3.3b rule 4.

**5 — D12's character limit is invisible until you hit it.** A user whose actual vocabulary is
product names with hyphens and digits (`GPT-4`, `Wi-Fi`, `S3`) will type five terms, none of which
can ever match, and none of which errors. *Mitigation:* the per-term `NOT HEARD` marker and the
recipe-shaped copy (§2.4) — and the copy is written as *"add a plain-letter spelling"* because in
almost every case there **is** a working configuration; the user just has to be told what it is.
*Residual:* the marker cannot render before the model is downloaded (§2.4), so a user who adds terms
first and downloads later sees nothing until then.

**Also on the list, below the top three:**
- **Vocabulary breaking a dictation** — D6. The gate/spotter/ask block must be independently wrapped
  so a throw still reaches `_store.Add` and `PasteAtCursor`, with a test per exit path — **and a
  wall-clock deadline around the whole block, because a hang throws nothing and skips the `finally`
  too** (§4.4).
- **The paste target** — an activatable ask card makes Jot's own window the foreground at paste time
  unless `_originWindow` is forced, on the **error** path as well as the happy one (§4.3).
- **Anchors** — three existing writers plus this feature's own splice; see §5.5.
- **Data lifecycle** — `Vocabulary\` missing from `JotDataPurge` / `DataFolderMigrator` means it survives
  Erase and is lost on a data-folder move (§5.5). Two lines; do them in M1.
- **Rewrite inherits a stale chip** — `_rewrite.Succeeded` routes into `OnTranscriptReady`
  (`PillController.cs:69`); it must pass an empty corrections list explicitly.
- **RETIRED: the name-bearing common-word list.** Verified name-stripped with the dual-meaning allowlist
  intact (§2.4).
- **RETIRED: the `CopyToOutputDirectory` packaging trap.** `EmbeddedResource` + a green test (§6.5).

---

## 12. Open decisions

| # | Decision | Status / recommendation |
|---|---|---|
| **DECIDE-1** | Build an `InfoTip` popover control, or lean on `SettingRow.Description` + a Help card? | **Don't build it here.** App-wide control decision, not a vocabulary one. |
| **DECIDE-2** | Dedicated `VocabularyPage`, or an inline `ui:CardExpander` block in Settings? | **RESOLVED: dedicated page** (§0a). Must carry a Back button (§2.2). |
| **DECIDE-3** | Is a paste-holding ask wanted on Windows? | **RESOLVED: yes, in v1.3** (§0a) — but D8 constrains it to *applied* corrections, and D9 fixes how it is built. |
| **DECIDE-4** | Does v1.3 include the learn loop? | **RESOLVED: yes** (§0a). It is now doubly load-bearing: with no block-ask, review is the *only* surface where a blocked near-miss is visible (§3.5). |
| **DECIDE-5** | Ship the pill chip in v1.3, or stay silent until calibration? | **Ship it, default on.** It is the only feedback a default-off feature has that it works. |
| **DECIDE-6** | Auto-detect: run on resolved-English, or refuse? | **RESOLVED by D5: refuse.** Resolved language is not obtainable today; the previous "run on resolved-English" answer promised behavior we cannot deliver (§6.2). Revisit in v1.4 with the locale-token work. |
| **DECIDE-7** | Erase / reset semantics. | **"Erase all data" deletes `<DataDir>\Vocabulary\`; "Reset settings" keeps it.** Registered in `JotDataPurge.DataSubdirs` **and** `DataFolderMigrator.MigratedItems`, with both test suites extended (§5.5). |
| **DECIDE-8** | Term cap. | **Cap at 200, advise 100** — and the advice now actually appears, in the page subtitle (§2.2). Every term is a spotter query, so the cap is a **latency budget**. **M0 gave that budget real numbers, and they change the shape of the argument:** the encoder pass is **1781 ms of the 1987 ms** at 60 s and is *independent of term count* — the per-term cost is only the DP over an already-computed log-prob matrix. So the cap is defensible at 200 unless M2 shows the DP is a material share of the 2.5 s deadline. **Re-confirm against M2's DP timings, not against the encoder numbers.** |
| **DECIDE-9** | "Always replace" grant — ever? | **Not in v1.3.** Mac never shipped it. Note its absence is what makes one of `AskPolicy`'s four guarantees dead code (§0b-RESOLVED). |
| **DECIDE-11** *(new)* | Keep the §3.3b late-correction tier, or collapse it to a plain skip above 60 s? | **Built for v1.3, provisionally.** "The term appears in what you paste" is the feature, and silently doing nothing on a 90 s dictation is not acceptable. **But if M2's duration distribution shows the 60–120 s band is rare, delete the whole background path** rather than ship a mode nobody hits (`vocabulary-ctc-port.md §4` item 9). Decide with the number, in M2. |
| **DECIDE-10** *(new)* | Anchor policy on user edits. | **Fingerprint + shift-on-known-geometry (`ReplaceNext`, the §2.7 splice, the §4.4 ask splice) + `AnchorsStale` for free-form `SaveEdit` *and* for `ReplaceAll`** — `ShiftAnchors(at, delta)` cannot express an N-site `string.Replace` (§5.5). Explicitly *not* "hide the block when `IsEdited`": §2.7's own gesture, `ReplaceNext` and `ReplaceAll` all set that one-way latch, so the rule would delete the review surface forever on any Find & Replace. |

---

## 13. Build order (UX only; engine milestones live in `vocabulary-ctc-port.md`)

Slots into that doc's **M3 (1–2 days)**, and splits it. Engine re-estimates for context: M0 2–3 d,
M1 remainder 1–2 d, M2 4–5 d.

- **M3a — management.** `VocabularyStore` (**new file**) + `Views/VocabularyPage.xaml`(`.xaml.cs`) +
  `ViewModels/VocabularyViewModel.cs` + `Models/VocabularyTerm.cs` (**all new**) + the Settings section
  + language InfoBar + bulk import + the right-click "Add to Vocabulary…" dialog + the §2.5b states
  + **the D12 validation states (§2.4): the length-1 hard block, the `NOT HEARD` row badge, the
  can't-work copy on the page form and the right-click dialog, and the "no markers until the model
  is ready" rule.** Testable with no model and no microphone — **except** the model-status rows and
  the live `IsSpottable` results, which need M2's tokenizer wiring.
- **M3b — signal.** The `TranscriptReady` payload widening, the pill chip (`OneLine` budget + column 3
  + folded automation name), the expand-panel list, the opt-out toggle, the model-status rows on
  both Settings and the page, **and the §3.3b late-correction rules — chip only if the pill is still
  up, never resurrect a dismissed pill, no background-work indicator.** Requires M2 (real
  detections).
- **M3c — learn + ask.** `CorrectionStore` + `CorrectionProvenance` (**new ports**), the review
  `ui:CardExpander`, pick/undo, the anchor fingerprint + shifting (§5.5), and
  `Controls/AskCardWindow.xaml` (**new**, a `PromptPickerWindow` clone) with the `Outcome ==
  "applied"` deck filter (§4.1), the forced paste target, the always-delivers `TaskCompletionSource`
  and the wall-clock deadline around the whole gate+ask block (§4.4). Fixture-driven from
  `correction_store_roundtrip.json`, `provenance_verdicts.json` and `ask_policy_select.json` — all three
  are in scope now that the ask ships.
- **Then** calibration (~20–30 real dictations, verdict logs), **then** unhide, **then**
  `features.md §9.5` + the `TourCatalog` entry + the `AdvancedFeatures` binding flip (§7.1), all in one
  commit.

### Concrete file list

**Add**
- `src/Jot/Views/VocabularyPage.xaml` + `.xaml.cs` — parameterless ctor resolving
  `App.Services.GetRequiredService<VocabularyViewModel>()`, mirroring `Views/PromptsPage.xaml.cs:7-11`.
  **Row 0 must carry a Back button.**
- `src/Jot/ViewModels/VocabularyViewModel.cs` — mirror `PromptsViewModel`: `ICollectionView Terms`,
  `SearchText`, `NewTerm`, alias chip collection, `EditingTerm`/`IsEditing`/`AddButtonText`,
  `AddTermCommand`/`EditTermCommand`/`DeleteTermCommand`/`CancelEditCommand`, **plus `BackCommand`**.
- `src/Jot/Models/VocabularyTerm.cs` — mirror `Models/PromptItem.cs`: `Guid Id`, `Term`,
  `ObservableCollection<string> Aliases`, computed `AliasSummary` and `Warning`.
- `src/Jot/Services/VocabularyStore.cs` — mirror `Services/PromptCatalog.cs` (optional `storageDir` ctor
  param for tests, `Load`/`Save`, `AddMapping`, one `SanitizeTerm` choke point, `MaxTermWords = 4`).
  Resolve the dir **once at construction** like `JsonRecordingStore.cs:30`.
- `src/Jot/Vocabulary/CorrectionStore.cs`, `src/Jot/Vocabulary/CorrectionProvenance.cs` — **new ports.**
- `src/Jot/Controls/AddToVocabularyWindow.xaml` + `.xaml.cs` — §2.7 modal.
- `src/Jot/Controls/AskCardWindow.xaml` + `.xaml.cs` — §4.3, a `PromptPickerWindow` sibling.

**Modify**
- `src/Jot/App.xaml.cs` ~`:2381-2389` — register `VocabularyStore`, `CorrectionStore`,
  `CorrectionProvenance`, `VocabularyViewModel` as singletons alongside `PromptCatalog`/`PromptsViewModel`.
- `src/Jot/Views/SettingsPage.xaml` `:298-349` — replace the chip block with the InfoBar + four
  `ctl:SettingRow`s; keep `Tag="section"`; "Manage…" → `INavigator.Navigate(typeof(VocabularyPage))`.
  **No `Visibility` attribute** — visible inside Advanced features, toggle default off (§7.1, revised
  2026-07-25).
- `src/Jot/ViewModels/SettingsViewModel.cs` `:554-571` — delete the in-memory stubs; add
  `VocabularyEnabled`, `VocabularyChipEnabled`, `TermCountText`, `ManageVocabularyCommand`, and the
  CTC model-status/download properties. While here, fix the live bug at `:473` (only `_download` is
  refreshed after a data-folder move; `_gpuDownload` is already missed) by moving to a collection.
- `src/Jot/Services/Abstractions/ISettingsStore.cs` — add `VocabularyEnabled` (default `false`) and
  `VocabularyChip` (default `true`). No migration needed (missing JSON keys fall back to defaults,
  `JsonSettingsStore.cs:29-45`).
- `src/Jot/Services/JotDataPurge.cs` `:26` and `src/Jot/Services/DataFolderMigrator.cs` `:33` — add
  `"Vocabulary"` to both lists (**M1**, not M3).
- `src/Jot/Views/RecordingDetailPage.xaml` `:210` (context menu, with `Copy`/`SelectAll` re-wired) + a
  review `ui:CardExpander` under the transcript; `.xaml.cs` for selection capture and the §5.4
  flash-via-`Select`.
- `src/Jot/ViewModels/RecordingDetailViewModel.cs` — review rows, pick/undo, and the §5.5 anchor
  policy: **`ReplaceNext` `:127-138` shifts**; **`ReplaceAll` `:140-151` and `SaveEdit` `:105-114`
  set `AnchorsStale`** (`ShiftAnchors(at, delta)` cannot express `string.Replace`'s N sites).
- `src/Jot/Recording/RecorderController.cs` `:226-253` — gate + ask insertion inside the D6 wrapper
  (**two nested deadlines: 2.5 s inner on the spotter, an outer one around the whole block;
  `finally` force-closes any live card** — §4.6), `TranscriptReady` widening, **forced paste target
  at `:243` whenever the block was entered** (note `PasteAtCursor` at `:244` is gated on `AutoPaste`
  at `:238`); `Start()` force-resolves a live deck **and cancels any in-flight §3.3b background
  pass**; and the D10 tier routing (skip above 120 s of trimmed speech).
- `src/Jot/Services/PillController.cs` `:69` (empty corrections for rewrite) and `:178` (chip payload).
- `src/Jot/Controls/PillWindow.xaml` `:22-40` (chip in column 3) + `.xaml.cs` `:193-199` (Success
  `OneLine` budget 60→46 with a chip, folded automation name), `:48` `ExpandPanel` correction list.
  **No focus/activation changes to this window, ever** (D9).
- `tests/Jot.Tests/JotDataPurgeTests.cs` `:39-69` and `tests/Jot.Tests/DataFolderMigratorTests.cs` —
  extend for `Vocabulary\`.
- `src/Jot/Views/PromptsPage.xaml` `:94, :98` — file the icon-button automation-name gap found here.
- **`src/Jot/Views/AboutPage.xaml` — a new "Models & licences" `ui:Card` after the "Your impact"
  card (D11).** Verified: the page has **no** attribution section today (logo / vision / privacy /
  impact / donate / iPhone / troubleshooting). This is a **licence term**, not polish, and it lands
  in the **engine** milestone M2, not M3.
- **At unhide only:** `docs/features.md` §9.5 (`:376-379`), `src/Jot/Controls/TourCatalog.cs` `:114`,
  `src/Jot/Views/HelpPage.xaml` (Basics card + Tours button), and flip `SettingsPage.xaml:300` to the
  `AdvancedFeatures` binding.

**Deliberately NOT modified**
- `src/Jot/Shell/MainWindow.xaml` — no sidebar entry, per §9. `RecordingDetailPage` sets the precedent
  for a programmatically-reached page.
- `src/Jot/Transcription/Nemotron/MelFrontend.cs` — the CTC front-end is a separate class
  (`vocabulary-ctc-port.md` D2).
