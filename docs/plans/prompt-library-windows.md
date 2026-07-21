# Prompt Library — Windows master design

Ship the **33 curated built-in rewrite prompts** (the Mac library, verbatim) into Jot for Windows, and make
the rewrite flow **seamless: press the rewrite hotkey → the picker opens with the default prompt already on
top and selected → press Enter (or click) to run it.** No hunting, no configuration required.

This doc is airtight by design: every field, seed, sort key, integration edit, and migration rule is pinned so
implementation is transcription, not decision-making. It is **v1 only** — per-prompt customization ("Rewrite
by voice") is a separate v2, stubbed at the end.

## Scope

**v1 ships:** the 33 canonical prompts (verbatim bodies/categories/ids), carried as an embedded JSON resource;
`PromptItem` widened to hold their fields; the picker always-opens with the default (`rewrite`) pinned to the
top and pre-selected so Enter runs it; stable-slug persistence + a one-time migration off the old key scheme.

**v1 does NOT ship (deferred):** the "type or speak a detail" augmentation flow (`voiceAugmentHint`) — that is
**Rewrite by voice, v2** (stub below). The `voiceAugmentHint` string is carried in the data model in v1 but is
**not read by any code path** — it is inert metadata until v2. Sample I/O preview drawer is v1.1 polish (§8),
gated so v1 works without it.

## Source of truth — port `prompt-library.json` verbatim

Canonical file: `projects/JOT-Transcribe/Resources/prompt-library.json` (version 1, 33 prompts). **Copy it
byte-for-byte** into the Windows repo at `src/Jot/Assets/prompt-library.json` and embed it as a resource. Do
**not** re-type prompt bodies into C# — the bodies are long, multi-paragraph, and load-bearing (e.g. the
`rewrite` articulation prompt). A single embedded copy is the one source of truth; re-typing invites drift.

- **Provenance:** the file is generated on Mac from `docs/plans/prompt-library-curation.md`. Windows treats it
  as **read-only, ported.** When Mac regenerates it, re-copy the file (a mechanical sync, tracked as a
  follow-up chore — not a Windows-side edit).
- **Fidelity gate:** a test asserts the embedded file parses to exactly **33** prompts and that the 33 `id`s
  match the pinned list in §"The 33 prompts" below. If Mac adds/removes a prompt, the count test fails loudly
  and forces a conscious re-sync rather than silent divergence.

### csproj — embed the JSON

