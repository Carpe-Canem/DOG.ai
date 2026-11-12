# DOG AI (Dynamic Output Generator) — Build Plan

## Status (updated 2026-11-12)

- **Done:** M0a (skeleton, offline guard), the design gate, M0b (shell and looks), M1 (engine + General chat), M2 (Models section), M3 (characters, lore, memory, group chat), M4 (Story), M5 (Documents).
- **Next:** M6 (Voice).
- **What M5 delivered (Documents):**
  - *Reading files:* PDF (its text layer, page by page), Word .docx (headings, lists and tables kept), markdown, text and code files. The text is copied into DOG AI, so the original can move. A scanned PDF with no text, an old .doc, a password-protected file or a binary file is refused with a plain reason. Each file is cut into passages of about 800 characters at paragraph and line boundaries, overlapping a little, each knowing its page and heading.
  - *Document search model:* a second, small llama-server runs the embedding model beside the chat model (about 1 GB of graphics memory for Qwen3 Embedding 0.6B). It starts when first needed and stops after ten idle minutes. It is the library model marked **Default for… → Document search**; without a choice, the first embedding model found. Embedding models are recognised from their header, are not offered for chat, and have no Load or Benchmark buttons. Changing the model indexes every document again in the background.
  - *Chat with documents (General):* **Add files…** in the side panel's Documents section adds files to that chat only. **Document collections** (link at the top of the chat list) holds shared sets of files; tick a collection in any chat's Documents panel to search it. The collections page lists each file's state (waiting, indexing with progress, ready, failed with a reason and **Try again**) and has **Try a search** to see what a question finds.
  - *Citations:* before each reply, the newest message (with the previous turn when it is short) is searched; the best passages go with that message, numbered, and the model is asked to cite them as [1], [2]. Cited numbers in the reply are small buttons, and a **Sources** row under the reply lists every passage given. Either opens the passage inside its document, marked, with **Open the file** and **Copy passage**. The prompt inspector lists the passages.
  - *Reference documents in character chats, groups and stories:* the same Documents panel; passages go in as background, not to be cited.
  - *Recall in long chats and stories:* when older messages are left out to fit, the left-out ones that match the newest message are found with the search model and sent along, next to the rolling summary. A switch under Memory turns it off per chat. The inspector says how many were recalled.
  - Database: one additive migration (collections, documents, passages, chat-collection links, message vectors; a sources column on messages; a recall switch on chats, on for existing ones). A test upgrades an M4 database.
- **How M5 behaves, decided while building it:**
  - *Found passages and recalled messages go with the newest user message, never in the system message.* llama-server reuses the prompt it processed last time up to the first change; text that changes every turn in the system message would mean reading the whole chat again for every reply. The standing "cite the passages" instruction stays in the system message, since it does not change.
  - *Search combines meaning and exact words.* Passages are ranked by vector similarity and by keyword scoring (BM25) and the two rankings are merged, so a code like "E4012" or a name is found even when the meaning score alone is weak.
  - *Thresholds were measured, not guessed:* with Qwen3 Embedding 0.6B a question and its answer passage scored 0.55–0.75, other passages of the same documents 0.27–0.43, unrelated text under 0.2. Passages need 0.45 (or 0.25 with a strong exact-word match); recalled messages need 0.5, because a wrong memory misleads more than a missing one.
  - *Passages are short (about 800 characters).* The first live try used 1,200 and a whole PDF page that mixed maintenance, error codes and warranty scored 0.28 against a question it answered.
  - *The passage list says which numbers exist* ("numbered 1 to 3"). Without it the model also cited the manual's own section numbers ([4], [5]); numbers that are not sources stay plain text.
  - *Vectors belong to one model.* Each is stored with the model's file name and only compared with vectors from the same model. Messages are embedded the first time recall needs them and again only if edited.
  - *"Open the file" opens documents in their usual program, but code and scripts in Notepad*, so opening a .bat or .ps1 shows it rather than running it.
  - *Problems never block a reply.* Without a search model, or if it fails to start, the reply is written without passages and says why under it.
