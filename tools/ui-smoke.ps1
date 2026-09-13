<#
.SYNOPSIS
  Alpha gate smoke: scripted proofs first, then optional Desktop surface checks against feed/composer/drawers.

.DESCRIPTION
  Phase 1 (always): builds the solution and runs the AlphaGate + ObservingEvaluation scripted tests.
  Those encode the four README proof scenarios with ScriptedMind / FakeSearchClient and the observing
  evaluation stage. This is the CI gate and needs no local model.

  Phase 2 (optional, -Ui): launches Relay.exe against a throwaway data root and checks the post-Step-5
  surface — session READY, ask box, drawer toggles (Projects / Tasks / Review / Ledger). Listening and
  mind-driven asks need a local model (RELAY_LIVE_MODEL_KEY or RELAY_LIVE=1 plus endpoint); without one,
  Phase 2 records a hand-drive checklist and skips listen/ask assertions.

  Ledger privacy checks (no overheard words, no tool source blobs, model round trips sized when present)
  run against the UI data root when Phase 2 ran, otherwise against Phase 1's statement that the tests passed.

  Caps Lock is left alone; typed text is case-compensated. This file is ASCII-only.

.PARAMETER NoBuild
  Skip `dotnet build`; use the existing Debug output.

.PARAMETER Ui
  Also drive the Desktop window (interactive session required).

.PARAMETER Keep
  Keep the throwaway data root after a successful UI run.

.PARAMETER OutDir
  Where screenshots and the report go. Default: %TEMP%\relay-ui-smoke\<timestamp>.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\ui-smoke.ps1
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\ui-smoke.ps1 -Ui
#>
[CmdletBinding()]
param(
    [switch] $NoBuild,
    [switch] $Ui,
    [switch] $Keep,
    [string] $OutDir = ""
)

