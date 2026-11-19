# Makes the user guide's pictures (docs/guide/img/<page>/<name>.png) from the list in tools/guide-shots.json.
#
#   pwsh tools/guide-shots.ps1                      every plain shot
#   pwsh tools/guide-shots.ps1 -Page install        the shots of one page
#   pwsh tools/guide-shots.ps1 -Only first-start,engines-offline
#   pwsh tools/guide-shots.ps1 -Online -Live        also the shots that go online, and those that need a model at work
#                                                   (-Only takes any shot it names, online and live ones too)
#   pwsh tools/guide-shots.ps1 -Page install -Force write the pictures even where the pixels did not change
#   pwsh tools/guide-shots.ps1 -Show                print the list and stop
#   pwsh tools/guide-shots.ps1 -Prepare made-up     only set up -GuideHome with that folder (to look around in it, or to
#                                                   draw new pictures for tools/GuideData/assets), then stop
#   pwsh tools/guide-shots.ps1 -Prepare made-up -Links voice   the same, with the list's "extraModels" set "voice" linked in
#
# How it works. The published portable build (publish\win-x64, so `dotnet publish src/DogAI.App -p:PublishProfile=win-x64`
# first) is copied to -GuideHome, a folder with a neutral name on the drive that holds the models (hard links need the same
# drive). Each shot names a folder:
#   empty    nothing in data\ or models\: what a first start shows
#   made-up  data\ filled by tools/GuideData (characters, chats, a story, kept pictures and a clip: all made up), the
#            engines and the models in the list's "models" hard-linked from -ModelSource and -EngineSource
#   none     the folder of the shot before it, as it is (a shot that follows from the one before)
# One run of DOG.ai per folder, page size, "links" and "env"; between shots the address is opened again.
# A shot's "links" names sets in the list's "extraModels" (files or whole folders from -ModelSource), linked in only for
# the shots that name them: the voice models would otherwise put the microphone buttons into every chat picture. Its
# "env" sets environment variables for its run (DOGAI_TEST_CARD_MIB: what a smaller graphics card is told).
#
# Before each picture the page is made still (no animation, no caret, no focus), the numbered marks are drawn on the
# controls the shot names, and the page's text and every box's value are searched for words that must never be in a
# public picture: the Windows user name, the owner's own folders, and the name of any model file in -ModelSource that the
# list does not link in. A shot that holds one is not written, and the run says so.
#
# A picture is written only when its pixels changed, leaving out the parts the list calls volatile (free memory, free
# disk space: they differ on every run). So making the pictures twice rewrites nothing.
#
# A shot's "lock" is a made-up password: its run's folder is locked with it before DOG.ai first starts (lock.json written
# into the filled folder, as Settings > Privacy leaves it before its restart), the lock screen is driven to encrypt and
# unlock, and the plain shots of that run show a locked DOG.ai. A shot of kind "window" in such a run is the lock screen
# itself, taken from the screen (PrintWindow) at the next start and trimmed to the window Windows shows.
# A shot's "after" steps run once its picture is taken (to cancel what the shot started). "{home}" in a step's text is
# -GuideHome; its "out" folder is emptied for every run (a shot's backup file goes there).
#
# Windows' own dialogs and the shots of the first start's downloads are taken by hand and kept; the list marks them
# "kind": "kept" so this script leaves them alone and the test still finds them listed.
param(
    [string[]]$Only = @(),
    [string]$Page,
    [switch]$Online,
    [switch]$Live,
    [switch]$Show,
    [switch]$Force,
    [ValidateSet('', 'empty', 'made-up')][string]$Prepare = '',
    [string[]]$Links = @(),
    [string]$GuideHome = 'D:\Apps\DOG.ai',
    [string]$ModelSource = 'D:\DOG.ai\Models',
    [string]$EngineSource = 'D:\DOG.ai\data\engine',
    [string]$Cache = 'D:\Apps\guide-cache',
    [int]$Port = 9333
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$list = Get-Content (Join-Path $PSScriptRoot 'guide-shots.json') -Raw | ConvertFrom-Json
# "a,b" given through pwsh -File arrives as one string.
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$shots = @($list.shots | Where-Object {
    ($Only.Count -eq 0 -or $_.name -in $Only) -and (-not $Page -or $_.page -eq $Page) -and $_.kind -ne 'kept' -and
    ($_.name -in $Only -or (($_.kind -ne 'online' -or $Online) -and ($_.kind -ne 'live' -or $Live)))
})
if ($Show) { $list.shots | Format-Table name, page, folder, kind, address -AutoSize | Out-String -Width 200; return }
if ($shots.Count -eq 0) { "No shot matches."; return }
$unknown = $Only | Where-Object { $_ -notin $list.shots.name }
if ($unknown) { throw "Not in the list: $($unknown -join ', ')" }

# --- Words that must not be in a picture --------------------------------------------------------------------------
$linked = [Collections.Generic.List[string]]::new()
foreach ($m in @($list.models) + @($list.extraModels.PSObject.Properties | ForEach-Object { $_.Value })) {
    $linked.Add([IO.Path]::GetFileNameWithoutExtension($m.name))
    $source = if ([IO.Path]::IsPathRooted($m.from)) { $m.from } else { Join-Path $ModelSource $m.from }
    if (Test-Path $source -PathType Container) { Get-ChildItem $source -Recurse -File | ForEach-Object { $linked.Add([IO.Path]::GetFileNameWithoutExtension($_.Name)) } }
}
$forbidden = [Collections.Generic.List[string]]::new()
$forbidden.Add($env:USERNAME)
foreach ($w in 'D:\DOG.ai', 'dogai-b', $ModelSource, $EngineSource) { $forbidden.Add($w) }
$mail = (git -C $repo config user.email) 2>$null
if ($mail) { $forbidden.Add(($mail -split '@')[0]) }
if (Test-Path $ModelSource) {
    Get-ChildItem $ModelSource -Recurse -FollowSymlink -File -Include *.gguf, *.safetensors, *.bin, *.onnx |
        ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Name) } |
        Where-Object { $_.Length -ge 8 -and $_ -match '[-_.]' -and $_ -notin $linked } | Sort-Object -Unique | ForEach-Object { $forbidden.Add($_) }
    # The folders the owner's models sit in are named after their Hugging Face repositories, which Browse can list.
    $linkedFolders = @(@($list.models) + @($list.extraModels.PSObject.Properties | ForEach-Object { $_.Value }) | ForEach-Object { ($_.from -split '\\')[0] })
    Get-ChildItem $ModelSource -Directory | Where-Object { $_.Name.Length -ge 8 -and $_.Name -match '[-_.]' -and $_.Name -notin $linkedFolders } |
        ForEach-Object { $forbidden.Add($_.Name) }
}
$forbiddenJson = ConvertTo-Json @($forbidden | Where-Object { $_ } | Sort-Object -Unique) -Compress

