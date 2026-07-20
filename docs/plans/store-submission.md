# Microsoft Store submission — prep checklist

## 2026-07-18 UPDATE — MSIX BUILT, REGISTERED, LAUNCHED (packaging step done)

The blocked "needs the human" items are cleared and the never-attempted packaging step is
now **done and verified**:

- **Real identity wired** into `src/Jot/Package.appxmanifest` (was placeholders `jot`/`CN=vinee`):
  - `Name="Vineetsriram.JotTranscribe"`  `Publisher="CN=1269BFA0-02DC-4524-8A77-55C4EE9EADD4"`
  - `PublisherDisplayName="Vineet sriram"`  `DisplayName="Jot Transcribe"` (the reserved
    Store name — MUST match exactly; the tile `VisualElements DisplayName` stays "Jot").
  - New MSIX-type product created in Partner Center; the old rejected EXE product's flow is dead.
- **csproj asset-copy bug fixed:** `<Content Include="Assets\**\*">` had no
  `CopyToOutputDirectory`, so StoreLogo/tiles never reached the publish output → the manifest's
  logo references would have been dangling. Added `CopyToOutputDirectory="PreserveNewest"`.
- **MSIX build route decided + scripted:** the MSBuild `GenerateAppxPackageOnBuild` target does
  NOT fire for this plain-WPF project (Appx targets aren't imported; the WinApp NuGet only
  provides `dotnet run` support + the SDK binaries, not Store packaging). So the build is a
  manual **`dotnet publish` → resolve manifest tokens → `makeappx pack`** pipeline, captured in
  **`build-msix.ps1`** at the repo root (`.\build-msix.ps1` builds the .msix; add `-Register`
  to also register the loose layout for local testing). makeappx/signtool come from the
  `microsoft.windows.sdk.buildtools` NuGet (cache on `D:\caches\nuget-packages`).
- **Deliverable:** `AppxPackages\JotTranscribe_1.0.0.0_x64.msix` — **98.6 MB, UNSIGNED**
  (upload as-is; Store signs it). The ~630 MB Parakeet model is NOT in the package (downloaded
  on first run to real `%LOCALAPPDATA%\Jot\models`), which keeps the package small.
- **VERIFIED locally (Developer Mode on, no elevation needed):**
  - Loose layout registers cleanly → `Vineetsriram.JotTranscribe_1.0.0.0_x64__xhkaqb0regjwm`.
    The `xhkaqb0regjwm` hash matches the Partner Center PFN → Publisher CN is correct.
  - Packaged app **launches under package identity**, main window shows (`MainWindowTitle=Jot`),
    ~1.5 GB working set = int8 model warmed up → **native ONNX Runtime + DirectML load fine
    under MSIX**, and the real `%LOCALAPPDATA%\Jot` model path is NOT virtualized (model found
    + loaded). Zero crash.log. This clears every "known unknown" the old packaging section listed.
- **STILL NOT VERIFIED (needs the human, real mic):** actual dictation hotkey→speak→paste inside
  the packaged app. The package is registered right now — test it, then upload + list.

Remaining to ship: (1) test real dictation in the registered package; (2) upload the .msix to the
MSIX product's Packages page in Partner Center; (3) fill the listing (description, screenshots,
IARC age rating, privacy URL below, data-collection + mic-capability justification); (4) Submit.

## 2026-07-17 UPDATE — first submission REJECTED; MSIX path confirmed

The user submitted the Velopack `Jot-win-Setup.exe` as an **"EXE or MSI app"** ("Jot Transcribe",
Partner Center ID `5654b512-39e3-4b47-bae0-3c97b8a3dd7d`). **Rejected 2026-07-15 under policy
10.2.9**: the EXE and its PE files are unsigned; EXE/MSI submissions require a CA-trusted code-signing
cert. Microsoft's own suggested fix: resubmit as **MSIX** (Store signs it automatically — no cert).

