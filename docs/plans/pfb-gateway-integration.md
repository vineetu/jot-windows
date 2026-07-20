# PFB AI Gateway integration — implementation notes

Implements the plan at <https://sites.simple-host.app/vineetu/jot-transcribe-windows/>
("Jot for Windows — PFB AI Gateway Integration Guide"). Adds the Sony/PlayStation **PFB AI
Gateway** as a Jot AI provider, ported from the shipping macOS `Flavor1Session`/`Flavor1Client`.

**Cannot be end-to-end verified off the Sony network** — the gateway host
`ai-gateway.dspprod.bis.sie.sony.com` is internal, and sign-in requires an interactive Okta
browser login. What *was* verified here (offline): the code compiles clean, the two
correctness-critical pieces pass a self-test (`--pfbselftest`, 23/23), and the sign-in UI
renders. The live call is verified by a colleague on the Sony network (smoke test below).

## What was added

- **`src/Jot/Services/Ai/PfbGateway.cs`** — single source of truth: prod/non-prod base URLs, the
  four model IDs, `SerializeBody(...)` (the three per-model quirks), and `ExtractContent(...)`.
- **`src/Jot/Services/Ai/PfbAuth.cs`** — sign-in lifecycle: locates/optionally downloads
  `gimme-ai-creds.exe` (`%LOCALAPPDATA%`), runs it (`--org pfb --quiet --no-clipboard`) with a
  ~125 s watchdog, captures the JWT off stdout, parses `exp`/`sub` (base64url), and stores the
  token DPAPI-encrypted **via `AiCredentials` under provider `"PFB"`** — so it flows into
  `AiConfig.ApiKey` through the existing cleanup/rewrite/ask paths with **zero call-site changes**.
- **`AiClient`** — a `pfb` branch for chat + test connection: `POST {base}/chat/completions`,
  `Authorization: Bearer <JWT>`, body from `PfbGateway`, OpenAI-style response parse. Non-streaming
  (matches the rest of the client). PFB-specific 401 ("session expired — sign in again") / 403
  ("no access") messaging. TestConnection refuses if not signed in.
- **`AiDefaults`** — PFB models + base URL. `NeedsKey("PFB")` stays false (no pasted key).
- **`SettingsViewModel` + `SettingsPage.xaml`** — "PFB" added to the provider list; a sign-in card
  replaces the API-key box: **Install sign-in helper** (CLI missing) → **Sign in to PFB** (opens
  Okta) → **Signed in as `<sub>` · expires in Xh Ym** + **Disconnect**. No auto/silent sign-in.
- **`App.xaml.cs`** — `PfbAuth` DI registration; `--pfbselftest` dev hook.

## The per-model quirks (verified by `--pfbselftest`)

Per the 2026-07-19 spec update: **no token limit** — omit `max_tokens` / `max_completion_tokens`
from every request (both families); the model runs uncapped.

| Field | GPT-5.6 (Luna / Terra) | Claude (Haiku 4.5 / Sonnet 5) |
|---|---|---|
| token limit | **omitted** | **omitted** |
| `temperature` | **omitted** | sent |
| `reasoning_effort` | `"none"` | **omitted** |

Actual bodies emitted (from the self-test):
```
gpt-5.6-luna  → {"model":"gpt-5.6-luna",...,"stream":false,"reasoning_effort":"none"}
claude-sonnet → {"model":"global.anthropic.claude-sonnet-5",...,"stream":false,"temperature":0.3}
```

## Colleague smoke test (on an in-network Sony Windows machine)

1. Install the CLI (or use the app's **Install sign-in helper** button):
   ```powershell
   Invoke-WebRequest -Uri "https://download.ai.studios.playstation.com/ai-gateway-cli/binaries/gimme-ai-creds-windows-amd64.exe" -OutFile "$env:LOCALAPPDATA\gimme-ai-creds.exe"
   ```
2. In Jot → Settings → AI → Provider = **PFB** → **Sign in to PFB** (complete Okta in the browser).
3. Pick a model, click **Test connection** — expect "Connected to PFB successfully."
4. Or the raw gateway check:
   ```powershell
   $env:TOKEN = (gimme-ai-creds.exe --org pfb --quiet --no-clipboard).Trim()
   curl.exe -X POST "https://ai-gateway.dspprod.bis.sie.sony.com/pfb/common/v1/chat/completions" `
     -H "Authorization: Bearer $env:TOKEN" -H "Content-Type: application/json" `
     -d '{\"model\":\"gpt-5.6-luna\",\"messages\":[{\"role\":\"user\",\"content\":\"Say hello in 5 words.\"}],\"reasoning_effort\":\"none\"}'
   ```
   A completion back = auth + endpoint + per-model body are all correct.

## Build flavors (Public vs Sony)

Selected at build time via `-p:Flavor=Sony` (defines the `SONY` compile symbol). Source of truth:
`src/Jot/BuildFlavor.cs`. Default is **Public**.

| | Public (default — Store) | Sony (`-p:Flavor=Sony`) |
|---|---|---|
| AI providers | None, OpenAI, Anthropic, Gemini, Ollama | **None, PFB only** |
| PFB in the UI | absent | present (sign-in card) |
| Sony/PlayStation hostnames in the binary | **stripped** (empty consts) | compiled in |
| AI InfoBar copy | "Bring your own provider" | "Powered by the PFB AI Gateway" |
| `--pfbselftest` dev hook | not compiled | compiled |

Verified (2026-07-18): both flavors build clean (0/0). A byte-search of the built `Jot.dll`
(UTF-16 + UTF-8) confirms the **Public** binary contains none of `ai-gateway…sony.com`,
`download.ai.studios`, `playstation.com`, `dspprod` (only the generic filename `gimme-ai-creds.exe`
remains). The **Sony** binary contains the gateway host and passes `--pfbselftest` 23/23.

Build the Sony MSIX with `.\build-msix.ps1` after `dotnet publish … -p:Flavor=Sony` (the script
currently builds Public; add `-p:Flavor=Sony` to its publish line for a Sony package). NOTE: the
Sony build should NOT use the public Store package identity (`Vineetsriram.JotTranscribe`) — it's
an internal/sideloaded build, so give it its own identity + distribution.

## Open questions / decisions for later

- **Streaming:** the app calls the gateway non-streaming (it reads a full reply). The SSE path (§7)
  isn't needed for cleanup/rewrite/ask; add later only if a live-token UI is wanted.
- **Model dropdown** shows the raw model IDs; could show the friendly `PfbGateway.Catalog` labels
  with a value-converter later. Functional as-is.