# --- The folder -------------------------------------------------------------------------------------------------------
$publish = Join-Path $repo 'publish\win-x64'
if (-not (Test-Path "$publish\app\DOG.ai.exe")) { throw "Publish first: dotnet publish src/DogAI.App -p:PublishProfile=win-x64" }
if ($GuideHome -match [regex]::Escape($env:USERNAME)) { throw "-GuideHome must not hold the Windows user name: it shows in the pictures." }

function Link-Model($name, $from) {
    $to = Join-Path "$GuideHome\models" $name
    $from = if ([IO.Path]::IsPathRooted($from)) { $from } else { Join-Path $ModelSource $from }
    # A whole folder (a voice model) is linked file by file.
    if (Test-Path $from -PathType Container) {
        $top = (Get-Item $from).FullName
        Get-ChildItem $from -Recurse -File | ForEach-Object { Link-Model (Join-Path $name $_.FullName.Substring($top.Length + 1)) $_.FullName }
        return
    }
    New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
    # A file on another drive (behind a folder link, say) cannot be hard-linked: it is copied once into -Cache, on
    # the guide's drive, and linked from there.
    $resolved = [IO.Path]::GetFullPath($from)
    $dir = Get-Item (Split-Path $resolved)
    if ($dir.LinkType) { $resolved = Join-Path $dir.Target (Split-Path $resolved -Leaf) }
    if ([IO.Path]::GetPathRoot($resolved) -ne [IO.Path]::GetPathRoot($GuideHome)) {
        $cached = Join-Path $Cache (Split-Path $resolved -Leaf)
        if (-not (Test-Path $cached) -or (Get-Item $cached).Length -ne (Get-Item $resolved).Length) {
            New-Item -ItemType Directory -Force $Cache | Out-Null
            "copying $(Split-Path $resolved -Leaf) once into $Cache"
            Copy-Item $resolved $cached -Force
        }
        $resolved = $cached
    }
    New-Item -ItemType HardLink -Path $to -Target $resolved | Out-Null
}