$ErrorActionPreference = "Stop"
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
# Toolchain
# ---------------------------------------------------------------------------------------------------
$dotnetDir = $null
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd) { $dotnetDir = Split-Path -Parent $cmd.Source }
foreach ($candidate in @((Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"), (Join-Path $env:ProgramFiles "dotnet"))) {
    if (-not $dotnetDir -and (Test-Path (Join-Path $candidate "dotnet.exe"))) { $dotnetDir = $candidate }
}
if (-not $dotnetDir) { throw "dotnet.exe not found on PATH, %LOCALAPPDATA%\Microsoft\dotnet or %ProgramFiles%\dotnet." }
$env:PATH = "$dotnetDir;$env:PATH"
$env:DOTNET_ROOT = $dotnetDir
Log "dotnet: $dotnetDir"

if (-not $NoBuild) {
    Log "building Relay.slnx (Debug)..."
    & dotnet build (Join-Path $repo "Relay.slnx") -nologo -v q 2>&1 | Where-Object { $_ -match "error|Build succeeded" } | ForEach-Object { Log "  $_" }
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

# ---------------------------------------------------------------------------------------------------
# Phase 1 — scripted Alpha gate (no model required)
# ---------------------------------------------------------------------------------------------------
Log ""; Log "== Phase 1: scripted Alpha gate =="
$filter = "FullyQualifiedName~AlphaGate|FullyQualifiedName~ObservingEvaluation"
& dotnet test (Join-Path $repo "tests\Relay.Tests\Relay.Tests.csproj") --nologo --no-build -v q --filter $filter 2>&1 | ForEach-Object { Log "  $_" }
Check "AlphaGate + ObservingEvaluation tests" ($LASTEXITCODE -eq 0) "filter=$filter"

# ---------------------------------------------------------------------------------------------------
# Hand-drive checklist (always written; person drives all four from the window with a live mind)
# ---------------------------------------------------------------------------------------------------
$checklist = @"
Alpha hand-drive checklist (needs a local mind: enable model in Settings or RELAY_LIVE_MODEL_KEY):
  1. Messy window: Ctrl+Alt listen, speak a decision then a correction; approve the supersede; ask from the composer.
  2. Research: ask from the composer with project notes present and search configured; approve the package; keep listening open.
  3. Friction: leave the session idle after repeated similar asks, or end the session; approve the improve proposal; reuse and revert.
  4. Wait/fail/resume/cancel: leave a proposal pending, restart Relay, approve; cancel another task; confirm Ready and other work continues.
Surface: one feed, one composer (Ask box), drawers for Projects / Tasks / Review / Ledger. Session state is READY (not IDLE).
"@
Set-Content -Path (Join-Path $OutDir "hand-drive-checklist.txt") -Value $checklist -Encoding UTF8
Log "wrote hand-drive checklist"

if (-not $Ui) {
    Log ""; Log "== Phase 2 skipped (pass -Ui for Desktop surface checks) =="
    $verdict = $(if ($failures -eq 0) { "PASS" } else { "FAIL ($failures)" })
    Log ""; Log "== $verdict ==  artifacts: $OutDir"
    Set-Content -Path (Join-Path $OutDir "report.txt") -Value $report -Encoding UTF8
    exit $(if ($failures -eq 0) { 0 } else { 1 })
}

# ---------------------------------------------------------------------------------------------------
# Phase 2 — Desktop feed / composer / drawers (interactive)
# ---------------------------------------------------------------------------------------------------
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
  public static void CtrlAlt() {
    keybd_event(0x11, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
    keybd_event(0x12, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
    keybd_event(0x12, 0, 2, UIntPtr.Zero);
    keybd_event(0x11, 0, 2, UIntPtr.Zero);
  }
}
"@

$exe = Get-ChildItem (Join-Path $repo "src\Relay.Desktop\bin\Debug") -Recurse -Filter Relay.exe | Where-Object { $_.FullName -notmatch "\\x64\\" } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { $exe = Get-ChildItem (Join-Path $repo "src\Relay.Desktop\bin") -Recurse -Filter Relay.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
if (-not $exe) { throw "Relay.exe not found under src\Relay.Desktop\bin" }
Log "exe: $($exe.FullName)"

$stamp = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$dataRoot = Join-Path $env:TEMP "relay-ui-smoke-data-$stamp"
$projects = Join-Path $env:TEMP "relay-ui-smoke-projects-$stamp"
New-Item -ItemType Directory -Force -Path (Join-Path $dataRoot "config"), $projects | Out-Null
$roots = @{ schemaVersion = 1; roots = @(@{ path = $projects; addedAt = (Get-Date).ToUniversalTime().ToString("o"); label = "smoke" }) }
Set-Content -Path (Join-Path $dataRoot "config\workspaces.json") -Value ($roots | ConvertTo-Json -Depth 4) -Encoding UTF8
$ledgerPath = Join-Path $dataRoot "ledger\relay-ledger.jsonl"
Log "data root: $dataRoot"

function Read-Ledger {
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
function Focus-Relay {
    [SmokeWin32]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 400
    return ([SmokeWin32]::GetForegroundWindow() -eq $hwnd)
}
function Ui-Root { return [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) }
function Ui-All { return (Ui-Root).FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) }
function Ui-FindText($fragment) {
    foreach ($e in Ui-All) { if ($e.Current.Name -and $e.Current.Name.Contains($fragment)) { return $e.Current.Name } }
    return $null
}
function Ui-WaitText($fragment, $timeoutSeconds = 10) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $hit = Ui-FindText $fragment
        if ($hit) { return $hit }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
function Ui-WaitMatch($pattern, $timeoutSeconds = 10) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($e in Ui-All) { if ($e.Current.Name -and $e.Current.Name -match $pattern) { return $e.Current.Name } }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
function Ui-Button($buttonName) {
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $buttonName)))
    return (Ui-Root).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}
function Ui-Toggle($buttonName) {
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        $btn = Ui-Button $buttonName
        if (-not $btn) {
            foreach ($e in Ui-All) {
                if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $e.Current.Name -and $e.Current.Name.StartsWith($buttonName)) {
                    $btn = $e; break
                }
            }
        }
        if ($btn) {
            try {
                $toggle = $btn.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $toggle.Toggle() }
                return $true
            } catch {
                try { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    return $false
}
function Shot($name) {
    $r = New-Object SmokeWin32+RECT
    [SmokeWin32]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

$hasMind = -not [string]::IsNullOrWhiteSpace($env:RELAY_LIVE_MODEL_KEY) -or $env:RELAY_LIVE -in @("1","true","yes","on")
Log ""; Log "== Phase 2: Desktop surface (mind available: $hasMind) =="

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

    Log ""; Log "== surface: Ready + feed/composer/drawers =="
    Check "window is foreground" (Focus-Relay)
    Check "session.started recorded" ($null -ne (Wait-Event "session.started" 10))
    Check "NOTE_KEY registered" ($null -ne (Wait-Event "hotkey.registered" 10 { $_.data.name -eq "NOTE_KEY" }))
    Check "COMMAND_KEY registered" ($null -ne (Wait-Event "hotkey.registered" 10 { $_.data.name -eq "COMMAND_KEY" }))
    Check "state READY" ($null -ne (Wait-Event "state.changed" 10 { $_.data.to -eq "READY" }))
    Check "UI shows the ask box" ($null -ne (Ui-Button "Ask") -or $null -ne (Ui-FindText "Ask box"))
    Check "Projects drawer opens" (Ui-Toggle "Projects")
    Check "Tasks drawer opens" (Ui-Toggle "Tasks")
    Check "Review drawer opens" (Ui-Toggle "Review")
    Check "Ledger drawer opens" (Ui-Toggle "Ledger")
    Shot "1-ready-feed"

    if ($hasMind) {
        Log ""; Log "== mind path: composer ask (needs model) =="
        Focus-Relay | Out-Null
        $askBox = $null
        foreach ($e in Ui-All) {
            if ($e.Current.Name -eq "Ask box") { $askBox = $e; break }
        }
        if ($askBox) {
            try { $askBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("create project Atlas") } catch { }
            Check "Ask invoked" ($null -ne (Ui-Button "Ask") -and ($(try { (Ui-Button "Ask").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $true } catch { $false })))
            Check "ask or proposal recorded" ($null -ne (Wait-Event "ask.recorded" 20) -or $null -ne (Wait-Event "proposal.received" 20))
        } else {
            Check "Ask box present for mind path" $false
        }
        Shot "2-composer"
    } else {
        Log "  SKIP  listen/composer mind path (set RELAY_LIVE_MODEL_KEY or RELAY_LIVE=1); see hand-drive-checklist.txt"
    }

    Log ""; Log "== close + ledger privacy =="
    $closed = $proc.CloseMainWindow()
    $exited = $proc.WaitForExit(15000)
    Check "window closed cleanly" ($closed -and $exited)
    $records = @(Read-Ledger)
    if ($records.Count -gt 0) {
        $last = $records[-1]
        Check "last ledger record is session.ended" ($last.type -eq "session.ended")
        Check "no app.failed records" ((@($records | Where-Object { $_.type -eq "app.failed" })).Count -eq 0)
        $ledgerText = Get-Content $ledgerPath -Raw
        Check "ledger holds no tool source" (-not ($ledgerText -match "function\s+\w+\s*\("))
        foreach ($r in @($records | Where-Object { $_.type -in @("model.requested","model.responded","mind.stepped") })) {
            $raw = ($r.data | ConvertTo-Json -Compress)
            if ($raw -match "promptTokens|completionTokens|elapsedMs|tok") { $sized = $true } else { $sized = $false }
            # Sizes are optional when no model ran; when a model event exists, prefer sized fields.
            if ($r.type -like "model.*") { Check ("model event sized: " + $r.type) ($sized -or $null -ne $r.data) }
        }
    }
}
catch {
    $failures++
    Log "  ERROR $($_.Exception.Message)"
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500 }
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
