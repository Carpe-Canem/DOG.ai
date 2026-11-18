# Lists the user guide's pages and pictures that may have fallen behind the app, and the command that makes the
# pictures again.
#
#   pwsh tools/guide-stale.ps1                   what is stale
#   pwsh tools/guide-stale.ps1 -All              every page, stale or not, with its sources and pictures
#   pwsh tools/guide-stale.ps1 -Since <commit>   compare with that commit instead of each page's and picture's own last change
#   pwsh tools/guide-stale.ps1 -Checked <page>   say the page was read against the app and is right as it is (writes
#                                                docs/guide/img/<page>/.checked; commit it): for a change that altered
#                                                nothing the page says or shows
#
# Each page in docs/guide names the source files it describes on its <!-- sources: … --> lines (globs: ** any folders,
# * part of a name, {a,b} either) and the addresses it covers on <!-- covers: … -->.
#   A PAGE is stale when one of its sources changed in a commit after the page's own last change (the newest commit that
#   touched docs/guide/<page>.md or anything in docs/guide/img/<page>/), or has changes not committed yet.
#   A PICTURE shows more than its own page's screens: the screen at its address belongs to the page that covers that
#   address, and a shot can name more addresses it shows ("shows" in tools/guide-shots.json). A picture is stale when
#   the sources of any of those pages changed after the picture itself (or its page's .checked) was last committed.
# Updating means: read the changes listed, fix the page's words, run the printed guide-shots command, look at the
# pictures, and commit page and pictures together (or -Checked when nothing needed changing).
param([switch]$All, [string]$Since, [string]$Checked)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$guide = Join-Path $repo 'docs\guide'
$shots = @((Get-Content (Join-Path $PSScriptRoot 'guide-shots.json') -Raw | ConvertFrom-Json).shots)

if ($Checked) {
    if (-not (Test-Path (Join-Path $guide "$Checked.md"))) { throw "No page docs/guide/$Checked.md" }
    $folder = Join-Path $guide "img\$Checked"
    New-Item -ItemType Directory -Force $folder | Out-Null
    Set-Content (Join-Path $folder '.checked') "Read against the app on $(Get-Date -Format yyyy-MM-dd); right as it is."
    "Wrote docs/guide/img/$Checked/.checked: commit it to mark $Checked.md current."
    return
}