function Prepare-Home($folder, $links = @()) {
    New-Item -ItemType Directory -Force $GuideHome | Out-Null
    # Only the program: publish\win-x64 also keeps files of older publishes.
    robocopy "$publish\app" "$GuideHome\app" /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    Copy-Item "$publish\DOG.ai.exe", "$publish\portable.txt" $GuideHome -Force
    foreach ($sub in 'data', 'models', 'out') { if (Test-Path "$GuideHome\$sub") { Remove-Item "$GuideHome\$sub" -Recurse -Force } }
    New-Item -ItemType Directory -Force "$GuideHome\out" | Out-Null
    if ($folder -eq 'empty') { return }
    New-Item -ItemType Directory -Force "$GuideHome\data\engine", "$GuideHome\models" | Out-Null
    foreach ($m in $list.models) { Link-Model $m.name $m.from }
    foreach ($set in $links) { foreach ($m in $list.extraModels.$set) { Link-Model $m.name $m.from } }
    foreach ($d in $list.emptyFolders) { New-Item -ItemType Directory -Force (Join-Path "$GuideHome\models" $d) | Out-Null }
    Get-ChildItem $EngineSource -Recurse -File | ForEach-Object {
        $to = Join-Path "$GuideHome\data\engine" $_.FullName.Substring($EngineSource.Length + 1)
        New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
        New-Item -ItemType HardLink -Path $to -Target $_.FullName | Out-Null
    }
    $filler = Join-Path $repo 'tools\GuideData\bin\Release\net9.0-windows10.0.19041.0\GuideData.exe'
    dotnet build (Join-Path $repo 'tools\GuideData\GuideData.csproj') -c Release -v q | Out-Null
    if ($LASTEXITCODE) { throw "tools/GuideData does not build." }
    & $filler fill $GuideHome
    if ($LASTEXITCODE) { throw "GuideData could not fill $GuideHome" }
}

# Locks the filled folder as Settings > Privacy leaves it before its restart: lock.json beside the still plain database.
# Written by a pwsh of its own, so this one never holds DogAI.Core.dll open.
function Lock-Home($password) {
    $script = "Add-Type -Path '$publish\app\DogAI.Core.dll'; `$f = [DogAI.Core.Privacy.LockFile]::Create('$password').File; `$f.Skin = 'workbench'; `$f.Theme = 'dark'; [IO.File]::WriteAllText('$GuideHome\data\lock.json', `$f.ToJson())"
    pwsh -NoProfile -Command $script
    if ($LASTEXITCODE -or -not (Test-Path "$GuideHome\data\lock.json")) { throw "The guide folder could not be locked." }
}

# A step from the list as the walkthrough takes it, with {home} filled in.
function Step-Of($s) {
    $step = @{}
    foreach ($p in $s.PSObject.Properties) { $step[$p.Name] = if ($p.Value -is [string]) { $p.Value.Replace('{home}', $GuideHome) } else { $p.Value } }
    $step
}

if ($Prepare) { Prepare-Home $Prepare $Links; "Ready: $GuideHome ($Prepare). Run it with: pwsh tools/walkthrough.ps1 -Exe $GuideHome\app\DOG.ai.exe -Portable"; return }

