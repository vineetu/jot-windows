# Adversarial review — `vocabulary-ux.md` vs. the actual codebase

> Reviewed 2026-07-25 against `src/Jot` @ working tree. Every claim below was checked by reading the
> file cited. §0a owner decisions (learn loop IN, ask-before-paste IN, dedicated `VocabularyPage`) and
> §0b-RESOLVED (ship `AskPolicy` unchanged) are treated as fixed and are **not** re-litigated — this
> review asks only whether the design as written can be built *given* them.
>
> **Verdict: NEEDS REVISION.** 4 blockers, 7 majors. The architecture is right and unusually
> well-grounded; the blockers are all fixable in the doc plus small, named code changes.

---

## 0. What is solid — verified, not assumed

These are the load-bearing claims, and they hold. Do not re-derive them.

| Claim | Verified at |
|---|---|
| **§1b(a) There is no post-gate transform chain.** `TextPipeline.Clean` → `_store.Add` → `PasteAtCursor` → `TranscriptReady` with nothing mutating text between. Inserting the gate after the clean gives offsets valid against the exact string saved *and* pasted. | `Recording/RecorderController.cs:226` (clean), `:235` (`_store.Add`), `:244` (paste), `:253` (`TranscriptReady`) |
| **§1b(b) There is one channel.** `TranscriptReady?.Invoke(text)` has exactly one call site. Widening it is a two-file change. | `RecorderController.cs:253` → `Services/PillController.cs:178` |
| **The rewrite-inherits-a-stale-chip trap is real.** `_rewrite.Succeeded` is routed into the same handler and never passes the gate. | `PillController.cs:69` |
| **§3.3 The `ExpandPanel`-only trap is real.** The panel opens only when `TranscriptText.Text` is non-empty, so nothing the user *must* see can live there alone. | `Controls/PillWindow.xaml.cs:293` (`ApplyExpansion`), `:309` (`ToggleExpand`); audit line 42 |
| **§5.5 `RecordingItem.Id` is a stable, persisted `Guid`** — a safe provenance key, and keeping provenance out of the library schema is correct. | `Models/RecordingItem.cs:18`; DTO round-trip `Services/JsonRecordingStore.cs:125,131` |
| **§2.4 / §11 "is our common-word list the SSA-name-stripped build?" — YES. Risk already retired.** 24,059 entries; `jamie`, `sarah`, `lisa` absent; `mark`, `rose`, `grace`, `april`, `may`, `will` present (the dual-meaning allowlist). | `Vocabulary/Resources/common-words.txt` |
| **§1c control inventory is accurate.** `SettingRow`, `Tag="section"` headers, `ui:CardExpander`, `ui:InfoBar`, the RecentsPage empty-state recipe, the chip `Border` recipe, the read-only borderless transcript `TextBox`, `INavigator.Parameter`, `TourCatalog.All` — all exist exactly where cited. | `Controls/SettingRow.xaml.cs:17-23`, `Views/SettingsPage.xaml:27,57,164`, `Views/RecentsPage.xaml:185-194`, `Views/RecordingDetailPage.xaml:151-160,210-214`, `Services/Navigation/Navigator.cs:11-31`, `Controls/TourCatalog.cs:114` |
| **§1c "there is no info-popover control" — TRUE.** Repo-wide: zero `Popup`/`Flyout`/`ToolTipService`/`InfoBadge`, one `ui:HyperlinkButton`. DECIDE-1 ("don't build one here") is correct. | `Views/SettingsPage.xaml:87` is the only hit |
| **§2.2 The PromptsPage shape is a real, reusable idiom.** Search box + `ListView` + per-row `ui:Button` via `RelativeSource AncestorType=ListView` + a dual-purpose `ui:CardExpander` driven by `IsEditing`/`AddButtonText`. | `Views/PromptsPage.xaml:21-23,25,91-98,106-123`; `ViewModels/PromptsViewModel.cs:20-31,71-109` |
| **§7.1 The tour is genuinely a one-entry change.** `TourCatalog.All` + `MarkShown` → `JotSettings.ShownTours`, no new bool, no migration. | `Controls/TourCatalog.cs:114,124`; `Services/Abstractions/ISettingsStore.cs:95` |
| **Central page scrolling means `VocabularyPage` needs no scroll plumbing.** `FindScrollViewer` deliberately skips a search box's content host so paging keys drive the list. | `Controls/PageScrolling.cs:62-71` |

The doc's §1 thesis — that the Mac's anti-annoyance signals are inert on this engine path — is also
confirmed in-repo by a test that exists and passes: `DetectionPath_ConfidenceSignalsAreInert`
(`tests/Jot.Tests/Vocabulary/VocabGoldenFixtureTests.cs:321`).

---

## BLOCKERS

### B1 — The ask card's trigger can never fire on the case the design draws

**Severity: BLOCKER.** §4.2's mockup, its "Stop asking" control, its `suppressedBlocks` writes, and §5.4's
assertion that common-word originals "propose and ask forever" all describe a **blocked** (`KEPT`)
common-word near-miss: `lista → Lisa`, "did you mean Lisa?".

`AskPolicy.Select` as ported will never select that record:

