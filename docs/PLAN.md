# DOG AI (Dynamic Output Generator) — Build Plan

## Status (updated 2026-11-12)

- **Done:** M0a (skeleton, offline guard), the design gate, M0b (shell and looks), M1 (engine + General chat), M2 (Models section), M3 part 1 (characters, cards, personas, continue, editable styles), M3 part 2 (lore, author's note, impersonate, memory, AI card builder, avatar cropping).
- **Next:** M3 part 3: group chat (several characters in one chat with turn-order control). Moved out of part 2 because it is the largest piece: a members table, a turn-order setting, each speaker's own system message, other characters' replies named in the history (they all share the "assistant" role), regenerate, swipe and continue keeping the same speaker, and the V3 `group_only_greetings`. `ChatMessage.SpeakerId` already exists. Then M4 (Story).
- **What M3 part 2 delivered:**
  - *Prompt in parts.* The system message is now built from parts: instruction, lore placed before the card, card and persona, lore placed after the card, example dialogue, the chat's own instructions, and the memory summary (`PromptLayout`, Core). Older turns are trimmed first. If even the newest message does not fit, example dialogue goes next, then memory, then lore; the instruction and card always stay. The card's final instruction and the author's note go *inside user turns*, never as an extra system message, because strict chat templates refuse a system message anywhere but first.
  - *Lore.* The card's embedded lorebook is used. Recent messages are searched for each entry's keywords (whole words; `/pattern/` keys are regular expressions with a 50 ms limit, since cards are untrusted). Entries can need a second keyword, be always on, match capitals exactly, or bring in other entries. The lore budget is the book's own, at most a quarter of the context. A **Lore** page (button on the character's page) edits entries; they are saved back into the card, so export gives them to other apps with unknown fields intact. **Import lore…** reads SillyTavern World Info files or another card's book. The prompt inspector lists the entries used.
  - *Author's note* per chat in the side panel, placed with the newest message or 1–8 messages back (default 4).
  - *"Write mine"* (impersonate) next to Send: the model drafts your next message into the box for you to edit. Nothing is saved.
  - *Memory.* When older messages stop fitting, they are summarised (in chunks if needed) and the summary is sent in their place. The summary runs a few messages ahead of what is trimmed so it is not rewritten after every reply. The side panel shows what it covers, lets you correct it, switch memory off, or forget it. The inspector shows when a summary was used.
  - *AI card builder.* On the new-character page, describe a character in a sentence and the loaded model fills in every field. The engine is asked for JSON matching a schema (structured output), so the answer always parses. The draft is not saved until you press Create.
  - *Avatar cropping.* After choosing a picture (characters and personas) a cropper shows the square used in lists and chats: drag it, move it with the arrow keys, size it with the slider, and see a round preview. Characters keep the full picture, so **Crop** can change it later; personas keep only the avatar, so they are cropped when the picture is chosen.
  - Database: one additive migration (new columns only). Chats from M3 part 1 get note depth 4 and memory on; a test upgrades a part 1 database.
