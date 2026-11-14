# DOG.ai

Private, almost fully offline Windows desktop app for running local LLMs. WPF host + Blazor Hybrid UI, .NET 9, maintained in VS 2022. The owner is not reading diffs line by line; explain outcomes in plain language.

**Read `docs/PLAN.md` first.** It has the full feature plan, the milestone table, and a Status section saying what is done and what is next. Update that Status section when a milestone finishes.

## Commands

```
dotnet build DogAI.sln
dotnet test DogAI.sln
dotnet ef migrations add <Name> --project src/DogAI.Data --startup-project src/DogAI.Data --output-dir Migrations
pwsh tools/walkthrough.ps1      # launch the app on a throwaway data folder and drive it (see below)
pwsh tools/make-icon.ps1        # rebuild DOG.ai.ico after editing Assets/icon.svg
```

Target is .NET 9 because VS 2022 cannot target .NET 10 (the app itself is `net9.0-windows10.0.19041.0`, which adds Windows Hello; the Debug exe is under `bin\Debug\net9.0-windows10.0.19041.0`). Pin Microsoft packages to 9.0.x. One exception, decided by the owner: `Microsoft.Data.Sqlite.Core` is 10.0.x in `DogAI.Data`, because 9.0.x cannot reopen an encrypted database (see "How locked data works").

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

## Names

The brand is **DOG.ai** (never "DOG AI"). Code names stay `DogAI.*` (namespaces, project folders); the programs are `DOG.ai.exe` (app and launcher). On disk: data folder `%LOCALAPPDATA%\DOG.ai`, database `DOG.ai.db`, logs `DOG.ai-<date>.log`. Old names are migrated at start (`AppPaths.AdoptLegacyFolder`, `RenameLegacyDatabase`). Do not rename: `DOGAI_*` environment variables, the backup label `"DOG AI"` and the `dogai.db` entry inside backup zips (old backups must restore), the Hugging Face token key `"DOG-AI/hf-token"`, `dogai-engine.txt`, `dogai-image-engine.txt`. The picture engine lives in `engine\images`; a llama.cpp update carries that folder across.

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

To test with a real model, set `$env:DOGAI_MODELS_DIR` to a folder holding a `.gguf` before running the script; a fresh data folder then uses it as the models folder. The Ollama blobs under `~\.ollama\models\blobs` are GGUF files and can be hard-linked there with a `.gguf` name. `dotnet test` runs the real-engine smoke test only when `$env:DOGAI_TEST_MODEL` points at a model file. The picture engine's smoke test runs only when `$env:DOGAI_SD_SERVER` points at sd-server.exe and `$env:DOGAI_TEST_IMAGE_MODEL` at an SDXL checkpoint (`$env:DOGAI_TEST_IMAGE_LORAS` optional); check graphics memory is free first. With all three set, `CardSharingSmokeTests` also runs a real chat model and real pictures on the same card. To draw in a walkthrough, set `DOGAI_SD_SERVER` and `DOGAI_MODELS_DIR` to a throwaway folder whose `image\checkpoints` and `image\loras` hold hard links to the sample files (same drive; remove the links afterwards). Models → Images then has `#draw-with` (checkpoint), `#test-lora`, `#test-picture` (`#test-picture-cancel` while drawing); the result is `#test-picture-result`, the note `#test-picture-note`. Wait for conditions in `eval` steps with a promise (model loads and replies take seconds).

## How chat works

