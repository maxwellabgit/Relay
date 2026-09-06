<#
.SYNOPSIS
  End-to-end smoke test of the real Relay desktop app.

.DESCRIPTION
  Builds the solution, launches Relay.exe against a throwaway data root, drives it exactly the way a
  user does (the window-scoped chords, typing into the capture surface, clicking Approve), and checks
  both the ledger and the rendered UI after every step:

    1. idle                      window up, both chords registered for this window
    2. Ctrl+X "create project"   plan -> proposal awaiting approval, nothing written yet
    3. Approve (UI Automation)   project folder created, turn completed
    4. Ctrl+Alt note             one sentence filed under the project, the other lands in the Inbox
    5. Ctrl+X recall             answer cites the filed note
    6. close the window          clean shutdown recorded, no incidents

  Screenshots and the ledger are written to the output folder. Exit code 0 means every check passed.
  Requires an interactive desktop session (the chords are real key presses) and nothing else stealing
  focus while it runs (about 40 seconds).

.PARAMETER NoBuild
  Skip `dotnet build`; use the existing Debug output.

.PARAMETER Keep
  Keep the throwaway data root and the seeded project folder after a successful run.

.PARAMETER OutDir
  Where screenshots, the ledger copy and the report go. Default: %TEMP%\relay-ui-smoke\<timestamp>.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\ui-smoke.ps1