```csharp
// src/Jot/Vocabulary/AskPolicy.cs:93-99
bool WorthAsking(CorrectionRecord r)
{
    if (keyboardSuppressed.Contains(PairKey(r))) return false;
    if (Granted(r)) return false;
    if (r.Shape == "merge" && r.Outcome == "kept") return MergeTeachEligible(r);
    return r.Outcome == "applied" || Prior(r) > 0;
}
```

A blocked common-word proposal has `Outcome == "kept"`, `Shape == null` on the detection path (§1), and
`Prior == 0` on first encounter. It falls through every branch to `false`. And §3.5 explicitly routes
blocks *off* the pill "because they belong on the review surface" — so in v1.3 a block has **no ask
surface at all**, which is precisely the outcome §0a decision 2 was meant to avoid.

Meanwhile the case that *does* select — `Outcome == "applied"`, i.e. every single-word APPLY (§1 line
107) — is nowhere in the §4.2 spec or the §8 copy deck.

**Fix (pick one, in writing, before M3):**
- **(a) Redraw §4.2 around an applied correction.** The card becomes "Jot wrote *Ramanathan* — did you
  mean that?" with chips `[ramanathan] [Ramanathan]`. Then delete "Stop asking", `suppressedBlocks`, and
  the block-flavoured copy at doc lines 989-992 from v1.3 — they have no reachable code path.
- **(b)** If the owner wants the block-ask, that *is* a Windows-specific change to `AskPolicy`
  (`WorthAsking` would need `|| (r.Outcome == "kept" && isCommonWordOriginal(r))`), which §0b-RESOLVED
  forbids. Escalate rather than quietly diverge.

Either way, `AskPolicy.cs:93-99` must be quoted in the implementation ticket so nobody builds the
mockup and then discovers the deck is always empty.

---

### B2 — The paste target is not the origin window by default; an activatable ask card pastes into the wrong place

**Severity: BLOCKER.**

```csharp
// src/Jot/Recording/RecorderController.cs:243
IntPtr target = s.ReturnToOrigin ? _originWindow : IntPtr.Zero;
```

`ReturnToOrigin` defaults to **false** (`Services/Abstractions/ISettingsStore.cs:44` — no initializer).
So in the default configuration `PasteAtCursor` receives `IntPtr.Zero` and resolves the target as
`GetForegroundWindow()` *at paste time* (`Delivery/TextInjector.cs:107`). That is safe today **only
because the pill is `WS_EX_NOACTIVATE`** — the comment at `RecorderController.cs:242` says so
explicitly ("the pill never steals focus").

The instant any activatable ask card exists, the foreground window at paste time is Jot's own. The
`IsOwnWindow` guard at `TextInjector.cs:81` protects only the `restoreTo` argument — it does **not**
cover the `IntPtr.Zero` path. Result: the transcript is pasted into the ask card, or nowhere.

The doc's entire §4.1(c) risk framing misses this. It worries about `WS_EX_NOACTIVATE` on the pill; the
actual failure is one line in the recorder.

**Fix:** when a deck ran, force `target = _originWindow` regardless of the `ReturnToOrigin` setting.
`PasteAtCursor` then calls `ForceForeground` + a 40 ms settle (`TextInjector.cs:83-86`, `:433-456`) —
the same restore the shipping rewrite path depends on (`Rewrite/RewriteController.cs:129`). Add a test
asserting the paste target when a deck is present.

---

### B3 — Vocabulary data is invisible to Erase **and** to the data-folder move

**Severity: BLOCKER.** This codebase's documented bug class.

```csharp
// src/Jot/Services/JotDataPurge.cs:26-27
private static readonly string[] DataSubdirs = ["models", "recordings", "logs"];
private static readonly string[] DataFiles = ["library.json", "aikey.dat", "stats.json"];

// src/Jot/Services/DataFolderMigrator.cs:33
private static readonly string[] MigratedItems = ["models", "recordings", "library.json"];
```

DECIDE-7 correctly says vocabulary must go into `JotDataPurge`'s single-source-of-truth list. **The doc
never mentions `DataFolderMigrator` at all.** Consequence: a user who moves their data folder to
another drive (a shipped, live-verified feature) silently loses their entire term list and correction
history — `Finish()` (`:186-199`) deletes only `MigratedItems` from the source, so vocabulary is
stranded in the old folder while the app reads the new one and shows an empty list.

