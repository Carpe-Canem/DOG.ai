# Launches the built DOG.ai app against a throwaway data folder and drives it through the
# WebView2 debugging port, so a change can be checked in the real app.
#
#   -Steps    list of hashtables, run in order:
#               @{ eval = '<javascript>' }       run in the page (promises are awaited)
#               @{ shot = '<name>' }             save a picture of the page
#               @{ dialog = '<file path>'; title = '<dialog title>' }  fill in a Windows open/save dialog that the previous
#                                                step opened (it types the path and presses Enter)
#               @{ ok = 'window title' }         press OK in a Windows message box the previous step opened
#               @{ press = 'ctrl+l' }            press a key in the page (ctrl/alt/shift + one letter)
#               @{ adopt = 30 }                  DOG.ai restarted itself (Lock now, idle lock, restore): wait up to this many
#                                                seconds for the old process to end and the new one to start, and follow it
#               @{ lock = @( <lock steps> ) }    drive the lock screen of the (adopted) process, then reconnect to its page;
#                                                add @{ stay = $true } inside to stay at the lock screen (no reconnect)
#               @{ tray = 'Lock now' }           open the tray icon's menu, print its items, and click the named one
#                                                (tray = '' only prints them)
#               @{ globalkey = 1500 }            hold Ctrl+Alt+Space this many ms in Notepad (started for it, closed after)
#               @{ close = 1 }                   close the main window (it hides in the tray when that is chosen in Settings)
#               @{ wait = 1500 }                 wait this many milliseconds
#               @{ log = 'text' }                print the app log lines containing this text
#               @{ realkey = 'ctrl+l' }          bring DOG.ai's window to the front and press real keys (ctrl/alt/shift + one
#                                                letter, or 'esc'), through Windows rather than the page
#               @{ winshot = 'name' }            save a picture of DOG.ai's window as Windows draws it (shows dialogs the page
#                                                cannot capture, such as the print preview)
#               @{ trayclick = 1 }               left-click the tray icon
#               @{ uiclick = 'Cancel' }          press the first button with this name in any of DOG.ai's windows (for example
#                                                in the print preview), through UI Automation
#               @{ procs = 'llama-server' }      print how many processes of that name are running (also in lock steps)
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
#               @{ click = 'Unlock' }                 press a button (Unlock, Hello, Forgot, SetPassword, Back, CancelLock,
#                                                     KeepLocked, OfferHello, NotNow)
#               @{ ok = 'window title' }              press OK in a message box the previous step opened
#               @{ read = 'Message' }                 print what a piece of text says (Message, Heading)
#               @{ shot = 'name' }                    save a picture of the lock screen
#               @{ wait = 1500 }                      wait this many milliseconds
#               @{ gone = 90 }                        wait up to this many seconds for the lock screen to close
#                                                     (Windows Hello needs a person at the PC)
#               @{ tray = '' } / @{ globalkey = 1500 } / @{ log = 'text' } as in -Steps
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

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
if (-not ('DogWalk.Win' -as [type])) {
    Add-Type -Namespace DogWalk -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
public struct RECT { public int Left, Top, Right, Bottom; }
public static System.Collections.Generic.List<IntPtr> WindowsOf(int pid, string classStart) {
    var found = new System.Collections.Generic.List<IntPtr>();
    EnumWindows((h, _) => {
        int owner; GetWindowThreadProcessId(h, out owner);
        if (owner == pid) {
            var name = new System.Text.StringBuilder(256); GetClassName(h, name, 256);
            if (name.ToString().StartsWith(classStart)) found.Add(h);
        }
        return true;
    }, IntPtr.Zero);
    return found;
}
'@
    [DogWalk.Win]::SetProcessDPIAware() | Out-Null
}
$A = [Windows.Automation.AutomationElement]

$script:app = Start-Process $exe -PassThru
$script:pids = [Collections.Generic.List[int]]::new()
$script:pids.Add($script:app.Id)
$script:ws = $null