`prompt-library.json` goes under `Assets\`, which is already swept into `Content` by the existing glob
(`<Content Include="Assets\**\*" .../>` in `Jot.csproj`), so it is copied to the output directory next to
`Jot.dll` with **no csproj change required**. Load it at runtime from disk beside the assembly (see §Catalog).

> Rationale for Content-on-disk over `EmbeddedResource`: the existing catalog already reads/writes JSON from
> disk and the Assets glob already ships files next to the exe; a plain `File.ReadAllText` of a bundled file is
> the lowest-friction path and matches how `aikey`/`prompts.json` are already handled. If a future change wants
> it truly embedded (single-file publish), switch to `<EmbeddedResource>` + `GetManifestResourceStream` — noted,
> not needed now (Velopack ships the whole output folder).

## Data model — widen `PromptItem`

Current `src/Jot/Models/PromptItem.cs` has: `Id (Guid)`, `Category`, `IsBuiltIn`, `Title`, `Body`, `IsPinned`,
`IsDefault`, `LastPickedSeq`. It is missing the curated fields and — critically — built-ins have **no stable
string id** (their persistence key is `b:{Category}/{Title}`, which breaks if a title is ever reworded).

**Add these members (all additive; nothing removed):**

```csharp
public sealed partial class PromptItem : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();          // unchanged: user-prompt identity
    public string Slug { get; init; } = "";                  // NEW: built-in stable id ("improve-writing"); "" for user prompts
    public string Category { get; init; } = "Essentials";
    public bool IsBuiltIn { get; init; } = true;
    public int Tier { get; init; } = 1;                      // NEW: 1 Essentials-visible, 2/3 long-tail (search)
    public string[] Tags { get; init; } = [];                // NEW: search terms (verbatim from JSON)
    public string SampleInput { get; init; } = "";           // NEW: preview drawer (v1.1)
    public string SampleOutput { get; init; } = "";          // NEW: preview drawer (v1.1)
    public string? VoiceAugmentHint { get; init; }           // NEW: v2 metadata; INERT in v1

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _body = "";
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isDefault;
    [ObservableProperty] private long _lastPickedSeq;
}
```

`Tier`, `Tags`, `SampleInput`, `SampleOutput`, `VoiceAugmentHint`, `Slug` are **`init`-only immutable** on
built-ins (seeded from JSON). User prompts keep `Slug=""`, `Tier=1`, `Tags=[]`, empty samples, null hint.

## The 33 prompts (id → category → tier)

Pinned so the fidelity test and default-seed are exact. Verbatim titles/bodies come from the JSON; only the
`id`/category/tier are enumerated here.

| # | id | Category | Tier |
|---|---|---|---|
| 1 | `improve-writing` | Essentials | 1 |
| 2 | `rewrite` | Essentials | 1 | **← default**
| 3 | `fix-spelling-grammar` | Essentials | 1 |
| 4 | `make-formal` | Essentials | 1 |
| 5 | `make-casual` | Essentials | 1 |
| 6 | `make-shorter` | Essentials | 1 |
| 7 | `make-longer` | Essentials | 1 |
| 8 | `summarize` | Essentials | 1 |
| 9 | `extract-key-points` | Essentials | 1 |
| 10 | `convert-to-ai-prompt` | Essentials | 1 |
| 11 | `translate` | Translate | 2 |
| 12 | `respond-to-email` | Email | 2 |
| 13 | `convert-to-jira-ticket` | Convert | 2 |
| 14 | `convert-to-action-items` | Convert | 2 |
| 15 | `convert-to-outline` | Convert | 2 |
| 16 | `convert-to-markdown-doc` | Convert | 2 |
| 17 | `tighten-and-clarify` | Rewrite | 2 |
| 18 | `make-assertive` | Rewrite | 2 |
| 19 | `plain-english` | Rewrite | 2 |
| 20 | `friendly-tone` | Rewrite | 2 |
| 21 | `confident-tone` | Rewrite | 2 |
| 22 | `bluf-email` | Email | 2 |
| 23 | `convert-to-mermaid` | Convert | 2 |
| 24 | `polish-for-publication` | Rewrite | 3 |
| 25 | `status-update-email` | Email | 3 |
| 26 | `meeting-minutes-to-actions` | Convert | 3 |
| 27 | `convert-to-checklist` | Convert | 3 |
| 28 | `convert-to-faq` | Convert | 3 |
| 29 | `pros-and-cons` | Convert | 3 |
| 30 | `slide-bullets` | Convert | 3 |
| 31 | `add-comments-to-code` | Code | 3 |
| 32 | `polite-decline` | Email | 3 |
| 33 | `trim-ai-fluff` | Rewrite | 3 |

Categories present: **Essentials, Translate, Email, Convert, Rewrite, Code** (6). `rewrite` (row 2) is the
default because its curated intent *is* "articulate what I dictated" — the natural one-press action.

## Catalog — rewrite `PromptCatalog.SeedBuiltIns`

Replace the 15 hardcoded `Add(...)` calls in `src/Jot/Services/PromptCatalog.cs` with a JSON loader. Keep the
class's existing shape (ObservableCollection, Load/Save of user state) — only the seed source changes.

```csharp
private static readonly string BundledLibrary =
    Path.Combine(AppContext.BaseDirectory, "Assets", "prompt-library.json");