There is also a doc-internal path contradiction: §2.5 (line 356) says `<DataDir>\vocabulary.json`;
§5.5 (line 730) and DECIDE-7 (line 1097) say `<DataDir>\Vocabulary\`.

**Fix:**
1. Settle on `<DataDir>\Vocabulary\` (folder), holding `vocabulary.json`, `corrections.json`,
   `provenance.json`. Fix §2.5.
2. Add `"Vocabulary"` to **`JotDataPurge.DataSubdirs`** *and* **`DataFolderMigrator.MigratedItems`**.
3. Extend `tests/Jot.Tests/JotDataPurgeTests.cs:39-69` (`SeedEverything` +
   `ArtifactPaths_CoverEveryKnownArtifact`) and `DataFolderMigratorTests.cs`. Those tests exist
   precisely so a new artifact can't be forgotten — use them.
4. Note the divergence from the closest precedent: `prompts.json`, the app's other user-authored list,
   lives in **`ConfigDir`** (`Services/PromptCatalog.cs:16,43`; `JotDataPurge.ConfigFiles`), not
   `DataDir`. Putting vocabulary under `DataDir` is defensible (it's user data, it should follow a
   move) but the doc should say so rather than leave it looking like an oversight.
5. `VocabularyStore` must resolve its directory **once at construction** from `JotPaths.DataDir(...)`,
   exactly like `JsonRecordingStore.cs:30`, or it will disagree with the library store after a move.

---

### B4 — `VocabularyPage` has no way back

**Severity: BLOCKER (dead-end UI).**

`MainWindow.xaml:23` sets `IsBackButtonVisible="Collapsed"`, and §9 correctly says Vocabulary gets no
sidebar entry. `RecordingDetailPage` — the precedent the doc cites — solves this with its own explicit
Back button:

```xml
<!-- src/Jot/Views/RecordingDetailPage.xaml:19-21 -->
<ui:Button Grid.Column="0" Appearance="Secondary" Command="{Binding BackCommand}"
           Icon="{ui:SymbolIcon ArrowLeft24}" ToolTip="Back" AutomationProperties.Name="Back" />
```
backed by `[RelayCommand] private void Back() => _navigator.GoBack();`
(`ViewModels/RecordingDetailViewModel.cs:94-95`).

§2.2's 4-row grid (title / search / list / add form) has no back affordance. A user who clicks
"Manage…" lands on a page with no sidebar entry and no exit.

**Fix:** Row 0 becomes the `RecordingDetailPage` header shape — Back button + title + subtitle in a
3-column grid — and `VocabularyViewModel` gets a `BackCommand`.

---

## MAJORS

### M1 — The "the pill can't take focus" framing is wrong, and it hides the easy answer

**Severity: MAJOR (misdirects the highest-risk workstream).**

§0b line 74-76 and §4.1(c) present two options: remove `WS_EX_NOACTIVATE` from the pill, or install a
global low-level keyboard hook that swallows Enter/Esc from the focused app. Both are bad, and neither
is necessary.

Verified: `PillWindow.OnSourceInitialized` does set `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`
(`Controls/PillWindow.xaml.cs:356-361`), and the doc quotes the comment correctly. But the app
**already ships an activatable, keyboard-driven overlay on the paste path**:

```csharp
// src/Jot/Controls/PromptPickerWindow.xaml.cs:12-17 (class comment)
/// Unlike the status pill it is *activatable* — it takes keyboard focus so the
/// user can type to filter. ... Enter commits, Esc / click-away cancels.

