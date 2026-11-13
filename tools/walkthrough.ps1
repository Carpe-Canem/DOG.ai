# Launches the built DOG.ai app against a throwaway data folder and drives it through the
# WebView2 debugging port, so a change can be checked in the real app.
#
#   -Steps    list of @{ eval = '<javascript>' } and @{ shot = '<name>' } hashtables, run in order;
#             @{ dialog = '<file path>'; title = '<dialog title>' } fills in a Windows open/save dialog
#             that the previous step opened (it types the path and presses Enter)
#   -DataDir  data folder for the run (default: a fresh temp folder, deleted afterwards)
#   -OutDir   where screenshots are written (default: a temp folder, printed at the end)
#   -Exe      run this DOG.ai.exe instead of the Debug build (for example publish\win-x64\app\DOG.ai.exe; point at the one
#             in app\, not the launcher in the root, which exits as soon as it has started the app)
#   -Keep     keep the data folder, e.g. to restart and check that something persisted
#   -Portable run the exe in portable mode (with -Exe pointing at a folder that has portable.txt): no data folder is
#             forced, the app keeps its data in its own data\ folder, which is never deleted
#   -LockSteps steps for the lock screen (locked data), run before anything else because the app shows no page until
#             it is unlocked. The lock screen is a plain Windows window, driven by its controls' ids:
#               @{ set = @{ Password = 'secret' } }   fill in boxes (Password, RecoveryKey, NewPassword, RepeatPassword)
#               @{ check = 'UseHello' }               tick a box
#               @{ click = 'Unlock' }                 press a button (Unlock, Hello, Forgot, SetPassword, Back, CancelLock)
#               @{ ok = 'window title' }              press OK in a message box the previous step opened
#               @{ read = 'Message' }                 print what a piece of text says
#               @{ shot = 'name' }                    save a picture of the lock screen
#               @{ wait = 1500 }                      wait this many milliseconds
#               @{ gone = 90 }                        wait up to this many seconds for the lock screen to close
#                                                     (Windows Hello needs a person at the PC)
#   -LockOnly stop after the lock steps (for runs that never unlock)
#
# Build first: dotnet build DogAI.sln
param(
    [object[]]$Steps = @(@{ shot = 'start' }),
    [string]$DataDir,
    [string]$OutDir = (Join-Path ([IO.Path]::GetTempPath()) 'dogai-walkthrough'),
    [int]$Width = 1400,
    [int]$Height = 860,
    [int]$Port = 9333,
    [string]$Exe,
    [switch]$Keep,
    [switch]$Portable,
    [object[]]$LockSteps = @(),
    [switch]$LockOnly
)
$ErrorActionPreference = 'Stop'
$exe = if ($Exe) { $Exe } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'src\DogAI.App\bin\Debug\net9.0-windows10.0.19041.0\DOG.ai.exe' }
if (-not (Test-Path $exe)) { throw "Build the solution first; $exe does not exist." }
if ($Portable) {
    # The home is the exe's folder, or its parent when the exe is in an "app" folder (the tidy publish layout).
    $homeDir = Split-Path $exe -Parent
    if (-not (Test-Path (Join-Path $homeDir 'portable.txt')) -and (Split-Path $homeDir -Leaf) -eq 'app') { $homeDir = Split-Path $homeDir -Parent }
    if (-not (Test-Path (Join-Path $homeDir 'portable.txt'))) { throw "-Portable needs portable.txt next to $exe (or one folder above it, for an app folder)." }
    $temporary = $false
    $DataDir = Join-Path $homeDir 'data'
    Remove-Item Env:DOGAI_DATA_DIR -ErrorAction SilentlyContinue
} else {
    $temporary = -not $DataDir
    if ($temporary) { $DataDir = Join-Path ([IO.Path]::GetTempPath()) ("dogai-run-" + [Guid]::NewGuid().ToString('N')) }
    $env:DOGAI_DATA_DIR = $DataDir
}
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$Port"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$app = Start-Process $exe -PassThru
try {
    if ($LockSteps.Count -gt 0) {
        # The lock screen is a WPF window, not a web page: find it by the password box it holds and drive it through UI Automation.
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
        if (-not ('DogWalk.Shot' -as [type])) {
            Add-Type -Namespace DogWalk -Name Shot -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
public struct RECT { public int Left, Top, Right, Bottom; }
'@
            [DogWalk.Shot]::SetProcessDPIAware() | Out-Null
        }
        $A = [Windows.Automation.AutomationElement]
        $byPid = [Windows.Automation.PropertyCondition]::new($A::ProcessIdProperty, $app.Id)
        function Find-LockPart($id) {
            foreach ($window in $A::RootElement.FindAll('Children', $byPid)) {
                $found = $window.FindFirst('Descendants', [Windows.Automation.PropertyCondition]::new($A::AutomationIdProperty, $id))
                if ($found) { return $found }
            }
            return $null
        }
        $lockWindow = $null
        foreach ($i in 1..40) {
            Start-Sleep -Milliseconds 500
            if (Find-LockPart 'Password') {
                $lockWindow = $A::RootElement.FindAll('Children', $byPid) | Where-Object { $_.FindFirst('Descendants', [Windows.Automation.PropertyCondition]::new($A::AutomationIdProperty, 'Password')) } | Select-Object -First 1
                break
            }
        }
        if (-not $lockWindow) { throw "The lock screen did not appear within 20 seconds (exited: $($app.HasExited)). Check $DataDir\logs." }
        foreach ($step in $LockSteps) {
            if ($step.set) {
                foreach ($id in $step.set.Keys) {
                    $box = Find-LockPart $id
                    if (-not $box) { "LOCK PART NOT FOUND: $id"; continue }
                    $box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue([string]$step.set[$id])
                }
            }
            if ($step.check) {
                $box = Find-LockPart $step.check
                if (-not $box) { "LOCK PART NOT FOUND: $($step.check)" }
                else {
                    $toggle = $box.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
                    if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle() }
                }
            }
            if ($step.click) {
                $button = Find-LockPart $step.click
                if (-not $button) { "LOCK PART NOT FOUND: $($step.click)" }
                elseif (-not $button.Current.IsEnabled) { "LOCK BUTTON DISABLED: $($step.click)" }
                else { $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 800 }
            }
            if ($step.ok) {
                $box = $null
                foreach ($i in 1..20) {
                    Start-Sleep -Milliseconds 250
                    $box = $A::RootElement.FindAll('Children', $byPid) | ForEach-Object { $_; $_.FindAll('Children', [Windows.Automation.Condition]::TrueCondition) } |
                        Where-Object { $_.Current.Name -eq $step.ok -and $_.Current.ClassName -eq '#32770' } | Select-Object -First 1
                    if ($box) { break }
                }
                $okButton = if ($box) { $box.FindFirst('Descendants', [Windows.Automation.PropertyCondition]::new($A::AutomationIdProperty, '1')) }
                if (-not $okButton) { "MESSAGE BOX NOT FOUND: $($step.ok)" }
                else {
                    # A Windows message box button is pressed with a window message (it offers no UI Automation "invoke").
                    [DogWalk.Shot]::PostMessage([IntPtr]$okButton.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null  # BM_CLICK
                    Start-Sleep -Milliseconds 800
                }
            }
            if ($step.read) {
                $part = Find-LockPart $step.read
                if ($part) { "$($step.read): $($part.Current.Name)" } else { "$($step.read): (not on screen)" }
            }
            if ($step.wait) { Start-Sleep -Milliseconds $step.wait }
            if ($step.gone) {
                # Wait (up to this many seconds) for the lock screen to close, e.g. while Windows Hello asks for a face or PIN.
                $closed = $false
                foreach ($i in 1..($step.gone * 2)) {
                    if (-not (Find-LockPart 'Password')) { $closed = $true; break }
                    Start-Sleep -Milliseconds 500
                }
                if (-not $closed) { "LOCK SCREEN STILL SHOWING after $($step.gone) seconds" }
            }
            if ($step.shot) {
                $handle = [IntPtr]$lockWindow.Current.NativeWindowHandle
                $rect = New-Object DogWalk.Shot+RECT
                [DogWalk.Shot]::GetWindowRect($handle, [ref]$rect) | Out-Null
                $bitmap = [Drawing.Bitmap]::new([Math]::Max(1, $rect.Right - $rect.Left), [Math]::Max(1, $rect.Bottom - $rect.Top))
                $graphics = [Drawing.Graphics]::FromImage($bitmap)
                $hdc = $graphics.GetHdc()
                [DogWalk.Shot]::PrintWindow($handle, $hdc, 2) | Out-Null   # 2 = also what the graphics card drew
                $graphics.ReleaseHdc($hdc); $graphics.Dispose()
                $file = Join-Path $OutDir "$($step.shot).png"
                $bitmap.Save($file, [Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
                "shot: $file"
            }
        }
        if ($LockOnly) { "lock screen still showing: $([bool](Find-LockPart 'Password')); app exited: $($app.HasExited)"; return }
    }

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
