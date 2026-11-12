# Launches the built DOG AI app against a throwaway data folder and drives it through the
# WebView2 debugging port, so a change can be checked in the real app.
#
#   -Steps    list of @{ eval = '<javascript>' } and @{ shot = '<name>' } hashtables, run in order;
#             @{ dialog = '<file path>'; title = '<dialog title>' } fills in a Windows open/save dialog
#             that the previous step opened (it types the path and presses Enter)
#   -DataDir  data folder for the run (default: a fresh temp folder, deleted afterwards)
#   -OutDir   where screenshots are written (default: a temp folder, printed at the end)
#   -Keep     keep the data folder, e.g. to restart and check that something persisted
#
# Build first: dotnet build DogAI.sln
param(
    [object[]]$Steps = @(@{ shot = 'start' }),
    [string]$DataDir,
    [string]$OutDir = (Join-Path ([IO.Path]::GetTempPath()) 'dogai-walkthrough'),
    [int]$Width = 1400,
    [int]$Height = 860,
    [int]$Port = 9333,
    [switch]$Keep
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\DogAI.App\bin\Debug\net9.0-windows\DogAI.exe'
if (-not (Test-Path $exe)) { throw "Build the solution first; $exe does not exist." }
$temporary = -not $DataDir
if ($temporary) { $DataDir = Join-Path ([IO.Path]::GetTempPath()) ("dogai-run-" + [Guid]::NewGuid().ToString('N')) }

$env:DOGAI_DATA_DIR = $DataDir
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$Port"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$app = Start-Process $exe -PassThru
try {
    $target = $null
    foreach ($i in 1..40) {
        Start-Sleep -Milliseconds 500
        try {
            $all = (Invoke-WebRequest "http://127.0.0.1:$Port/json" -TimeoutSec 2).Content | ConvertFrom-Json
            $target = @($all) | Where-Object type -eq 'page' | Select-Object -First 1
        } catch {}
        if ($target) { break }
    }
    if (-not $target) { throw "The app did not show a page within 20 seconds (exited: $($app.HasExited)). Check $DataDir\logs." }
    Start-Sleep -Seconds 2

    $ws = [System.Net.WebSockets.ClientWebSocket]::new()
    $ws.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
    $script:id = 0
    function Send($method, $params) {
        $script:id++
        $msg = @{ id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 6 -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($msg)
        $ws.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, [Threading.CancellationToken]::None).Wait()
        while ($true) {
            $buf = [byte[]]::new(1048576); $ms = [IO.MemoryStream]::new()
            do {
                $r = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), [Threading.CancellationToken]::None).Result
                $ms.Write($buf, 0, $r.Count)
            } while (-not $r.EndOfMessage)
            $obj = [Text.Encoding]::UTF8.GetString($ms.ToArray()) | ConvertFrom-Json
            if ($obj.id -eq $script:id) { return $obj.result }
        }
    }

    Send 'Emulation.setDeviceMetricsOverride' @{ width = $Width; height = $Height; deviceScaleFactor = 1; mobile = $false } | Out-Null
    foreach ($step in $Steps) {
        if ($step.eval) {
            $r = Send 'Runtime.evaluate' @{ expression = $step.eval; awaitPromise = $true; returnByValue = $true }
            if ($r.exceptionDetails) { "EVAL ERROR: $($r.exceptionDetails.exception.description)" }
            elseif ($null -ne $r.result.value) { "=> $($r.result.value)" }
            Start-Sleep -Milliseconds 600
        }
        if ($step.dialog) {
            # A Windows open/save dialog opened by the previous step: put the path in its file name box and
            # press its OK button, through UI Automation (sending keys would need the window to have focus).
            Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
            $A = [Windows.Automation.AutomationElement]
            $byPid = [Windows.Automation.PropertyCondition]::new($A::ProcessIdProperty, $app.Id)
            $dlg = $null
            foreach ($i in 1..30) {
                Start-Sleep -Milliseconds 300
                $dlg = $A::RootElement.FindAll('Children', $byPid) | ForEach-Object { $_.FindAll('Children', [Windows.Automation.Condition]::TrueCondition) } |
                    Where-Object { $_.Current.Name -eq $step.title -and $_.Current.ClassName -eq '#32770' } | Select-Object -First 1
                if ($dlg) { break }
            }
            if (-not $dlg) { "DIALOG NOT FOUND: $($step.title)" }
            else {
                # The file name box is the Edit with id 1148 (open dialogs) or 1001 (save dialogs); control 1 of class Button is Open/Save.
                # Set the text and press the button with window messages, which needs no focus.
                if (-not ('DogWalk.Win' -as [type])) {
                    Add-Type -Namespace DogWalk -Name Win -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);
[DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
'@
                }
                $all = $dlg.FindAll('Descendants', [Windows.Automation.Condition]::TrueCondition)
                $box = $all | Where-Object { $_.Current.AutomationId -in '1148', '1001' -and $_.Current.ClassName -eq 'Edit' } | Select-Object -First 1
                $ok = $all | Where-Object { $_.Current.AutomationId -eq '1' -and $_.Current.ClassName -eq 'Button' } | Select-Object -First 1
                if (-not $box -or -not $ok) { "DIALOG CONTROLS NOT FOUND: $($step.title)" }
                else {
                    [DogWalk.Win]::SendMessage([IntPtr]$box.Current.NativeWindowHandle, 0x000C, [IntPtr]::Zero, $step.dialog) | Out-Null  # WM_SETTEXT
                    [DogWalk.Win]::PostMessage([IntPtr]$ok.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null  # BM_CLICK
                    Start-Sleep -Milliseconds 1500
                }
            }
        }
        if ($step.shot) {
            $r = Send 'Page.captureScreenshot' @{ format = 'png' }
            $file = Join-Path $OutDir "$($step.shot).png"
            [IO.File]::WriteAllBytes($file, [Convert]::FromBase64String($r.data))
            "shot: $file"
        }
    }
    $ws.Dispose()
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    "--- warnings and errors in the app log ---"
    Get-ChildItem "$DataDir\logs" -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName | Select-String '\[(WRN|ERR|FTL)\]' }
    if ($temporary -and -not $Keep) { Start-Sleep -Milliseconds 500; Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue }
    else { "data folder kept: $DataDir" }
}