// :188-194 — Tool window so the palette stays out of Alt+Tab; still activatable (no NOACTIVATE).
SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
```

and the surrounding flow is exactly the ask's flow: capture the target HWND *before*
(`RewriteController.cs:77` `_origin = TextInjector.CaptureForegroundWindow()`), let a focus-stealing
window run a keyboard interaction, then restore and paste
(`RewriteController.cs:129` `PasteAtCursor(result, _origin, …)` → `ForceForeground`,
`TextInjector.cs:433-456`).

**See §"The ask card — concrete recommendation" below for the implementable design.** Rewrite §0b's
second prerequisite and §4.1(c) to point at `PromptPickerWindow` instead of at a keyboard hook.

---

### M2 — §0b-RESOLVED overstates what `AskPolicy` actually gives you on this path

**Severity: MAJOR (the anti-nag guarantee sold to the owner is half what ships).**

§0b-RESOLVED lists four guarantees. Two are live on the Windows detection path; two are dead code:

| Guarantee | Status | Evidence |
|---|---|---|
| answered pair → `keyboardSuppressed`, never asked again | **live** | `AskPolicy.cs:95` |
| whole payload capped at `MaxAsks = 3` | **live** | `AskPolicy.cs:23,106` |
| "always replace" grant stops consuming ask budget | **dead** | `Granted()` reads `OverrideEntry.AlwaysReplace` (`AskPolicy.cs:77-86`); DECIDE-9 ships no always-replace in v1.3 → always `false` |
| merge-teach phrases get exactly one card ever | **dead** | `MergeTeachEligible` requires `r.Shape == "merge"` (`AskPolicy.cs:70`); §1 establishes `shape` is always `nil` on `ApplyFromDetections` |

Also dead as a consequence: `Selection.AltTerm`/`AltFind` (`AskPolicy.cs:123-127`) read
`r.Alternates`, which is always `[]` on this path (`CorrectionRecord.cs:29`) — so the card can never
render an alternate suggestion.

The honest worst case is therefore: **up to 3 asks per dictation, per unanswered pair, on every
dictation containing a newly-applied term, until the user answers each one.** Not "3 on the first
dictation, then quiet" — a second dictation with the same unanswered pair asks again, because nothing
was written to `keyboardSuppressed` unless the user answered.

Separately, §4.2 proposes "two Windows-only additions" (never ask twice for a pair until answered;
auto-suppress at `blockedKeeps >= 2`). Neither is in the ported `AskPolicy`, and building them is
exactly the Windows-specific gate §0b-RESOLVED says not to build. **Resolve this contradiction in the
doc** — either they're in scope (and §0b-RESOLVED's "unchanged" is wrong) or they're out (and §4.2's
trigger section should be deleted, not left as a spec).

---

### M3 — Three existing writers invalidate correction anchors, and one of them is this feature's own

**Severity: MAJOR (silent data loss of the review surface).**

`RecordingDetailPage` already mutates `Item.Transcript` from three places:

```csharp
// ViewModels/RecordingDetailViewModel.cs:105-114  SaveEdit — replaces the WHOLE string
// :127-138  ReplaceNext — splices at Item.Transcript.IndexOf(FindText), sets IsEdited, persists
// :140-151  ReplaceAll  — string.Replace over the whole transcript
```

and §2.7's own "Add to Vocabulary…" adds a fourth: it replaces the selected span with the canonical
term *in this transcript*.

Every one of these shifts `CorrectionRecord.PublishedStart` for all anchors after the edit point.
§5.5's rule ("strict-only, no nearest-match fallback, hide the row if it doesn't resolve") then
silently deletes most of the user's review list the first time they fix a typo — including immediately
after they use this feature's own right-click entry point. The doc never names this interaction.

Persistence detail that makes it worse: `JsonRecordingStore` saves on *every* `Transcript` change
(`:90-106`), so the corrupted state is committed instantly with no undo.

**Fix (concrete, cheapest first):**
- **(a) Simplest, recommended for v1.3:** hide the whole review block when `Item.IsEdited` is true, and
  say so in copy. One binding, zero anchor math, no wrong-word edits possible. State it in §5.5 rather
  than leaving "hide the row" to do work it can't do.
- **(b) If you want review to survive edits:** for the two edits whose geometry the app knows
  (`ReplaceNext`/`ReplaceAll` — it has `i`, `FindText.Length`, `ReplaceText.Length`; and the §2.7
  splice), shift every stored `PublishedStart >= i` by `(replaceLen - findLen)` inside
  `CorrectionProvenance`. For free-form `SaveEdit`, set an `anchorsStale` flag and collapse the block
  to one tertiary line.
- Either way, add the guard test §1b asks for: *fail the build if any text mutation is inserted between
  the gate and `_store.Add`* — and a second one pinning that `ReplaceNext` keeps anchors consistent.

---

### M4 — §5.4's "flash the changed span" is not buildable on this control, and the doc says so elsewhere

**Severity: MAJOR (spec contradicts itself).**

The transcript is a read-only borderless `TextBox Style="{x:Null}"`
(`Views/RecordingDetailPage.xaml:210-214`). §9 already states: *"WPF's read-only `TextBox` cannot do
styled runs at all — this would mean a `RichTextBox` or `FlowDocument` rewrite."* §5.4 then asks for a
"brief accent wash" over the changed span.

**Fix:** implement the flash as `TranscriptBox.Select(start, length)` + `Focus()` — the WPF selection
highlight *is* the accent wash, it scrolls the span into view for free, and it costs one code-behind
method. Note it needs code-behind (the VM has no handle on the control); `RecordingDetailPage.xaml.cs:29-53`
is the in-repo precedent for VM-adjacent code-behind on this page.

---

### M5 — "Ships hidden" and "bound to `AdvancedFeatures`" are different things — and Settings search leaks the second one

**Severity: MAJOR.** §7.1 asserts both.

The house rule in this repo is a **literal** `Visibility="Collapsed"` with no binding. That is not
cosmetic — the Settings search indexer *depends* on it:

```csharp
// src/Jot/Views/SettingsPage.xaml.cs:196-198
if (fe.Visibility == Visibility.Collapsed
    && fe.GetBindingExpression(VisibilityProperty) is null)
    continue; // statically hidden by design — don't descend