private void SeedBuiltIns()
{
    // Ported, read-only catalog. Parse failure must not brick rewrite: fall back to a tiny inline
    // seed so the picker is never empty (a missing/corrupt bundled file is a packaging bug, not a
    // user-data problem). Log and continue.
    try
    {
        string json = File.ReadAllText(BundledLibrary);
        var lib = JsonSerializer.Deserialize<BundledLibraryDto>(json, BundledOptions);
        if (lib?.Prompts is null or { Count: 0 }) { SeedFallback(); return; }
        foreach (BundledPromptDto p in lib.Prompts)
            Prompts.Add(new PromptItem
            {
                Slug = p.Id, Category = p.Category, Title = p.Title, Body = p.Body,
                Tier = p.Tier, Tags = p.Tags ?? [], SampleInput = p.SampleInput ?? "",
                SampleOutput = p.SampleOutput ?? "", VoiceAugmentHint = p.VoiceAugmentHint,
                IsBuiltIn = true,
            });
    }
    catch (Exception ex) { Services.JotLog.Info($"prompt library load failed: {ex.Message}"); SeedFallback(); }
}

// Last-ditch seed if the bundled JSON is missing/corrupt — just the default, so Rewrite still works.
private void SeedFallback() =>
    Prompts.Add(new PromptItem { Slug = "rewrite", Category = "Essentials", Title = "Rewrite",
        Body = "Rewrite this.", Tier = 1, IsBuiltIn = true });

private sealed record BundledLibraryDto(int Version, List<BundledPromptDto> Prompts);
private sealed record BundledPromptDto(string Id, string Title, int Tier, string Category,
    string[]? Tags, string Body, string? SampleInput, string? SampleOutput, string? VoiceAugmentHint);
