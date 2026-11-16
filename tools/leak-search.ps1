# Searches folders for things that must not be on disk after a run of DOG.ai: marker words (any case, as UTF-8 and
# as UTF-16) and pieces of pictures (their bytes, and their base64 text as UTF-8 and UTF-16). It prints every file
# that holds one, with what it holds. It only reads.
#
# The leak check it is for (CLAUDE.md, "How pictures work: the short map" and "How the Images section works"): draw
# with a made-up word per picture, save each picture's data: address with a walkthrough step
# @{ eval = '...'; save = '<file>.txt' }, close DOG.ai, then:
#
#   & tools/leak-search.ps1 -Words word1, word2 -Pics <folder of the saved .txt files> -Skip "$env:TEMP\claude" -Roots @(
#     @{ name = 'data folder'; path = '<data folder>' },                       # database, logs, WebView2 folder
#     @{ name = 'models folder'; path = '<models folder>'; since = $start },   # since: only files written after that
#     @{ name = 'engine folder'; path = '<engine folder>' },
#     @{ name = 'TEMP'; path = $env:TEMP; since = $start })
#
# A plain data folder may hold only what was kept (its words and picture bytes, in DOG.ai.db); a locked one nothing.
# A piece of a kept picture can be missing from a plain database where it crosses a page of the file; that is not a miss.
param(
    [Parameter(Mandatory)] [object[]]$Roots,
    [string[]]$Words = @(),
    [string]$Pics,
    [string[]]$Skip = @()
)

if (-not ('LeakSearch.Finder' -as [type])) {
Add-Type -Language CSharp @'
using System;
using System.Collections.Generic;
using System.IO;
namespace LeakSearch {
  public sealed class Pattern { public string Name; public byte[] Bytes; public bool AnyCase; }
  public static class Finder {
    // The names of the patterns found in the file. Reads in 8 MB pieces that overlap by the longest pattern.
    public static List<string> Find(string path, List<Pattern> patterns, out string problem) {
      var found = new List<string>();
      problem = null;
      int longest = 0;
      foreach (var p in patterns) longest = Math.Max(longest, p.Bytes.Length);
      try {
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan)) {
          var buffer = new byte[(8 << 20) + longest];
          var lower = new byte[buffer.Length];
          int kept = 0;
          while (true) {
            int read = file.Read(buffer, kept, buffer.Length - kept);
            if (read <= 0) break;
            int have = kept + read;
            for (int i = 0; i < have; i++) { byte b = buffer[i]; lower[i] = (b >= 65 && b <= 90) ? (byte)(b + 32) : b; }
            foreach (var p in patterns) {
              if (found.Contains(p.Name)) continue;
              var hay = p.AnyCase ? new ReadOnlySpan<byte>(lower, 0, have) : new ReadOnlySpan<byte>(buffer, 0, have);
              if (hay.IndexOf(p.Bytes) >= 0) found.Add(p.Name);
            }
            kept = Math.Min(longest, have);
            Buffer.BlockCopy(buffer, have - kept, buffer, 0, kept);
          }
        }
      } catch (Exception e) { problem = e.GetType().Name + ": " + e.Message; }
      return found;
    }
  }
}
'@
}

$patterns = [Collections.Generic.List[LeakSearch.Pattern]]::new()
function Add-Pattern($name, [byte[]]$bytes, $anyCase) { $patterns.Add([LeakSearch.Pattern]@{ Name = $name; Bytes = $bytes; AnyCase = $anyCase }) }
foreach ($w in $Words) {
    $lower = $w.ToLowerInvariant()
    Add-Pattern "word:$w (utf-8)" ([Text.Encoding]::UTF8.GetBytes($lower)) $true
    Add-Pattern "word:$w (utf-16)" ([Text.Encoding]::Unicode.GetBytes($lower)) $true
}
$pictures = @()
if ($Pics) {
    foreach ($f in Get-ChildItem $Pics -Filter *.txt) {
        $address = [IO.File]::ReadAllText($f.FullName)
        $comma = $address.IndexOf(',')
        if ($comma -lt 0 -or $address.Length -lt 4000) { "PICTURE NOT SAVED: $($f.Name) ($($address.Length) characters)"; continue }
        $b64 = $address.Substring($comma + 1)
        $raw = [Convert]::FromBase64String($b64)
        $pictures += "$($f.BaseName): $($raw.Length) bytes"
        # Three pieces of 24 bytes: near the start (see below for a JPEG), the middle, near the end. The base64 piece is the same place in
        # the picture's own base64 text (as a data: address or a JSON value would hold it).
        # A JPEG begins with its tables, and two pictures written by the same program can share one byte for byte (a
        # piece of a picture held only in memory then "turns up" inside another that was kept: seen in M17 step 6). So
        # for a JPEG the first piece is taken from the picture data itself, a little after the last table.
        $first = 300
        if ($raw.Length -gt 4 -and $raw[0] -eq 0xFF -and $raw[1] -eq 0xD8) {
            $p = 2
            while ($p + 4 -lt $raw.Length -and $raw[$p] -eq 0xFF) {
                $size = $raw[$p + 2] * 256 + $raw[$p + 3]
                if ($raw[$p + 1] -eq 0xDA) { $first = $p + 2 + $size + 200; break }
                $p += 2 + $size
            }
            if ($first + 24 -ge $raw.Length / 2) { $first = 300 }
        }
        $n = 0
        foreach ($at in $first, [int]($raw.Length / 2), ($raw.Length - 150)) {
            $n++
            Add-Pattern "picture:$($f.BaseName) piece $n (bytes)" ([byte[]]$raw[$at..($at + 23)]) $false
            $text = $b64.Substring([int][math]::Floor($at / 3) * 4, 40)
            Add-Pattern "picture:$($f.BaseName) piece $n (base64)" ([Text.Encoding]::ASCII.GetBytes($text)) $false
            Add-Pattern "picture:$($f.BaseName) piece $n (base64, utf-16)" ([Text.Encoding]::Unicode.GetBytes($text)) $false
        }
    }
}
"patterns: $($patterns.Count) ($($Words.Count) words, $($pictures.Count) pictures)"
$pictures | ForEach-Object { "  $_" }
if ($patterns.Count -eq 0) { throw 'Nothing to search for: give -Words, -Pics or both.' }

$skipFull = @($Skip | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') + '\' })
foreach ($root in $Roots) {
    if (-not (Test-Path $root.path)) { "[$($root.name)] not there: $($root.path)"; continue }
    $files = Get-ChildItem $root.path -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object {
        $full = $_.FullName; -not ($skipFull | Where-Object { $full.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }) }
    $all = @($files).Count
    if ($root.since) { $files = $files | Where-Object { $_.LastWriteTime -ge $root.since -or $_.CreationTime -ge $root.since } }
    $files = @($files)
    $bytes = ($files | Measure-Object Length -Sum).Sum
    $hits = 0; $unread = @()
    foreach ($f in $files) {
        $problem = $null
        $found = [LeakSearch.Finder]::Find($f.FullName, $patterns, [ref]$problem)
        if ($problem) { $unread += "$($f.FullName): $problem" }
        if ($found.Count -gt 0) { $hits++; "  HIT $($f.FullName) [$($f.Length) bytes]"; $found | ForEach-Object { "      $_" } }
    }
    "[$($root.name)] $($files.Count) files searched$(if ($root.since) { " of $all (written since $($root.since.ToString('HH:mm')))" }), $([math]::Round($bytes / 1MB, 1)) MB, $hits with a hit, $($unread.Count) could not be read"
    $unread | Select-Object -First 8 | ForEach-Object { "  UNREAD $_" }
}