```

and search force-reveals the entire advanced pane while typing:

```csharp
// :135
if (searching) ForceVisible(AdvancedPanel); else RestoreVisibility(AdvancedPanel);
```

So if v1.3 binds the Vocabulary section to `AdvancedFeatures`: (a) every Advanced user sees it
immediately, and (b) **any** user who types "vocab" in Settings search surfaces the whole section even
with Advanced off. That is not "hidden".

Context: `AdvancedFeatures` has no default initializer (`ISettingsStore.cs:18` → `false`), and the
wizard deliberately does not flip it on (`ViewModels/WizardViewModel.cs:188`) — so the Advanced pane is
genuinely off for most users. But the search path defeats it.

**Fix:** state the intent explicitly and stage it:
- **v1.3:** keep the literal `Visibility="Collapsed"` on the Vocabulary `StackPanel` (it is already
  literal at `Views/SettingsPage.xaml:300`, and its own comment says *"Rebind Visibility to
  AdvancedFeatures once implemented"*). This preserves the search-leak immunity and matches
  `docs/ux-audit-2026-07-20.md:93` and `docs/features.md:376-379`.
- **At unhide:** flip to `Visibility="{Binding AdvancedFeatures, Converter={StaticResource BoolToVis}}"`
  in the same commit as the calibration pass, the `features.md §9.5` rewrite, and the tour entry.
- Keep the section inside `AdvancedPanel` (`SettingsPage.xaml:164`) either way, as §2.1 says.

---

### M6 — "Fail closed, loudly" targets a trap this repo does not have, and contradicts the ported code

**Severity: MAJOR (would build a wrong warning for a state that can't occur).**

§6.4 names *"the `Content` / `CopyToOutputDirectory` csproj trap"* as our analogue of the Mac's
missing-bundle bug. That trap does not exist here — the lists are embedded in the assembly:

```xml
<!-- src/Jot/Jot.csproj:39 -->
<EmbeddedResource Include="Vocabulary\Resources\common-words*.txt" />
```

and the port loads them via `GetManifestResourceStream` (`Vocabulary/CommonWords.cs:61-67`). There is
already a test pinning every one of them: `CommonWordLists_AreEmbeddedAndPopulated`
(`VocabGoldenFixtureTests.cs:292-301`).

Worse, the ported behaviour contradicts §6.4's contract. On a missing *named* resource the provider
reports once to `IDiagnosticsSink` and returns an empty set (`CommonWords.cs:53-93`) — it does **not**
disable the gate. And `Words(null)` returns empty *silently and intentionally* for a language with no
list (`:49`, `ResourceFor` `:39-45` — only 21 of the app's 40 locales ship one). §6.4's "assert at
startup, disable vocabulary, force the toggle off and disabled" is new behaviour layered on top of a
provider designed to degrade, not to fail closed.

**Fix:** rewrite §6.4 to say the packaging risk is retired (embedded resource + green test), delete
the *"Vocabulary is turned off — a required file is missing. Reinstall Jot to fix this."* copy, and
keep the fail-closed InfoBar **only** for the spotter-model states
(`notDownloaded / downloading / ready / failed(reason)`), which is a genuine runtime condition with a
real user action (`[Download]` / `[Retry]`). If you want a last-resort assert, hang it off the existing
`IDiagnosticsSink` report rather than inventing a second detection path.

---

### M7 — §1c's "already-ported core" is stale in both directions

**Severity: MAJOR (mis-scopes the build and sends the implementer to files they don't have).**

§1c line 172 lists the ported core as *"`Seams.cs`, `CorrectionKey.cs`"*. Actually present in
`src/Jot/Vocabulary/`:

- `VocabularyGate.cs` — **44 KB, fully ported**, including `ApplyFromDetections` (`:363`),
  `LowConfidence = 0.85f` (`:40`), the `Detection` record (`:83`), the `Result`/`Proposal` shapes with
  `PublishedStart` (`:71`).
- `AskPolicy.cs`, `CorrectionRecord.cs`, `CommonWords.cs`, `Seams.cs`, `CorrectionKey.cs`
- 22 embedded common-word lists
- 10 golden-fixture tests (`VocabGoldenFixtureTests.cs`), including the two the doc cites.

**Consequence:** the doc makes ~15 citations of the form `VocabularyGate.swift:469`, `:711`, `:727-741`,
`:694`. Those point at a Swift file the Windows implementer does not have checked out. Every one should
be re-pointed at `src/Jot/Vocabulary/VocabularyGate.cs` (or carry both).

Conversely, the doc treats as existing several things that are **not ported** (zero hits in `src/`):
`VocabularyStore` (§2.7 `AddMapping`, `SanitizeTerm`, `maxTermWords = 4`), `CorrectionStore` (§5.4
`Adjust`, `blockedKeeps`), `CorrectionProvenance` (§5.4 `SetVerdict`), and the CTC spotter itself
(no `Spotter` symbol anywhere). §13's M3a/M3c ticket lists should name these as *new files*, not ports
in place.

---

## MINORS

**m1 — §3.3's `FitTail` claim is wrong for the state the chip lives in.** `FitTail` runs only from
`SetLiveText` (`PillWindow.xaml.cs:66`), i.e. during Recording. `PillState.Success` uses `OneLine(text)` —
a flat 60-char truncation (`:195`, `:347-352`) — and `Capsule.Width` is reset to `double.NaN` (auto) for
every non-Recording state (`:156`). There is no fixed slot to measure and no `FitTail` to run before; the
chip just widens the auto-sized capsule (`SizeToContent="WidthAndHeight"`, `PillWindow.xaml:11`).
*Fix:* correct the sentence, and shorten `OneLine`'s budget (60 → ~46) when a chip is present so the pill
doesn't outgrow `LineText`'s `MaxWidth="320"` (`PillWindow.xaml:35`).

**m2 — The chip means editing `LineRow`'s column grid.** `LineRow` is a fixed 4-column grid — dot /
waveform / caption (`*`) / elapsed (`PillWindow.xaml:22-40`). `Elapsed` is column 3 and is collapsed
outside Recording. Either add a 5th column or reuse column 3 (cleaner, no layout change, but couples the
chip to the timer's slot). Name the choice in the doc.

**m3 — Replacing the transcript context menu costs Copy / Select All.** §2.7 replaces the default WPF menu.
`Copy` and `Select All` must be re-wired as `ApplicationCommands.Copy` / `SelectAll` with `CommandTarget`
bound to the TextBox. The menu belongs on the read-only box only (`RecordingDetailPage.xaml:210`), **not**
the edit-mode `ui:TextBox` (`:217`). Selection offsets live on the control, so `Add to Vocabulary…` needs
code-behind — precedent at `RecordingDetailPage.xaml.cs:29-53`.

**m4 — Path inconsistency inside the doc.** §2.5 line 356 `<DataDir>\vocabulary.json` vs §5.5 line 730 /
DECIDE-7 line 1097 `<DataDir>\Vocabulary\`. See B3.

**m5 — Copy problems.**
- `"One-time 130 MB download."` — the figure is asserted nowhere in code. Bind it to the asset manifest
  (`Services/Download/AssetManifest.cs`) or soften to "about 130 MB". A hard number that ships wrong is an
  overpromise on a metered connection.
- `"Common word — Jot will ask before using this, and won't swap it on its own."` — **factually wrong as
  shipped** (B1: `AskPolicy` will not ask about a block). Truthful version:
  `Common word — Jot won't swap this on its own. You can confirm it on the recording.`