Consequences / next steps:
1. The EXE-type registration **cannot be converted** to MSIX. The user must (in Partner Center):
   cancel any pending review → delete the "Jot Transcribe" EXE product (frees the name) → create a
   NEW product of type **MSIX or PWA app** → reserve "Jot Transcribe" → Product management →
   **Product identity** → copy Package/Identity/Name, Package/Identity/Publisher (CN=GUID), and
   Publisher display name into `src/Jot/Package.appxmanifest` (currently placeholders `jot` /
   `CN=vinee`). NOTE: the EXE-type app has no Product identity page — that page only exists on
   MSIX-type registrations (this confused the user; expected).
2. Then: build MSIX with real identity, install + launch the packaged build locally (the never-yet-
   verified step), fix whatever breaks, upload to the new app's Packages page, submit.
3. Microphone capability was added to the manifest 2026-07-17 (`<DeviceCapability Name="microphone"/>`).


**Related docs:** `../features.md` = feature status; `fixit-worklist.md` = broader todo;
**this doc** = what's needed specifically to list Jot on the Microsoft Store. Scope here is
**code-side prep only** — creating the actual Partner Center account, obtaining a
code-signing certificate, and submitting the app for review all require the user's own
identity, so those steps are captured below as "needs the human" and are explicitly out of
scope for an agent to do.

