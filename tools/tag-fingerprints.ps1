<#
Adds tags to the list of tags left out of DOG.ai's bundled tag list, as SHA-256 fingerprints only
(src/DogAI.Core/Images/Tags/excluded-tags.txt). The words themselves never go into the repository.

  Usage:   pwsh tools/tag-fingerprints.ps1 -Words <private word list>
           then rebuild the tag list: pwsh tools/make-taglist.ps1 -Source <path to danbooru_e621_merged.csv>

The word list is a plain text file kept OUTSIDE the repository: one tag a line, in any spelling the sites use
("some_tag" or "some tag"); blank lines and lines starting with # are skipped. Each tag is put in the form prompts use
(DOG.ai's own TagList.Normalise: lower case, spaces for underscores) and hashed with SHA-256, exactly as DOG.ai does.
Fingerprints already listed are not added twice. Nothing of the word list is printed: only counts.
#>
param(
    [Parameter(Mandatory)] [string]$Words,
    [string]$List = (Join-Path $PSScriptRoot '..\src\DogAI.Core\Images\Tags\excluded-tags.txt')
)
$ErrorActionPreference = 'Stop'

# DOG.ai's own code makes the fingerprints, so this script and the app always agree.
$core = & (Join-Path $PSScriptRoot 'core-dll.ps1')
Add-Type -LiteralPath $core

$listPath = [System.IO.Path]::GetFullPath($List)
$lines = [System.IO.File]::ReadAllLines($listPath)
$header = @($lines | Where-Object { $_.StartsWith('#') })
$hashes = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($line in $lines) { if ($line.Trim().Length -eq 64) { [void]$hashes.Add($line.Trim().ToLowerInvariant()) } }
$before = $hashes.Count

$read = 0
foreach ($word in [System.IO.File]::ReadAllLines([System.IO.Path]::GetFullPath($Words))) {
    $w = $word.Trim()
    if ($w.Length -eq 0 -or $w.StartsWith('#')) { continue }
    $read++
    [void]$hashes.Add([DogAI.Core.Images.TagFingerprints]::Of($w))
}

[System.IO.File]::WriteAllLines($listPath, [string[]]($header + @($hashes)), [System.Text.UTF8Encoding]::new($false))
"$read tags read; $($hashes.Count - $before) new fingerprints added; $($hashes.Count) in the list."
"Now rebuild the tag list with tools/make-taglist.ps1 and update the count in Images/Tags/README.md."