#>
[CmdletBinding()]
param(
    [switch] $NoBuild,
    [switch] $Keep,
    [string] $OutDir = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SmokeWin32 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  // Ctrl+Alt is a modifier-only chord; SendKeys cannot press modifiers alone, so press them raw.
  public static void CtrlAlt() {
    keybd_event(0x11, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
    keybd_event(0x12, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
    keybd_event(0x12, 0, 2, UIntPtr.Zero);
    keybd_event(0x11, 0, 2, UIntPtr.Zero);
  }
}
"@

$repo = Split-Path -Parent $PSScriptRoot
if ($OutDir -eq "") { $OutDir = Join-Path $env:TEMP ("relay-ui-smoke\" + (Get-Date -Format "yyyyMMdd-HHmmss")) }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$report = New-Object System.Collections.Generic.List[string]
$failures = 0

function Log($text) { Write-Host $text; $script:report.Add($text) }
function Check($name, $ok, $detail = "") {
    if ($ok) { Log ("  PASS  {0}{1}" -f $name, $(if ($detail) { "  ($detail)" } else { "" })) }
    else { $script:failures++; Log ("  FAIL  {0}{1}" -f $name, $(if ($detail) { "  ($detail)" } else { "" })) }
}

# ---------------------------------------------------------------------------------------------------
# Toolchain: dotnet may not be on PATH (the local install lives under %LOCALAPPDATA%).
# ---------------------------------------------------------------------------------------------------
$dotnetDir = $null
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd) { $dotnetDir = Split-Path -Parent $cmd.Source }
foreach ($candidate in @((Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"), (Join-Path $env:ProgramFiles "dotnet"))) {
    if (-not $dotnetDir -and (Test-Path (Join-Path $candidate "dotnet.exe"))) { $dotnetDir = $candidate }
}
if (-not $dotnetDir) { throw "dotnet.exe not found on PATH, %LOCALAPPDATA%\Microsoft\dotnet or %ProgramFiles%\dotnet." }
$env:PATH = "$dotnetDir;$env:PATH"
$env:DOTNET_ROOT = $dotnetDir   # the framework-dependent Relay.exe needs this when dotnet is not in the default location
Log "dotnet: $dotnetDir"

if (-not $NoBuild) {
    Log "building Relay.slnx (Debug)…"
    & dotnet build (Join-Path $repo "Relay.slnx") -nologo -v q 2>&1 | Where-Object { $_ -match "error|Build succeeded" } | ForEach-Object { Log "  $_" }
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}
$exe = Get-ChildItem (Join-Path $repo "src\Relay.Desktop\bin\Debug") -Recurse -Filter Relay.exe | Where-Object { $_.FullName -notmatch "\\x64\\" } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw "Relay.exe not found under src\Relay.Desktop\bin\Debug" }
Log "exe: $($exe.FullName)  ($($exe.LastWriteTime))"

# ---------------------------------------------------------------------------------------------------
# Fixture: a throwaway data root and a registered project folder, so the voice path can create projects.
# ---------------------------------------------------------------------------------------------------
$stamp = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$dataRoot = Join-Path $env:TEMP "relay-ui-smoke-data-$stamp"
$projects = Join-Path $env:TEMP "relay-ui-smoke-projects-$stamp"
New-Item -ItemType Directory -Force -Path (Join-Path $dataRoot "config"), $projects | Out-Null
$roots = @{ schemaVersion = 1; roots = @(@{ path = $projects; addedAt = (Get-Date).ToUniversalTime().ToString("o"); label = "smoke" }) }
Set-Content -Path (Join-Path $dataRoot "config\workspaces.json") -Value ($roots | ConvertTo-Json -Depth 4) -Encoding UTF8
$ledgerPath = Join-Path $dataRoot "ledger\relay-ledger.jsonl"
Log "data root: $dataRoot"
Log "project folder: $projects"

# ---------------------------------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------------------------------
function Read-Ledger {
    # Relay holds the ledger open for append; read with a share mode that tolerates the writer.
    if (-not (Test-Path $ledgerPath)) { return @() }
    $fs = [System.IO.File]::Open($ledgerPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try { $reader = New-Object System.IO.StreamReader($fs); $text = $reader.ReadToEnd() } finally { $fs.Dispose() }
    $records = @()
    foreach ($line in ($text -split "`n")) {
        $line = $line.TrimEnd("`r")
        if ($line.Length -eq 0) { continue }
        try { $records += ($line | ConvertFrom-Json) } catch { }
    }
    return $records
}

function Wait-Event($type, $timeoutSeconds = 15, $where = $null) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $hits = @(Read-Ledger | Where-Object { $_.type -eq $type })
        if ($where) { $hits = @($hits | Where-Object $where) }
        if ($hits.Count -gt 0) { return $hits[-1] }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Count-Event($type) { return @(Read-Ledger | Where-Object { $_.type -eq $type }).Count }

function Wait-Until($condition, $timeoutSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Shot($name) {
    $r = New-Object SmokeWin32+RECT
    [SmokeWin32]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { Log "  (no window rect for $name; window gone?)"; return }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $path = Join-Path $OutDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Log "  screenshot $path"
}

function Focus-Relay {
    [SmokeWin32]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 400
    return ([SmokeWin32]::GetForegroundWindow() -eq $hwnd)
}

function Ui-Root { return [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) }

function Ui-FindText($fragment) {
    $all = (Ui-Root).FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) { if ($e.Current.Name -and $e.Current.Name.Contains($fragment)) { return $e.Current.Name } }
    return $null
}

function Ui-Invoke($buttonName) {
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $buttonName)))
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        $btn = (Ui-Root).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($btn) { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Type-Text($text) { [System.Windows.Forms.SendKeys]::SendWait($text); Start-Sleep -Milliseconds 300 }
function Press-CommandKey { [System.Windows.Forms.SendKeys]::SendWait("^x"); Start-Sleep -Milliseconds 500 }
function Press-NoteKey { [SmokeWin32]::CtrlAlt(); Start-Sleep -Milliseconds 500 }

# ---------------------------------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------------------------------
$env:RELAY_DATA_ROOT = $dataRoot
$proc = Start-Process -FilePath $exe.FullName -PassThru
Remove-Item Env:RELAY_DATA_ROOT
$hwnd = [IntPtr]::Zero
try {
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and $hwnd -eq [IntPtr]::Zero) {
        Start-Sleep -Milliseconds 500
        if ($proc.HasExited) { break }
        $proc.Refresh(); $hwnd = $proc.MainWindowHandle
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw "Relay window did not appear (process exited: $($proc.HasExited))" }
    Start-Sleep -Seconds 2

    Log ""; Log "== 1. idle =="
    Check "window is foreground" (Focus-Relay)
    $started = Wait-Event "session.started" 10
    Check "session.started recorded" ($null -ne $started)
    $noteKey = Wait-Event "hotkey.registered" 10 { $_.data.name -eq "NOTE_KEY" }
    $commandKey = Wait-Event "hotkey.registered" 10 { $_.data.name -eq "COMMAND_KEY" }
    Check "NOTE_KEY registered for this window" ($noteKey -and $noteKey.data.scope -eq "window") "$($noteKey.data.chord)"
    Check "COMMAND_KEY registered for this window" ($commandKey -and $commandKey.data.scope -eq "window") "$($commandKey.data.chord)"
    Check "state IDLE" ($null -ne (Wait-Event "state.changed" 10 { $_.data.to -eq "IDLE" }))
    Check "UI shows the chords" ($null -ne (Ui-FindText "NOTE"))
    Shot "1-idle"

    Log ""; Log "== 2. Ctrl+X: create project Atlas =="
    Focus-Relay | Out-Null
    Press-CommandKey
    Check "COMMAND_CAPTURE entered" ($null -ne (Wait-Event "state.changed" 5 { $_.data.to -eq "COMMAND_CAPTURE" }))
    Type-Text "create project Atlas"
    Press-CommandKey
    Check "AWAITING_APPROVAL reached" ($null -ne (Wait-Event "state.changed" 15 { $_.data.to -eq "AWAITING_APPROVAL" }))
    Check "capture committed verbatim" ($null -ne (Wait-Event "capture.committed" 5 { $_.data.text -eq "create project Atlas" }))
    Check "create_project proposed by rules" ($null -ne (Wait-Event "proposal.received" 5 { $_.data.action -eq "create_project" }))
    Check "nothing executed before approval" ((Count-Event "execution.started") -eq 0)
    Check "UI shows the proposal" ($null -ne (Ui-FindText "create_project"))
    Shot "2-awaiting-approval"

    Log ""; Log "== 3. Approve =="
    Check "Approve button invoked" (Ui-Invoke "Approve")
    Check "approval recorded by user" ($null -ne (Wait-Event "approval.granted" 10 { $_.data.by -eq "user" }))
    $created = Wait-Event "project.created" 15
    Check "project.created" ($null -ne $created)
    Check "turn completed (executed)" ($null -ne (Wait-Event "turn.completed" 10))
    $atlas = Join-Path $projects "atlas"
    Check "project folder exists inside the registered folder" (Test-Path $atlas) $atlas
    Check "COMPLETED reached" ($null -ne (Wait-Event "state.changed" 10 { $_.data.to -eq "COMPLETED" }))
    Start-Sleep -Milliseconds 800
    Check "UI lists the project as active" ($null -ne (Ui-FindText "atlas · active"))
    Shot "3-executed"
    Start-Sleep -Seconds 5   # let the COMPLETED receipt return to IDLE

    Log ""; Log "== 4. Ctrl+Alt: silent note (one filed, one to the Inbox) =="
    Focus-Relay | Out-Null
    Press-NoteKey
    Check "NOTE_CAPTURE entered" ($null -ne (Wait-Event "state.changed" 5 { $_.data.to -eq "NOTE_CAPTURE" }))
    Type-Text "We decided the Atlas beta ships on October 14. Buy compost for the garden this weekend."
    Press-NoteKey
    Check "note routed to atlas" ($null -ne (Wait-Event "note.routed" 15))
    Check "second sentence left unrouted" ($null -ne (Wait-Event "note.routing_deferred" 10))
    Check "no command turn started by a note" ((Count-Event "command.recorded") -eq 1)
    Start-Sleep -Milliseconds 800
    Check "UI Inbox shows the unrouted note" ($null -ne (Ui-FindText "compost"))
    Check "UI Inbox counts exactly one unrouted note" ($null -ne (Ui-FindText "1 unrouted"))
    Check "Review stays empty (routing is not a Review item)" ($null -ne (Ui-FindText "Nothing awaiting your decision."))
    Shot "4-inbox"
    Start-Sleep -Seconds 5

    Log ""; Log "== 5. Ctrl+X: recall =="
    Focus-Relay | Out-Null
    Press-CommandKey
    Type-Text "what did I decide about the atlas beta"
    Press-CommandKey
    Check "recall turn completed" (Wait-Until { (Count-Event "turn.completed") -ge 2 } 15)
    Check "search tool was called" ($null -ne (Wait-Event "tool.called" 5))
    Start-Sleep -Milliseconds 800
    Check "UI answer cites the filed decision" ($null -ne (Ui-FindText "October 14"))
    Shot "5-recall"

    Log ""; Log "== 6. close =="
    $closed = $proc.CloseMainWindow()
    $exited = $proc.WaitForExit(15000)
    Check "window closed cleanly" ($closed -and $exited)
    $records = @(Read-Ledger)
    $last = $records[-1]
    Check "last ledger record is session.ended" ($last.type -eq "session.ended") "reason $($last.data.reason), final state $($last.data.finalState)"
    Check "no app.failed records" ((@($records | Where-Object { $_.type -eq "app.failed" })).Count -eq 0)
    Check "no relay events (Relay never synthesizes input)" ((@($records | Where-Object { $_.type -like "flow.*" })).Count -eq 0)
    Check "no incidents written" (-not (Test-Path (Join-Path $dataRoot "incidents")) -or (@(Get-ChildItem (Join-Path $dataRoot "incidents") -File -ErrorAction SilentlyContinue)).Count -eq 0)
    Check "staging draft cleared" (-not (Test-Path (Join-Path $dataRoot "staging\drafts\current.json")))
}
catch {
    $failures++
    Log "  ERROR $($_.Exception.Message)"
}
finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500 }
    if (Test-Path $ledgerPath) { Copy-Item $ledgerPath (Join-Path $OutDir "relay-ledger.jsonl") -Force }
    $verdict = $(if ($failures -eq 0) { "PASS" } else { "FAIL ($failures)" })
    Log ""; Log "== $verdict ==  artifacts: $OutDir"
    Set-Content -Path (Join-Path $OutDir "report.txt") -Value $report -Encoding UTF8
    if ($failures -eq 0 -and -not $Keep) {
        Remove-Item -Recurse -Force $dataRoot, $projects -ErrorAction SilentlyContinue
    } else {
        Log "kept data root $dataRoot and project folder $projects"
    }
}
exit $(if ($failures -eq 0) { 0 } else { 1 })
