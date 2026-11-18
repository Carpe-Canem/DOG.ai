# DOG.ai user guide

DOG.ai runs language, picture and video models on your own Windows PC. It works offline. It goes online only when you
switch that on in the Models section, and then only to Hugging Face and GitHub to download models and engines.

## Contents

1. [Install and set up](install.md): from the download to your first reply.
2. [Finding your way](finding-your-way.md): the parts of the window, the three looks, Ctrl+K, Search and Settings.
3. [General chat](general-chat.md): questions, versions of a reply, folders, pictures in a question, assistants and
   saved prompts.
4. [Characters and group chats](characters.md): character cards, lore, personas, memory, pictures in a chat, group
   chats.
5. [Stories](stories.md): adventures with choices and a branch map, written stories along an outline.
6. [Documents](documents.md): chats that answer from your own files, and cite them.
7. [Images](images.md)
8. [Video](video.md)
9. [Voice](voice.md)
10. [Models](models.md)
11. [Privacy and backups](privacy-and-backups.md)
12. [Settings, keys and when something goes wrong](settings-and-help.md)

The guide describes DOG.ai as it is in the same commit. Every picture in it was taken from the real app, with made-up
characters and chats.

## How this guide is kept

This part is for whoever changes DOG.ai.

- **Every page starts with hidden lines.** `<!-- covers: … -->` names the app's addresses the page explains, and
  `<!-- sources: … -->` names the source files it describes (globs: `**` any folders, `*` part of a name, `{a,b}`
  either). A page still to be written also has `<!-- stub -->`.
- **Which pages to update:** `pwsh tools/guide-stale.ps1` lists every page whose source files changed since the page
  was last committed, and every picture that shows a screen whose page's sources changed since the picture was last
  committed (a picture's `address`, and its `shows` in the shot list, say which pages' screens it shows), with the
  changed files, the commits that changed them, and the command that makes those pictures again. `-All` lists every
  page. When a change altered nothing a page says or shows, `-Checked <page>` writes a note that, once committed,
  marks the page current.
- **The pictures:** `pwsh tools/guide-shots.ps1 -Page <page>` (or `-Only <name>,<name>`) makes them from the list in
  `tools/guide-shots.json`, on a folder filled by `tools/GuideData` with made-up content. A picture is written only
  when its pixels changed (`-Force` writes it anyway); `pwsh tools/guide-sheets.ps1` makes contact sheets to look at. Publish first (`dotnet publish src/DogAI.App -p:PublishProfile=win-x64`): the pictures are
  taken from the published portable build.
- **The test:** `GuideTests` fails when a screen or Settings category has no page, a screen file belongs to no page, a
  picture or link is missing, a picture is not in the shot list, an id in the shot list is in no `.razor` file, or the
  guide names a place in Models that does not exist.
- **Never in a picture:** the owner's own data, folders or model names, or a path holding the Windows user name. The
  shot script refuses a picture whose page holds one of them. Look at every picture before committing it.
