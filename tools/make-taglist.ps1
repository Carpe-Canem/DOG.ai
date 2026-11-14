<#
Rebuilds the tag list DOG.ai ships (src/DogAI.Core/Images/Tags/tag-list.txt) from a tag file of the
a1111-sd-webui-tagcomplete project. Fetch the source file by hand first; the owner can also add the full file in the
app (Models -> Images -> Extra tags).

  Source:  https://github.com/DominikDoom/a1111-sd-webui-tagcomplete  (MIT licence), tags/danbooru_e621_merged.csv
  Usage:   pwsh tools/make-taglist.ps1 -Source <path to danbooru_e621_merged.csv>

The rules are DOG.ai's own (DogAI.Core.Images.TagCsv), so this script and the app read tag files the same way:
general tags from Danbooru (category 0) and e621 (7) and e621's species tags (12), with at least 100 posts; artists,
characters, series, meta and lore tags left out (see Tags/README.md). Any tag or other spelling whose fingerprint is in
Tags/excluded-tags.txt is left out too; add to that list with tools/tag-fingerprints.ps1. Nothing left out is printed.
Each line of the result: the tag as the sites write it, a tab, its post count, and (when it has any) a tab and its
other spellings separated by spaces. After a rebuild, update the numbers in Tags/README.md.
#>
param(
    [Parameter(Mandatory)] [string]$Source,
    [string]$Target = (Join-Path $PSScriptRoot '..\src\DogAI.Core\Images\Tags\tag-list.txt'),
    [string]$Excluded = (Join-Path $PSScriptRoot '..\src\DogAI.Core\Images\Tags\excluded-tags.txt')
)
$ErrorActionPreference = 'Stop'

$core = & (Join-Path $PSScriptRoot 'core-dll.ps1')
Add-Type -LiteralPath $core

$target = [System.IO.Path]::GetFullPath($Target)
$count = [DogAI.Core.Images.TagCsv]::BuildBundledList([System.IO.Path]::GetFullPath($Source), [System.IO.Path]::GetFullPath($Excluded), $target)
"$count tags written to $target ($([math]::Round((Get-Item $target).Length / 1MB, 2)) MB)"
"Source SHA-256: $((Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash)"