Compiled 2026-07-15. Nothing in the "needs the human" section has been started; the "done"
section has been built and built-verified (`dotnet build`) but **not** verified inside an
actual MSIX package (that packaging step itself hasn't been attempted yet — see below).

---

## Done (code-side)

- [x] **Velopack now skips itself when running as a packaged app.** `App.xaml.cs` adds
  `IsRunningAsPackagedApp()` — a small P/Invoke against `GetCurrentPackageFullName`
  (kernel32.dll) that returns `true` when the process has MSIX/Store package identity
  (`APPMODEL_ERROR_NO_PACKAGE` == 15700 means NO package identity → normal Velopack-installed
  run; any other result, typically `ERROR_INSUFFICIENT_BUFFER` == 122, means it IS packaged).
  `OnStartup` now wraps the `VelopackApp.Build()...Run()` call in
  `if (!IsRunningAsPackagedApp())`. No new NuGet/WinRT dependency — matches the existing raw
  Win32 P/Invoke style used elsewhere in this file and in `HotkeyManager.cs`/`TextInjector.cs`.
  **Verified:** `dotnet build` compiles clean with the change. **Not yet verified:** actually
  running inside an MSIX package (no package has been built yet — see below), so the
  packaged-branch detection itself hasn't been exercised against a real package identity, only
  reasoned about from the documented Win32 API contract.
- [x] **Privacy policy is live** at
  <https://sites.simple-host.app/jot-transcribe/jot-windows-privacy/> and linked from the
  About page (`AboutPage.xaml` "Your privacy" card → "Read the full privacy policy" button →
  `AboutPage.xaml.cs` `OnOpenPrivacyPolicy` → existing `OpenUrl` helper).
- [x] **README refreshed** to reflect real status (real on-device STT, working UI, AI
  cleanup/rewrite caveats) instead of the stale "transcription stubbed" text, plus License and
  Privacy sections.

## Needs the human user directly (cannot be done by an agent)

- [ ] **Partner Center developer account.** As of Microsoft's 2025 policy change, the
  **individual developer account is now free** (previously a one-time fee). Registration
  requires **identity verification: a photo ID + a selfie match** — this is tied to the
  user's own government ID and can't be delegated. Sign up at
  <https://partner.microsoft.com/dashboard/registration>.
- [x] **Code-signing certificate — NOT needed.** Confirmed via Microsoft Learn (2026-07-15
  research pass): Store submissions do **not** require a CA-trusted certificate. Microsoft
  re-signs the package with its own certificate automatically after certification passes.
  A developer-owned cert is only required for **sideloading/enterprise distribution outside
  the Store** — not applicable here since Jot is Store-only. One less thing to buy/manage.
- [ ] **Reserve the app name + get package identity.** In Partner Center → create a new app
  submission → reserve a name (e.g. "Jot" or "Jot for Windows" if taken) → the app's
  **"App identity"** page (under Product management) then generates the **Package/Identity/Name**,
  **Publisher** (a `CN=...` string), and **Publisher Display Name** values needed for the MSIX
  manifest. This has to happen in the user's own Partner Center account. Once reserved, paste
  those 3 values back so the MSIX packaging attempt (below) can use the real identity instead
  of a placeholder.
- [ ] **The actual Store submission + review.** Listing text, screenshots, pricing, age
  rating questionnaire answers, and hitting "Submit" all happen in Partner Center under the
  verified account.

## Next real engineering step: MSIX packaging (not yet attempted)

Jot is a CLI-driven `dotnet publish`/`vpk`-based app (`net10.0-windows`, no Visual Studio
solution-level packaging project today — see `src/Jot/Jot.csproj`). Recommendation: use the
newer **`dotnet publish -p:WindowsPackageType=MSIX`** / WinApp SDK CLI packaging route rather
than adding a separate **Windows Application Packaging Project** (`.wapproj`), since a
`.wapproj` assumes a Visual Studio multi-project solution shape this repo doesn't have, while
the `dotnet publish` MSIX route stays inside the existing single-project, CLI-driven build.

**This has not been attempted or verified yet.** Known unknowns to resolve when it's tackled:
- Whether `net10.0-windows` + WPF + WinForms interop (tray `NotifyIcon`) + the native ONNX
  Runtime/DirectML dependencies package cleanly under MSIX (native deps + non-UWP APIs like
  `RegisterHotKey`/low-level hooks/WASAPI are usually fine in an MSIX **unpackaged-identity**
  Win32 app, but this needs a real end-to-end packaged build + launch to confirm — do not
  claim it works without running it).
- Whether the bundled `ffmpeg.exe` (shipped via Git LFS, copied next to the exe) needs any
  special handling under MSIX's file-layout/virtualization.
- App identity/manifest changes MSIX packaging requires beyond the existing `app.manifest`.
- Confirm `IsRunningAsPackagedApp()` (this session's addition) actually returns `true` once a
  real packaged build exists — currently only reasoned from the Win32 API docs, not observed.

## Store policy requirements to satisfy at listing time

- [ ] **Declare the Microphone capability** in the package manifest, with a **justification
  string** explaining why Jot needs it (dictation — captures speech to transcribe on-device).
- [ ] **Age rating** — complete the **IARC rating questionnaire** in Partner Center (Jot has
  no objectionable content; expect the lowest rating tier, but the questionnaire itself must
  still be filled out).
- [ ] **Privacy policy URL** — enter
  <https://sites.simple-host.app/jot-transcribe/jot-windows-privacy/> in the Store listing's
  privacy policy field (Partner Center → Store listing).
- [ ] **Data collection disclosure** — the Store listing has a "how does this app use your
  data" questionnaire; answer honestly per the privacy policy (on-device only, no telemetry,
  optional BYO-provider AI calls only when the user configures a key).

## Suggested order once the human-only steps unblock

1. Partner Center account + identity verification (human).
2. Attempt the `dotnet publish -p:WindowsPackageType=MSIX` packaging route; fix whatever
   breaks (expect native-dependency and manifest friction — budget real time, don't assume
   it's a one-liner).
3. Verify a packaged build actually launches, dictates, and that
   `IsRunningAsPackagedApp()` correctly returns `true` and skips Velopack.
4. Fill out the Store listing (capability justification, IARC questionnaire, privacy URL,
   data-collection disclosure, screenshots, description). No separate code-signing step —
   the Store signs the package automatically at certification.
5. Submit for review (human).
