# Lists the user guide's pages that may have fallen behind the app, and the command that makes their pictures again.
#
#   pwsh tools/guide-stale.ps1             every page whose sources changed since the page was last updated
#   pwsh tools/guide-stale.ps1 -All        every page, stale or not, with its sources and shots
#   pwsh tools/guide-stale.ps1 -Since <commit>   compare with that commit instead of each page's own last change
#
# Each page in docs/guide names the source files it describes on its <!-- sources: … --> lines (globs: ** any folders,
# * part of a name, {a,b} either). A page is stale when a commit after the page's own last change (the newest commit
# that touched docs/guide/<page>.md or docs/guide/img/<page>/) changed one of those files, or when one of them has
# changes not committed yet. Updating a page means: read the changes listed, fix the page's words, run the printed
# guide-shots command, look at the pictures, and commit the page with them (which makes the page current again).
param([switch]$All, [string]$Since)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$guide = Join-Path $repo 'docs\guide'
$shots = (Get-Content (Join-Path $PSScriptRoot 'guide-shots.json') -Raw | ConvertFrom-Json).shots

function To-Regex($glob) {
    $p = [regex]::Escape($glob.Replace('\', '/'))
    $p = $p.Replace('\*\*/', '(.*/)?').Replace('\*\*', '.*').Replace('\*', '[^/]*').Replace('\{', '(').Replace('}', ')').Replace(',', '|')
    "^$p$"
}

$uncommitted = @(git -C $repo status --porcelain | ForEach-Object { $_.Substring(3).Replace('\', '/') })
$stale = 0
foreach ($file in Get-ChildItem $guide -Filter *.md | Where-Object Name -ne 'README.md' | Sort-Object Name) {
    $page = $file.BaseName
    $text = Get-Content $file.FullName -Raw
    $sources = [regex]::Matches($text, '<!--\s*sources:\s*(.*?)\s*-->', 'Singleline') |
        ForEach-Object { $_.Groups[1].Value -split ',(?![^{]*\})|\r?\n' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    $patterns = $sources | ForEach-Object { To-Regex $_ }
    $base = if ($Since) { $Since } else { git -C $repo log -1 --format=%h -- "docs/guide/$page.md" "docs/guide/img/$page/" }
    $changed = @()
    if ($base) {
        $changed += git -C $repo log --format='%h %s' --name-only "$base..HEAD" | ForEach-Object { $_ }
    }
    # Pair each changed file with the commit that changed it.
    $hits = [Collections.Generic.List[string]]::new()
    $commit = ''
    foreach ($line in $changed) {
        if ($line -match '^[0-9a-f]{7,} ') { $commit = $line; continue }
        if (-not $line) { continue }
        if ($patterns | Where-Object { $line -match $_ }) { $hits.Add("  $line  ($commit)") }
    }
    foreach ($f in $uncommitted) { if ($patterns | Where-Object { $f -match $_ }) { $hits.Add("  $f  (not committed)") } }
    $isStub = $text -match '<!-- stub -->'
    if (-not $base -and -not $isStub) { $hits.Add('  (the page has never been committed)') }
    if ($hits.Count -eq 0 -and -not $All) { continue }
    if ($hits.Count) { $stale++ }
    $state = if ($isStub) { 'not written yet' } elseif ($hits.Count) { 'STALE' } else { 'current' }
    "$page.md: $state (last updated in $(if ($base) { $base } else { 'no commit' }))"
    $hits | Sort-Object -Unique
    $names = @($shots | Where-Object { $_.page -eq $page -and $_.kind -ne 'kept' } | ForEach-Object name)
    $kept = @($shots | Where-Object { $_.page -eq $page -and $_.kind -eq 'kept' } | ForEach-Object name)
    if ($names) { "  pictures: pwsh tools/guide-shots.ps1 -Page $page   (or -Only $($names -join ','))" }
    if ($kept) { "  taken by hand, look whether still right: $($kept -join ', ')" }
    if ($All) { "  sources: $($sources -join ', ')" }
    ''
}
if (-not $All) { if ($stale) { "$stale page(s) to look at." } else { 'Every written page is current.' } }
