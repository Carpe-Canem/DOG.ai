# DOG AI (Dynamic Output Generator) — Build Plan

## Status (updated 2026-11-12)

- **Done:** M0a (skeleton, offline guard), the design gate, M0b (shell and looks).
- **Next:** M1, Engine + General chat. Nothing in M1 is started.
- **Decided after this plan was first written:**
  - The app ships **three switchable looks** instead of one: Workbench (default), Fireside and Playhouse, each in light and dark, chosen in Settings. See `CLAUDE.md` for how they are built. The "Console" proposal was dropped.
  - The logo and Windows icon are the **D-Dog** mark (`src/DogAI.App/Assets/icon.svg`).
  - Every new screen is checked in all six look/theme combinations as part of its milestone.
  - Original mockups are kept for reference in `design/proposals.html` and `design/icons.html`. Model names in them are placeholders.

## Context

You want a private, almost fully offline Windows desktop app for running local LLMs, maintainable in VS 2022, with three distinct modes (General, Character chat, Story) plus one online section for browsing and downloading models with hardware-aware recommendations.

`C:\Claude\DOG-AI` is empty and not a git repo, so this is a greenfield build.

**Your machine (measured):** RTX 4090 (24 GB VRAM), Core Ultra 9 285K (24 cores), 32 GB RAM, VS 2022 Pro 17.14 with the .NET desktop workload, .NET SDK 9.0.320. `D:` is empty (931 GB free); `C:` has 184 GB free.

**Decisions you made:** WPF + Blazor Hybrid UI; app-managed `llama-server` engine; extras = chat with documents, voice in/out. Image understanding and image generation are out of scope.

**Decisions I made (say so if you disagree):**
- **Target `net9.0-windows`.** It is the newest framework VS 2022 supports. .NET 10 needs VS 2026; the code will move over with a one-line change if you upgrade.
- **Models live in `D:\DOG-AI\Models`** by default (configurable). App data (database, cards, settings) in `%LOCALAPPDATA%\DOG-AI`.
- **Two online touchpoints, not one:** Hugging Face (models, voices) and GitHub releases of llama.cpp (engine updates). Both live in the same Models section and nothing else in the app can reach the network.

## Architecture

Solution `DogAI.sln`, seven projects:

| Project | Role |
|---|---|
| `src/DogAI.App` | WPF host window, `BlazorWebView`, DI startup, tray/hotkeys, WebView2 lockdown |
| `src/DogAI.UI` | Razor class library: pages, components, CSS, bundled JS (highlight.js, KaTeX), no CDN |
| `src/DogAI.Core` | Domain models and logic with no UI or I/O dependencies: prompt builder, context budgeter, card parsing, lorebook matching, memory, story engine |
| `src/DogAI.Engine` | `llama-server` process manager, streaming client, GGUF header parser, hardware probe, fit estimator, voice engines |
| `src/DogAI.Data` | EF Core + SQLite, migrations, repositories, FTS5 search, vector storage |
| `src/DogAI.Hub` | **The only assembly allowed to open outbound connections**: Hugging Face client, download manager, engine updater |
| `tests/DogAI.Tests` | xUnit tests |

**Engine.** One `llama-server` child process per loaded model, bound to `127.0.0.1` on a random port, plus a small second instance for embeddings. The app talks to it over its HTTP API: streaming chat completions, `/tokenize` for exact token counts, `/apply-template` + `/completion` for continue/prefill, and JSON-schema-constrained output for structured results. Chat templates come from the GGUF itself. Idle auto-unload frees VRAM. On first run the app can adopt the `llama-server` you already have from winget until its own copy is downloaded.

**Offline guarantee (enforced, not just promised).**
- All `HttpClient` use sits behind one gateway in `DogAI.Hub`, gated by an "Online" switch that is off by default and only turns on inside the Models section.
- WebView2 is restricted to the app's own origin; any other request is blocked and logged. Links open in your browser only after a confirmation.
- Imported card and document text is treated as untrusted: markdown renders with raw HTML disabled.
- No telemetry, no update checks outside the Models section.

