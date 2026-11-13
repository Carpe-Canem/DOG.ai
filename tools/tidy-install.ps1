# One-time tidy of an old "flat" DOG.ai install, where the program files sit loose in the install folder
# beside data\ and Models\ (the layout the D-DOG-AI publish profile used to make).
#
#   pwsh tools/tidy-install.ps1 -Path D:\DOG-AI -WhatIf     # show what would move; changes nothing
#   pwsh tools/tidy-install.ps1 -Path D:\DOG-AI             # do it
#
# It MOVES (never deletes) every top-level item except data, models, app and portable.txt into <Path>\app.
# Chats, models and settings stay exactly where they are, and because the app finds its home one level above
# app\, nothing needs rewriting. Afterwards run a publish to add the small launcher and refresh app\:
#
#   dotnet publish src/DogAI.App -p:PublishProfile=D-DOG-AI
#
# Close DOG.ai first. Back up first (Settings > Backup, or copy <Path>\data).
[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path $Path).Path.TrimEnd('\')
if (-not (Test-Path "$root\DogAI.exe")) { throw "$root has no DogAI.exe: this does not look like an old flat install (already tidied?)." }
if (Test-Path "$root\app\DogAI.exe") { throw "$root\app\DogAI.exe already exists: this install already has the tidy layout." }
if (-not (Test-Path "$root\data\dogai.db")) { throw "$root\data\dogai.db is missing: refusing to touch a folder that is not a DOG.ai install." }
if (-not (Test-Path "$root\portable.txt")) { throw "$root\portable.txt is missing: without it the app would stop seeing its data." }
$running = Get-Process DogAI -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($root, 'OrdinalIgnoreCase') }
if ($running) { throw "DOG.ai is running from $root. Close it first (also check the tray icon)." }

$keep = 'data', 'models', 'app', 'portable.txt'
$items = Get-ChildItem -LiteralPath $root -Force | Where-Object { $keep -notcontains $_.Name.ToLowerInvariant() }
"{0} items to move into {1}\app" -f $items.Count, $root
if ($PSCmdlet.ShouldProcess("$root", "move $($items.Count) program items into app\")) {
    New-Item -ItemType Directory -Force "$root\app" | Out-Null
    foreach ($item in $items) { Move-Item -LiteralPath $item.FullName -Destination "$root\app\" }
    "Done. Now run: dotnet publish src/DogAI.App -p:PublishProfile=D-DOG-AI   (adds the launcher, refreshes app\)"
    "Until then you can start DOG.ai from $root\app\DogAI.exe."
}
