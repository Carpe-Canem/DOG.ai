<#
Builds DogAI.Core and returns the path of its DLL, for scripts that use DOG.ai's own code
(make-taglist.ps1, tag-fingerprints.ps1). Needs PowerShell 7 on .NET 9 or later.
#>
$ErrorActionPreference = 'Stop'
if ([Environment]::Version.Major -lt 9) { throw "This script needs PowerShell 7.5 or later (.NET 9); this one runs on .NET $([Environment]::Version)." }
$project = Join-Path $PSScriptRoot '..\src\DogAI.Core\DogAI.Core.csproj'
$output = dotnet build $project -c Debug --nologo -v q 2>&1
if ($LASTEXITCODE -ne 0) { $output | Write-Host; throw 'DogAI.Core did not build.' }
[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\DogAI.Core\bin\Debug\net9.0\DogAI.Core.dll'))