- `"Shows a small mark on the finished-dictation popup."` — "popup" isn't the app's word. The tour calls it
  *"a small floating pill"* (`Controls/TourCatalog.cs:41`). Use "…on the pill that appears when a dictation
  finishes."
- `"Vocabulary is turned off — a required file is missing. Reinstall Jot to fix this."` — see M6; also
  "Reinstall Jot" is not actionable for a Store user. If kept, point at Help → Send feedback
  (`Views/HelpPage.xaml:12`), the app's existing recourse.
- DECIDE-8 promises "advise 100 in the page subtitle", but §2.2's subtitle (*"Terms Jot should prefer when
  it transcribes. Kept on this PC."*) doesn't carry it. Add it or drop the decision.
- `"Runs entirely on this PC."` — accurate, on-voice, keep. Sentence case / no exclamations is respected
  throughout. `Add term` / `Save changes` / `Cancel` match `PromptsViewModel.AddButtonText`
  (`PromptsViewModel.cs:26`) — good consistency.

**m6 — States not designed.**
- **Duplicate term on single add/edit.** §2.5 covers the bulk case ("Skipped 3 — already in your list") but
  the add form has no duplicate rule or copy. Merge aliases into the existing term? Reject? Scroll to it?
- **Term cap (200) reached.** No copy, no disabled state, and bulk import can cross it silently.
- **Same alias claimed by two terms.** Aliases are the gate's main plausibility lever (§2.3) — a collision
  is a real conflict with no stated rule.
- **Editing a term that has correction history.** Renaming `Nemotron` → `NeMotron` orphans every
  `CorrectionStore` net keyed on the old pair (`CorrectionRecord.MappingKey`, `CorrectionRecord.cs:40`).
  Undefined.
- **Model downloading / failed, seen from the page and the dialog.** §2.1 covers `[Download]`/`[Retry]` in
  Settings only. `VocabularyPage` and the §2.7 dialog have no downloading or failed state. The app already
  has the pattern to copy: `ModelDownload` bound to a Settings row, and
  `DataFolderMigrator.IsMigrating/Progress/StatusText` (`DataFolderMigrator.cs:45-51`).
- **Recording deleted while its review data exists.** `Delete()` does `_store.Delete(Item)` then `GoBack()`
  (`RecordingDetailViewModel.cs:193-198`), and Recents can delete too. §5.5's "discard on delete" must hook
  `IRecordingStore.Items.CollectionChanged` (the same seam `JsonRecordingStore.cs:69-76` uses), not the VM
  command, or provenance leaks and grows unbounded.
- **Sample data.** `JotSettings.ShowSampleData` seeds demo rows; the review block must not render on them.

**m7 — Accessibility / keyboard nav.**
- Pill chip: `AutomationProperties.Name` is set on the **Window** (`PillWindow.xaml.cs:198`), so the chip
  must be folded into that one string — not added as a separate automation peer, or Narrator announces it
  twice. §3.3's copy is right; say where it goes.
- `VocabularyPage`: no tab order, no `Delete`-key affordance on a selected row, and icon-only Edit/Delete
  buttons carry only `ToolTip` — which WPF does **not** expose as an automation name (PromptsPage has the
  same latent gap at `PromptsPage.xaml:94,98`). Specify `AutomationProperties.Name="Edit {term}"` /
  `"Delete {term}"`.
- §2.7 modal: no stated initial focus, no Esc-to-cancel, no default button. `PromptPickerWindow` is the
  precedent for all three (`:61-67` initial focus, `:135-138` Esc).
- §5.3 review rows: chips are the interactive elements, with no keyboard story at all (arrow between rows?
  Enter to pick? where does focus land after Undo?).
- Reduced motion is handled for the v1.4 countdown ring but not for the §5.4 flash.

**m8 — §5.5's "re-commit on the ReTranscribe path" is bigger than it reads.**
`RecordingDetailViewModel.ReTranscribe` (`:215-237`) calls `_transcriber.TranscribeAsync` directly and
never runs `TextPipeline.Clean` — it already diverges from the dictation path. Routing the gate through it
means adding both the pipeline *and* the gate there. Scoping ReTranscribe out is defensible (it only runs
for `IsPending` rows, `:218`) — but say which, in writing. Same for `Import/MediaImporter`.

