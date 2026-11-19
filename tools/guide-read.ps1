# Reads the user guide against the running app: walks every screen and dialog the guide describes, saves what each one
# says, and lists every name the guide writes in bold (buttons, places, settings) that is on none of its page's screens.
#
#   pwsh tools/guide-shots.ps1 -Prepare made-up -Links voice     first: the made-up folder, with the voice models
#   pwsh tools/guide-read.ps1                                    walk, save the text, compare
#   pwsh tools/guide-read.ps1 -Skin fireside-dark -Shots         the same walk in another look (workbench | fireside |
#                                                                playhouse, then dark | light), a picture of each screen
#   pwsh tools/guide-read.ps1 -Width 700 -Skin workbench-dark -Shots     at a narrow page
#   pwsh tools/guide-read.ps1 -CompareOnly                       compare again with the text saved by the last walk
#
# What it writes into -Out (never into the repo): <screen>.txt (what is visible, every text on the page hidden parts
# included, and titles, labels and placeholders), <screen>.png with -Shots, run.log. Each screen's line in run.log says
# whether anything reaches past the window's right edge. Look at the pictures with
#   pwsh tools/guide-sheets.ps1 -From <Out> -Out <another folder>
#
# A name it lists is not wrong by that alone: the walk reaches no screen that needs a model at work, a download, the
# lock or the connection (the guide's pictures show those). "other screen" means the name is on a screen of another page
# (a cross-reference); "on no screen read" is what to look up by hand. A walk takes about six minutes: start it in the
# background. A new screen or dialog the guide describes gets a line in the list below.
param(
    [string]$Out = 'D:\Apps\guide-read',
    [int]$Width = 1400,
    [string]$Skin = '',
    [switch]$Shots,
    [switch]$CompareOnly,
    [string]$GuideHome = 'D:\Apps\DOG.ai',
    [int]$Port = 9333
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repo = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Force $Out | Out-Null

if (-not $CompareOnly) {
    Get-ChildItem $Out -File | Remove-Item
    $helpers = @'
window.W = {
  sleep: ms => new Promise(r => setTimeout(r, ms)),
  wait: async (sel, n = 60) => { for (let i = 0; i < n; i++) { if (document.querySelector(sel)) return true; await W.sleep(250); } return false; },
  go: async (addr, sel) => { Blazor.navigateTo(addr); await W.sleep(1500); if (sel) await W.wait(sel); await W.sleep(700); return "at " + location.pathname; },
  item: async (title) => { const a = [...document.querySelectorAll("a.li, .list .li")].find(a => (a.querySelector(".li-title")?.textContent.trim() ?? "").startsWith(title)); a?.click(); await W.sleep(1800); return a ? "opened " + title : "NOT FOUND " + title; },
  btn: async (text, scope) => { const b = [...(scope ? document.querySelector(scope) : document).querySelectorAll("button, a.btn, a")].find(b => b.textContent.trim().startsWith(text)); b?.click(); await W.sleep(1500); return b ? "clicked " + text : "NOT FOUND " + text; },
  click: async (sel) => { const e = document.querySelector(sel); e?.click(); await W.sleep(1500); return e ? "clicked " + sel : "NOT FOUND " + sel; },
  dump: () => { const attrs = [...document.querySelectorAll("[title],[aria-label],[placeholder]")].map(e => [e.getAttribute("title"), e.getAttribute("aria-label"), e.getAttribute("placeholder")].filter(x => x).join(" | ")); return "VISIBLE:\n" + document.body.innerText + "\n\nALL TEXT:\n" + document.body.textContent.replace(/\s+/g, " ") + "\n\nATTRS:\n" + [...new Set(attrs)].join("\n"); },
  edges: () => { const over = [...document.querySelectorAll("body *")].filter(e => { const r = e.getBoundingClientRect(); return r.width && r.height && r.right > innerWidth + 1 && getComputedStyle(e).position !== "fixed" && !e.closest("[hidden]"); }).slice(0, 5).map(e => e.tagName + "." + e.className + " " + Math.round(e.getBoundingClientRect().right)); return innerWidth + " wide; past the edge: " + (over.join("; ") || "nothing"); }
}; "helpers"
'@
    $steps = [Collections.Generic.List[object]]::new()
    $steps.Add(@{ wait = 3000 })
    $steps.Add(@{ eval = $helpers })
    # Opening DOG.ai loads the General default by itself: the message box says when it is in.
    $steps.Add(@{ eval = '(async () => { for (let i = 0; i < 120; i++) { if (document.querySelector(".composer textarea")?.placeholder?.startsWith("Message")) return "model in"; await W.sleep(500); } return "not loaded"; })()' })
    if ($Skin) {
        $skinName, $theme = $Skin -split '-'
        $steps.Add(@{ eval = "(async () => { await W.go(`"settings/appearance`", `"#settings-search`"); document.querySelector(`"input[name=skin][value=$skinName]`").click(); await W.sleep(800); document.querySelector(`"input[name=theme][value=$theme]`").click(); await W.sleep(1000); return `"look $Skin`"; })()" })
    }
    function Screen($name, $js) {
        $steps.Add(@{ eval = "(async () => { $js; return 'ok $name'; })()" })
        $steps.Add(@{ eval = 'W.dump()'; save = (Join-Path $Out "$name.txt") })
        $steps.Add(@{ eval = "'${name}: ' + W.edges()" })
        if ($Shots) { $steps.Add(@{ shot = $name }) }
        $steps.Add(@{ eval = '(document.querySelector("#jump-input") ?? document.querySelector(".modal-back input, .modal-back button"))?.focus(); "ok"' })
        $steps.Add(@{ press = 'esc' })
        $steps.Add(@{ wait = 300 })
    }
    # The screens, named <page>-<what>: the comparison reads a page's names against the screens that start with its name.
    Screen 'general-chat' 'await W.go("general", ".composer textarea"); await W.item("Coastal walk")'
    Screen 'general-chat-picture' 'await W.go("general", ".composer textarea"); await W.item("Where is this harbour")'
    Screen 'general-chat-styles' 'await W.go("general", ".composer textarea"); await W.item("Coastal walk"); await W.btn("Edit styles")'
    Screen 'general-chat-library' 'await W.go("general/library", ".list .li"); await W.item("Coder")'
    Screen 'general-chat-prompts' 'await W.go("general/library", ".list .li"); await W.item("Summarise text")'
    Screen 'characters-page' 'await W.go("characters", ".list .li"); await W.item("Biscuit")'
    Screen 'characters-editor' 'await W.go("characters", ".list .li"); await W.item("Biscuit"); await W.click("a[href$=\"/edit\"]"); await W.wait(".editor-tabs")'
    Screen 'characters-new' 'await W.go("characters/new", ".editor-tabs")'
    Screen 'characters-lore' 'await W.go("characters", ".list .li"); await W.item("Biscuit"); await W.click("a[href$=\"/lore\"]"); await W.sleep(1500)'
    Screen 'characters-chat' 'await W.go("characters", ".list .li"); await W.item("Mara"); await W.click("a.li[href^=\"characters/chat/\"]"); await W.sleep(1500)'
    Screen 'characters-personas' 'await W.go("characters/personas", ".main"); await W.click("button.persona")'
    Screen 'characters-group-new' 'await W.go("characters/group/new", ".pick-grid .persona")'
    Screen 'characters-group' 'await W.go("characters", ".list .li"); await W.item("Biscuit and Mara")'
    Screen 'stories-new' 'await W.go("story/new", ".editor-tabs")'
    Screen 'stories-adventure' 'await W.go("story", ".list .li"); await W.item("The Island Ferry")'
    Screen 'stories-written' 'await W.go("story", ".list .li"); await W.item("The Lighthouse Cat")'
    Screen 'documents-collection' 'await W.go("general/documents", ".list .li"); await W.item("Saltmere visitor notes")'
    Screen 'documents-chat' 'await W.go("general", ".composer textarea"); await W.item("Ferry times to the island")'
    Screen 'documents-passage' 'await W.go("general", ".composer textarea"); await W.item("Ferry times to the island"); await W.click("button.cite")'
    Screen 'images-make' 'await W.go("images", "#images-prompt")'
    Screen 'images-write' 'await W.go("images", "#images-prompt"); await W.click("#images-write"); await W.click("#writer-from-picture")'
    Screen 'images-saved' 'await W.go("images", "#images-prompt"); await W.click("#images-saved")'
    Screen 'images-kept' 'await W.go("images/kept", ".kept-item"); await W.click(".kept-item"); await W.wait("#kept-large")'
    Screen 'images-upscale' 'await W.go("images/kept", ".kept-item"); await W.click(".kept-item"); await W.wait("#kept-upscale"); await W.click("#kept-upscale"); await W.sleep(2500)'
    Screen 'images-maker' 'await W.go("story", ".list .li"); await W.item("The Island Ferry"); const b = [...document.querySelectorAll("button.illustrate")].find(b => b.textContent.includes("Redraw")); b.click(); await W.wait("#maker-use"); await W.sleep(1500); await W.click("#maker-edit")'
    Screen 'video-make' 'await W.go("video", "#video-prompt")'
    Screen 'video-write' 'await W.go("video", "#video-prompt"); await W.click("#video-write")'
    Screen 'video-kept' 'await W.go("video/kept", ".kept-clip"); await W.click(".kept-clip"); await W.wait("#kept-clip-player"); await W.sleep(1200)'
    foreach ($a in 'models', 'models/browse', 'models/get-pictures', 'models/get-video', 'models/get-voice', 'models/library', 'models/pictures', 'models/pictures/loras', 'models/pictures/tags', 'models/video', 'models/voice', 'models/engines') {
        Screen ("models-" + ($a -replace '/', '-')) "await W.go(`"$a`", `"#downloads-tray`"); for (let i = 0; i < 80 && document.querySelector(`"#models-measuring`"); i++) await W.sleep(250); await W.sleep(800)"
    }
    Screen 'models-downloads' 'await W.go("models", "#downloads-tray"); await W.click("#downloads-tray")'
    Screen 'models-library-menu' 'await W.go("models/library", "#default-general"); await W.click("[data-model] .more-btn")'
    Screen 'settings-all' 'await W.go("settings/chats", "#settings-search")'
    Screen 'finding-search' 'await W.go("search", "#search-jump")'
    Screen 'finding-home' 'await W.go("", ".nav-btn, .nav a, a")'
    $steps.Add(@{ eval = 'Blazor.navigateTo("general"); "ok"' }); $steps.Add(@{ wait = 1500 })
    $steps.Add(@{ press = 'ctrl+k' }); $steps.Add(@{ wait = 800 })
    $steps.Add(@{ eval = 'W.dump()'; save = (Join-Path $Out 'finding-jump.txt') })
    if ($Shots) { $steps.Add(@{ shot = 'finding-jump' }) }
    $steps.Add(@{ press = 'esc' })
    foreach ($v in 'DOGAI_MODELS_DIR', 'DOGAI_LLAMA_SERVER', 'DOGAI_SD_SERVER', 'DOGAI_DATA_DIR', 'DOGAI_TEST_CARD_MIB') { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
    try {
        & (Join-Path $PSScriptRoot 'walkthrough.ps1') -Exe "$GuideHome\app\DOG.ai.exe" -Portable -OutDir $Out -Width $Width -Height 860 -Port $Port -Steps $steps.ToArray() *>&1 | Out-String -Width 4000 | Set-Content (Join-Path $Out 'run.log')
    } catch { "FAILED: $_" | Add-Content (Join-Path $Out 'run.log') }
    $log = Get-Content (Join-Path $Out 'run.log')
    "screens walked: $(@($log | Where-Object { $_ -match '=> ok ' }).Count)"
    $log | Where-Object { $_ -match 'NOT FOUND|EVAL ERROR|FAILED|not loaded' } | ForEach-Object { "  $_" }
    $log | Where-Object { $_ -match 'past the edge: ' -and $_ -notmatch 'past the edge: nothing' } | ForEach-Object { "  $_" }
}

# --- The guide's bold names against the screens ------------------------------------------------------------------------
function Flat($text) { ($text -replace "[\u2019]", "'" -replace "\u00a0", ' ') -replace '\s+', ' ' }
$dumps = @{}
Get-ChildItem $Out -Filter *.txt | ForEach-Object { $dumps[$_.BaseName] = Flat (Get-Content $_.FullName -Raw) }
if ($dumps.Count -eq 0) { "No screens saved in $Out."; return }
# Which screens belong to a page (by the start of their names).
$own = @{
    'characters' = @('characters-'); 'documents' = @('documents-'); 'finding-your-way' = @('finding-', 'settings-all', 'general-chat')
    'general-chat' = @('general-chat'); 'images' = @('images-', 'settings-all'); 'install' = @('models-', 'general-chat'); 'models' = @('models-')
    'privacy-and-backups' = @('settings-all'); 'settings-and-help' = @('settings-all'); 'stories' = @('stories-'); 'video' = @('video-')
    'voice' = @('models-models-voice', 'models-models-get-voice', 'settings-all', 'general-chat', 'characters-editor')
}
$everything = ($dumps.Values -join ' ')
foreach ($file in Get-ChildItem (Join-Path $repo 'docs\guide') -Filter *.md | Where-Object Name -ne 'README.md' | Sort-Object Name) {
    $page = $file.BaseName
    $text = Flat ((Get-Content $file.FullName -Raw) -replace '(?s)<!--.*?-->', '')
    $starts = $own[$page] ?? @("$page-")
    $mine = ($dumps.Keys | Where-Object { $k = $_; $starts | Where-Object { $k.StartsWith($_) } } | ForEach-Object { $dumps[$_] }) -join ' '
    $rows = foreach ($m in [regex]::Matches($text, '\*\*(.+?)\*\*')) {
        $term = $m.Groups[1].Value.Trim().TrimEnd(':', '.', ',')
        $parts = @($term -split "\u203a" | ForEach-Object { $_.Trim() })
        if (-not ($parts | Where-Object { -not $mine.Contains($_) })) { continue }
        $where = if (-not ($parts | Where-Object { -not $everything.Contains($_) })) { 'other screen     ' } else { 'on no screen read' }
        "   $where $term"
    }
    $rows = @($rows | Sort-Object -Unique)
    "$($file.Name): $($rows.Count) bold names not on its own screens"
    $rows
}