// BundledOptions = new(JsonSerializerDefaults.Web) — camelCase to match the JSON keys.
```

`BundledOptions` uses `PropertyNameCaseInsensitive = true` (or Web defaults) so `sampleInput`/`voiceAugmentHint`
bind. Ignore `providerCompatibility` and `note`/`generatedFromDoc` (extra JSON keys are dropped by default).

### Stable-slug persistence key (the migration)

Today `Key(p)` is `p.IsBuiltIn ? $"b:{p.Category}/{p.Title}" : $"u:{p.Id}"`. Change the built-in branch to the
**slug**:

```csharp
private static string Key(PromptItem p) => p.IsBuiltIn ? $"b:{p.Slug}" : $"u:{p.Id}";
```

**Why + migration:** pins, the default choice, and pick-order persist to `%LOCALAPPDATA%\Jot\prompts.json`
keyed by `Key`. The old build wrote keys like `b:Essentials/Improve writing`; the 33-prompt catalog changes
titles and categories, so **every old built-in key is now orphaned** (it matches no seeded prompt). Consequences
if unhandled: a user who had pinned/default prompts loses them silently, AND — worse — no prompt is `IsDefault`,
so the picker opens with an arbitrary top row. Handle it deterministically:

1. **Old built-in keys simply don't match** any new slug and are ignored on Load (harmless; the loop in `Load`
   already only applies state to prompts whose `Key` is found). User prompts (`u:{Guid}`) are unaffected —
   their keys are stable.
2. **Default seed-on-absence:** after `SeedBuiltIns()` + `Load()`, if **no** prompt has `IsDefault`, set the
   `rewrite`-slug prompt as default (and `Save()` so it sticks). This covers both first run and the migration.
   Exact code in `PromptCatalog` ctor, after `Load()`:
   ```csharp
   if (!Prompts.Any(p => p.IsDefault))
   {
       PromptItem? def = Prompts.FirstOrDefault(p => p.Slug == "rewrite") ?? Prompts.FirstOrDefault();
       if (def is not null) { def.IsDefault = true; Save(); }
   }
   ```
   `_loading` is `true` during the ctor; call this **after** `_loading = false` (or call `Save()` directly here
   — `Save()` early-returns while `_loading`), so the seeded default persists. **Pin this ordering:** set
   `_loading=false` first, then run the default-seed block, so its `Save()` writes.
3. **One-time, self-healing:** because the default now persists under the stable key `b:rewrite`, it survives
   every future catalog re-sync. No versioned migration record is needed — "no default present ⇒ seed rewrite"
   is idempotent and covers the upgrade in one branch.

## The seamless flow — always-open picker, default on top, Enter runs it

Two behavior changes; both small.

### 1. `RewriteController.BeginRewrite` — always open the picker

Today it **bypasses** the picker whenever a default exists:
```csharp
public void BeginRewrite(Action openPicker)
{
    if (!CaptureContext()) { NothingSelected?.Invoke(); return; }
    PromptItem? def = DefaultPrompt;
    if (def is not null) RunRewrite(def.Body);   // ← silent, no picker
    else openPicker();
}
```
The user's decided UX is **always show the library** (discoverable + seamless), with the default one press away.
Change to:
```csharp
public void BeginRewrite(Action openPicker)
{
    if (!CaptureContext()) { NothingSelected?.Invoke(); return; }
    openPicker();   // always show the library; the default is pre-selected on top (Enter runs it)
}
```
**`DefaultPrompt` becomes dead code — remove it.** `RewriteController.DefaultPrompt` (line 57) is referenced
**only** inside the old `BeginRewrite` (line 64), which this change deletes. The VM does **not** use it — the
picker sorts off `PromptItem.IsDefault` directly (VM sort descriptors), never through the controller. So delete
the `DefaultPrompt` property along with the branch. (Which prompt is default lives in the catalog/`IsDefault`,
not on the controller.) `RunRewrite` is unchanged. The wiring is confirmed real: `App.xaml.cs` calls
`BeginRewrite(OpenRewritePicker)`, and `OpenRewritePicker` constructs `PromptPickerWindow` with
`PromptChosen = item => _rewrite!.RunRewrite(item.Body)` — so picking any row (including the pre-selected
default via Enter) runs the rewrite. No change needed at that call site.

> **Note — capture cost every time:** `CaptureContext()` now runs before the picker on *every* rewrite (it
> already did on the picker path, so this is not new for that path; it *is* new relative to the old default
> path, which also called it first — so no change). Capture is a UIA read or one synthetic Ctrl+C; the existing
> default path already paid it. No regression.

### 2. Sort the default to the top + pre-select it

`PromptPickerViewModel` currently sorts `IsPinned desc → LastPickedSeq desc → Title asc`. Add `IsDefault` as the
**first** (highest-priority) sort key so the default always lands at index 0 regardless of pins/recents:

```csharp
cvs.SortDescriptions.Add(new SortDescription(nameof(PromptItem.IsDefault), ListSortDirection.Descending));
cvs.SortDescriptions.Add(new SortDescription(nameof(PromptItem.IsPinned),  ListSortDirection.Descending));
cvs.SortDescriptions.Add(new SortDescription(nameof(PromptItem.LastPickedSeq), ListSortDirection.Descending));
cvs.SortDescriptions.Add(new SortDescription(nameof(PromptItem.Title), ListSortDirection.Ascending));
```

`PromptPickerWindow.OnLoaded` already does `if (List.Items.Count > 0) List.SelectedIndex = 0;` — with the new
sort, index 0 **is** the default, so it is pre-selected. `OnPreviewKeyDown`'s `Enter` case already executes
`PickCommand` on `List.SelectedItem`. **Net: press hotkey → picker → Enter → default runs.** No XAML change
needed for the core flow.

- **Typing still works — no change needed.** `PromptPickerWindow.xaml` sets `IsSynchronizedWithCurrentItem="True"`
  on the `List` ListView (line 39), so its `SelectedItem` is bound to the collection view's **current item**.
  `OnSearchTextChanged` (VM) already calls `Prompts.MoveCurrentToFirst()` after each filter refresh, which moves
  the current item — and therefore the ListView selection — to the top filtered row. So Enter always has a
  target as the user types. **No new `CollectionChanged`/`SelectedIndex` handler is required** (an earlier draft
  of this doc prescribed one; it is redundant given `IsSynchronizedWithCurrentItem`).

### 3. Default is visible in the list — already present (verify-only)

The picker row template **already renders a "default" badge** bound to `IsDefault`: `PromptPickerWindow.xaml`
lines 82–86 have a `Border` with `Visibility="{Binding IsDefault, Converter={StaticResource BoolToVis}}"` around
a `TextBlock Text="default"`. So once `rewrite` is `IsDefault` and sorted to the top, the badge shows with **no
XAML change**. This is verify-only. The Prompts **settings page** already exposes "set default" (Ctrl+D in the
picker via `SetDefaultCommand`), so users can re-point the default; that path is unchanged.

## Prompts settings page

`PromptsViewModel` / the Prompts page already lists the catalog and supports add/edit/delete of user prompts,
pin, and set-default. With 33 built-ins it just shows more rows — **verify** the page groups or scrolls
acceptably (central page-scroll shipped in 0.3.0 covers it). No functional change required; one **check**: the
page must not offer edit/delete on built-ins (it already guards `IsBuiltIn` in `PromptCatalog`, but confirm the
page's buttons are disabled/hidden for built-ins so users don't try). Tier can drive optional grouping
(Essentials shown first) but that is polish, not required for v1.

## §8 Sample I/O preview drawer (v1.1 — gated, optional)

The data model carries `SampleInput`/`SampleOutput`. A preview drawer in the picker (show the selected prompt's
before/after) is a **nice-to-have deferred to v1.1** so v1 ships the flow first. When built: a collapsible panel
in `PromptPickerWindow` bound to `List.SelectedItem.SampleInput/SampleOutput`, empty-collapsed when a user
prompt (no samples) is selected. Explicitly **out of v1's definition of done** — listed so the fields aren't
mistaken for dead code.

## Rewrite by voice (v2 stub — NOT built in v1)

Many curated prompts carry a `voiceAugmentHint` (e.g. `improve-writing` → "Add a tone or audience direction").
The Mac flow: after choosing such a prompt, the user types **or speaks** a short detail that is appended to the
instruction ("…for execs"). **This augmentation flow is not designed yet and is out of v1 scope.**

**Disambiguation (important — Windows already has a similarly-named hotkey):** Jot for Windows *already* ships
`RewriteController.ToggleVoiceRewrite`, a **free-form spoken-instruction** rewrite (press → speak any
instruction → it rewrites). That is a *different feature* from "augment a **chosen library prompt** with a
spoken detail." v2 "Rewrite by voice" = the **augmentation** flow (prompt + spoken/typed detail), layered on the
picker. v1 leaves `voiceAugmentHint` inert and changes nothing about `ToggleVoiceRewrite`.

**v2 sketch (design later, do not build now):** when a prompt with a non-null `voiceAugmentHint` is picked,
instead of running immediately, show a one-line input (hint as placeholder) with a mic affordance; typed text or
transcribed speech is appended to `Body` as `"\n\nAdditional direction: {detail}"` before `RunRewrite`. Prompts
with a null hint run immediately (v1 behavior). Open questions for the v2 doc: modal vs inline, reuse of the
recorder mid-picker, Esc/cancel semantics, and whether the detail persists as a per-prompt preset.

## Integration diffs (exact, v1)

1. **`src/Jot/Assets/prompt-library.json`** (NEW) — byte-copy of the Mac file. Shipped via the existing
   `Content` glob (no csproj edit).
2. **`src/Jot/Models/PromptItem.cs`** — add `Slug`, `Tier`, `Tags`, `SampleInput`, `SampleOutput`,
   `VoiceAugmentHint` (all `init`/additive).
3. **`src/Jot/Services/PromptCatalog.cs`** — replace `SeedBuiltIns` with the JSON loader + `SeedFallback`;
   change `Key` built-in branch to `b:{Slug}`; add the default-seed-on-absence block in the ctor after
   `_loading=false`.
4. **`src/Jot/ViewModels/PromptPickerViewModel.cs`** — prepend the `IsDefault` sort descriptor.
5. **`src/Jot/Rewrite/RewriteController.cs`** — `BeginRewrite` always calls `openPicker()` (drop the silent
   default branch); **delete the now-unused `DefaultPrompt` property**.
6. **`src/Jot/Controls/PromptPickerWindow.xaml(.cs)`** — **no change.** Selection-follows-filter is already
   handled by `IsSynchronizedWithCurrentItem="True"` + `MoveCurrentToFirst`; the "default" badge already exists
   in the row template (lines 82–86). Verify-only.
7. **`src/Jot/Views/PromptsPage.xaml(.cs)` / `PromptsViewModel`** — verify-only: built-ins non-editable in UI;
   33 rows scroll acceptably. No behavior change expected.

## Tests (add to `tests/Jot.Tests` — the project created for offline cleanup)

- **`PromptLibraryFidelityTests`**: the bundled JSON parses; `Prompts.Count(IsBuiltIn) == 33`; the set of
  built-in `Slug`s equals the pinned 33-id list; every built-in has a non-empty `Body`; `rewrite`, `improve-
  writing`, `trim-ai-fluff` bodies contain their known anchor substrings (guards against a truncated copy).
- **`PromptCatalogDefaultTests`**: fresh state (no `prompts.json`) ⇒ exactly one `IsDefault`, and it is
  `rewrite`. Simulated upgrade (a `prompts.json` with old `b:Essentials/Improve writing` keys and no matching
  default) ⇒ after load, exactly one `IsDefault` == `rewrite`; old orphan keys ignored; a `u:{Guid}` user
  prompt in that file survives.
- **`PromptCatalogPersistenceTests`**: set default to `summarize`, pin `translate`, reload ⇒ both persist under
  `b:summarize`/`b:translate`; re-sync (reseed) keeps them.
- **`PromptPickerSortTests`** (VM-level): with `rewrite` default, the first item of the `Prompts` view is
  `rewrite`, ahead of a pinned non-default and a recently-picked non-default.

(These need the `Jot.Tests` project + `Jot.sln` from the offline-cleanup design; the two features share it.)

## Risks & mitigations

1. **Catalog drift Mac↔Windows** — mitigated by the fidelity count/id test: a Mac change breaks the Windows
   build until re-synced. Sync is a byte-copy chore, tracked separately.
2. **Silent loss of a user's old pinned/default on upgrade** — the default self-heals to `rewrite`; **old pins
   on built-ins do not carry over** (titles/categories changed, so keys can't map). This is accepted and
   documented: built-in pins reset once on upgrade to the 33-prompt library; user prompts and their pins are
   unaffected. If preserving old built-in pins ever matters, add a best-effort title→slug remap table — not
   worth it for v1 (built-ins re-pin in one keystroke).
3. **Bundled file missing at runtime** (bad package) — `SeedFallback` keeps Rewrite working with the default
   only; logged. The fidelity test catches it in CI before ship.
4. **Provider compatibility ignored** — `providerCompatibility` is dropped in v1; all 33 show for every
   provider. Acceptable: an incompatible provider just yields a normal AI result/error; no crash. Filtering by
   provider is a possible v2 refinement, flagged not built.

## Reference files
- `projects/JOT-Transcribe/Resources/prompt-library.json` — canonical 33 prompts (the port source).
- `projects/JOT-Transcribe/Sources/PromptLibrary/PromptStore.swift` — Mac catalog behavior (reference).
- `src/Jot/Services/PromptCatalog.cs`, `Models/PromptItem.cs`, `ViewModels/PromptPickerViewModel.cs`,
  `Controls/PromptPickerWindow.xaml.cs`, `Rewrite/RewriteController.cs` — the Windows files edited here.
- `docs/plans/offline-cleanup-windows.md` — sibling v1 design; shares the `Jot.Tests` project + `Jot.sln`.