function To-Regex($glob) {
    $p = [regex]::Escape($glob.Replace('\', '/'))
    $p = $p.Replace('\*\*/', '(.*/)?').Replace('\*\*', '.*').Replace('\*', '[^/]*').Replace('\{', '(').Replace('}', ')').Replace(',', '|')
    "^$p$"
}
function Lines($text, $key) {
    [regex]::Matches($text, "<!--\s*${key}:\s*(.*?)\s*-->", 'Singleline') |
        ForEach-Object { $_.Groups[1].Value -split ',(?![^{]*\})|\r?\n' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }
}
function Last-Commit([string[]]$paths) { if ($Since) { $Since } else { git -C $repo log -1 --format=%h -- @paths } }

# Every page: its sources as patterns, what it covers, whether it is a stub.
$pages = [ordered]@{}
foreach ($file in Get-ChildItem $guide -Filter *.md | Where-Object Name -ne 'README.md' | Sort-Object Name) {
    $text = Get-Content $file.FullName -Raw
    $pages[$file.BaseName] = [pscustomobject]@{
        Sources = @(Lines $text 'sources'); Patterns = @(Lines $text 'sources' | ForEach-Object { To-Regex $_ })
        Covers = @(Lines $text 'covers'); Stub = $text -match '<!-- stub -->'
    }
}
function Page-Of($address) { foreach ($k in $pages.Keys) { if ($pages[$k].Covers -contains $address) { return $k } }; $null }

# Changed files with their commits, per starting commit (cached), plus what is not committed yet.
$uncommitted = @(git -C $repo status --porcelain | ForEach-Object { $_.Substring(3).Replace('\', '/') })
$cache = @{}
function Changes($base) {
    if (-not $base) { return @() }
    if (-not $cache.ContainsKey($base)) {
        $list = [Collections.Generic.List[object]]::new(); $commit = ''
        foreach ($line in git -C $repo log --format='%h %s' --name-only "$base..HEAD") {
            if ($line -match '^[0-9a-f]{7,} ') { $commit = $line } elseif ($line) { $list.Add([pscustomobject]@{ File = $line; Commit = $commit }) }
        }
        foreach ($f in $uncommitted) { $list.Add([pscustomobject]@{ File = $f; Commit = 'not committed' }) }
        $cache[$base] = $list
    }
    $cache[$base]
}
function Hits($base, $patterns) {
    @(Changes $base | Where-Object { $f = $_.File; $patterns | Where-Object { $f -match $_ } } | ForEach-Object { "$($_.File)  ($($_.Commit))" } | Sort-Object -Unique)
}

$staleCount = 0
foreach ($page in $pages.Keys) {
    $p = $pages[$page]
    $pageBase = Last-Commit @("docs/guide/$page.md", "docs/guide/img/$page/")
    $pageHits = Hits $pageBase $p.Patterns
    if (-not $pageBase -and -not $p.Stub) { $pageHits = @('(the page has never been committed)') }

    # Each picture of the page, against the sources of every page whose screens it shows.
    $staleShots = @(); $shotLines = @()
    foreach ($shot in $shots | Where-Object page -eq $page) {
        $shown = @($page) + @(@($shot.address) + @($shot.shows) | Where-Object { $null -ne $_ } | ForEach-Object { Page-Of $_ } | Where-Object { $_ }) | Sort-Object -Unique
        $patterns = @($shown | ForEach-Object { $pages[$_].Patterns })
        $picture = "docs/guide/img/$page/$($shot.name).png"
        $base = Last-Commit @($picture, "docs/guide/img/$page/.checked")
        $hits = if ($base) { Hits $base $patterns } else { @('(never committed)') }
        if ($hits.Count) {
            $staleShots += $shot
            $shotLines += "    $($shot.name) [$($shot.kind)] shows $($shown -join ', '):"
            $shotLines += $hits | ForEach-Object { "      $_" }
        }
    }
    $isStale = $pageHits.Count -or $staleShots.Count
    if (-not $isStale -and -not $All) { continue }
    if ($isStale) { $staleCount++ }
    $state = if ($p.Stub) { 'not written yet' } elseif ($isStale) { 'STALE' } else { 'current' }
    "$page.md: $state (page last updated in $(if ($pageBase) { $pageBase } else { 'no commit' }))"
    if ($pageHits.Count) { '  the page''s own sources changed:'; $pageHits | ForEach-Object { "    $_" } }
    if ($staleShots.Count) {
        '  pictures that may show something else now:'
        $shotLines
        $made = @($staleShots | Where-Object kind -ne 'kept')
        if ($made.Count) {
            $flags = (@($made.kind | Sort-Object -Unique) | ForEach-Object { @{ online = ' -Online'; live = ' -Live' }[$_] }) -join ''
            "  make them again: pwsh tools/guide-shots.ps1 -Only $(($made.name) -join ',')$flags"
        }
        $kept = @($staleShots | Where-Object kind -eq 'kept')
        if ($kept.Count) { "  taken by hand, take again or check: $(($kept.name) -join ', ')" }
    }
    if ($All) { "  sources: $($p.Sources -join ', ')"; "  pictures: $((@($shots | Where-Object page -eq $page).name) -join ', ')" }
    ''
}
if (-not $All) { if ($staleCount) { "$staleCount page(s) to look at. When a page needed no change: pwsh tools/guide-stale.ps1 -Checked <page>, then commit." } else { 'Every page and picture is current.' } }