- **Tried live** with Mag-Mell 12B (ChatML) and Qwen3 Embedding 0.6B Q8_0 (downloaded from Hugging Face with permission; checksum matched): a General chat with a 3-page PDF manual and a Word file (two answers citing the right passages, citation buttons and the Sources row opening the passage viewer, the inspector listing the passages); a shared collection with **Try a search** finding the error-code page; an interactive story with a world file as reference (the narrator got the matching sections) and, with a 4,096-token context, recall of left-out passages (in one run two passages about meeting the guild master were recalled; in a rerun with the final 0.5 threshold nothing left out matched closely enough, so none were). Screens checked in all three looks, dark and light: the General chat with files and cited replies, the passage viewer, and the collections page; the story side panel in Workbench dark only.
- **Left out of M5, still owed:** drag-and-drop of files onto the window (files are added with the Windows dialog); scanned PDFs (would need OCR); old .doc, .odt, .rtf, HTML-as-document and e-books; reranking with a second model; standalone lorebooks (still owed from M3). Not tried live: a large document (hundreds of pages; indexing speed is unmeasured), changing the search model with documents already indexed (unit-tested), a group chat with documents, and recall in a character chat (unit-tested; tried live in a story).
- **What M4 delivered (Story):**
  - *Two kinds of story*, chosen when you press **New story**: an **interactive adventure** (you are the main character; the model narrates and you choose) and a **written story** (you are the author; the model writes passage by passage along your outline).
  - *Story bible* for both: title, premise, genre, tone, point of view, tense, cast, world, style guide. It is sent with every passage and can be changed at any time (**Story bible** in the side panel); changes apply from the next passage. **Draft the story bible** has the loaded model fill it in from a one-line idea (and, for a written story, an outline of chapters), using the same structured-output method as the card builder. Nothing is saved until you press Create.
  - *Interactive adventure:* **Begin the story** writes an opening. After every passage a second, short request asks the model for two to four choices as structured output, so they always arrive well-formed; press one, or type your own action in the box. **Other choices** asks again. Optional per story: **state tracking** (location, inventory, stats, facts; the model updates them after each passage, they are sent with your next action, and you can correct them in the side panel) and **dice** (the model marks risky choices; pressing one, or ticking "Roll dice" for your own action, rolls a 20-sided dice and the narrator is told the result: 1 critical failure, 2–7 failure, 8–12 partial success, 13–19 success, 20 critical success).
  - *Branch map:* every path taken, drawn as a tree. Pick any point to go back to it; what you do next starts a new branch and nothing is lost. **Go back to here** under any earlier passage does the same. Each branch has its own state, so going back also brings back the inventory and stats from then.
  - *Written story:* **Write next** writes the next passage, optionally steered by a line you type first. A box above it says which outline chapter the passage belongs to; pick the next chapter when you are ready to move on, and a chapter heading appears. Select any text in a passage and a bar offers **Rewrite, Expand, Shorten, Change tone, Other**; the replacement is shown beside the original and nothing changes until you press **Use this**.
  - *Both kinds:* every passage can be rewritten (the earlier version is kept; arrows flip between versions), continued, edited by hand, copied, and inspected (**Prompt** shows exactly what the model was sent). Style and memory work as in chats: when the story outgrows the context, older passages are summarised.
  - *Save a copy* of the branch on screen as **Markdown**, a single **web page**, or an **EPUB** e-book (title page, contents, one file per chapter). The files contain nothing that points outside themselves.
  - The model picker in Story labels the default model for Story.
  - Database: one additive migration (a `Stories` table and a nullable `ExtraJson` column on messages). A test upgrades an M3 database.
- **How M4 behaves, decided while building it:**
  - *A story is a conversation.* Passages are replies in the same message tree as chats, so versions, branching, continue, the prompt inspector and memory needed no second engine. The player's actions (interactive) and the author's directions (written) are the user turns. Story settings live in their own table; choices, state, dice rolls and chapter numbers are kept with each message.
  - *Choices come from a second request, not from the passage itself.* The passage streams as ordinary prose (better writing, and you can read it as it arrives); then the same prompt plus the passage is sent again with a JSON schema for the choices and state. The engine reuses the prompt it has just processed, so this takes a second or two.
  - *The narrator is reminded of the rules next to your newest action* (length, do not ask what the player does, the dice result, the current state), because models follow what is nearest the end. A closing "What do you do?" that slips through anyway is removed from the saved passage, since the choices are right below it.
  - *Rewriting a selection is held to the selection's size:* the request states the length in words, the reply is capped in tokens, and the temperature is lowered. The first live try, without this, turned one sentence into a page.
  - *Rewriting a selection changes the passage in place* (after you accept it). Rewriting a whole passage keeps the old version beside it.
  - *Going back never deletes.* In a written story, writing on from an earlier passage makes the later text another version; the arrows on the direction line switch between them.
  - *A turn that got no passage* (a problem, or stopped at once) is replaced by the next one rather than followed by it, so two user turns never go to the model in a row.
  - *New stories start with the Creative style.* Dice are off and state tracking is on unless changed.