- Chat logic is `ChatService` in Core. It talks to the model only through `IChatEngine`, so tests use a fake engine with the real SQLite store.
- Messages are a tree (`MessageTree`). Regenerate and edit add a sibling and move `Conversation.ActiveLeafId`; nothing is overwritten.
- Model output goes through `MarkdownRenderer` (Core), which disables raw HTML and neutralises non-web links. Never render model text with `MarkupString` any other way.
- Speech colours: `MarkdownRenderer.Render(..., speech: true)` wraps quoted lines in `span.say` and marks actions `em.act` (character chats, group chats and stories; never General). Colours are the `--say`, `--act` and `--say-1…6` tokens in `app.css`; a group member's colour is the `tone-N` class from `SpeakerInfo.Tone`. The switch is `AppearanceState.SpeechColours` (Settings, Reading).
- UI needs from Windows (file dialog, clipboard) go through `IDesktopShell`, implemented in App.
- General and Characters share `Components/Chat/` (`ChatThread`, `ChatComposer`, `StylePanel`, `MemoryPanel`, `PromptInspector`); Story reuses the composer and the panels. Character chats run through the same `ChatService`; `CharacterPrompt.Layout` (Core) turns card, persona, lore, author's note and memory into a `PromptLayout`, and `CharacterCard` reads and writes card files. Styles come from `PresetCatalog`, not `SamplerPresets` directly.
- A prompt is a `PromptLayout`: system-message parts (each with a drop order for when space runs out) plus text placed inside user turns. Never add a `system` message after the first one; many chat templates reject it. Notes go into user turns via `PromptLayout.Arrange`.
- Lore lives in the card's `character_book` inside `Character.ExtraJson`; edit it through `Lorebook` (`For`, `SaveTo`) so unknown fields survive export. Keys from cards are untrusted: matching uses a regex timeout.
- Group chats are `Mode = "group"` conversations with members in `ChatMembers` (`IChatStore.GetMembersAsync`/`SetMembersAsync`) and each reply's writer in `ChatMessage.SpeakerId`. Who speaks is `GroupTurns` (Core); the speaker's prompt is `CharacterPrompt.GroupLayout`. In `BuildPromptAsync` group history is name-prefixed and same-role turns are merged, and the layout's `Directive` ends every group prompt with a user turn: keep roles alternating. The page is `Pages/GroupChat.razor`; side-panel settings shared with one-to-one chats are `Components/Chat/RoleplayPanels.razor`.
- DOG.ai's own model jobs (summaries, card builder) set `MaxTokens` / `JsonSchema` on a copy of a preset; both are `[JsonIgnore]` so they never end up in a user's saved style. Check their output before keeping it (see `CharacterPrompt.IsUsableSummary`).

## How stories work