function ByPid { [Windows.Automation.PropertyCondition]::new($A::ProcessIdProperty, $script:app.Id) }

function Find-LockPart($id) {
    foreach ($window in $A::RootElement.FindAll('Children', (ByPid))) {
        $found = $window.FindFirst('Descendants', [Windows.Automation.PropertyCondition]::new($A::AutomationIdProperty, $id))
        if ($found) { return $found }
    }
    return $null
}

function Show-Log($text) {
    Get-ChildItem "$DataDir\logs" -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName | Select-String -SimpleMatch $text } | ForEach-Object { "log: $($_.Line)" }
}

function Press-Ok($title) {
    $box = $null
    foreach ($i in 1..20) {
        Start-Sleep -Milliseconds 250
        $box = $A::RootElement.FindAll('Children', (ByPid)) | ForEach-Object { $_; $_.FindAll('Children', [Windows.Automation.Condition]::TrueCondition) } |
            Where-Object { $_.Current.Name -eq $title -and $_.Current.ClassName -eq '#32770' } | Select-Object -First 1
        if ($box) { break }
    }
    $okButton = if ($box) { $box.FindFirst('Descendants', [Windows.Automation.PropertyCondition]::new($A::AutomationIdProperty, '1')) }
    # A box with only OK gives that button id 2.
    if (-not $okButton -and $box) { $okButton = $box.FindFirst('Descendants', [Windows.Automation.PropertyCondition]::new($A::AutomationIdProperty, '2')) }
    if (-not $okButton) { "MESSAGE BOX NOT FOUND: $title"; return }
    # A Windows message box button is pressed with a window message (it offers no UI Automation "invoke").
    [DogWalk.Win]::PostMessage([IntPtr]$okButton.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null  # BM_CLICK
    Start-Sleep -Milliseconds 800
}

function Open-Tray($click) {
    # The tray icon's hidden window gets the same message Windows sends on a right click (WM_USER + 1024, WM_RBUTTONUP).
    $windows = [DogWalk.Win]::WindowsOf($script:app.Id, 'WindowsForms10.Window')
    if ($windows.Count -eq 0) { "TRAY: no tray icon"; return }
    foreach ($w in $windows) { [DogWalk.Win]::PostMessage($w, 0x0800, [IntPtr]::Zero, [IntPtr]0x0205) | Out-Null }
    Start-Sleep -Milliseconds 900
    $items = $A::RootElement.FindAll('Descendants', [Windows.Automation.AndCondition]::new((ByPid),
        [Windows.Automation.PropertyCondition]::new($A::ControlTypeProperty, [Windows.Automation.ControlType]::MenuItem)))
    $names = @($items | ForEach-Object { $_.Current.Name })
    "tray menu: " + ($names -join ' | ')
    $target = if ($click) { $items | Where-Object { $_.Current.Name -eq $click } | Select-Object -First 1 }
    if ($target) { $target.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); "tray: clicked $click" }
    else {
        if ($click) { "TRAY ITEM NOT FOUND: $click" }
        [DogWalk.Win]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero); [DogWalk.Win]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)  # Escape closes it
    }
    Start-Sleep -Milliseconds 600
}

function Hold-GlobalKey($ms) {
    # Ctrl+Alt+Space is for other programs (inside DOG.ai it is ignored), so hold it in a Notepad started for this.
    $pad = Start-Process notepad -PassThru
    Start-Sleep -Milliseconds 1500
    $padWindow = $A::RootElement.FindAll('Children', [Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ClassName -eq 'Notepad' } | Select-Object -First 1
    if ($padWindow) { [DogWalk.Win]::SetForegroundWindow([IntPtr]$padWindow.Current.NativeWindowHandle) | Out-Null }
    Start-Sleep -Milliseconds 400
    $k = [DogWalk.Win]
    $k::keybd_event(0x11, 0, 0, [UIntPtr]::Zero); $k::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); $k::keybd_event(0x20, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds $ms
    $k::keybd_event(0x20, 0, 2, [UIntPtr]::Zero); $k::keybd_event(0x12, 0, 2, [UIntPtr]::Zero); $k::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1500
    Get-Process notepad -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -ge $pad.StartTime.AddSeconds(-1) } | Stop-Process -Force -ErrorAction SilentlyContinue
    "held Ctrl+Alt+Space for $ms ms"
}