**m9 — §13 M3a's "testable with no model and no microphone" is very slightly optimistic.** The fail-closed
assert and the model-status rows are not. Move the model-status row to M3b.

---

## The ask card — concrete recommendation

**Do not touch `PillWindow`.** Build `Jot.Controls.AskCardWindow` as a sibling of `PromptPickerWindow`,
copying its window recipe verbatim:

- `WindowStyle=None`, `AllowsTransparency`, `Topmost`, `ShowInTaskbar=False`, same dark capsule look.
- `OnSourceInitialized` sets **`WS_EX_TOOLWINDOW` only** — *no* `WS_EX_NOACTIVATE`
  (`PromptPickerWindow.xaml.cs:189-194`). That is the entire trick: the pill keeps its NOACTIVATE
  forever, and a *different* window takes focus. No low-level hook, no change to the paste path's
  focus model, no risk to the 2026-07-23 fix.
- `OnLoaded` → focus the first chip (`PromptPickerWindow.xaml.cs:61-67`).
- `OnPreviewKeyDown` → ←/→ between chips, Enter commits, Esc = keep-original-and-advance
  (`PromptPickerWindow.xaml.cs:99-149`).
- Position: reuse the **pill's** bottom-center-on-anchor-monitor math (`PillWindow.xaml.cs:238-257`),
  not the picker's centered math, so the card appears where the user is already looking.

**Sequencing in `StopAndDeliverAsync`** (`RecorderController.cs:228-254`) — keep the existing shape and
insert the ask *between* the gate and `_store.Add`, so §1b's "what is saved is what is pasted"
simplification survives:

1. gate the cleaned text → `(text, records)`;
2. `AskPolicy.Select(...)` → **if empty, nothing changes at all** (this is what keeps zero-ask
   dictations byte-identical to today, and it will be the overwhelming majority);
3. if non-empty: `await AskCardWindow.RunAsync(deck)` on the dispatcher. It owns its own resolution and
   **always** completes — timeout, Esc, click-away, and a new recording starting all resolve to
   keep-original;
4. splice answers into `text`;
5. `_store.Add(BuildRecording(result, text))`;
6. paste.

**Three requirements the design currently misses:**

1. **Force the paste target** (B2). When a deck ran, pass `_originWindow` unconditionally at
   `RecorderController.cs:243`, ignoring `ReturnToOrigin`. `PasteAtCursor` then does
   `ForceForeground(_originWindow)` + a 40 ms settle (`TextInjector.cs:83-86`) — the identical restore
   the shipping rewrite path relies on.
2. **`Deactivated` must resolve, not close.** `PromptPickerWindow` does
   `Deactivated += (_,_) => { if (CloseOnDeactivate) Close(); }` (`:40`). For the ask, deactivation means
   "the user clicked back into their document" — that must resolve-to-default **and still deliver**. This
   is exactly the Mac M4/M5 blocker §4.2 already names. Model it as a single
   `TaskCompletionSource<Answers>` that every exit path completes, with the paste in a `finally`.
3. **Guard against a second deck.** `Toggle` / `PressToStart` are global and reachable while the card is
   up (`RecorderController.cs:61,128`). `Start()` must force-resolve any live deck first. Note
   `DisarmStopHotkey` has already run by this point (`:190`), so Esc is free for the card to consume.

**Cost:** one new window (~250 lines, structurally a copy of `PromptPickerWindow`), one
`TaskCompletionSource` handshake, one changed line at `:243`. It is *not* "the highest-risk thing we
could put on the hot path" — the risk is confined to step 3/6 ordering and the always-delivers
invariant, both fully testable with no microphone (feed a synthetic deck; assert `_store.Add` and
`PasteAtCursor` are each called exactly once on every exit path: answer / Esc / timeout / deactivate /
new-recording-interrupt).

**But B1 still gates this.** With `AskPolicy` shipped unchanged, the deck can only ever contain
*applied* corrections. §4.2's mockup, its "Stop asking" control, and its block-flavoured copy describe
a record the policy will never select. Redraw §4.2 before writing the window.

---

## Concrete file list

### Add
- `src/Jot/Views/VocabularyPage.xaml` + `.xaml.cs` — parameterless ctor resolving
  `App.Services.GetRequiredService<VocabularyViewModel>()`, mirroring `Views/PromptsPage.xaml.cs:7-11`.
  **Row 0 must carry a Back button** (B4).
- `src/Jot/ViewModels/VocabularyViewModel.cs` — mirror `PromptsViewModel`: `ICollectionView Terms`,
  `SearchText`, `NewTerm`, alias chip collection, `EditingTerm`/`IsEditing`/`AddButtonText`,
  `AddTermCommand` / `EditTermCommand` / `DeleteTermCommand` / `CancelEditCommand`, **plus `BackCommand`**.
- `src/Jot/Models/VocabularyTerm.cs` — mirror `Models/PromptItem.cs`: `Guid Id`, `Term`,
  `ObservableCollection<string> Aliases`, computed `AliasSummary` and `Warning`.