# --- The page script run before every picture -------------------------------------------------------------------------
# Stills the page, draws the marks, finds the volatile parts and looks for forbidden words. Returns one line of JSON.
function Before-Shot($shot) {
    $name = ConvertTo-Json "$($shot.name)" -Compress
    $marks = ConvertTo-Json @($shot.marks | Where-Object { $_ }) -Compress
    $beside = if ($shot.marksBeside) { 'true' } else { 'false' }
    $volatile = ConvertTo-Json @(@($list.volatile) + @($shot.volatile) | Where-Object { $_ }) -Compress
    @"
(async () => {
  let s = document.getElementById('guide-still');
  if (!s) { s = document.createElement('style'); s.id = 'guide-still'; document.head.appendChild(s); }
  s.textContent = '*,*::before,*::after{animation:none!important;transition:none!important;caret-color:transparent!important}';
  document.activeElement?.blur?.();
  document.getElementById('guide-marks')?.remove();
  // Every picture on the page drawn first (avatars, kept pictures): one still loading made a picture differ between runs.
  for (let i = 0; i < 50 && [...document.images].some(im => !im.complete); i++) await new Promise(r => setTimeout(r, 100));
  await Promise.all([...document.images].map(im => im.decode?.().catch(() => {})));
  await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
  const out = { missing: [], found: [], volatile: [] };
  const layer = document.createElement('div'); layer.id = 'guide-marks';
  layer.style.cssText = 'position:fixed;inset:0;pointer-events:none;z-index:2147483647';
  $marks.forEach((sel, i) => {
    const e = document.querySelector(sel);
    const r = e?.getBoundingClientRect();
    if (!e || !r.width) { out.missing.push(sel); return; }
    const ring = document.createElement('div');
    ring.style.cssText = 'position:fixed;border:2px solid #ff3d7f;border-radius:6px;box-sizing:border-box;' +
      'left:' + (r.left - 3) + 'px;top:' + (r.top - 3) + 'px;width:' + (r.width + 6) + 'px;height:' + (r.height + 6) + 'px';
    const n = document.createElement('div');
    n.textContent = String(i + 1);
    // The number sits on the ring's corner; "marksBeside" puts it left of the ring, for a control whose first
    // letter or tick box is in that corner.
    const x = Math.max(2, Math.min(innerWidth - 24, r.left - ($beside ? 30 : 12))), y = Math.max(2, Math.min(innerHeight - 24, $beside ? r.top + Math.min(r.height, 30) / 2 - 11 : r.top - 12));
    n.style.cssText = 'position:fixed;left:' + x + 'px;top:' + y + 'px;width:22px;height:22px;border-radius:11px;' +
      'background:#ff3d7f;color:#fff;font:700 13px/22px Segoe UI,sans-serif;text-align:center;box-shadow:0 0 0 2px #fff';
    layer.append(ring, n);
  });
  document.body.appendChild(layer);
  for (const sel of $volatile) for (const e of document.querySelectorAll(sel)) {
    const r = e.getBoundingClientRect();
    if (r.width && r.height) out.volatile.push([Math.floor(r.left), Math.floor(r.top), Math.ceil(r.right), Math.ceil(r.bottom)]);
  }
  const text = (document.body.innerText + '\n' + [...document.querySelectorAll('input,textarea,select')].map(e => e.value).join('\n')).toLowerCase();
  for (const w of $forbiddenJson) if (text.includes(w.toLowerCase())) out.found.push(w);
  return 'GUIDE ' + $name + ' ' + JSON.stringify(out);
})()
"@
}