- **Tried live** with Mag-Mell 12B (ChatML template): an adventure from a one-line idea through bible draft, opening, choices, a typed action with dice, state updates, the branch map (going back and branching), other choices, editing the state by hand (it was still there after leaving and returning), continue, and hand-editing a passage; a written story through bible and six-chapter outline draft, three passages with a direction and a chapter change, rewriting a selection four ways, rewriting a passage, editing the outline afterwards, and all three exports through the real Save dialog. All story screens (landing, new story, adventure, branch map, written story with the selection bar, rewrite dialog, story bible page) were looked at in all three looks, dark and light.
- **What the live tries showed about the model, not fixed by code:** passages run about 350–450 words when asked for 250. The dice result was followed in two of three rolls after the wording was strengthened (a failed roll was once still narrated as a success). Rewrites of a selection sometimes drift into the sentence that follows. A stronger model should do better on all three.
- **Left out of M4, still owed:**
  - *Hierarchical summaries* (scene → chapter → story so far). There is one rolling "story so far" summary, the same mechanism as chat memory. It was not run live in a story (unit-tested).
  - *Lorebooks and document collections as world reference.* Standalone lorebooks are still owed from M3; documents are M5. The bible's World field is the place for world facts until then.
  - *Narrator read-aloud* comes with voice in M6.
  - *Scene beats as their own level.* The outline is chapters, each with a free-text plan; beats can be written into the plan.
  - A past action or direction cannot be edited; go back and do something else instead. The branch map is for adventures only.
  - Not tried: opening the EPUB in an e-reader (its structure and well-formedness are unit-tested), a strict-template model (Gemma, Mistral `[INST]`; role alternation is unit-tested), a reasoning model, and a story long enough to need the summary.