- `src/Jot/Services/VocabularyStore.cs` — mirror `Services/PromptCatalog.cs` (optional `storageDir` ctor
  param for tests, `Load`/`Save`, `AddMapping`, one `SanitizeTerm` choke point, `MaxTermWords = 4`).
  Resolve the dir **once at construction** like `JsonRecordingStore.cs:30`.
- `src/Jot/Vocabulary/CorrectionStore.cs`, `src/Jot/Vocabulary/CorrectionProvenance.cs` — **new ports,
  not "already there"** (M7).
- `src/Jot/Controls/AddToVocabularyWindow.xaml` + `.xaml.cs` — §2.7 modal.
- *(v1.4, per B1's resolution)* `src/Jot/Controls/AskCardWindow.xaml` + `.xaml.cs`.

### Modify
- `src/Jot/App.xaml.cs` **~:2381-2389** — register `VocabularyStore`, `CorrectionStore`,
  `VocabularyViewModel` as singletons alongside `PromptCatalog`/`PromptsViewModel`.
- `src/Jot/Views/SettingsPage.xaml` **:298-349** — replace the chip block with the InfoBar + four
  `ctl:SettingRow`s; keep the header's `Tag="section"`; "Manage…" → `INavigator.Navigate(typeof(VocabularyPage))`.
  Keep `Visibility="Collapsed"` (literal) for v1.3 — see M5.
- `src/Jot/ViewModels/SettingsViewModel.cs` **:554-571** — delete the in-memory stubs
  (`VocabularyTerms`, `NewVocabTerm`, `AddVocabTermCommand`, `RemoveVocabTermCommand`); add
  `VocabularyEnabled`, `VocabularyChipEnabled`, `TermCountText`, `ManageVocabularyCommand`, model-status.
- `src/Jot/Services/Abstractions/ISettingsStore.cs` — add `VocabularyEnabled` (default `false`) and
  `VocabularyChip` (default `true`). No migration needed (missing JSON keys fall back to defaults,
  `JsonSettingsStore.cs:29-45`).
- `src/Jot/Services/JotDataPurge.cs` **:26** and `src/Jot/Services/DataFolderMigrator.cs` **:33** — add
  `"Vocabulary"` to both lists (**B3**).
- `src/Jot/Views/RecordingDetailPage.xaml` **:210** (context menu) + a review `ui:CardExpander` under the
  transcript; `.xaml.cs` for selection capture and the §5.4 flash-via-`Select`.
- `src/Jot/ViewModels/RecordingDetailViewModel.cs` — review rows, pick/undo; **anchor handling in
  `SaveEdit` :105-114, `ReplaceNext` :127-138, `ReplaceAll` :140-151** (**M3**).
- `src/Jot/Recording/RecorderController.cs` **:226-253** — gate insertion, `TranscriptReady` widening,
  **forced paste target at :243** (**B2**).
- `src/Jot/Services/PillController.cs` **:69** (empty corrections for rewrite) and **:178** (chip payload).
- `src/Jot/Controls/PillWindow.xaml` **:22-40** (chip column) + `.xaml.cs` **:193-199** (Success line
  budget + automation name), `:48` `ExpandPanel` correction list.
- `tests/Jot.Tests/JotDataPurgeTests.cs` **:39-69** and `tests/Jot.Tests/DataFolderMigratorTests.cs` —
  extend for `Vocabulary\`.
- **At unhide only:** `docs/features.md` §9.5 (**:376-379**), `src/Jot/Controls/TourCatalog.cs` **:114**,
  `src/Jot/Views/HelpPage.xaml` (Basics card + Tours button), and flip `SettingsPage.xaml:300` to the
  `AdvancedFeatures` binding.

### Deliberately NOT modified
- `src/Jot/Shell/MainWindow.xaml` — no sidebar entry, per §9. Correct; `RecordingDetailPage` sets the
  precedent for a programmatically-reached page.

---

## Verdict

**NEEDS REVISION.**

The doc is unusually well-grounded — its central architectural bets (gate between clean and save; one
`TranscriptReady` channel; PromptsPage-shaped page; provenance keyed off `RecordingItem.Id`; no anchor
reconciliation) all check out against real code, and the one risk I expected to find live (the
name-bearing common-word list) is already retired. Revise rather than rethink.

Blocking before implementation starts:
1. **B1** — redraw §4.2 around an *applied* correction, or escalate the `AskPolicy` change. As written
   the ask deck will always be empty for the case the mockup shows.
2. **B2** — force the paste target to `_originWindow` when a deck ran.
3. **B3** — `Vocabulary\` into `JotDataPurge` *and* `DataFolderMigrator`, plus both test suites; settle
   the `<DataDir>\Vocabulary\` path.
4. **B4** — give `VocabularyPage` a Back button.

Then fix M1 (point the ask at `PromptPickerWindow`, delete the keyboard-hook framing), M2 (state the
real anti-nag guarantee), M3 (anchors vs. the three existing transcript writers), M5 (pick hidden vs.
Advanced-gated), M6 (§6.4 targets a trap that doesn't exist), and M7 (re-point the ~15 Swift citations
at `Vocabulary/VocabularyGate.cs`; mark `VocabularyStore`/`CorrectionStore`/`CorrectionProvenance` as
new files).