# --- Run -----------------------------------------------------------------------------------------------------------------
$work = Join-Path ([IO.Path]::GetTempPath()) ('dogai-guide-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
foreach ($v in 'DOGAI_MODELS_DIR', 'DOGAI_LLAMA_SERVER', 'DOGAI_SD_SERVER', 'DOGAI_DATA_DIR', 'DOGAI_TEST_CARD_MIB') { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
$results = @{}
$groups = $shots | Group-Object { "$($_.folder)|$($_.width)x$($_.height)|$(@($_.links) -join '+')|$($_.lock)|$(($_.env | ConvertTo-Json -Compress) ?? '')" }
$lastFolder = $null
foreach ($group in $groups) {
    $folder, $size, $linkNames, $lockWith = ($group.Name -split '\|')[0..3]
    $w, $h = $size -split 'x'
    $runEnv = $group.Group[0].env
    $pageShots = @($group.Group | Where-Object kind -ne 'window')
    $windowShots = @($group.Group | Where-Object kind -eq 'window')
    if ($folder -ne 'none') { Prepare-Home $folder @($linkNames -split '\+' | Where-Object { $_ }); $lastFolder = $folder }
    if ($runEnv) { foreach ($p in $runEnv.PSObject.Properties) { Set-Item "Env:$($p.Name)" "$($p.Value)" } }
    # A locked run: the lock screen asks for the new password, encrypts, and (on a PC with Windows Hello) offers Hello.
    $lockSteps = @()
    if ($lockWith) {
        if ($folder -eq 'none') { throw "A locked shot needs a folder of its own." }
        Lock-Home $lockWith
        $lockSteps = @(@{ set = @{ Password = $lockWith } }, @{ click = 'Unlock' }, @{ wait = 3000 }, @{ click = 'NotNow' }, @{ gone = 120 })
    }
    $steps = [Collections.Generic.List[object]]::new()
    $steps.Add(@{ wait = 3000 })
    # Opening DOG.ai loads the General default by itself: wait until the status bar says it is in.
    if ($folder -eq 'made-up') {
        $steps.Add(@{ eval = "(async () => { for (let i = 0; i < 120; i++) { const t = document.querySelector('.status')?.innerText ?? ''; if (t && !/Loading|No model/.test(t)) return t; await new Promise(r => setTimeout(r, 500)); } return 'not loaded'; })()" })
    }
    foreach ($shot in $pageShots) {
        # Whatever the shot before left open (Ctrl+K, a dialog, a menu) closes first.
        $steps.Add(@{ eval = "(document.querySelector('#jump-input') ?? document.querySelector('.modal-back input, .modal-back button'))?.focus(); 'ok'" })
        $steps.Add(@{ press = 'esc' })
        $steps.Add(@{ wait = 300 })
        $steps.Add(@{ eval = "document.querySelector('#jump') ? 'GUIDE-STILL-OPEN #jump' : 'closed'" })
        $steps.Add(@{ eval = "document.activeElement?.blur?.(); 'ok'" })
        if ($shot.address) {
            $addr = $shot.address | ConvertTo-Json
            $steps.Add(@{ eval = "Blazor.navigateTo($addr); 'GUIDE-NAV'" })
            $steps.Add(@{ wait = 1200 })
        }
        if ($shot.waitFor) {
            $sel = $shot.waitFor | ConvertTo-Json
            $steps.Add(@{ eval = "(async () => { for (let i = 0; i < 120; i++) { if (document.querySelector($sel)) return 'ready'; await new Promise(r => setTimeout(r, 250)); } return 'GUIDE-TIMEOUT ' + $sel; })()" })
        }
        foreach ($s in $shot.steps) { $steps.Add((Step-Of $s)) }
        $steps.Add(@{ wait = [int]($shot.settle ?? 500) })
        # Nothing hovered: the real cursor, if it is over the window, highlighted whatever it was on (a picture's frame, a
        # tab), and a picture came out different from one run to the next.
        # Transitions go off first, or a highlight that is let go is caught half faded.
        $steps.Add(@{ eval = "(() => { let s = document.getElementById('guide-still'); if (!s) { s = document.createElement('style'); s.id = 'guide-still'; document.head.appendChild(s); } s.textContent = '*,*::before,*::after{animation:none!important;transition:none!important;caret-color:transparent!important}'; const n = document.createElement('style'); n.id = 'guide-nohover'; n.textContent = '*,*::before,*::after{pointer-events:none!important}'; document.head.appendChild(n); return 'no hover'; })()" })
        $steps.Add(@{ hover = '1,1' })
        $steps.Add(@{ wait = 300 })
        $steps.Add(@{ eval = (Before-Shot $shot) })
        $steps.Add(@{ shot = $shot.name })
        $steps.Add(@{ eval = "document.getElementById('guide-marks')?.remove(); document.getElementById('guide-nohover')?.remove(); 'unmarked'" })
        foreach ($s in $shot.after) { $steps.Add((Step-Of $s)) }
        # A shot that changes the look puts back the one a new folder starts with (Workbench, Match Windows), or every shot after it in the
        # same run would show the look it left.
        if (($shot.steps | ConvertTo-Json -Depth 5 -Compress) -match 'name=(skin|theme)') {
            $steps.Add(@{ eval = "Blazor.navigateTo('settings/appearance'); 'GUIDE-NAV'" })
            $steps.Add(@{ wait = 1200 })
            $steps.Add(@{ eval = "document.querySelector('input[name=skin][value=workbench]').click(); 'ok'" })
            $steps.Add(@{ wait = 800 })
            $steps.Add(@{ eval = "document.querySelector('input[name=theme][value=system]').click(); 'ok'" })
            $steps.Add(@{ wait = 1200 })
        }
    }
    $log = & (Join-Path $PSScriptRoot 'walkthrough.ps1') -Exe "$GuideHome\app\DOG.ai.exe" -Portable -OutDir $work -Width $w -Height $h -Port $Port -LockSteps $lockSteps -Steps $steps.ToArray() *>&1 | Out-String -Width 100000
    # The lock screen itself: DOG.ai started again on the now encrypted folder and left at its lock screen.
    if ($windowShots) {
        $atLock = @(@{ wait = 1500 }) + @($windowShots | ForEach-Object { @{ shot = $_.name } })
        $log += & (Join-Path $PSScriptRoot 'walkthrough.ps1') -Exe "$GuideHome\app\DOG.ai.exe" -Portable -OutDir $work -Port $Port -LockSteps $atLock -LockOnly *>&1 | Out-String -Width 100000
    }
    if ($runEnv) { foreach ($p in $runEnv.PSObject.Properties) { Remove-Item "Env:$($p.Name)" -ErrorAction SilentlyContinue } }
    $log -split "`r?`n" | Where-Object { $_ -match 'GUIDE-TIMEOUT|GUIDE-STILL-OPEN|EVAL ERROR|TARGET NOT FOUND|not loaded|DIALOG NOT FOUND|LOCK SCREEN|LOCK BUTTON' } | ForEach-Object { "  $_" }
    # The page script prints one line per shot, naming it: a shot whose script failed gets no result, never another's.
    foreach ($line in $log -split "`r?`n") {
        if ($line.Trim() -match '^=> GUIDE (\S+) (\{.*\})$') { $results[$Matches[1]] = $Matches[2] | ConvertFrom-Json }
    }
    ($log -split "`r?`n" | Where-Object { $_ -match '\[(WRN|ERR|FTL)\]' }) | ForEach-Object { "  app log: $_" }
}

# --- Write what changed ------------------------------------------------------------------------------------------------
Add-Type -AssemblyName System.Drawing
function Same-Pixels($a, $b, $skip) {
    $x = [Drawing.Bitmap]::new($a); $y = [Drawing.Bitmap]::new($b)
    try {
        if ($x.Width -ne $y.Width -or $x.Height -ne $y.Height) { return $false }
        $rect = [Drawing.Rectangle]::new(0, 0, $x.Width, $x.Height)
        $dx = $x.LockBits($rect, 'ReadOnly', 'Format32bppArgb'); $dy = $y.LockBits($rect, 'ReadOnly', 'Format32bppArgb')
        $n = $dx.Stride * $x.Height
        $px = [byte[]]::new($n); $py = [byte[]]::new($n)
        [Runtime.InteropServices.Marshal]::Copy($dx.Scan0, $px, 0, $n); [Runtime.InteropServices.Marshal]::Copy($dy.Scan0, $py, 0, $n)
        $x.UnlockBits($dx); $y.UnlockBits($dy)
        # Blank the volatile parts in both before comparing.
        foreach ($r in $skip) {
            $l = [Math]::Max(0, $r[0]); $t = [Math]::Max(0, $r[1]); $rr = [Math]::Min($x.Width, $r[2]); $bb = [Math]::Min($x.Height, $r[3])
            for ($row = $t; $row -lt $bb; $row++) {
                $start = $row * $dx.Stride + $l * 4; $len = ($rr - $l) * 4
                if ($len -gt 0) { [Array]::Clear($px, $start, $len); [Array]::Clear($py, $start, $len) }
            }
        }
        return [Linq.Enumerable]::SequenceEqual($px, $py)
    } finally { $x.Dispose(); $y.Dispose() }
}

# A window as PrintWindow gives it has Windows' unseen resize border round it (black): cut to what is not black.
function Trim-Window($file) {
    $b = [Drawing.Bitmap]::new($file)
    $rect = [Drawing.Rectangle]::new(0, 0, $b.Width, $b.Height)
    $d = $b.LockBits($rect, 'ReadOnly', 'Format32bppArgb')
    $px = [byte[]]::new($d.Stride * $b.Height)
    [Runtime.InteropServices.Marshal]::Copy($d.Scan0, $px, 0, $px.Length)
    $b.UnlockBits($d)
    # A column or row belongs to the border when nearly all of it is pure black (the title bar runs across the side
    # borders, so "all of it" would keep them).
    $cols = [int[]]::new($b.Width); $rows = [int[]]::new($b.Height)
    for ($y = 0; $y -lt $b.Height; $y++) {
        for ($x = 0; $x -lt $b.Width; $x++) {
            $i = $y * $d.Stride + $x * 4
            if ($px[$i + 3] -eq 0 -or ($px[$i] + $px[$i + 1] + $px[$i + 2]) -eq 0) { $cols[$x]++; $rows[$y]++ }
        }
    }
    $l = 0; while ($l -lt $b.Width -and $cols[$l] -ge 0.8 * $b.Height) { $l++ }
    $r = $b.Width - 1; while ($r -gt $l -and $cols[$r] -ge 0.8 * $b.Height) { $r-- }
    $t = 0; while ($t -lt $b.Height -and $rows[$t] -ge 0.8 * $b.Width) { $t++ }
    $bottom = $b.Height - 1; while ($bottom -gt $t -and $rows[$bottom] -ge 0.8 * $b.Width) { $bottom-- }
    if ($r -le $l -or $bottom -le $t) { $b.Dispose(); return }
    $cut = $b.Clone([Drawing.Rectangle]::new($l, $t, $r - $l + 1, $bottom - $t + 1), 'Format32bppArgb'); $b.Dispose()
    $cut.Save($file, [Drawing.Imaging.ImageFormat]::Png); $cut.Dispose()
}

$failed = 0
foreach ($shot in $shots) {
    $made = Join-Path $work "$($shot.name).png"
    $target = Join-Path $repo "docs\guide\img\$($shot.page)\$($shot.name).png"
    $r = $results[$shot.name]
    if (-not (Test-Path $made)) { "NOT MADE    $($shot.name)"; $failed++; continue }
    # The lock screen has no page to ask: it shows no data, only its own words.
    if ($shot.kind -eq 'window') { Trim-Window $made; $r = [pscustomobject]@{ found = @(); missing = @(); volatile = @() } }
    if (-not $r) { "NO CHECK    $($shot.name) (its page script did not answer)"; $failed++; continue }
    if ($r.found.Count) { "REFUSED     $($shot.name): the page shows $($r.found -join ', ')"; $failed++; continue }
    if ($r.missing.Count) { "NO MARK     $($shot.name): $($r.missing -join ', ') not on the page"; $failed++; continue }
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    if (-not $Force -and (Test-Path $target) -and (Same-Pixels $made $target $r.volatile)) { "same        $($shot.name)"; continue }
    Copy-Item $made $target -Force
    "written     $($shot.name) -> docs/guide/img/$($shot.page)/$($shot.name).png"
}
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
if ($failed) { "$failed shot(s) not written."; exit 1 }