**Data.** SQLite. Messages are stored as a tree (each has a parent), which gives regenerate, swipes, edit-and-branch, and CYOA branching from one mechanism. Embeddings are stored as blobs with in-memory cosine search, which is plenty for single-user scale.

## Features

### Shared across all modes
- Streaming replies with stop, regenerate, continue, edit any message, branch from any point
- Sampler presets (temperature, min-p, top-p/k, DRY, XTC, repetition penalty), per mode and per character
- **Context budgeter**: exact token counting with priority trimming (system > card > lore > memory > recent turns) and a live context meter
- **Prompt inspector**: view the exact text sent to the model for any reply
- Collapsed display of reasoning ("thinking") output for reasoning models
- Tokens/sec and VRAM readout; quick model switcher in the title bar
- Full-text search across all chats and stories; backup/restore to a single archive; dark and light themes

### 1. General mode
- Assistants: reusable system-prompt profiles (coder, editor, tutor, etc.) with their own model and preset
- Markdown, syntax-highlighted code with copy button, LaTeX, tables
- Folders, pinning, auto-titling, export to Markdown
- Prompt library with fill-in variables
- **Chat with documents:** drop PDF, DOCX, text, markdown, or code files into a chat or a reusable "collection". Local embedding model finds relevant passages; answers cite the source passage, which opens on click.

### 2. Character mode
- **Import** Character Card V2 and V3 from PNG (embedded metadata) and JSON, including embedded lorebooks and alternate greetings. **Export** back to PNG/JSON.
- **Card editor** with all standard fields, avatar cropping, tags, and a live token count
- **AI card builder**: describe a character in a sentence and the loaded model drafts the full card for you to edit
- User personas (who *you* are in the chat), switchable per chat
- Lorebooks / world info: keyword-triggered entries with a token budget, attachable to characters or chats
- Swipes on every reply, alternate greetings, author's note, impersonate (model drafts your turn)
- **Long-term memory**: rolling summary of older turns plus vector recall of relevant past messages, so long chats stay coherent past the context window
- Group chats with multiple characters and turn-order control
- Per-character voice for read-aloud

### 3. Story mode
- **Story Bible**: premise, genre, tone, POV, tense, cast, world facts, style guide. Always in context and editable mid-story. Can be AI-drafted from a one-line idea.
- **Authored stories**: outline → chapters → scene beats; "write next" with optional steering; select text to rewrite, expand, shorten, or change tone
- **Interactive (CYOA)**:
  - Narrator turns end in 2–4 choices produced by schema-constrained output, so choices are always well-formed, plus a free-text "do something else"
  - Optional tracked state (inventory, stats, location, flags) the model updates each turn and you can edit
  - Optional dice checks for outcomes
  - **Branch map**: a visual tree of every path taken; click any node to rewind or explore an alternative
- Hierarchical summaries (scene → chapter → story so far) to keep long stories consistent
- Lorebooks and document collections usable as world reference
- Narrator read-aloud
- Export to Markdown, HTML, and EPUB

### 4. Models section (the online part)
- **Hardware profile**: GPU, VRAM (total and currently free), RAM, CPU, disk space per drive
- **Browse and search** Hugging Face GGUF repos with sort by trending/downloads/recent, model card view, and per-file listing
- **Fit badge on every file**, computed from file size, layer count, and the context length you want:
  - Fits fully on GPU (fast)
  - Partial offload (slower, with an estimate)
  - Too large
- Per repo, the best quantization for your hardware is pre-selected
- **Curated picks** per use case (General, Roleplay, Story, Embedding, Voice) for your VRAM tier. Shipped as a JSON file, refreshable when online. I'll research the current model landscape when building this list rather than rely on memory.
- **Download manager**: queue, pause/resume, SHA-256 verification, free-space check, optional Hugging Face token for gated models (stored encrypted with Windows DPAPI)
- **Library**: installed models with metadata read from the GGUF, add existing GGUF folders, delete, set defaults per mode
- **Benchmark** after download: measures real tokens/sec and auto-tunes GPU layers and context size
- **Engine updater**: installs/updates the CUDA build of llama.cpp

