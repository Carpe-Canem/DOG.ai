# Contact sheets of the user guide's pictures, four to a sheet, for looking at them all (by you and by the owner) before
# they are committed.
#   pwsh tools/guide-sheets.ps1 [-Page install] [-Out <folder>]     prints the sheets' paths
#   pwsh tools/guide-sheets.ps1 -From <folder> -Out <folder>        sheets of any folder of pictures (a walkthrough's)
param([string]$Page, [string]$Out = (Join-Path ([IO.Path]::GetTempPath()) 'dogai-guide-sheets'), [string]$From)
Add-Type -AssemblyName System.Drawing
$img = if ($From) { $From } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'docs\guide\img' }
$files = Get-ChildItem $img -Recurse -Filter *.png | Where-Object { -not $Page -or $_.Directory.Name -eq $Page } | Sort-Object FullName
New-Item -ItemType Directory -Force $Out | Out-Null
Get-ChildItem $Out -Filter 'sheet-*.png' | Remove-Item
$cell = 700; $cellH = 470; $pad = 8; $label = 22
for ($i = 0; $i -lt $files.Count; $i += 4) {
    $group = $files[$i..([Math]::Min($i + 3, $files.Count - 1))]
    $sheet = [Drawing.Bitmap]::new(2 * ($cell + $pad) + $pad, 2 * ($cellH + $label + $pad) + $pad)
    $g = [Drawing.Graphics]::FromImage($sheet); $g.Clear([Drawing.Color]::FromArgb(40, 40, 40))
    $g.InterpolationMode = 'HighQualityBicubic'
    $font = [Drawing.Font]::new('Segoe UI', 10)
    for ($k = 0; $k -lt $group.Count; $k++) {
        $x = $pad + ($k % 2) * ($cell + $pad); $y = $pad + [Math]::Floor($k / 2) * ($cellH + $label + $pad)
        $b = [Drawing.Bitmap]::new($group[$k].FullName)
        $scale = [Math]::Min([double]$cell / $b.Width, [double]$cellH / $b.Height)   # as doubles: with a 700 px picture the first was the whole number 1, and the second was rounded to 1 too
        $g.DrawImage($b, $x, $y + $label, [int]($b.Width * $scale), [int]($b.Height * $scale))
        $g.DrawString("$($group[$k].Directory.Name)/$($group[$k].Name)  ($($b.Width)x$($b.Height))", $font, [Drawing.Brushes]::White, $x, $y + 2)
        $b.Dispose()
    }
    $file = Join-Path $Out ('sheet-{0:00}.png' -f ($i / 4 + 1))
    $sheet.Save($file); $g.Dispose(); $sheet.Dispose()
    $file
}