function Front-Window {
    $w = $A::RootElement.FindAll('Children', (ByPid)) | Select-Object -First 1
    if (-not $w) { return [IntPtr]::Zero }
    $h = [IntPtr]$w.Current.NativeWindowHandle
    # Windows lets a program bring another to the front only just after a key press; a lone Alt counts.
    [DogWalk.Win]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [DogWalk.Win]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [DogWalk.Win]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 500
    return $h
}

function Real-Key($combo) {
    $h = Front-Window
    # Never type into someone else's window: only go on when DOG.ai really is in front.
    if ($h -eq [IntPtr]::Zero -or [DogWalk.Win]::GetForegroundWindow() -ne $h) { "REAL KEYS NOT SENT: DOG.ai is not the window in front"; return }
    $parts = $combo.ToLower() -split '\+'
    $mods = @(); if ($parts -contains 'ctrl') { $mods += 0x11 }; if ($parts -contains 'alt') { $mods += 0x12 }; if ($parts -contains 'shift') { $mods += 0x10 }
    $key = $parts[-1]
    $vk = if ($key -eq 'esc') { 0x1B } else { [int][char]$key.ToUpper() }
    foreach ($m in $mods) { [DogWalk.Win]::keybd_event([byte]$m, 0, 0, [UIntPtr]::Zero) }
    [DogWalk.Win]::keybd_event([byte]$vk, 0, 0, [UIntPtr]::Zero); [DogWalk.Win]::keybd_event([byte]$vk, 0, 2, [UIntPtr]::Zero)
    foreach ($m in $mods) { [DogWalk.Win]::keybd_event([byte]$m, 0, 2, [UIntPtr]::Zero) }
    Start-Sleep -Milliseconds 800
    "pressed $combo (real keys)"
}