- A story is a `Mode = "story"` conversation plus a `Story` row (`IStoryStore`): kind (interactive or authored), bible, outline, dice and state switches. Passages are replies in the normal message tree; the user turns are the player's actions (interactive) or the author's directions (authored). Per-message story details (choices, state, dice roll, chapter) are JSON in `ChatMessage.ExtraJson`, read and written through `StoryTurn`.
- Story logic is the `ChatService.Story.cs` partial and `DogAI.Core/Stories`: `StoryPrompt` (layout, `ForPrompt`, choices schema and parsing, revision prompt), `StoryBuilder` (AI bible draft), `BranchMap`, `StoryExport`. `StoryPrompt.ForPrompt` rewrites user turns for the model (dice result, "write the next passage of chapter N"); the saved turns stay as typed.
- Interactive passages take two requests: prose first, then the same prompt plus the passage with a JSON schema for choices and state (`ChoicesAsync`). Rules the model must not forget (length, dice, state) go in `PromptLayout.FinalInstruction`, next to the newest user turn.
- Character cards in a story are links, not copies: `Story.Characters` (a JSON list of `StoryCharacter`: card id plus `Main`, the card the player plays). `ChatService.StoryCastAsync` reads the cards for every passage and `StoryPrompt.Layout` adds them as a `Cast` part, their examples and the lore that fired (`StoryCast.cs`). A card's own system prompt and post-history instruction are not used in stories. `SqliteCharacterStore.DeleteAsync` takes a deleted card out of stories. `story/new?character=<id>` (the **Start a story** button) preloads a card; `StartStoryAsync(..., opening)` saves a card's greeting as the first passage of an adventure.
- The opening passage has no parent; `PromptLayout.OpeningTurn` supplies the user turn chat templates need before it.
- Per-line speakers: `Quotations` (Core/Text) finds quotations with the same rules for rendered HTML and plain text; `SpeakerGuess` pins each one to a person or to nobody (it reads a passage paragraph by paragraph, in order, and resolves "she said" through each card's `Pronoun`; one instance per passage). The people are `StoryPrompt.People`: the cards in cast order, then names from the bible's Cast box with no colour or voice. `MarkdownRenderer.Render(..., speakers)` adds `tone-N` to a pinned `span.say`; `SpeakerTones.Of` gives a character's tone (stories and group chats). Reading aloud takes a `VoiceCast` (`ReplyVoice.CastForAsync`); `VoicedPieces` cuts a passage into lines per voice. Change quote rules only in `Quotations`, so colour and voice keep agreeing (a test checks).
- Story requests (passages, choices, rewrites, a story's summaries, the bible draft) go through `StoryOptions.Apply`, which sets `SamplerPreset.Thinking = false` unless Settings allows thinking in stories; `LlamaApi.ChatRequestBody` turns that into `chat_template_kwargs`. New story requests must use it too.
- A character chat becomes an adventure through `ChatService.StoryFromChatAsync` (copies `MessagesForStory` of the shown branch and the summary; the chat is not touched). The page is `story/new?chat=<id>`.
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

## How pictures share the graphics card (M9 step 3)

- `IImageEngine` from the app's services is `CardCoordinator` (Core/Images) wrapped around `ImageServer` (Engine/Images). Draw only through it; only the engine's own screens (install, update) use `ImageServer`.
- Per picture it estimates the need (`PictureMemory`), measures the card (`ICardMeter`, implemented by `CardMeter`), and either keeps both models or sets the chat model aside: `EngineManager.RestAsync` gives `EngineState.Resting`, where the program is gone but model, context size and settings are remembered. `IsReady` is true while resting (chat pages keep working) and the next request loads the model again (`BeginUseAsync`); `IsLoaded` is true only when it is in memory. Use `IsLoaded` for passive things that must not bring it back (token counts while typing).
- Setting aside is not the owner's unload: never route it through `ModelLauncher.UnloadByUserAsync`, and never load a chat model from the coordinator.
- One thing at a time changes the card. Every chat model load takes a turn first and keeps it until the load ends: `ModelLauncher.LoadAsync` and `ModelBenchmark` do (`EngineManager.TakeCardTurnAsync`); raw `EngineManager.LoadAsync` does not, so do not call it from new code without a turn. A picture waits for a request in progress, so never draw from inside a streaming request: finish the chat model's job first.
- A waiting picture reports the stages `CardCoordinator.Waiting` and `MakingRoom`. The coordinator's `Noted` lines go to the log ("Graphics card: …") and never contain a prompt.
- The picture model is let go after `ImageServer.IdleStop` (2 minutes), or at once when the chat model needs the room. Drawing counts as busy for the idle lock through `IImageEngine.IsBusy` in `AppLock`.

## How image models work (M9 step 4)

- Folder: `AppSettings.ImageFolder()` = `<models>\image` with `checkpoints`, `loras`, `vae`, `upscalers` (`ImageFolders`, Core), created only when needed (`ImageLibrary.Ensure`, Engine: on Open folder, a download, a picture). The names match sd-server's own layout.
- Recognition: `SafetensorsHeader` and `ImageFileRecognizer` (Core, pure) read tensor names and a few metadata fields; `ImageLibrary` (Engine) opens files read-only, reads only the header and remembers results by size and date. `.ckpt`, `.pt`, `.pth`, `.bin` are never opened (refused). Family is guessed from whole words in names (`Words`), never substrings; metadata is untrusted and never shown wholesale (it can hold the maker's own file paths).
- Settings: `ImageModelCatalog` (Core) is one JSON document (key `image-models`) in the settings store. Files are named by their path inside their own folder, never a full path, so `PortableMove` needs nothing new. A card is saved only when the owner changes it; untouched ones follow `CheckpointSettings.ForFamily`. `ImagePrompt.Compose` turns a card plus LoRAs into an `ImageRequest`; `ImageFolders.ModelFor` gives the `ImageModel`.
- UI: `Components/Models/ImagesTab.razor` (+ `CheckpointCard`, `PictureEngineBox`). The picture engine's install and update live only there; the Engine tab links to it. Image downloads carry `ImageDownloads.UseCase` and never become chat-model records.

## How kept pictures work (M9 step 5)

- A kept picture is a row in the `Pictures` table: `Picture` and `IPictureStore` (Core/Images), `SqlitePictureStore` (Data). `IPictureStore.AddAsync` is the only way a picture reaches the disk: pictures that are tried and not kept stay in memory. Never write a picture, thumbnail or preview to a file or cache; show them from memory (`data:` address).
- Draw anything that may be kept with `request.ForKeeping()` (WebP at `PictureFormat.Quality`, 90, chosen by eye; the engine encodes it). `Picture.Of(...)` makes the row from a request and its result; `ToRequest()` and `ModelIn(imageRoot)` draw it again (same seed and settings give the same bytes).
- The owner is `OwnerKind` + `OwnerId` (`PictureOwners`: character, persona, message, test), with no foreign key. Triggers in the `Pictures` migration delete a message's, character's or persona's pictures with it; a new kind with its own table needs a trigger in a new migration.
- Files are named by their path inside their own folder; the store refuses full paths and anything that is not WebP (`WebpInfo`). Never add a column holding a full path, so `PortableMove` keeps needing nothing.
- Log: only `ImageServer.Logged` is written to the log (`picture engine: …`). It passes through `PictureLogFilter` (Core): status lines only, and none sharing a word with a prompt sent in the last ten minutes. `ImageServer.Output` is everything the engine prints and must never be logged. Anything new that sends text to the picture engine must tell the filter first (`Sending`).
- For checks: `$env:DOGAI_TEST_PICTURE_WORDS` adds its words to the test picture's prompt and shows `#keep-picture`, `#keep-note` and `#kept-pictures` (rows `[data-picture]` with `[data-act=again]` and `[data-act=remove]`) under the test picture.

## How locked data works (M8)

- **SQLite build:** `SQLite3MC.PCLRaw.bundle` (SQLite3 Multiple Ciphers, ChaCha20-Poly1305) with `Microsoft.EntityFrameworkCore.Sqlite.Core`; never add the plain `Microsoft.EntityFrameworkCore.Sqlite` or `Microsoft.Data.Sqlite` package (it would bring a second, plain SQLite). A plain database works on it exactly as before.
- **Every connection goes through `DatabaseLock.ConnectionString(file, key)`** (Data). The key is 32 bytes, given as `raw:<hex>` in `Password`; `null` means a plain database. Never write a connection string by hand. The running app's key is the `DatabaseKey` singleton (Core/Privacy; `Bytes` is null when the lock is off), registered by `App.BuildServices`; `AddDogData` and `SqliteBackupService` read it. Static helpers (`DatabaseBackup`, `PortableMove`) take the key as an optional last argument.
- **Core** (`DogAI.Core/Privacy`): `LockFile` (`lock.json` beside the database: the database key wrapped by password and by recovery key with PBKDF2-SHA256 600,000 rounds + AES-GCM, and per PC by a Windows Hello signature through HKDF; a `Check` value lets code confirm it holds the right key before re-wrapping; `WithoutHello()` for backups; `Pending`, `Skin`, `Theme`), `RecoveryKey`, `UnlockThrottle` (count kept in `unlock-tries.json`), `Unlocker` (what the lock screen does), `IHelloSigner` (implemented by `WindowsHello` in App; tests use `PrivacyTests.FakeHello`). Tests pass a low round count to `LockFile.Create`.
- **Encrypt and decrypt** (`DatabaseLock.Encrypt` / `Decrypt`): a `VACUUM INTO` copy under the new key, compared with the original (integrity check, schema, row count of every table), swapped in, WAL mode put back. Not `PRAGMA rekey` (it refuses WAL databases). They run **only at start**, from `DataLock.Settle`, because nothing else may have the database open. `DataLock.Look` tells the state from the files alone: `Off`, `Locked`, `TurningOn` (lock file present, database still plain: it is encrypted when the password is entered at the lock screen), `TurningOff` (`LockFile.Pending`). So turning the lock on from a future Settings screen means: write `lock.json`, restart. `DatabaseLock.Recover` tidies a conversion that was cut short.
- **Start-up order** in `App.OnStartup`: `DatabaseBackup.ApplyPending` (moves files only, needs no key) → `PortableMove.MovedFrom` → `Unlock` (`LockWindow`, a plain WPF window; nothing else exists yet: no services, tray, global key or model) → `DatabaseBackup.FinishRestore` and `PortableMove.Apply` with the key → `BuildServices`. Anything new that opens the database at start goes after the unlock. `Application.MainWindow` is set by hand because the lock screen is the first window WPF sees.
- **Backups:** a locked database gives an encrypted `dogai.db` plus `lock.json` (no Hello entries) and a manifest with `Locked = true` and no counts (format 2; plain backups stay format 1). `StageAsync` takes this DOG.ai's key and the backup's password or recovery key (`BackupLockedException` when missing or wrong). What is staged for a locked DOG.ai is always under its own key; a locked backup staged for a plain DOG.ai brings its lock file along.
- **Settings → Privacy** is `Components/Library/PrivacySettings.razor` over `PrivacyLock` (Core/Privacy). Every change re-reads `lock.json` first (the lock screen may have changed it). Turning on writes `lock.json` only after the recovery-key check (`RecoveryCheck`); a new recovery key is shown and checked before `UseRecoveryKeyAsync`; turning off sets `Pending` and restarts. Password rules: `LockPassword`. The lock screen's look and the tray setting are copied into `lock.json` (`LockLook`) by `AppLock`.
- **Lock screen look:** `LockWindow` + `LockStyles.xaml` + `LockPalette` (App/LockLooks.cs). Its colours are copied from the tokens in `app.css`; change both together.
- **Locking:** `AppLock` (App, behind `IAppLock`): Lock now, the idle lock (`IdleLock` in Core: PC-wide idle from `GetLastInputInfo`, never earlier than the unlock, postponed while busy for at most one more period; `DOGAI_IDLE_LOCK_SECONDS` shortens it for tests), and Ctrl+L (`LockKey.razor` with `chat.js` `watchLockKey` in the page, and `MainWindow.PreviewKeyDown` for when the page has no focus). The recovery key prints alone through `chat.js` `printOnly` and the `print-one` rules in `app.css`. Locking is `WpfDesktopShell.RestartProcess(hidden)`; it sets `WpfDesktopShell.Exiting` so close-to-tray never keeps the old process. `--hidden` starts the lock screen in the tray. While locked, `App.LockedTray` shows a tray icon with Quit only.
- llama-server never inherits `LLAMA_ARG_*` / `LLAMA_LOG_*` settings (`EngineEnvironment.Clean`); a new engine process must call it too.
- Exports through `IDesktopShell.SaveFileAsync` show a one-time note while locked (`AppSettings.PlainExportNoted`).
- Never log a password, recovery key, key or connection string.
- Walkthrough: `tools/walkthrough.ps1 -LockSteps @(@{ set = @{ Password = '...' } }, @{ click = 'Unlock' }) -Steps ...` drives the lock screen by its controls' automation ids (see the script's header); `-LockOnly` for runs that never unlock; `@{ gone = 90 }` waits for the lock screen to close (Windows Hello needs a person at the PC). After a lock or restore restart, `@{ adopt = 30 }` follows the new process and `@{ lock = @(...) }` drives its lock screen; `@{ tray = 'Lock now' }`, `@{ globalkey = 1500 }`, `@{ press = 'ctrl+l' }`, `@{ close = 1 }` and `@{ log = 'text' }` exist too. Give each run its own `-Port` if an earlier one may still be running.

## How polish features work (M7)

- **Assistants and prompts:** `DogAI.Core/Library`. `LibraryCatalog` keeps both lists as JSON documents in the settings store (keys `assistants`, `prompts`; a first run gets starters, and deleting them all is respected). Starting a chat from an assistant copies its instructions and style into the chat (`Components/Library/AssistantPanel`). Prompt blanks are `[[name]]` / `[[name|default]]` (`PromptVariables`), not `{{ }}`, which character cards use. The composer's book button opens `PromptPicker`.
- **Search:** `DogAI.Core/Search` (`ChatSearch` builds the FTS5 query from typed words and cuts snippets; `ChatHit`; `ChatExport`), `SqliteSearchStore` in Data. The `MessageSearch` FTS5 table lives only in the `Polish` migration's raw SQL, kept current by three triggers on `Messages`; EF does not know it. Snippets mark matches with control characters; draw them with `ChatSearch.Parts` as text, never as markup. A hit opens through `ChatService.SelectBranchAsync`, then `chat.js` `scrollToMessage` finds `[data-mid]`. Story route ids are conversation ids.
- **Chat folders (General only):** `ChatFolder` and `ChatFolders` (Core/Chat: `CleanName`, `Named`, `Arrange` sorts the list into pinned, folders, loose). `Conversation.FolderId` has no foreign key (avoids a table rebuild); `IChatStore.DeleteFolderAsync` clears it. `UpdateAsync` never touches the folder; use `SetFolderAsync`.
- **Backup and restore:** `DatabaseBackup` (Data) behind `IBackupService` (Core). Backup is `VACUUM INTO` plus `backup.json` in a zip. Restore only stages (`restore\dogai.db`); `App.OnStartup` calls `DatabaseBackup.ApplyPending` before anything opens the database. `IDesktopShell.Restart()` relaunches with `--after <pid>` so the new process waits for the old. New machine-specific settings go in `DatabaseBackup.MachineSettings` so a restore keeps this PC's values.
- **Code colours and maths:** `DogAI.UI/wwwroot/vendor` (highlight.js, KaTeX, with licences and checksums in `vendor/README.txt`) and `js/enhance.js`, loaded from `index.html`. `MarkdownView` writes each code block as one HTML-encoded markup piece with `data-lang`; `MarkdownRenderer` has `UseMathematics`, which marks formulas `.math`. Do not make Blazor render the inside of a block that JS colours.
- **Links:** `LinkGuard` (in `MainLayout`, inside the `.shell` div so it gets the theme) listens through `chat.js` `watchLinks` and asks before `IDesktopShell.OpenUrl`. Any new modal must also sit inside `.shell`.
- **Windows extras:** `DogAI.App/WindowsIntegration` (tray icon via WinForms `NotifyIcon`, close-to-tray, the Ctrl+Alt+Space low-level hook on its own thread; text arrives through `Dictation.HeardForOtherPrograms`). Settings: `TrayIcon`, `CloseToTray`, `DictateAnywhere`, all off by default.
- **Publish:** `dotnet publish src/DogAI.App -p:PublishProfile=win-x64` writes `publish\win-x64`: a small launcher `DOG.ai.exe` (project `DogAI.Launcher`, only starts `app\DOG.ai.exe`), `portable.txt`, and `app\` with every program file. Targets in `DogAI.App.csproj` drop other platforms' native files, empty `app\` before publishing (only a folder named `app`, never the root, so `data\` and `models\` are safe), and publish the launcher and marker into the root. Check a publish by running the exe in `app\` (not the launcher, which exits at once): `tools/walkthrough.ps1 -Exe <path to app\DOG.ai.exe> -Portable`. `tools/tidy-install.ps1` converts an old flat install (program files loose beside `data` and `Models`) to this layout.
- **Portable mode:** a `portable.txt` next to DOG.ai.exe, or one level above it when the exe sits in a folder named `app` (the publish layout; never in Debug builds), makes `AppPaths.ForProgram` put data in `<home>\data` and models in `<home>\models`, forced on every start. `DOGAI_DATA_DIR` still wins (test runs). Portable runs point the process's TEMP/TMP at `data\temp` (inherited by llama-server and native libraries), and `LlamaServerLocator` uses only `data\engine` (no winget/PATH fallback; `DOGAI_LLAMA_SERVER` still wins). `PortableMove` (Data) rewrites saved paths in the Settings JSON and `ModelRecords` when the folder moved (`data\home.txt` records the last home). Any new setting or table that stores a full path must be added to `PortableMove`. Walkthrough: `tools/walkthrough.ps1 -Exe <copy>\app\DOG.ai.exe -Portable`.
- **Tool quirk when writing code through the shell:** doubled backslashes can be collapsed and `\n` in Python strings can turn into real newlines; use the Edit/Write tools for C# or JS that contains escapes.

Check every new screen in all three skins, dark and light. Say plainly what was and was not looked at. Never point a test run at the real data folder (`%LOCALAPPDATA%\DOG.ai`), and never run a portable walkthrough from the owner's real portable folder; copy `publish\win-x64` somewhere temporary first.

## Machine

RTX 4090 (24 GB VRAM), Core Ultra 9 285K, 32 GB RAM. Models go in `D:\DOG.ai\Models`. `llama-server` (winget) and Ollama are already installed.