### Voice
- **Speech-to-text**: Whisper via `Whisper.net`, push-to-talk and hands-free
- **Text-to-speech**: neural voices (Piper/Kokoro) via `sherpa-onnx`, spoken sentence by sentence as the reply streams; Windows built-in voices as a zero-download fallback
- Audio via `NAudio`. Voice models download through the Models section.

## Design gate (completed; outcome is in Status above)

No styling, navigation, or page layout gets built until you have picked a look. This happens in the middle of M0, right after the bare shell proves the WPF + Blazor pipeline runs.

- I produce **four distinct design directions** as clickable HTML mockups. Because the app's UI is HTML/CSS, the chosen mockup carries straight over into the real app.
- Each direction differs in both **aesthetic** (palette, typography, density, how much "DOG" personality the branding has) and **layout** (for example: left icon rail with mode workspaces, top tabs, a three-pane layout with a persistent context panel, a home-screen launcher that opens each mode full-window).
- Each mockup shows the same four screens so they compare fairly: General chat, Character chat, Story (CYOA turn with choices and branch map), and the Models browser with fit badges. Dark and light variants of each.
- You pick one, or mix parts ("layout of B, colours of D"). I do one revision round on the pick.
- The result is frozen as a set of design tokens (CSS variables) and a shell layout in `DogAI.UI`, which every later milestone builds on. Later screens that the mockups did not cover get a quick mockup for your approval before they are built.

## Milestones

Each ends with a runnable app, passing tests, and a git commit.

| # | Milestone | Delivers |
|---|---|---|
| M0a ✅ | Skeleton | `git init`, solution, bare WPF + Blazor shell, settings, SQLite, logging, offline lockdown. No styling. |
| ✅ | **Design gate** | Four design proposals → three kept as switchable looks; D-Dog icon chosen |
| M0b ✅ | Shell | Navigation, three looks with light/dark, Settings screen, placeholder mode pages, app icon |
| M1 | Engine + General chat | Engine manager, load a GGUF from disk, streaming chat, markdown, message tree, presets, context budgeter, prompt inspector |
| M2 | Models section | Hardware probe, HF browse, fit badges, download manager, library, curated picks, benchmark, engine updater |
| M3 | Characters | Card import/export, editor, personas, lorebooks, swipes, rolling-summary memory, AI card builder, group chat |
| M4 | Story | Story Bible, authored mode, CYOA with structured choices and state, branch map, exports |
| M5 | Documents | Embedding server, ingestion, collections, citations; vector recall added to character and story memory |
| M6 | Voice | Speech-to-text, text-to-speech, per-character voices |
| M7 | Polish | Assistants, prompt library, search, backup/restore, self-contained publish profile |

Possible later additions, not planned: app lock / encrypted database, local OpenAI-compatible API for other tools, image understanding, image generation.

## Verification

- `dotnet build` and `dotnet test` clean at every milestone; solution opens and F5-runs in VS 2022.
- **Unit tests:** card PNG/JSON round-trip (V2 and V3), GGUF header parser, fit estimator, prompt builder and context budgeter, lorebook matching, message-tree branching, CYOA schema parsing, download resume and hash check against a local test server.
- **Engine smoke test:** load a small GGUF, stream a reply, confirm GPU offload and VRAM release on unload.
- **Offline test:** with the Online switch off, exercise all three modes and confirm the only sockets opened are loopback; then confirm with the network adapter disabled that everything outside the Models section works.
- **Manual walkthrough per milestone** in the running app (I'll drive it and report what I could and could not verify).

## Model note

Fable is worth keeping for M0–M1, where the architecture gets set. From M2 onward most of the work is routine implementation against that architecture, and Sonnet 5.5 or Opus 5.5 would handle it at lower usage; I'll flag any milestone where the harder reasoning is worth switching back for (M4's story engine is the likely one).