- **How M3 part 2 behaves, decided while building it:**
  - *The summary belongs to one branch.* It records the last message it covers and is only used while that message is on the branch shown. Switching to another greeting or an earlier edit means a new summary is written when needed, replacing the old one (one summary per chat). Editing a reply's text in place does not mark the summary out of date.
  - *Bad summaries are refused.* Small models sometimes repeat the request back, loop ("User 2: User 2: …"), or run to the length limit. Such text is thrown away rather than sent with every later reply, and the next try waits a few messages. The request sits after the messages, where small models follow it best, with repetition penalties on.
  - *Impersonate writes inside an open user turn* (raw completion from where the user's turn starts in the model's own template), which makes the model speak as the user much more reliably than asking it to. If the chat already ends with an unanswered message of yours, it asks instead.
  - *Lore matches whole words* so "cat" does not fire on "category". A keyword containing a comma cannot be typed in the editor (commas separate keywords); imported ones are kept.
  - *Lore is searched in the last 4 messages* unless the book says otherwise (SillyTavern's default is 2).
- **Tried live** with two local role-play models: Self After Dark 8B (Llama 3 template) and Mag-Mell 12B (ChatML template). Lore fired and was listed, the note and final instruction landed in user turns, the card builder produced a full card, cropping saved the chosen square, and memory summarised 31 messages in a 4,096-token context. The 12B model wrote good summaries and good "Write mine" drafts. The 8B model did not: it echoed the request back or looped when summarising, and in a fresh chat its draft was a placeholder. That is what the refusal checks above guard against. **Not tried:** a model with a strict template (Mistral `[INST]`, Gemma), a reasoning model summarising, and a SillyTavern World Info file from the wild (the import is tested with a sample).
- **Left out of M3 part 2, still owed:** group chat (part 3, above); standalone lorebooks shared between characters or attached to a chat (lore lives in each card for now; World Info import copies entries into a card); the SillyTavern "character's note" (`extensions.depth_prompt`) is kept on export but not used.
- **What M3 part 1 delivered:**
  - *Characters section.* Import Character Card V1/V2/V3 from PNG or JSON; export as a PNG card (V2 and V3 inside, as other apps expect) or as V3 JSON. Fields DOG AI does not edit (extensions, the embedded lorebook, V3-only fields) are kept and written back unchanged.
  - *Card editor* with every standard field, extra greetings, a picture, a style for new chats, and a live token count (exact when a model is loaded).
  - *Personas:* who you are in a chat (name, description, picture). One is the default; each chat can switch.
  - *Character chat:* greetings are alternatives you flip through with the arrows; the last reply always shows arrows, and the right arrow past the newest writes another version (swipe). The prompt inspector shows the full card text sent.
  - *Continue* (General and Characters) carries on the last reply from where it stopped. *Styles* can now be copied and edited ("Edit styles…" under Style); the four built-in ones stay fixed.
  - The model picker labels and lists first the default model for the mode you are in.
  - `tools/walkthrough.ps1` can now fill in Windows open/save dialogs, so import and export are tested in the real app.
- **How M3 part 1 behaves, decided while building it:**
  - *Continue is hidden on a reply that has only "thinking" text* (stopped before any answer): the raw continuation cannot keep thinking apart from the answer.
  - *Continue changes the reply in place* rather than adding a version beside it: it is the same reply made longer. The raw prompt (template up to the reply, then its text so far) goes to the engine's `/completion`, so the model carries on mid-sentence.
  - *Greeting first:* a character chat's prompt starts with a short user turn, "[Start a new chat]", before the greeting, because strict chat templates (Mistral `[INST]`, Gemma) refuse a conversation that opens with a reply. Tried with real models using the ChatML and Llama 3 templates, which accept it; no model with a strict template was on this PC to try.
  - *Names in greetings are filled in when the chat starts.* Switching persona later changes what the model is told, but greetings already shown keep the old name.
  - *The card's "final instruction"* (post-history instructions) went at the end of the system message in part 1; since part 2 it goes with the newest user message.
  - *Pictures* are converted to PNG on import (so any picture can carry a card on export) and a 256-pixel avatar is cut from the top-centre square. There is no manual crop yet.
  - *No database links* from chats to characters and personas: adding them would make SQLite rebuild the chats table when upgrading. The store deletes a character's chats and clears a deleted persona itself. Upgrading an M2 database was tested.
- **Left out of M3 part 1, still owed:** everything listed under Next. Not tried live: a card with a JPEG or WebP picture (the code converts them through Windows, but only PNG cards were imported in the app).
- **What M2 delivered:** a Models section with five parts. *Recommended* shows researched picks per use case with a fit badge and the best file for this PC pre-selected. *Browse* searches Hugging Face, shows each repository's files with fit badges and the model card. *Library* lists models from the models folder and any added folders, and can load, benchmark, delete, or make one the default per mode. *Downloads* queues downloads, pauses and resumes them, checks free space first and verifies every file's SHA-256 before using it. *Engine* installs and updates DOG AI's own CUDA build of llama.cpp. The side panel shows hardware, graphics memory, the context size used for fit, and an optional Hugging Face token stored with Windows DPAPI. The status bar now shows tokens per second and graphics memory, and an unused model unloads itself after 30 minutes.
- **How M2 behaves, decided while building it:**
  - *Going online* is a button in the Models section, off at start. Leaving the section goes offline, unless a download or engine install is running; then it goes offline when that finishes. "Go offline" pauses running downloads.
  - *Fit* adds the model file, the memory for the chosen context (read from the model's header where possible), and working memory, then compares with free graphics memory. Memory held by the currently loaded model counts as free, since loading another model releases it. A partial fit gives a layer count, which loading now uses instead of "all layers".
  - *Too large* models are refused before anything is unloaded, with a message saying how much memory they need and how much is free. A benchmark's proven settings override the estimate for the same context size. If the header cannot be read, the model is loaded anyway and the engine decides.
  - *Benchmark* loads the model with the estimated layer count, steps down if loading fails, measures, and saves the layers and speed for later loads. It tunes GPU layers only; it does not change the context size.
  - *Stable engine releases*: llama.cpp now marks numbered builds as pre-releases and publishes a stable release that names one build (b12110 on 2026-11-12). DOG AI installs that build, picking the newest CUDA version the driver supports (CUDA 13.4 here) plus its runtime.
  - *Curated picks* are in `src/DogAI.Core/Models/curated-models.json` (researched 2026-11-12). There is no "refresh" button yet: the only allowed hosts are Hugging Face and GitHub, and there is no online copy of the list. Publishing the JSON in a GitHub repo would make it refreshable. Uncensored role-play fine-tunes were left out of the defaults; they can be found through Browse. Voice picks come with M6.
- **Left out of M2, still owed:**
  - Per-mode defaults are saved and the model picker labels the current mode's default (General and Characters); Story will read them in M4. Nothing loads a default automatically yet.
  - Links in model cards do not open. The confirm-then-open-in-browser prompt is still to do (planned for M7).
  - Not tried live: a gated model with a token, partial offload of a model bigger than the card, and the 30-minute idle unload. Unit tests cover the logic.
  - The download resume and checksum tests use an in-process fake server, not a real local web server as the Verification section suggests.
  - Model cards written in HTML have their tags removed (the text stays) so they read cleanly; images show as links, since remote images are blocked.
  - The real data folder still uses the winget engine until Install is clicked once in Models → Engine.
- **What M1 delivered:** the app starts and stops `llama-server` itself (found from winget for now), loads any GGUF from the models folder or picked by hand, and General mode has streaming chat with stop, regenerate, edit-and-branch, version switching, markdown with code copy buttons, collapsed "thinking", four style presets, per-chat instructions, a context meter with automatic trimming of old turns, and a prompt inspector. Chats are saved in SQLite as a message tree.
- **Left out of M1 on purpose, still owed by a later milestone:**
  - ~~"Continue" a stopped reply, and editing your own sampler presets.~~ Done in M3 part 1.
  - Syntax colouring and LaTeX in replies (needs highlight.js and KaTeX bundled). Planned with M7 polish.
  - ~~Idle auto-unload, VRAM readout and GPU-layer tuning.~~ Done in M2.
  - ~~The winget `llama-server` is a Vulkan build.~~ M2's Engine tab installs the CUDA build.
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
| M1 ✅ | Engine + General chat | Engine manager, load a GGUF from disk, streaming chat, markdown, message tree, presets, context budgeter, prompt inspector |
| M2 ✅ | Models section | Hardware probe, HF browse, fit badges, download manager, library, curated picks, benchmark, engine updater |
| M3 | Characters | Card import/export, editor, personas, lorebooks, swipes, rolling-summary memory, AI card builder, group chat (parts 1 and 2 done; group chat is part 3) |
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