- **What M3 part 3 delivered (group chat):**
  - *Group chats* live in the Characters section, under "Group chats" in the list, with a **New group** button (shown once there are two characters). Pick two or more characters and a turn order, then start. A group keeps its members in its own table; deleting a character takes it out of groups but keeps the chats and what it said ("Removed character").
  - *Turn order*, changeable at any time in the side panel: **Natural** (whoever your message names answers, by full or first name, in the order named; if nobody is named, whoever has been quiet longest), **Everyone in turn** (every member who is not muted answers once, in list order; Stop ends the whole round), **You choose** (nobody answers until you pick). Under the message box a row of member buttons makes one of them speak now, and **Anyone** lets turn order pick (someone the last speaker named, else the quietest, never the one who just spoke). Members can be reordered, muted (they stay in the scene but only speak when picked), removed and added.
  - *Each speaker gets its own prompt:* its own card (and its own main instruction, if the card has one) with a group version of the default instruction ("…never write the words or actions of {{user}} or of Ann and Bo"), a short note on every other member (their description, cut to about 700 characters, each card's {{char}} filled with its own name), lore from every member's book, the chat's scene/instructions, author's note and memory.
  - *Other characters' replies are named in the history* ("Ann: …"). Replies from several characters in a row are joined into one assistant turn, and the prompt always ends with a short user turn "[Write Bo's next reply.]", so roles strictly alternate (strict templates such as Gemma and Mistral refuse anything else, and llama-server would treat a trailing assistant turn as text to continue). A leading "Bo:" the model copies is removed from the reply, and writing stops if a line starts with another member's or the user's name.
  - *Regenerate, swipe and continue keep the same speaker.* The opening message's alternatives are every member's greetings, each member's V3 `group_only_greetings` first, so the arrows choose who opens the scene and how. One-to-one chats never use group-only greetings. The card editor has a new **Group-only greetings** section; they are saved inside the card and exported.
  - *Memory and "Write mine"* work in groups: summaries label each line with its speaker; the draft is told not to speak for any member.
  - The side-panel settings shared by character and group chats (persona, style, instructions, author's note, memory) moved into one component, `RoleplayPanels`.
  - Fixed on the way: the summary request was built with `string.Format`, which turned `{{char}}` into `{char}`, so the names were never filled in (the model was asked about "the story so far between {char} and {user}"). It also said "write an summary".
  - Database: one additive migration (a `TurnOrder` column and a new `ChatMembers` table). A test upgrades a part 2 database.
- **How M3 part 3 behaves, decided while building it:**
  - *One opening slot, not one greeting per member.* Chaining a greeting from each member would hang the second greeting under one version of the first, so flipping the first would hide it. Others join once the user writes, or when picked.
  - *Muted members do not answer even when named*; pressing their button is the way to hear them.
  - *A group chat is one chat*, not a reusable "group" with several chats. **New chat** in the side panel starts a fresh chat with the same members, order, mutes and turn order.
  - *Lore budget in a group* is the largest budget any member's book sets, at most a quarter of the context.
- **Tried live** with Mag-Mell 12B (ChatML template) and three test characters: naming Pip made Pip answer; with "Everyone in turn" all three answered in list order, each in character, none writing another's lines; the copied "Name:" prefixes were removed; regenerating kept the same speaker. The prompt inspector showed the alternating prompt described above. Screens checked in all three looks, dark and light (group chat and the new-group form). **Not tried:** a model with a strict template (Gemma, Mistral `[INST]`; none on this PC, so alternation is guarded by unit tests only), the member up/down/mute/add buttons and the group-only greetings section of the card editor were seen on screen but not clicked through, and "Write mine" and memory were not run live in a group (unit-tested).
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
  - *Impersonate writes inside an open user turn* (raw completion from where the user's turn starts in the model's own template), which makes the model speak as the user much more reliably than asking it to. If the chat already ends with an unanswered message of yours, it asks instead. The text the template puts after a user's message is used as a stop string (and the draft is cut there too), so on templates whose turn end is ordinary text, like Mistral's `[/INST]`, the draft cannot run on into the character's reply; that case is unit-tested only.
  - *Lore matches whole words* so "cat" does not fire on "category". A keyword containing a comma cannot be typed in the editor (commas separate keywords); imported ones are kept.
  - *Lore is searched in the last 4 messages* unless the book says otherwise (SillyTavern's default is 2).
- **Tried live** with two local role-play models: Self After Dark 8B (Llama 3 template) and Mag-Mell 12B (ChatML template). Lore fired and was listed, the note and final instruction landed in user turns, the card builder produced a full card, cropping saved the chosen square, and memory worked in a 4,096-token context (after a fix: the first run left 35 messages out but summarised only 31, because the summary's own size pushed out more turns; now the check is repeated with the summary in place, and the final run covered 41 with 37 left out). The 12B model wrote good summaries and good "Write mine" drafts. The 8B model did not: it echoed the request back or looped when summarising, and in a fresh chat its draft was a placeholder. That is what the refusal checks above guard against. **Not tried:** a model with a strict template (Mistral `[INST]`, Gemma), a reasoning model summarising, and a SillyTavern World Info file from the wild (the import is tested with a sample).
- **Left out of M3 part 2, still owed:** standalone lorebooks shared between characters or attached to a chat (lore lives in each card for now; World Info import copies entries into a card); the SillyTavern "character's note" (`extensions.depth_prompt`) is kept on export but not used.
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
  - Per-mode defaults are saved and the model picker labels the current mode's default (General, Characters and, since M4, Story). Nothing loads a default automatically yet.
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
| M3 ✅ | Characters | Card import/export, editor, personas, lorebooks, swipes, rolling-summary memory, AI card builder, group chat |
| M4 ✅ | Story | Story Bible, authored mode, CYOA with structured choices and state, branch map, exports |
| M5 ✅ | Documents | Embedding server, ingestion, collections, citations; vector recall added to character and story memory |
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
