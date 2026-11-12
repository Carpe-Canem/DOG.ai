# Rebuilds src/DogAI.App/Assets/dogai.ico from icon.svg.
# Renders the SVG once at 512 px with headless Edge, then scales it down to each icon size.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$svg = Join-Path $root 'src\DogAI.App\Assets\icon.svg'
$ico = Join-Path $root 'src\DogAI.App\Assets\dogai.ico'
$work = Join-Path ([IO.Path]::GetTempPath()) ("dogai-icon-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge was not found; it is needed to render the SVG.' }

$html = Join-Path $work 'icon.html'
$svgMarkup = (Get-Content $svg -Raw) -replace '<svg ', '<svg width="512" height="512" '
Set-Content $html "<!DOCTYPE html><html><body style='margin:0;background:transparent'>$svgMarkup</body></html>"
$png = Join-Path $work 'icon-512.png'
$run = Start-Process $edge -Wait -PassThru -ArgumentList @(
    '--headless', '--disable-gpu', '--hide-scrollbars', '--default-background-color=00000000',
    '--window-size=512,512', "--user-data-dir=`"$work\profile`"", "--screenshot=`"$png`"", "`"file:///$($html -replace '\\','/')`"")
if (-not (Test-Path $png)) { throw "Edge did not produce a screenshot (exit code $($run.ExitCode))." }

Add-Type -AssemblyName System.Drawing
$source = [System.Drawing.Image]::FromFile($png)
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = foreach ($size in $sizes) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.DrawImage($source, 0, 0, $size, $size); $g.Dispose()
    $ms = [IO.MemoryStream]::new(); $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}
$source.Dispose()

# ICO container: header, one 16-byte directory entry per image, then the PNG data.
$out = [IO.MemoryStream]::new(); $w = [IO.BinaryWriter]::new($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $w.Write($frame) }
$w.Flush(); [IO.File]::WriteAllBytes($ico, $out.ToArray())
Copy-Item $png (Join-Path $root 'src\DogAI.App\Assets\icon-512.png') -Force
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
"Wrote $ico ($((Get-Item $ico).Length) bytes, sizes: $($sizes -join ', '))"
