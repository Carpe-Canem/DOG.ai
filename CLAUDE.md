# DOG AI

Private, almost fully offline Windows desktop app for running local LLMs. WPF host + Blazor Hybrid UI, .NET 9, maintained in VS 2022. The owner is not reading diffs line by line; explain outcomes in plain language.

**Read `docs/PLAN.md` first.** It has the full feature plan, the milestone table, and a Status section saying what is done and what is next. Update that Status section when a milestone finishes.

## Commands

```
dotnet build DogAI.sln
dotnet test DogAI.sln
dotnet ef migrations add <Name> --project src/DogAI.Data --startup-project src/DogAI.Data --output-dir Migrations
pwsh tools/walkthrough.ps1      # launch the app on a throwaway data folder and drive it (see below)
pwsh tools/make-icon.ps1        # rebuild dogai.ico after editing Assets/icon.svg
```

Target is `net9.0-windows` because VS 2022 cannot target .NET 10. Pin Microsoft packages to 9.0.x.

## Projects

| Project | Role |
|---|---|
| `src/DogAI.App` | WPF window, DI startup, WebView2 lockdown, Windows theme detection |
| `src/DogAI.UI` | Razor components, pages, `wwwroot/css/app.css` |
| `src/DogAI.Core` | Domain logic with no UI or I/O dependencies |
| `src/DogAI.Engine` | Starts/stops `llama-server` (`EngineManager`), loopback-only client, GGUF header reader |
| `src/DogAI.Data` | EF Core + SQLite |
| `src/DogAI.Hub` | The only code allowed to go online |
| `tests/DogAI.Tests` | xUnit. Cannot reference App or UI, so keep testable logic in Core |

## Rules that must hold

- **Offline.** Never construct an `HttpClient` anywhere. Inject `HubHttp` from `DogAI.Hub`; it refuses unless `OnlineGate` is on and the host is on the allow-list in `OriginPolicy`. The gate is off by default and only the Models section may turn it on. Talking to the local engine on `127.0.0.1` will need its own loopback-only client in `DogAI.Engine`, not `HubHttp`.
- **No external web resources.** No CDNs, web fonts or remote images. Bundle JS/CSS under `DogAI.UI/wwwroot`. The CSP in `src/DogAI.App/wwwroot/index.html` and the filter in `MainWindow.xaml.cs` block anything outside the app origin.
- **Untrusted text.** Imported cards, documents and model output render with raw HTML disabled.
- **`.gitignore`:** the template's `*.app` rule was removed because it hid `src/DogAI.App`. Do not re-add it. The template's `Backup*/` and `*.pubxml` rules would hide `src/DogAI.Core/Backup` and the publish profile, so they are negated at the end of the file.

## How the looks work

Three skins (`workbench` default, `fireside`, `playhouse`), each in dark and light. State is `AppearanceState` in Core; the choice is saved in settings.

- `MainLayout.razor` puts `skin-<id>`, `data-theme` and `data-screen` on the root and picks a shell component (`Layout/*Shell.razor`).
- Pages do not know which skin is active. They fill named regions with `<SectionContent SectionName="...">`: `title`, `list`, `composer`, `aside`. The page body is the main area. Empty regions collapse via `:empty`, so keep outlet wrappers free of whitespace.
- `app.css` has three parts: tokens per skin/theme, shared components that use only tokens, and per-skin layout overrides. New screens should need only shared components; add per-skin overrides sparingly.
- Fireside uses a serif (`--read`) for reading text. Playhouse has a home screen at `/` and a colour per mode; the other skins redirect `/` to `/general`.
- In `.razor` `@code`, avoid switch-expression arms that start with `<` (Razor parses them as tags).

## Verifying a change

Tests alone are not enough for UI work. Run the app and look.

`tools/walkthrough.ps1` starts the built app with a temporary data folder and a WebView2 debugging port, runs JavaScript steps, and saves screenshots you can open with the Read tool. Example:

```powershell
pwsh tools/walkthrough.ps1 -Steps @(
  @{ eval = "document.querySelector(`"a[href='settings']`").click()" },
  @{ eval = "document.querySelector(`"input[value='fireside']`").click()" },
  @{ shot = 'fireside-settings' })
```

A step `@{ dialog = '<path>'; title = '<dialog title>' }` fills in a Windows open or save dialog that the previous step opened (for card import and export). Run the script with `&` inside PowerShell, not `pwsh -File`, so the step list arrives intact.

To test with a real model, set `$env:DOGAI_MODELS_DIR` to a folder holding a `.gguf` before running the script; a fresh data folder then uses it as the models folder. The Ollama blobs under `~\.ollama\models\blobs` are GGUF files and can be hard-linked there with a `.gguf` name. `dotnet test` runs the real-engine smoke test only when `$env:DOGAI_TEST_MODEL` points at a model file. Wait for conditions in `eval` steps with a promise (model loads and replies take seconds).

## How chat works

- Chat logic is `ChatService` in Core. It talks to the model only through `IChatEngine`, so tests use a fake engine with the real SQLite store.
- Messages are a tree (`MessageTree`). Regenerate and edit add a sibling and move `Conversation.ActiveLeafId`; nothing is overwritten.
- Model output goes through `MarkdownRenderer` (Core), which disables raw HTML and neutralises non-web links. Never render model text with `MarkupString` any other way.
- UI needs from Windows (file dialog, clipboard) go through `IDesktopShell`, implemented in App.
- General and Characters share `Components/Chat/` (`ChatThread`, `ChatComposer`, `StylePanel`, `MemoryPanel`, `PromptInspector`); Story reuses the composer and the panels. Character chats run through the same `ChatService`; `CharacterPrompt.Layout` (Core) turns card, persona, lore, author's note and memory into a `PromptLayout`, and `CharacterCard` reads and writes card files. Styles come from `PresetCatalog`, not `SamplerPresets` directly.
- A prompt is a `PromptLayout`: system-message parts (each with a drop order for when space runs out) plus text placed inside user turns. Never add a `system` message after the first one; many chat templates reject it. Notes go into user turns via `PromptLayout.Arrange`.
- Lore lives in the card's `character_book` inside `Character.ExtraJson`; edit it through `Lorebook` (`For`, `SaveTo`) so unknown fields survive export. Keys from cards are untrusted: matching uses a regex timeout.
- Group chats are `Mode = "group"` conversations with members in `ChatMembers` (`IChatStore.GetMembersAsync`/`SetMembersAsync`) and each reply's writer in `ChatMessage.SpeakerId`. Who speaks is `GroupTurns` (Core); the speaker's prompt is `CharacterPrompt.GroupLayout`. In `BuildPromptAsync` group history is name-prefixed and same-role turns are merged, and the layout's `Directive` ends every group prompt with a user turn: keep roles alternating. The page is `Pages/GroupChat.razor`; side-panel settings shared with one-to-one chats are `Components/Chat/RoleplayPanels.razor`.
- DOG AI's own model jobs (summaries, card builder) set `MaxTokens` / `JsonSchema` on a copy of a preset; both are `[JsonIgnore]` so they never end up in a user's saved style. Check their output before keeping it (see `CharacterPrompt.IsUsableSummary`).

## How stories work

- A story is a `Mode = "story"` conversation plus a `Story` row (`IStoryStore`): kind (interactive or authored), bible, outline, dice and state switches. Passages are replies in the normal message tree; the user turns are the player's actions (interactive) or the author's directions (authored). Per-message story details (choices, state, dice roll, chapter) are JSON in `ChatMessage.ExtraJson`, read and written through `StoryTurn`.
- Story logic is the `ChatService.Story.cs` partial and `DogAI.Core/Stories`: `StoryPrompt` (layout, `ForPrompt`, choices schema and parsing, revision prompt), `StoryBuilder` (AI bible draft), `BranchMap`, `StoryExport`. `StoryPrompt.ForPrompt` rewrites user turns for the model (dice result, "write the next passage of chapter N"); the saved turns stay as typed.
- Interactive passages take two requests: prose first, then the same prompt plus the passage with a JSON schema for choices and state (`ChoicesAsync`). Rules the model must not forget (length, dice, state) go in `PromptLayout.FinalInstruction`, next to the newest user turn.
- The opening passage has no parent; `PromptLayout.OpeningTurn` supplies the user turn chat templates need before it.
- In UI code the name `Story` is the page class inside `Pages/Story.razor`; the pages alias the model as `StoryInfo`. Components are in `Components/Stories/`. Selecting text for a rewrite is reported by `wwwroot/js/story.js` and matched back to the markdown source with `TextSpan.Find`.

## How documents work

- Core: `DogAI.Core/Documents`. `TextExtractor` reads PDF (PdfPig), .docx (zip + XML, DTDs refused) and text/code; `Chunker` cuts passages (~800 chars, page and heading kept); `Retriever` merges vector and BM25 rankings; `DocumentLibrary` adds files, indexes in the background, searches, and recalls messages. Storage is `ICollectionStore` (`SqliteCollectionStore`).
- Every document belongs to a collection. A chat's own files are a collection with `ConversationId` set (hidden from lists, deleted with the chat); shared ones are attached through `ChatCollections`.
- The search model is `IEmbedder`, implemented by `EmbeddingServer` (Engine): a second llama-server with `--embedding`, started on demand. Its model is `DefaultModels["embedding"]`, else the first embedding model found (`GgufInfo.IsEmbedding`). Query and document wording per model family is `EmbeddingFormat`. Vectors are normalised floats tagged with the model id; never compare across models.
- In `ChatService.Documents.cs`, passages (`PromptPartKind.Documents`) and recalled messages (`Recall`) are parts that `PromptLayout` places in the newest user turn (`PromptPart.InUserTurn`), never the system message, to keep llama-server's prompt cache. What a reply was given is `ReplySources` JSON in `ChatMessage.SourcesJson`.
- Citations: `MarkdownRenderer.Render(text, citations)` turns `[n]` into `button.cite`; `chat.js` `watchCitations` passes clicks to `ChatThread.OpenCitation`. `PassageViewer` shows the passage in its document as plain text.
- `dotnet test` does not rebuild the app. Run `dotnet build DogAI.sln` before `tools/walkthrough.ps1`, or it runs old code.

## How voice works

- Core: `DogAI.Core/Voice`. Interfaces `ISpeechRecognizer`, `ISpeechSynthesizer`, `IMicrophone`, `IAudioOutput`, `IAudioDevices`; logic `ReadAloud` (reads text piece by piece, next piece synthesised while the current one plays; `Begin`/`Feed`/`End` for streaming, `queue` for group members), `ReplyVoice` (picks the voice: story narrator, group speaker, character, else default; reads `ReplyKind.Reply` only when auto-read is on), `Dictation` (push to talk and hands-free; `Paused` keeps hands-free deaf while a reply is written or read), `SentenceSplitter`, `SpeechText` (markdown → words to say), `VoiceActivity` (hands-free speech detection), `Transcript` (drops Whisper's invented text), `VoiceId` (saved as text: `windows:<name>`, `kokoro:<folder>/<speaker>`, `piper:<folder>`), `VoiceCatalog` (recommended downloads), `Wav`.
- Engine: `DogAI.Engine/Voice`. `WhisperRecognizer` (Whisper.net, Vulkan then CPU), `VoiceBox` (sherpa-onnx Kokoro/Piper plus System.Speech Windows voices), `Microphone`/`Speakers`/`AudioDevices` (NAudio WinMM), `VoiceLibrary` (finds models in `<models>/voice`; Kokoro voice names come from the .onnx metadata via `OnnxMetadata`). Keep sherpa-onnx on 1.13.x: 2.x dropped espeak-ng.
- UI: `ChatComposer` has the microphone, hands-free and read-aloud buttons; `ReadAloudButton` sits in reply actions; `VoicePicker` in the character editor and story setup; `Components/Voice/VoiceSettings` in Settings; `Components/Models/VoiceTab` in Models. A `<select>` whose options load after the first render must mark the chosen `<option selected>`, or it shows the first option.
- Walkthroughs without hardware: `$env:DOGAI_TEST_MIC` = a WAV file played as the microphone; `$env:DOGAI_TEST_SPEAKERS` = a folder that receives each spoken clip as a WAV. `dotnet test` runs the real-voice test only when `$env:DOGAI_TEST_VOICE_DIR` is a models folder with a `voice` folder.

## How polish features work (M7)

- **Assistants and prompts:** `DogAI.Core/Library`. `LibraryCatalog` keeps both lists as JSON documents in the settings store (keys `assistants`, `prompts`; a first run gets starters, and deleting them all is respected). Starting a chat from an assistant copies its instructions and style into the chat (`Components/Library/AssistantPanel`). Prompt blanks are `[[name]]` / `[[name|default]]` (`PromptVariables`), not `{{ }}`, which character cards use. The composer's book button opens `PromptPicker`.
- **Search:** `DogAI.Core/Search` (`ChatSearch` builds the FTS5 query from typed words and cuts snippets; `ChatHit`; `ChatExport`), `SqliteSearchStore` in Data. The `MessageSearch` FTS5 table lives only in the `Polish` migration's raw SQL, kept current by three triggers on `Messages`; EF does not know it. Snippets mark matches with control characters; draw them with `ChatSearch.Parts` as text, never as markup. A hit opens through `ChatService.SelectBranchAsync`, then `chat.js` `scrollToMessage` finds `[data-mid]`. Story route ids are conversation ids.
- **Backup and restore:** `DatabaseBackup` (Data) behind `IBackupService` (Core). Backup is `VACUUM INTO` plus `backup.json` in a zip. Restore only stages (`restore\dogai.db`); `App.OnStartup` calls `DatabaseBackup.ApplyPending` before anything opens the database. `IDesktopShell.Restart()` relaunches with `--after <pid>` so the new process waits for the old. New machine-specific settings go in `DatabaseBackup.MachineSettings` so a restore keeps this PC's values.
- **Code colours and maths:** `DogAI.UI/wwwroot/vendor` (highlight.js, KaTeX, with licences and checksums in `vendor/README.txt`) and `js/enhance.js`, loaded from `index.html`. `MarkdownView` writes each code block as one HTML-encoded markup piece with `data-lang`; `MarkdownRenderer` has `UseMathematics`, which marks formulas `.math`. Do not make Blazor render the inside of a block that JS colours.
- **Links:** `LinkGuard` (in `MainLayout`, inside the `.shell` div so it gets the theme) listens through `chat.js` `watchLinks` and asks before `IDesktopShell.OpenUrl`. Any new modal must also sit inside `.shell`.
- **Windows extras:** `DogAI.App/WindowsIntegration` (tray icon via WinForms `NotifyIcon`, close-to-tray, the Ctrl+Alt+Space low-level hook on its own thread; text arrives through `Dictation.HeardForOtherPrograms`). Settings: `TrayIcon`, `CloseToTray`, `DictateAnywhere`, all off by default.
- **Publish:** `dotnet publish src/DogAI.App -p:PublishProfile=win-x64` writes `publish\win-x64`. A target in `DogAI.App.csproj` drops other platforms' native files. Check a publish by running that exe: `tools/walkthrough.ps1 -Exe <path to DogAI.exe>`.
- **Portable mode:** a `portable.txt` next to DogAI.exe (shipped only in publish output, never in Debug builds) makes `AppPaths.ForProgram` put data in `<exe folder>\data` and models in `<exe folder>\models`, forced on every start. `DOGAI_DATA_DIR` still wins (test runs). Portable runs point the process's TEMP/TMP at `data\temp` (inherited by llama-server and native libraries), and `LlamaServerLocator` uses only `data\engine` (no winget/PATH fallback; `DOGAI_LLAMA_SERVER` still wins). `PortableMove` (Data) rewrites saved paths in the Settings JSON and `ModelRecords` when the folder moved (`data\home.txt` records the last home). Any new setting or table that stores a full path must be added to `PortableMove`. Walkthrough: `tools/walkthrough.ps1 -Exe <copy>\DogAI.exe -Portable`.
- **Tool quirk when writing code through the shell:** doubled backslashes can be collapsed and `\n` in Python strings can turn into real newlines; use the Edit/Write tools for C# or JS that contains escapes.

Check every new screen in all three skins, dark and light. Say plainly what was and was not looked at. Never point a test run at the real data folder (`%LOCALAPPDATA%\DOG-AI`), and never run a portable walkthrough from the owner's real portable folder; copy `publish\win-x64` somewhere temporary first.

## Machine

RTX 4090 (24 GB VRAM), Core Ultra 9 285K, 32 GB RAM. Models go in `D:\DOG-AI\Models`. `llama-server` (winget) and Ollama are already installed.
