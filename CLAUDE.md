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
- **`.gitignore`:** the template's `*.app` rule was removed because it hid `src/DogAI.App`. Do not re-add it.

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

To test with a real model, set `$env:DOGAI_MODELS_DIR` to a folder holding a `.gguf` before running the script; a fresh data folder then uses it as the models folder. The Ollama blobs under `~\.ollama\models\blobs` are GGUF files and can be hard-linked there with a `.gguf` name. `dotnet test` runs the real-engine smoke test only when `$env:DOGAI_TEST_MODEL` points at a model file. Wait for conditions in `eval` steps with a promise (model loads and replies take seconds).

## How chat works

- Chat logic is `ChatService` in Core. It talks to the model only through `IChatEngine`, so tests use a fake engine with the real SQLite store.
- Messages are a tree (`MessageTree`). Regenerate and edit add a sibling and move `Conversation.ActiveLeafId`; nothing is overwritten.
- Model output goes through `MarkdownRenderer` (Core), which disables raw HTML and neutralises non-web links. Never render model text with `MarkupString` any other way.
- UI needs from Windows (file dialog, clipboard) go through `IDesktopShell`, implemented in App.

Check every new screen in all three skins, dark and light. Say plainly what was and was not looked at. Never point a test run at the real data folder (`%LOCALAPPDATA%\DOG-AI`).

## Machine

RTX 4090 (24 GB VRAM), Core Ultra 9 285K, 32 GB RAM. Models go in `D:\DOG-AI\Models`. `llama-server` (winget) and Ollama are already installed.
