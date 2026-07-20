# Windows Copilot (on-device NPU AI) as a Jot provider — research + plan

**Status: RESEARCHED, NOT IMPLEMENTED (deliberately). Verdict: not yet "soundproof" — deferred.**
Compiled 2026-07-18. Applies to BOTH flavors (it's fully on-device, so it fits the Sony
data-governance rule too — nothing leaves the machine, like Ollama).

## What it is

"Windows Copilot" on-device AI = the **Windows AI APIs / Phi Silica** in the Windows App SDK
(`Microsoft.Windows.AI` + `Microsoft.Windows.AI.Text`, the `LanguageModel` class). It's a
Microsoft SLM that runs **on the NPU** of a Copilot+ PC (or a supported GPU), with **no API key
and no cloud**. It ships built-in **Text Intelligence Skills** — including **rewrite** (rephrase
for clarity/readability) and **summarize** — which map directly onto Jot's cleanup/rewrite.

Good-fit facts:
- Runs on **x64** NPUs (Intel + AMD Copilot+ PCs), not just ARM64 — Jot's x64 build is compatible.
- Requires a **packaged MSIX app identity** — which Jot now has (the Store MSIX).
- No key, on-device → fits the Public flavor's privacy story AND the Sony "no external online AI"
  rule (it's local).

## Why it is NOT soundproof to add right now (the blockers)

1. **[HIGH] Reintroduces the WinAppSDK ↔ ONNX Runtime collision Jot deliberately avoided.**
   Phi Silica requires `Microsoft.WindowsAppSDK`. Jot's `Jot.csproj` explicitly refuses that package
   because it transitively ships an `onnxruntime.dll` that collides with
   `Microsoft.ML.OnnxRuntime.DirectML` (the STT engine) → APPX1101 "duplicate payload path". This is
   a documented class of failure (microsoft/onnxruntime #14915, microsoft/WindowsAppSDK #3639).
   Adding Copilot risks breaking the **core of the app** (speech-to-text). No verified clean
   workaround found — this must be proven resolvable BEFORE anything else.
2. **[HIGH] Untestable on this machine** — no NPU here. Correct behavior can only be verified on a
   real Copilot+ PC (Snapdragon X / Intel / AMD, 40+ TOPS NPU, Win11 build 26100+).
3. **[MED] Phi Silica is being deprecated** → replaced by **Aion Instruct** (Windows Insider Oct
   2026, retail Nov 2026; Phi Silica removed thereafter). Building on Phi Silica now targets a model
   with ~4 months of life. The `LanguageModel` surface may carry to Aion, but that's unconfirmed.
4. **[MED] Preview/experimental SDK** — Phi Silica needs Windows App SDK 2.0-preview1 /
   2.2.2-experimental. Shipping a Store feature on a preview SDK is risky.
5. **[LOW] Only works under MSIX identity** — so it'd be available in the Store/MSIX build, not the
   unpackaged Velopack install. Fine as a gated capability, but worth noting.

## Go / no-go test sequence (do these IN ORDER before writing the feature)

1. **Collision spike (the gate) — RUN 2026-07-19: FAILED, as feared.** Added
   `Microsoft.WindowsAppSDK` 2.3.1 (latest stable) to `Jot.csproj` and published — hard
   **APPX1101** at pack time:
   ```
   APPX1101: Payload contains two or more files with the same destination path 'onnxruntime.dll'.
     microsoft.windows.ai.machinelearning\2.1.74\runtimes\win-x64\native\onnxruntime.dll   (via WinAppSDK)
     microsoft.ml.onnxruntime.directml\1.20.1\runtimes\win-x64\native\onnxruntime.dll      (Jot's STT)
   ```
   So WinAppSDK transitively pulls **`Microsoft.Windows.AI.MachineLearning` 2.1.74**, which ships its
   own `onnxruntime.dll` that collides with the STT engine's DirectML runtime. The csproj comment is
   accurate and current. Spike reverted; clean 0/0 build restored. **This is the hard blocker,
   empirically confirmed — not a stale note.**
   - The two runtimes are DIFFERENT builds wanting the same payload path, and the STT is pinned to
     ORT 1.20.1 (newer fails DLL-init on this GPU), so naive `ExcludeAssets`/dedup would break one
     side. No cheap workaround.
   - **The real unlock: migrate STT off `Microsoft.ML.OnnxRuntime.DirectML` onto Windows ML**
     (`Microsoft.Windows.AI.MachineLearning`, which *is* ORT and is what WinAppSDK's AI already
     ships) — then there is exactly ONE onnxruntime on the payload and Phi Silica + STT coexist.
     This is the already-planned "single seam" swap in `Transcription/Onnx/OnnxSessionFactory.cs`
     (see [[jot-windows-port]]/[[stt-benchmarks]]). Copilot AI should ride on THAT migration, not
     precede it.
   - Remaining sub-steps (only after the migration makes the spike pass): confirm the packaged app
     still transcribes (`--transcribe`/`--streamtest`) with no duplicate onnxruntime.
2. **Device test.** On a real Copilot+ PC: confirm `LanguageModel.GetReadyState()` reports available,
   the model auto-provisions, and the rewrite/summarize skills return sane output.
3. Only then implement below.

## Clean design (for when the gate passes)

- **New provider "Windows AI" (a.k.a. "Copilot · on-device")**, added to **both** flavors'
  `BuildFlavor.AiProviders`. It's local, so it doesn't violate the Sony rule.
- **NOT an HTTP provider.** The Windows AI API is in-process, not REST — so it does NOT go through
  `AiClient`'s HTTP path. Introduce a second `IAiClient` implementation (e.g. `WindowsAiClient`) OR a
  provider branch that calls the `LanguageModel` rewrite/summarize skills directly. Keep the
  `IAiClient` contract (Cleanup/Rewrite/Ask/TestConnection) so the call sites don't change.
  - Cleanup → the summarize/rewrite skill with a "clean, don't change meaning" prompt (reuse the
    existing faithfulness guard in `AiClient.IsFaithfulCleanup`).
  - Rewrite/Ask → `LanguageModel.GenerateResponseAsync` (or the Rewrite skill).
- **Capability gating (critical for a clean UX):** only list/enable the provider when
  `LanguageModel` reports ready. Show a friendly "Requires a Copilot+ PC (NPU) and Windows 11 24H2+"
  state otherwise — never a dead option. Runtime check via the Windows AI readiness API + an OS-build
  + packaged-identity check. Do NOT ship a disabled stub in a build where it can't work.
- **`NeedsKey` = false**; no base-URL/model overrides; no sign-in. Simplest provider UI of all.
- **Isolate the WinAppSDK dependency** so it can't regress STT: consider a separate assembly / a
  `#if COPILOT` build dimension, or `PrivateAssets` on the WinAppSDK ONNX bits, so the STT
  DirectML runtime remains the single ONNX on the payload path.
- **Prefer the GA surface / Aion Instruct** once available (Nov 2026) rather than pinning to
  deprecating Phi Silica, if the API is compatible.

## Recommendation

**Defer — confirmed not soundproof (the collision spike FAILED on 2026-07-19).** The blocker is now
proven, not hypothetical: WinAppSDK's AI runtime and the STT engine both ship `onnxruntime.dll` and
collide at pack time. The clean path is: **first migrate STT onto Windows ML** (the shared ORT
host), which removes the duplicate; **then** add the on-device Copilot provider on top. Also wait
for a Copilot+ PC to test on and, ideally, for the GA API pointed at Aion Instruct rather than the
soon-removed Phi Silica. Functional fit and privacy story are excellent — this is a "when, not if,"
gated on the Windows ML migration.

## Sources
- Get started with Phi Silica — <https://learn.microsoft.com/en-us/windows/ai/apis/phi-silica>
- What are Windows AI APIs — <https://learn.microsoft.com/en-us/windows/ai/apis/>
- Copilot+ PC developer guide (NPU devices) — <https://learn.microsoft.com/en-us/windows/ai/npu-devices/>
- APPX1101 duplicate onnxruntime payload — <https://github.com/microsoft/onnxruntime/issues/14915>,
  <https://github.com/microsoft/WindowsAppSDK/issues/3639>
- Phi Silica → Aion Instruct deprecation + RTX/x64 preview (June 2026) —
  <https://windowsforum.com/threads/windows-app-sdk-2-2-2-experimental-phi-silica-local-ai-on-rtx-pcs-june-2026.426064/>