function Win-Shot($name) {
    $w = $A::RootElement.FindAll('Children', (ByPid)) | Select-Object -First 1
    if (-not $w) { "NO WINDOW for winshot $name"; return }
    $handle = [IntPtr]$w.Current.NativeWindowHandle
    $rect = New-Object DogWalk.Win+RECT
    [DogWalk.Win]::GetWindowRect($handle, [ref]$rect) | Out-Null
    $bitmap = [Drawing.Bitmap]::new([Math]::Max(1, $rect.Right - $rect.Left), [Math]::Max(1, $rect.Bottom - $rect.Top))
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    [DogWalk.Win]::PrintWindow($handle, $hdc, 2) | Out-Null   # DOG.ai's window only, even if another window covers it
    $graphics.ReleaseHdc($hdc); $graphics.Dispose()
    $file = Join-Path $OutDir "$name.png"
    $bitmap.Save($file, [Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
    "shot: $file"
}

function Tray-Click {
    $windows = [DogWalk.Win]::WindowsOf($script:app.Id, 'WindowsForms10.Window')
    if ($windows.Count -eq 0) { "TRAY: no tray icon"; return }
    foreach ($w in $windows) {
        [DogWalk.Win]::PostMessage($w, 0x0800, [IntPtr]::Zero, [IntPtr]0x0201) | Out-Null
        [DogWalk.Win]::PostMessage($w, 0x0800, [IntPtr]::Zero, [IntPtr]0x0202) | Out-Null
    }
    Start-Sleep -Milliseconds 1200
    "tray: left-clicked"
}

function Ui-Click($name) {
    $cond = [Windows.Automation.AndCondition]::new((ByPid), [Windows.Automation.PropertyCondition]::new($A::NameProperty, $name))
    $button = $A::RootElement.FindAll('Descendants', $cond) | Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button } | Select-Object -First 1
    if (-not $button) { "UI BUTTON NOT FOUND: $name"; return }
    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1000
    "pressed button: $name"
}

function Adopt($seconds) {
    # A restart starts DOG.ai.exe with "--after <old pid>"; follow the new process.
    $old = $script:app.Id
    $next = $null
    foreach ($i in 1..($seconds * 2)) {
        $next = Get-CimInstance Win32_Process -Filter "Name='$(Split-Path $exe -Leaf)'" | Where-Object { $_.CommandLine -like "*--after $old*" } | Select-Object -First 1
        if ($next) { break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $next) { "ADOPT: no new DOG.ai started within $seconds seconds (old one exited: $($script:app.HasExited))"; return }
    $oldGone = $script:app.WaitForExit(20000)
    $script:app = Get-Process -Id $next.ProcessId
    $script:pids.Add($script:app.Id)
    if ($script:ws) { try { $script:ws.Dispose() } catch {} ; $script:ws = $null }
    "adopted new DOG.ai (pid $($script:app.Id)); old pid $old exited: $oldGone; arguments: $($next.CommandLine -replace '^.*?\.exe"?\s*','')"
}

function Run-LockSteps($steps) {
    $lockWindow = $null
    foreach ($i in 1..40) {
        $lockWindow = $A::RootElement.FindAll('Children', (ByPid)) | Where-Object { $_.FindFirst('Descendants', [Windows.Automation.PropertyCondition]::new($A::AutomationIdProperty, 'Password')) } | Select-Object -First 1
        if ($lockWindow) { break }
        # A lock screen hidden in the tray is not found until it is shown; tray and log steps may still run.
        if ($steps | Where-Object { $_.tray -ne $null -or $_.globalkey -or $_.log }) { if ($i -ge 6) { break } }
        Start-Sleep -Milliseconds 500
    }
    if (-not $lockWindow) { "lock screen not on screen (exited: $($script:app.HasExited))" }
    foreach ($step in $steps) {
        if ($step.set) {
            foreach ($id in $step.set.Keys) {
                $box = Find-LockPart $id
                if (-not $box) { "LOCK PART NOT FOUND: $id"; continue }
                $box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue([string]$step.set[$id])
            }
        }
        if ($step.click) {
            $button = Find-LockPart $step.click
            if (-not $button) { "LOCK PART NOT FOUND: $($step.click)" }
            elseif (-not $button.Current.IsEnabled) { "LOCK BUTTON DISABLED: $($step.click)" }
            else { $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 800 }
        }
        if ($step.ok) { Press-Ok $step.ok }
        if ($step.read) {
            $part = Find-LockPart $step.read
            if ($part) { "$($step.read): $($part.Current.Name)" } else { "$($step.read): (not on screen)" }
        }
        if ($null -ne $step.tray) { Open-Tray $step.tray }
        if ($step.globalkey) { Hold-GlobalKey $step.globalkey }
        if ($step.log) { Show-Log $step.log }
        if ($step.realkey) { Real-Key $step.realkey }
        if ($step.winshot) { Win-Shot $step.winshot }
        if ($step.trayclick) { Tray-Click }
        if ($step.uiclick) { Ui-Click $step.uiclick }
        if ($step.procs) { "$($step.procs) processes: $(@(Get-Process $step.procs -ErrorAction SilentlyContinue).Count)" }
        if ($step.wait) { Start-Sleep -Milliseconds $step.wait }
        if ($step.gone) {
            $closed = $false
            foreach ($i in 1..($step.gone * 2)) {
                # The heading is on every panel of the lock screen (the password box is not, during the Hello offer).
                if (-not (Find-LockPart 'Heading')) { $closed = $true; break }
                Start-Sleep -Milliseconds 500
            }
            if (-not $closed) { "LOCK SCREEN STILL SHOWING after $($step.gone) seconds" }
        }
        if ($step.shot) {
            if (-not $lockWindow) { $lockWindow = $A::RootElement.FindAll('Children', (ByPid)) | Where-Object { $_.FindFirst('Descendants', [Windows.Automation.PropertyCondition]::new($A::AutomationIdProperty, 'Password')) } | Select-Object -First 1 }
            if (-not $lockWindow) { "LOCK SCREEN NOT ON SCREEN for shot $($step.shot)"; continue }
            $handle = [IntPtr]$lockWindow.Current.NativeWindowHandle
            $rect = New-Object DogWalk.Win+RECT
            [DogWalk.Win]::GetWindowRect($handle, [ref]$rect) | Out-Null
            $bitmap = [Drawing.Bitmap]::new([Math]::Max(1, $rect.Right - $rect.Left), [Math]::Max(1, $rect.Bottom - $rect.Top))
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            $hdc = $graphics.GetHdc()
            [DogWalk.Win]::PrintWindow($handle, $hdc, 2) | Out-Null   # 2 = also what the graphics card drew
            $graphics.ReleaseHdc($hdc); $graphics.Dispose()
            $file = Join-Path $OutDir "$($step.shot).png"
            $bitmap.Save($file, [Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
            "shot: $file"
        }
    }
}

function Connect-Page {
    $target = $null
    foreach ($i in 1..60) {
        Start-Sleep -Milliseconds 500
        try {
            $all = (Invoke-WebRequest "http://127.0.0.1:$Port/json" -TimeoutSec 2).Content | ConvertFrom-Json
            $target = @($all) | Where-Object type -eq 'page' | Select-Object -First 1
        } catch {}
        if ($target) { break }
    }
    if (-not $target) { throw "The app did not show a page within 30 seconds (exited: $($script:app.HasExited)). Check $DataDir\logs." }
    Start-Sleep -Seconds 2
    $script:ws = [System.Net.WebSockets.ClientWebSocket]::new()
    $script:ws.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
    Send 'Emulation.setDeviceMetricsOverride' @{ width = $Width; height = $Height; deviceScaleFactor = 1; mobile = $false } | Out-Null
}

$script:id = 0
function Send($method, $params) {
    $script:id++
    $msg = @{ id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 6 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($msg)
    $script:ws.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, [Threading.CancellationToken]::None).Wait()
    while ($true) {
        $buf = [byte[]]::new(1048576); $ms = [IO.MemoryStream]::new()
        do {
            $r = $script:ws.ReceiveAsync([ArraySegment[byte]]::new($buf), [Threading.CancellationToken]::None).Result
            $ms.Write($buf, 0, $r.Count)
        } while (-not $r.EndOfMessage)
        $obj = [Text.Encoding]::UTF8.GetString($ms.ToArray()) | ConvertFrom-Json
        if ($obj.id -eq $script:id) { return $obj.result }
    }
}

function Press-Key($combo) {
    $parts = $combo.ToLower() -split '\+'
    $key = $parts[-1]
    $mods = 0
    if ($parts -contains 'alt') { $mods += 1 }
    if ($parts -contains 'ctrl') { $mods += 2 }
    if ($parts -contains 'shift') { $mods += 8 }
    $code = 'Key' + $key.ToUpper()
    $vk = [int][char]$key.ToUpper()
    Send 'Input.dispatchKeyEvent' @{ type = 'keyDown'; modifiers = $mods; key = $key; code = $code; windowsVirtualKeyCode = $vk } | Out-Null
    Send 'Input.dispatchKeyEvent' @{ type = 'keyUp'; modifiers = $mods; key = $key; code = $code; windowsVirtualKeyCode = $vk } | Out-Null
    "pressed $combo"
}

try {
    if ($LockSteps.Count -gt 0) {
        Run-LockSteps $LockSteps
        if ($LockOnly) { "lock screen still showing: $([bool](Find-LockPart 'Password')); app exited: $($script:app.HasExited)"; return }
    }
    Connect-Page

    foreach ($step in $Steps) {
        if ($step.adopt) { Adopt $step.adopt }
        if ($step.lock) {
            Run-LockSteps $step.lock
            if (-not ($step.lock | Where-Object { $_.stay })) { Connect-Page }
        }
        if ($step.realkey) { Real-Key $step.realkey }
        if ($step.winshot) { Win-Shot $step.winshot }
        if ($step.trayclick) { Tray-Click }
        if ($step.uiclick) { Ui-Click $step.uiclick }
        if ($null -ne $step.tray) { Open-Tray $step.tray }
        if ($step.globalkey) { Hold-GlobalKey $step.globalkey }
        if ($step.log) { Show-Log $step.log }
        if ($step.procs) { "$($step.procs) processes: $(@(Get-Process $step.procs -ErrorAction SilentlyContinue).Count)" }
        if ($step.close) {
            # Only the visible window (UI Automation lists no hidden ones); WM_CLOSE is what the title bar's X sends.
            foreach ($w in $A::RootElement.FindAll('Children', (ByPid))) { [DogWalk.Win]::PostMessage([IntPtr]$w.Current.NativeWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
            Start-Sleep -Milliseconds 1000
            "closed the window; still running: $(-not $script:app.HasExited)"
        }
        if ($step.ok) { Press-Ok $step.ok }
        if ($step.press) { Press-Key $step.press }
        if ($step.eval) {
            $r = Send 'Runtime.evaluate' @{ expression = $step.eval; awaitPromise = $true; returnByValue = $true }
            if ($r.exceptionDetails) { "EVAL ERROR: $($r.exceptionDetails.exception.description)" }
            elseif ($null -ne $r.result.value) { "=> $($r.result.value)" }
            Start-Sleep -Milliseconds 600
        }
        if ($step.dialog) {
            # A Windows open/save dialog opened by the previous step: put the path in its file name box and
            # press its OK button, through UI Automation (sending keys would need the window to have focus).
            $dlg = $null
            foreach ($i in 1..30) {
                Start-Sleep -Milliseconds 300
                $dlg = $A::RootElement.FindAll('Children', (ByPid)) | ForEach-Object { $_.FindAll('Children', [Windows.Automation.Condition]::TrueCondition) } |
                    Where-Object { $_.Current.Name -eq $step.title -and $_.Current.ClassName -eq '#32770' } | Select-Object -First 1
                if ($dlg) { break }
            }
            if (-not $dlg) { "DIALOG NOT FOUND: $($step.title)" }
            else {
                # The file name box is the Edit with id 1148 (open dialogs) or 1001 (save dialogs); control 1 of class Button is Open/Save.
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
        if ($step.wait) { Start-Sleep -Milliseconds $step.wait }
        if ($step.shot) {
            $r = Send 'Page.captureScreenshot' @{ format = 'png' }
            $file = Join-Path $OutDir "$($step.shot).png"
            [IO.File]::WriteAllBytes($file, [Convert]::FromBase64String($r.data))
            "shot: $file"
        }
    }
    if ($script:ws) { $script:ws.Dispose() }
}
finally {
    # Every DOG.ai this run started or followed, plus any restart still on its way.
    foreach ($p in $script:pids) { Stop-Process -Id $p -Force -ErrorAction SilentlyContinue }
    foreach ($p in $script:pids) {
        Get-CimInstance Win32_Process -Filter "Name='$(Split-Path $exe -Leaf)'" | Where-Object { $_.CommandLine -like "*--after $p*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    }
    "--- warnings and errors in the app log ---"
    Get-ChildItem "$DataDir\logs" -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName | Select-String '\[(WRN|ERR|FTL)\]' }
    if ($temporary -and -not $Keep) { Start-Sleep -Milliseconds 800; Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue }
    else { "data folder kept: $DataDir" }
}
