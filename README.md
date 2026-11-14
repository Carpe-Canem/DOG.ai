# DOG.ai

A private Windows desktop app for running language and picture models on your own PC. It works offline: it goes online only when you switch that on in the Models section, and only to Hugging Face and GitHub to download models and engines.

## What it does

- **General chat** with local models, with folders, search, assistants and a prompt library.
- **Characters:** character cards (import and export), personas, lorebooks, memory and group chats.
- **Stories:** interactive adventures with choices, and written stories along an outline, with a branch map and exports.
- **Documents:** chat with your own PDFs, Word files and text, with citations.
- **Voice:** talk to it and have replies read aloud, with a voice per character.
- **Pictures:** character and persona portraits and story illustrations, drawn locally.
- **Locked data:** the database can be encrypted with a password, with Windows Hello to unlock day to day.
- **Portable mode:** the whole app, its data and its models can live in one folder.

## How it is built

A WPF window hosting a Blazor Hybrid UI on .NET 9. Chat models run through [llama.cpp](https://github.com/ggml-org/llama.cpp)'s `llama-server` and pictures through [stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp), both started and stopped by the app and reachable only on this PC.

| Folder | What is in it |
|---|---|
| `src/DogAI.App` | The window, start-up and Windows integration |
| `src/DogAI.UI` | Pages, components and styles |
| `src/DogAI.Core` | The app's logic, with no UI or I/O |
| `src/DogAI.Engine` | Running the model engines |
| `src/DogAI.Data` | The SQLite database |
| `src/DogAI.Hub` | The only code that goes online |
| `tests/DogAI.Tests` | Tests |

## Building

Needs Windows 10 or 11 and the .NET 9 SDK.

```
dotnet build DogAI.sln
dotnet test DogAI.sln
dotnet publish src/DogAI.App -p:PublishProfile=win-x64
```

The publish step writes a self-contained portable copy to `publish\win-x64`.

## More

- [`docs/PLAN.md`](docs/PLAN.md): the plan, what is done, and what has and has not been checked.
- [`CLAUDE.md`](CLAUDE.md): how the code is laid out and the rules it keeps to.
