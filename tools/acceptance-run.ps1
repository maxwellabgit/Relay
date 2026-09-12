<#
.SYNOPSIS
  Runs one scenario of an end-to-end acceptance suite against the real Relay desktop app and writes the
  suite's machine-readable artifacts and screenshots.

.DESCRIPTION
  The suite is a JSON file (schema 0.1): scenarios with an input (mode "command" or "listen" and a list
  of turns with ids, speakers and text), standing grants, frozen adapters and expectations. This runner
  does what a person would do at the keyboard and records what the app did; it does not score the run.

    1. builds and launches Relay.exe against a throwaway data root, in the suite's deterministic mode:
       the rule grammar, no model, no network. A "listen" scenario needs more than that: reading a
       conversation is the mind's, so point model.endpoint at a local gateway and enable it, or the
       chord will dictate and nothing will be read (docs\10, step 1);
    2. creates every project the scenario's standing grants name (project_memory.read:<Name>);
    3. seeds the frozen adapters as notes ("remember that <fact> -- <Project>", approved) so the app has
       the same project memory the fixture assumes; a branch's frozen history adapter is seeded the same way;
    4. plays the turns: command mode enters each turn between Ctrl+X presses; listen mode opens the stream
       with Ctrl+Alt and enters each turn with a pause so it becomes its own segment(s). A turn arrives
       the way dictation delivers an utterance: as one chunk appended to the capture surface (through UI
       Automation), not as keystrokes. Speaker labels and turn ids are not entered (the capture surface has
       neither; dictation does not carry them either); the transcript artifact keeps them and maps every
       turn to the segments the app actually cut;
    5. takes screenshots of the window at the regions the suite's views ask for, scrolling through UI
       Automation, and dumps every UIA name next to each screenshot;
    6. closes the window and derives the artifacts from the ledger and the records on disk:
         run_manifest.json        what ran, with what mind and planner, against which build
         input_transcript.json    the turns as given and as typed, each with its segment ids (matched by SHA-256)
         detected_tasks.json      every task record with its excerpt and the turn ids the excerpt covers
         state_transitions.jsonl  state.changed records
         tool_calls.jsonl         tool.*, model.*, external.* records
         approval_events.jsonl    proposal.*, approval.*, execution.*, changeset.* records
         evidence.json            excerpts, notes (staging and project), observe.* records
         mutations.jsonl          project.*, note.*, patch.*, artifact.*, settings.changed records
         final_output.json        end state, what was shown, the inbox, counts, the mechanical part of the
                                  comparison with expected_task, and the no-words-in-the-ledger check
       plus the ledger copy, the whole data root and the project folder, so nothing has to be re-run to
       look at a detail.

  Scoring against the rubric needs a reader: compare the artifacts with expected_task, required_plan and
  the branch expectations, then fill the suite's analysis_output_template.

  Requires an interactive desktop session and nothing else stealing focus while it runs (one to two
  minutes). The only key presses are the two chords, and they are sent only after the Relay window has
  been confirmed as the foreground window. Typographic characters in the turns (em dashes, curly quotes,
  ellipses) are entered in their ASCII forms; the transcript records both.
  This file is deliberately ASCII-only: Windows PowerShell reads a BOM-less script as ANSI.

.PARAMETER Suite
  Path to the suite JSON.

.PARAMETER Scenario
  Scenario id. Default: the first scenario in the suite.

.PARAMETER Branch
  Branch id for a scenario with branches. Default: the branch whose frozen history adapter returns
  nothing (needs no seeding), else the first.

.PARAMETER Approve
  In command mode, approve what the task leaves awaiting approval (the suite's "the user approved the
  displayed scope" precondition). Without it the proposal stays pending and is recorded that way.

.PARAMETER NoBuild
  Skip `dotnet build`; use the existing Debug output.

.PARAMETER Keep
  Keep the throwaway data root and project folder in place (they are copied into OutDir regardless).

.PARAMETER OutDir
  Where everything goes. Default: %TEMP%\relay-acceptance\<scenario>-<timestamp>.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\acceptance-run.ps1 -Suite C:\path\suite.json -Scenario e2e_observed_worker_permissions_history_check
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Suite,
    [string] $Scenario = "",
    [string] $Branch = "",
    [switch] $Approve,
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
public static class AcceptWin32 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  // Windows only lets the foreground thread hand the foreground over; attach to it for the call.
  public static bool ForceForeground(IntPtr hWnd) {
    IntPtr fg = GetForegroundWindow();
    if (fg == hWnd) return true;
    uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
    uint me = GetCurrentThreadId();
    bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
    try { ShowWindow(hWnd, 9); BringWindowToTop(hWnd); SetForegroundWindow(hWnd); }
    finally { if (attached) AttachThreadInput(me, fgThread, false); }
    System.Threading.Thread.Sleep(300);
    return GetForegroundWindow() == hWnd;
  }
  public static void CtrlAlt() {
    keybd_event(0x11, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
    keybd_event(0x12, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
    keybd_event(0x12, 0, 2, UIntPtr.Zero);
    keybd_event(0x11, 0, 2, UIntPtr.Zero);
  }
}
"@

# ---------------------------------------------------------------------------------------------------
# Suite
# ---------------------------------------------------------------------------------------------------
$suiteDoc = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $Suite).Path) | ConvertFrom-Json   # not $suite: variables are case-insensitive and $Suite is the path parameter
$scenarios = @($suiteDoc.scenarios)
if ($scenarios.Count -eq 0) { throw "the suite has no scenarios" }
if ($Scenario -eq "") { $sc = $scenarios[0] } else { $sc = @($scenarios | Where-Object { $_.id -eq $Scenario })[0] }
if (-not $sc) { throw "scenario '$Scenario' is not in the suite; ids: $(($scenarios | ForEach-Object { $_.id }) -join ', ')" }
$branchDoc = $null
if ($sc.PSObject.Properties["branches"] -and @($sc.branches).Count -gt 0) {
    $branches = @($sc.branches)
    if ($Branch -ne "") { $branchDoc = @($branches | Where-Object { $_.id -eq $Branch })[0]; if (-not $branchDoc) { throw "branch '$Branch' is not in scenario '$($sc.id)'; ids: $(($branches | ForEach-Object { $_.id }) -join ', ')" } }
    else { $branchDoc = @($branches | Where-Object { -not $_.frozen_history_adapter -or @($_.frozen_history_adapter.results).Count -eq 0 })[0]; if (-not $branchDoc) { $branchDoc = $branches[0] } }
}
$mode = $sc.input.mode
$turns = @($sc.input.turns)
if ($mode -notin @("command", "listen")) { throw "input mode '$mode' is not supported (command, listen)" }

$repo = Split-Path -Parent $PSScriptRoot
if ($OutDir -eq "") { $OutDir = Join-Path $env:TEMP ("relay-acceptance\" + $sc.id + "-" + (Get-Date -Format "yyyyMMdd-HHmmss")) }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$report = New-Object System.Collections.Generic.List[string]
$shots = New-Object System.Collections.Generic.List[object]
$problems = 0
$runStarted = [DateTimeOffset]::UtcNow

function Log($text) { Write-Host $text; $script:report.Add($text) }
function Note($name, $ok, $detail = "") {
    if ($ok) { Log ("  ok    {0}{1}" -f $name, $(if ($detail) { "  ($detail)" } else { "" })) }
    else { $script:problems++; Log ("  MISS  {0}{1}" -f $name, $(if ($detail) { "  ($detail)" } else { "" })) }
}

Log "suite: $($suiteDoc.suite_id) (schema $($suiteDoc.schema_version))"
Log "scenario: $($sc.id) - $($sc.title)"
if ($branchDoc) { Log "branch: $($branchDoc.id)" }
Log "mode: $mode, $($turns.Count) turn(s)"

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
if (-not $NoBuild) {
    Log "building Relay.slnx (Debug)..."
    & dotnet build (Join-Path $repo "Relay.slnx") -nologo -v q 2>&1 | Where-Object { $_ -match "error|Build succeeded" } | ForEach-Object { Log "  $_" }
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}
$exe = Get-ChildItem (Join-Path $repo "src\Relay.Desktop\bin\Debug") -Recurse -Filter Relay.exe | Where-Object { $_.FullName -notmatch "\\x64\\" } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { $exe = Get-ChildItem (Join-Path $repo "src\Relay.Desktop\bin") -Recurse -Filter Relay.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
if (-not $exe) { throw "Relay.exe not found under src\Relay.Desktop\bin" }
Log "exe: $($exe.FullName)  ($($exe.LastWriteTime))"
$commit = ""
try { $commit = (& git -C $repo rev-parse HEAD 2>$null) } catch { }

# ---------------------------------------------------------------------------------------------------
# Fixture
# ---------------------------------------------------------------------------------------------------
$stamp = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$dataRoot = Join-Path $env:TEMP "relay-acceptance-data-$stamp"
$projects = Join-Path $env:TEMP "relay-acceptance-projects-$stamp"
New-Item -ItemType Directory -Force -Path (Join-Path $dataRoot "config"), $projects | Out-Null
$roots = @{ schemaVersion = 1; roots = @(@{ path = $projects; addedAt = (Get-Date).ToUniversalTime().ToString("o"); label = "acceptance" }) }
Set-Content -Path (Join-Path $dataRoot "config\workspaces.json") -Value ($roots | ConvertTo-Json -Depth 4) -Encoding UTF8
$ledgerPath = Join-Path $dataRoot "ledger\relay-ledger.jsonl"
Log "data root: $dataRoot"
Log "project folder: $projects"

# Projects the standing grants name, e.g. project_memory.read:Lightshift -> Lightshift.
$projectNames = New-Object System.Collections.Generic.List[string]
if ($sc.initial_authorization -and $sc.initial_authorization.standing_grants) {
    foreach ($g in @($sc.initial_authorization.standing_grants)) {
        if ($g -match '^[a-z_]+\.[a-z_]+:([^:]+)') { $name = $Matches[1]; if (-not $projectNames.Contains($name)) { $projectNames.Add($name) } }
    }
}
# Facts the frozen adapters hold, seeded as notes so the app's project memory matches the fixture.
$seedFacts = New-Object System.Collections.Generic.List[object]
if ($sc.frozen_project_adapter -and $sc.frozen_project_adapter.results) {
    foreach ($r in @($sc.frozen_project_adapter.results)) { $seedFacts.Add(@{ source_id = $r.source_id; fact = $r.fact; project = $(if ($projectNames.Count -gt 0) { $projectNames[0] } else { "" }) }) }
}
if ($branchDoc -and $branchDoc.frozen_history_adapter -and $branchDoc.frozen_history_adapter.results) {
    foreach ($r in @($branchDoc.frozen_history_adapter.results)) { $seedFacts.Add(@{ source_id = $r.source_id; fact = $r.recorded_fact; project = $(if ($projectNames.Count -gt 0) { $projectNames[0] } else { "" }) }) }
}
Log "projects to create: $(if ($projectNames.Count -gt 0) { $projectNames -join ', ' } else { '(none)' })"
Log "facts to seed: $($seedFacts.Count)"

# ---------------------------------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------------------------------
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

function Count-Event($type, $where = $null) {
    $hits = @(Read-Ledger | Where-Object { $_.type -eq $type })
    if ($where) { $hits = @($hits | Where-Object $where) }
    return $hits.Count
}

function Wait-Until($condition, $timeoutSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Ui-Root { return [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) }
function Ui-All { return (Ui-Root).FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) }

function Ui-FindMatch($pattern) {
    foreach ($e in Ui-All) { if ($e.Current.Name -and $e.Current.Name -match $pattern) { return $e.Current.Name } }
    return $null
}

function Ui-WaitMatch($pattern, $timeoutSeconds = 10) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $hit = Ui-FindMatch $pattern
        if ($hit) { return $hit }
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

function Ui-Invoke($buttonName, $timeoutSeconds = 10) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $btn = Ui-Button $buttonName
        if ($btn) { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

# The main scroll viewer: the largest element that scrolls.
function Ui-Scroller {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty, $true)
    $best = $null; $bestArea = 0
    foreach ($e in (Ui-Root).FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        $r = $e.Current.BoundingRectangle
        if ($r.IsEmpty) { continue }
        $area = $r.Width * $r.Height
        if ($area -gt $bestArea) { $best = $e; $bestArea = $area }
    }
    return $best
}

function Ui-ScrollTop {
    $s = Ui-Scroller
    if (-not $s) { return }
    $sp = $s.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    if ($sp.Current.VerticalScrollPercent -gt 0) { $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0); Start-Sleep -Milliseconds 500 }
}

# Scrolls the main viewer so the first element whose name matches sits at the top of the viewport.
# An element scrolled out of view reports an empty rectangle, so the viewer is paged until it appears.
function Ui-ScrollTo($pattern) {
    $s = Ui-Scroller
    if (-not $s) { return $false }
    $sp = $s.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $target = $null
    foreach ($e in Ui-All) { if ($e.Current.Name -and $e.Current.Name -match $pattern) { $target = $e; break } }
    if (-not $target) { return $false }
    $viewSize = $sp.Current.VerticalViewSize
    if ($viewSize -le 0 -or $viewSize -ge 100) { return $true }
    $er = $target.Current.BoundingRectangle
    if ($er.IsEmpty) {
        $step = [Math]::Max(5.0, [double]$viewSize * 0.8)
        $pct = 0.0
        while ($pct -le 100.0 -and $er.IsEmpty) {
            $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, [double]$pct); Start-Sleep -Milliseconds 250
            $er = $target.Current.BoundingRectangle
            $pct += $step
        }
        if ($er.IsEmpty) { return $false }
    }
    $vr = $s.Current.BoundingRectangle
    $content = $vr.Height * 100.0 / $viewSize
    $scrollable = $content - $vr.Height
    if ($scrollable -le 0) { return $true }
    $offset = $sp.Current.VerticalScrollPercent / 100.0 * $scrollable
    $wanted = $offset + ($er.Top - $vr.Top) - 10
    $pct = [Math]::Max(0.0, [Math]::Min(100.0, $wanted / $scrollable * 100.0))
    $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, [double]$pct)
    Start-Sleep -Milliseconds 600
    return $true
}

function Ui-CaptureBox {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "CaptureBox")
    $box = (Ui-Root).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($box) { return $box }
    # Fallback: the edit control that is not the ask box.
    $edits = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    foreach ($e in (Ui-Root).FindAll([System.Windows.Automation.TreeScope]::Descendants, $edits)) { if ($e.Current.Name -ne "Ask box") { return $e } }
    return $null
}

# Dictation delivers an utterance as one chunk when the speaker lets go of the key, not as keystrokes; so
# does this. The capture surface's text is extended through UI Automation, which raises the same
# TextChanged the app sees from Flow or a paste. (Per-character SendKeys dropped runs of characters
# whenever the app was busy with a listening pass, which is exactly when the text matters.)
function Enter-Text($text) {
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        $box = Ui-CaptureBox
        if ($box) {
            try { $box.SetFocus() } catch { }
            $vp = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $vp.SetValue($vp.Current.Value + $text)
            Start-Sleep -Milliseconds 300
            return $true
        }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Focus-CaptureBox { $box = Ui-CaptureBox; if ($box) { try { $box.SetFocus(); Start-Sleep -Milliseconds 300 } catch { } } }

function Shot($name, $shows) {
    $r = New-Object AcceptWin32+RECT
    [AcceptWin32]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { Log "  (no window rect for $name; window gone?)"; return }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $path = Join-Path $OutDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($e in Ui-All) { if ($e.Current.Name) { $names.Add(("[{0}] {1}" -f $e.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""), $e.Current.Name)) } }
    Set-Content -Path (Join-Path $OutDir "$name.uia.txt") -Value $names -Encoding UTF8
    $script:shots.Add(@{ file = "$name.png"; uia = "$name.uia.txt"; shows = $shows; at = (Get-Date).ToUniversalTime().ToString("o") })
    Log "  screenshot $name.png  ($shows)"
}

function Shot-At($name, $shows, $scrollPattern) {
    if ($scrollPattern -eq $null) { Ui-ScrollTop } elseif ($scrollPattern -eq "") { } elseif (-not (Ui-ScrollTo $scrollPattern)) { Log "  (could not scroll to '$scrollPattern'; shooting where the view is)" }
    Shot $name $shows
}

function Focus-Relay {
    for ($i = 0; $i -lt 5; $i++) { if ([AcceptWin32]::ForceForeground($hwnd)) { return $true }; Start-Sleep -Milliseconds 400 }
    return $false
}

# The chords are real key presses; they must never reach another window (Ctrl+X in an editor cuts a line).
function Ensure-Focus { if (-not (Focus-Relay)) { throw "Relay is not the foreground window; refusing to press keys" } }

# Typographic characters are kept out of what is entered so the segment texts stay comparable across
# input paths (Flow emits ASCII punctuation). The transcript records both forms.
function Normalize-Typed($text) {
    $t = $text
    $t = $t.Replace([string][char]0x2014, " - ").Replace([string][char]0x2013, "-").Replace([string][char]0x2026, "...")
    $t = $t.Replace([string][char]0x2018, "'").Replace([string][char]0x2019, "'").Replace([string][char]0x201C, '"').Replace([string][char]0x201D, '"')
    $t = $t.Replace([string][char]0x00A0, " ")
    return $t
}

function Press-CommandKey { Ensure-Focus; [System.Windows.Forms.SendKeys]::SendWait("^x"); Start-Sleep -Milliseconds 500 }
function Press-NoteKey { Ensure-Focus; [AcceptWin32]::CtrlAlt(); Start-Sleep -Milliseconds 500 }

# A typed instruction through the command path, approved when it proposes something. Returns the task.completed record.
# A capture cancelled from outside the script (an Esc from the keyboard while the window was forced forward) is retried once.
function Run-Command($text, $label) {
    $before = Count-Event "task.completed"
    $settled = $false
    for ($attempt = 1; $attempt -le 2 -and -not $settled; $attempt++) {
        $cancelledBefore = Count-Event "capture.cancelled"
        Press-CommandKey
        if (-not (Wait-Event "state.changed" 5 { $_.data.to -eq "COMMAND_CAPTURE" })) { Note "$label - COMMAND_CAPTURE entered" $false; return $null }
        if (-not (Enter-Text $text)) { Note "$label - capture box found" $false; return $null }
        Press-CommandKey
        $settled = Wait-Until { (Count-Event "task.completed") -gt $before -or (Count-Event "task.failed") -gt 0 -or (Count-Event "state.changed" { $_.data.to -eq "AWAITING_APPROVAL" }) -gt 0 -or (Count-Event "capture.cancelled") -gt $cancelledBefore } 20
        if ((Count-Event "capture.cancelled") -gt $cancelledBefore) { $settled = $false; Log "  (capture cancelled from outside the script; retrying)"; Start-Sleep -Seconds 2 }
    }
    if (-not $settled) { Note "$label" $false; return $null }
    $state = @(Read-Ledger | Where-Object { $_.type -eq "state.changed" })[-1].data.to
    if ($state -eq "AWAITING_APPROVAL") {
        $approveAll = Ui-Button "Approve all"
        if (-not $approveAll) { foreach ($e in Ui-All) { if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $e.Current.Name -like "Approve all*") { $approveAll = $e; break } } }
        if ($approveAll) { $approveAll.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } else { Ui-Invoke "Approve" | Out-Null }
        $settled = Wait-Until { (Count-Event "task.completed") -gt $before -or (Count-Event "task.failed") -gt 0 } 20
    }
    Note "$label" $settled
    Start-Sleep -Seconds 5   # let the COMPLETED receipt return to IDLE
    return @(Read-Ledger | Where-Object { $_.type -eq "task.completed" })[-1]
}

# ---------------------------------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------------------------------
$typedTurns = New-Object System.Collections.Generic.List[object]
$inputStarted = $null
$inputEnded = $null

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

    Log ""; Log "== setup =="
    Note "window is foreground" (Focus-Relay)
    Note "session.started" ($null -ne (Wait-Event "session.started" 10))
    Note "state IDLE" ($null -ne (Wait-Event "state.changed" 10 { $_.data.to -eq "IDLE" }))
    $listeningChip = Ui-WaitMatch "^(Listening|No mind)" 5
    Log "  listening chip: $listeningChip"

    foreach ($name in $projectNames) {
        Run-Command "create project $name" "project $name created" | Out-Null
        Note "project.created for $name" ($null -ne (Wait-Event "project.created" 5 { $_.data.name -eq $name -or $_.data.slug -eq $name.ToLowerInvariant() }))
    }
    foreach ($seed in $seedFacts) {
        $text = "remember that " + $seed.fact + $(if ($seed.project) { " -- " + $seed.project } else { "" })
        Run-Command $text "seeded $($seed.source_id)" | Out-Null
    }
    if ($projectNames.Count -gt 0 -or $seedFacts.Count -gt 0) { Shot-At "00-setup" "projects and seeded notes in place before the scenario input" $null }
    $setupRecords = (Read-Ledger).Count

    Log ""; Log "== input ($mode) =="
    $inputStarted = [DateTimeOffset]::UtcNow
    if ($mode -eq "command") {
        foreach ($turn in $turns) {
            $typed = Normalize-Typed $turn.text
            $typedAt = [DateTimeOffset]::UtcNow
            $before = Count-Event "task.completed"
            Press-CommandKey
            Note "COMMAND_CAPTURE entered" ($null -ne (Wait-Event "state.changed" 5 { $_.data.to -eq "COMMAND_CAPTURE" }))
            Note "turn $($turn.id) entered" (Enter-Text $typed)
            Press-CommandKey
            $typedTurns.Add(@{ id = $turn.id; speaker = $turn.speaker; final = $turn.final; text = $turn.text; typed_text = $typed; typed_at = $typedAt.ToString("o") })
            Note "capture committed" ($null -ne (Wait-Event "capture.committed" 10))
            Wait-Until { (Count-Event "task.completed") -gt $before -or (Count-Event "task.failed") -gt 0 -or @(Read-Ledger | Where-Object { $_.type -eq "state.changed" })[-1].data.to -eq "AWAITING_APPROVAL" } 30 | Out-Null
            Shot-At "01-task-detection" "the task as detected: origin, kind, focused prompt, state, process tag" $null
            Shot-At "02-tasks" "the Tasks region with lane, origin and cost" "^TASKS$"
            $state = @(Read-Ledger | Where-Object { $_.type -eq "state.changed" })[-1].data.to
            if ($state -eq "AWAITING_APPROVAL") {
                Shot-At "03-approval" "what awaits approval: action, target, tier, policy reasons, Approve/Reject" "^RESPONSE$"
                if ($Approve) {
                    $approveAll = $null
                    foreach ($e in Ui-All) { if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $e.Current.Name -like "Approve all*") { $approveAll = $e; break } }
                    if ($approveAll) { $approveAll.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } else { Note "Approve invoked" (Ui-Invoke "Approve") }
                    Note "task settled after approval" (Wait-Until { (Count-Event "task.completed") -gt $before -or (Count-Event "task.failed") -gt 0 } 60)
                    Shot-At "04-after-approval" "the response after the approved step ran: answer, sources, proposals with results" "^RESPONSE$"
                } else { Log "  proposal left pending (run with -Approve to approve the displayed scope)" }
            }
            Shot-At "05-summary" "the response: answer, knowledge state, sources, tags" "^RESPONSE$"
        }
    }
    else {
        Press-NoteKey
        Note "NOTE_CAPTURE entered" ($null -ne (Wait-Event "state.changed" 5 { $_.data.to -eq "NOTE_CAPTURE" }))
        $streamStarted = Wait-Event "stream.started" 5
        Note "stream started" ($null -ne $streamStarted) $(if ($streamStarted) { "mind $($streamStarted.data.mind), observe every $($streamStarted.data.observeIntervalMs) ms" } else { "needs a mind: enable the model gateway" })
        Note "UI shows LISTENING" ($null -ne (Ui-WaitMatch "^LISTENING" 5))
        foreach ($turn in $turns) {
            $typed = Normalize-Typed $turn.text
            $typedAt = [DateTimeOffset]::UtcNow
            $entered = Enter-Text ($typed + " ")
            $typedTurns.Add(@{ id = $turn.id; speaker = $turn.speaker; final = $turn.final; text = $turn.text; typed_text = $typed; typed_at = $typedAt.ToString("o") })
            Log ("  {0} {1} ({2}, {3} chars)" -f $(if ($entered) { "entered" } else { "FAILED " }), $turn.id, $turn.speaker, $typed.Length)
            Start-Sleep -Milliseconds 1700   # longer than segmentQuietMs: the turn closes before the next one starts
        }
        $inputEnded = [DateTimeOffset]::UtcNow
        # Wait for a listening pass after the last segment (the observe timer fires every observeIntervalMs).
        $lastSegment = @(Read-Ledger | Where-Object { $_.type -eq "stream.segment" })[-1]
        $read = Wait-Until { @(Read-Ledger | Where-Object { ($_.type -eq "observe.checked" -or $_.type -eq "observe.raised" -or $_.type -eq "observe.failed") -and [DateTimeOffset]$_.ts -gt [DateTimeOffset]$lastSegment.ts }).Count -gt 0 } 20
        Note "the mind read the last segment" $read
        Start-Sleep -Seconds 3   # let tasks raised by the last pass finish
        Shot-At "06a-listening" "the listening view: buffer, segments, passes, raises, excerpts" $null
        Shot-At "06b-attention-while-listening" "what the arbiter surfaced while listening" "^ATTENTION$"
        Focus-CaptureBox   # scrolling through UIA moves keyboard focus; give it back to the surface before stopping
        Press-NoteKey
        $stopped = Wait-Event "stream.stopped" 15 { $_.data.reason -eq "stopped" }
        Note "stream stopped" ($null -ne $stopped)
        Note "COMPLETED after listening" ($null -ne $stopped -and $null -ne (Wait-Event "state.changed" 10 { $_.data.to -eq "COMPLETED" -and [DateTimeOffset]$_.ts -gt [DateTimeOffset]$stopped.ts }))
        Start-Sleep -Seconds 1
        Shot-At "06c-receipt" "the receipt for the stream" $null
        Shot-At "06d-conversation-extraction" "Attention, Review and Inbox after the stream: what was extracted and where it went" "^ATTENTION$"
        Shot-At "06e-inbox" "the Inbox: unrouted notes with their text and candidates" "^INBOX$"
        Shot-At "06f-tasks" "every task with lane, origin, presentation and cost" "^TASKS$"
        # The diagnostics drawer for the first observed task: the ambient row's tiny 'details' first, then any 'Details'.
        $opened = Ui-Invoke "details" 3
        if (-not $opened) { $opened = Ui-Invoke "Details" 3 }
        Note "diagnostics drawer opened" $opened
        if ($opened) {
            Note "drawer shows the focused prompt" ($null -ne (Ui-WaitMatch "^Focused prompt$" 5))
            Shot-At "07-task-details" "one task in full: focused prompt, why it started, planner, steps, tool calls, proposals, presentation, cost" ""   # the drawer scrolls itself into view
        }
        Shot-At "08-relay" "the compiled preferences, grants and change sets" "^RELAY$"
    }
    Shot-At "09-audit-timeline" "the activity feed: chronological ledger events" "^ACTIVITY$"

    Log ""; Log "== close =="
    $closed = $proc.CloseMainWindow()
    $exited = $proc.WaitForExit(15000)
    Note "window closed cleanly" ($closed -and $exited)
}
catch {
    $problems++
    Log "  ERROR $($_.Exception.Message)"
    Log "  $($_.ScriptStackTrace)"
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500 }
}

# ---------------------------------------------------------------------------------------------------
# Artifacts
# ---------------------------------------------------------------------------------------------------
Log ""; Log "== artifacts =="
try {
$records = @(Read-Ledger)
$dataCopy = Join-Path $OutDir "data-root"
$projectsCopy = Join-Path $OutDir "projects"
if (Test-Path $dataRoot) { Copy-Item -Recurse -Force $dataRoot $dataCopy }
if (Test-Path $projects) { Copy-Item -Recurse -Force $projects $projectsCopy }
if (Test-Path $ledgerPath) { Copy-Item $ledgerPath (Join-Path $OutDir "relay-ledger.jsonl") -Force }

function Write-Jsonl($name, $items) {
    $lines = @($items | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 30 })
    Set-Content -Path (Join-Path $OutDir $name) -Value $lines -Encoding UTF8
    Log "  $name  ($($lines.Count) record(s))"
}
function Write-Json($name, $obj) {
    Set-Content -Path (Join-Path $OutDir $name) -Value ($obj | ConvertTo-Json -Depth 30) -Encoding UTF8
    Log "  $name"
}
function Read-JsonFile($path) { try { return ([System.IO.File]::ReadAllText($path) | ConvertFrom-Json) } catch { return $null } }
# File.ReadAllText, not Get-Content: Get-Content decorates its strings with PSDrive/PSProvider members that ConvertTo-Json then walks for minutes.
function Read-TextFile($path) { return [System.IO.File]::ReadAllText($path) }

# input_transcript.json: each turn re-cut with the segmenter's rule and matched to stream.segment records by SHA-256.
$sha = [System.Security.Cryptography.SHA256]::Create()
function Sha256Hex($text) { return (($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text)) | ForEach-Object { $_.ToString("x2") }) -join "") }
$sentenceEnd = New-Object System.Text.RegularExpressions.Regex('[.!?]+["\u201D'')\]]*\s+')
$segmentRecords = @($records | Where-Object { $_.type -eq "stream.segment" })
$segmentIndex = @{}
foreach ($s in $segmentRecords) { if (-not $segmentIndex.ContainsKey($s.data.sha256)) { $segmentIndex[$s.data.sha256] = New-Object System.Collections.Generic.List[object] }; $segmentIndex[$s.data.sha256].Add($s) }
$segmentToTurn = @{}
$transcriptTurns = New-Object System.Collections.Generic.List[object]
foreach ($t in $typedTurns) {
    $pieces = New-Object System.Collections.Generic.List[string]
    if ($mode -eq "listen") {
        $pending = $t.typed_text + " "
        $cursor = 0
        foreach ($m in $sentenceEnd.Matches($pending)) { $piece = $pending.Substring($cursor, $m.Index + $m.Length - $cursor).Trim(); if ($piece.Length -gt 0) { $pieces.Add($piece) }; $cursor = $m.Index + $m.Length }
        $rest = $pending.Substring($cursor).Trim(); if ($rest.Length -gt 0) { $pieces.Add($rest) }
    }
    $segs = New-Object System.Collections.Generic.List[object]
    foreach ($piece in $pieces) {
        $hash = Sha256Hex $piece
        $rec = $null
        if ($segmentIndex.ContainsKey($hash) -and $segmentIndex[$hash].Count -gt 0) { $rec = $segmentIndex[$hash][0]; $segmentIndex[$hash].RemoveAt(0) }
        # Plain variables, not $(if ...) inside the literal: Windows PowerShell 5.1 sometimes compiles such a
        # subexpression in a hashtable argument into "Argument types do not match".
        $segId = $null; $segAt = $null
        if ($rec) { $segmentToTurn[$rec.data.segmentId] = $t.id; $segId = $rec.data.segmentId; $segAt = $rec.data.at }
        $segs.Add(@{ text = $piece; sha256 = $hash; chars = $piece.Length; segmentId = $segId; at = $segAt; matched = ($null -ne $rec) })
    }
    $transcriptTurns.Add(@{ id = $t.id; speaker = $t.speaker; final = $t.final; text = $t.text; typed_text = $t.typed_text; typed_at = $t.typed_at; segments = $segs })
}
$unmatched = @()
foreach ($k in $segmentIndex.Keys) { foreach ($s in $segmentIndex[$k]) { $unmatched += @{ segmentId = $s.data.segmentId; chars = $s.data.chars; sha256 = $s.data.sha256 } } }
$inputStartedText = $null; if ($inputStarted) { $inputStartedText = $inputStarted.ToString("o") }
$inputEndedText = $null; if ($inputEnded) { $inputEndedText = $inputEnded.ToString("o") }
$branchId = $null; if ($branchDoc) { $branchId = [string]$branchDoc.id }
Write-Json "input_transcript.json" @{
    scenario_id = $sc.id; mode = $mode; input_started = $inputStartedText; input_ended = $inputEndedText
    note = "Speaker labels and turn ids are not part of the capture surface; they were not typed. Segments are matched to turns by the SHA-256 the ledger records for each segment."
    turns = $transcriptTurns; unmatched_segments = $unmatched
}

# detected_tasks.json: task records on disk, each with the turns its excerpt covers.
$tasksDir = Join-Path $dataRoot "tasks"
$excerptsDir = Join-Path $dataRoot "excerpts"
$taskEntries = New-Object System.Collections.Generic.List[object]
if (Test-Path $tasksDir) {
    foreach ($f in (Get-ChildItem $tasksDir -Filter "*.json" -File | Where-Object { $_.Name -notlike "*.live.json" } | Sort-Object Name)) {
        $rec = Read-JsonFile $f.FullName
        if (-not $rec) { continue }
        $taskId = $rec.taskId
        $excerpt = $null; $turnIds = @()
        if ($rec.excerptId -and (Test-Path (Join-Path $excerptsDir ($rec.excerptId + ".json")))) {
            $excerpt = Read-JsonFile (Join-Path $excerptsDir ($rec.excerptId + ".json"))
            if ($excerpt -and $excerpt.segments) { $turnIds = @($excerpt.segments | ForEach-Object { $segmentToTurn[$_.segmentId] } | Where-Object { $_ } | Select-Object -Unique) }
        }
        $ledgerFor = @($records | Where-Object { $_.data.PSObject.Properties["taskId"] -and $_.data.taskId -eq $taskId } | ForEach-Object { @{ seq = $_.seq; ts = $_.ts; type = $_.type } })
        $taskEntries.Add(@{ record_file = ("data-root\tasks\" + $f.Name); record = $rec; source_turn_ids = $turnIds; excerpt = $excerpt; ledger_events = $ledgerFor; during_input = ($inputStarted -ne $null -and $rec.startedAt -and ([DateTimeOffset]$rec.startedAt) -ge $inputStarted) })
    }
}
Write-Json "detected_tasks.json" @{ scenario_id = $sc.id; tasks = $taskEntries }

Write-Jsonl "state_transitions.jsonl" @($records | Where-Object { $_.type -eq "state.changed" })
Write-Jsonl "tool_calls.jsonl" @($records | Where-Object { $_.type -like "tool.*" -or $_.type -like "model.*" -or $_.type -like "external.*" -or $_.type -like "agent_run.*" })
Write-Jsonl "approval_events.jsonl" @($records | Where-Object { $_.type -like "proposal.*" -or $_.type -like "approval.*" -or $_.type -like "execution.*" -or $_.type -like "changeset.*" })
Write-Jsonl "mutations.jsonl" @($records | Where-Object { $_.type -like "project.*" -or $_.type -like "note.*" -or $_.type -like "patch.*" -or $_.type -like "artifact.*" -or $_.type -eq "settings.changed" -or $_.type -like "workspace.*" })

# evidence.json: excerpts (with turn ids), notes in staging and in projects, and the listening passes.
$excerptEntries = @()
if (Test-Path $excerptsDir) {
    foreach ($f in (Get-ChildItem $excerptsDir -Filter "*.json" -File | Sort-Object Name)) {
        $e = Read-JsonFile $f.FullName
        if (-not $e) { continue }
        $ids = @(); if ($e.segments) { $ids = @($e.segments | ForEach-Object { $segmentToTurn[$_.segmentId] } | Where-Object { $_ } | Select-Object -Unique) }
        $excerptEntries += @{ file = ("data-root\excerpts\" + $f.Name); source_turn_ids = $ids; excerpt = $e }
    }
}
$stagingNotes = @()
$notesDir = Join-Path $dataRoot "staging\notes"
if (Test-Path $notesDir) { foreach ($f in (Get-ChildItem $notesDir -Filter "*.json" -File | Sort-Object Name)) { $n = Read-JsonFile $f.FullName; if ($n) { $stagingNotes += @{ file = ("data-root\staging\notes\" + $f.Name); note = $n } } } }
$projectFiles = @()
if (Test-Path $projects) { foreach ($f in (Get-ChildItem $projects -Recurse -File | Sort-Object FullName)) { $entry = @{ file = ("projects\" + $f.FullName.Substring($projects.Length + 1)); bytes = $f.Length }; if ($f.Extension -eq ".json") { $entry.content = Read-JsonFile $f.FullName } else { $entry.content = Read-TextFile $f.FullName }; $projectFiles += $entry } }
Write-Json "evidence.json" @{
    scenario_id = $sc.id
    excerpts = $excerptEntries
    staging_notes = $stagingNotes
    project_files = $projectFiles
    listening_passes = @($records | Where-Object { $_.type -like "observe.*" })
    excerpt_events = @($records | Where-Object { $_.type -eq "stream.excerpt_stored" })
}

# final_output.json: end state, what was shown, the inbox, counts and the mechanical part of the comparison.
$finalState = $null
$stateRecords = @($records | Where-Object { $_.type -eq "state.changed" }); if ($stateRecords.Count -gt 0) { $finalState = $stateRecords[-1].data.to }
$scenarioTasks = @($taskEntries | Where-Object { $_.during_input })
$expected = $sc.expected_task
$expectedOrigin = $null; $expectedCount = $null; $expectedKind = $null; $expectedTurnIds = $null
if ($expected) { $expectedOrigin = $expected.origin; $expectedCount = $expected.count; $expectedKind = $expected.kind; $expectedTurnIds = @($expected.source_turn_ids) }
$matchingOrigin = @($scenarioTasks | Where-Object { $expectedOrigin -eq $null -or $_.record.origin -eq $expectedOrigin })
$ledgerText = ""; if (Test-Path $ledgerPath) { $ledgerText = Read-TextFile $ledgerPath }
$leaks = @()
if ($mode -eq "listen") {
    foreach ($t in $transcriptTurns) { foreach ($s in $t.segments) { if (($s.text -split "\s+").Count -ge 4 -and $ledgerText.IndexOf($s.text, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $leaks += @{ turn = $t.id; text = $s.text } } } }
}
$inbox = @($stagingNotes | Where-Object { $_.note.routing -eq "unrouted" -or $_.note.status -eq "draft" } | ForEach-Object { @{ noteId = $_.note.noteId; type = $_.note.type; text = $_.note.text; routing = $_.note.routing; status = $_.note.status; spans = $_.note.spans } })
$inputSeq = 0
if ($inputStarted) { $firstAfter = @($records | Where-Object { [DateTimeOffset]$_.ts -ge $inputStarted })[0]; if ($firstAfter) { $inputSeq = $firstAfter.seq } }
$afterInput = @($records | Where-Object { $_.seq -ge $inputSeq })
Write-Json "final_output.json" @{
    scenario_id = $sc.id; branch_id = $branchId; mode = $mode
    final_state = $finalState
    stream = @($records | Where-Object { $_.type -eq "stream.stopped" } | ForEach-Object { $_.data })
    attention_shown = @($afterInput | Where-Object { $_.type -eq "attention.shown" -or $_.type -eq "task.presented" } | ForEach-Object { $_.data })
    attention_suppressed = @($afterInput | Where-Object { $_.type -eq "attention.suppressed" } | ForEach-Object { $_.data })
    inbox = $inbox
    tasks = @($scenarioTasks | ForEach-Object { @{ taskId = $_.record.taskId; origin = $_.record.origin; kind = $_.record.kind; status = $_.record.status; outcome = $_.record.outcome; summary = $_.record.summary; focusedPrompt = $_.record.focusedPrompt; steps = $_.record.steps; presentation = $_.record.presentation; presentationReason = $_.record.presentationReason; source_turn_ids = $_.source_turn_ids; excerptId = $_.record.excerptId; proposals = $_.record.proposals } })
    counts = @{
        tasks_during_input = $scenarioTasks.Count
        tool_calls = @($afterInput | Where-Object { $_.type -eq "tool.called" }).Count
        model_calls = @($afterInput | Where-Object { $_.type -eq "model.requested" }).Count
        approvals_granted = @($afterInput | Where-Object { $_.type -eq "approval.granted" }).Count
        standing_grants_used = @($afterInput | Where-Object { $_.type -eq "approval.standing_grant" }).Count
        notes_drafted = @($afterInput | Where-Object { $_.type -eq "note.draft_created" }).Count
        notes_routed = @($afterInput | Where-Object { $_.type -eq "note.routed" }).Count
        notes_unrouted = @($afterInput | Where-Object { $_.type -eq "note.routing_deferred" }).Count
        project_mutations = @($afterInput | Where-Object { $_.type -like "project.*" -or $_.type -eq "note.written" -or $_.type -eq "note.modified" -or $_.type -eq "note.moved" -or $_.type -eq "note.superseded" -or $_.type -eq "patch.applied" }).Count
        ledger_records_total = $records.Count
        ledger_records_after_input = $afterInput.Count
    }
    expected_task = $expected
    observed_against_expected = @{
        expected_count = $expectedCount
        observed_count_with_expected_origin = $matchingOrigin.Count
        observed_kinds = @($matchingOrigin | ForEach-Object { $_.record.kind })
        expected_kind = $expectedKind
        expected_source_turn_ids = $expectedTurnIds
        observed_source_turn_ids = @($matchingOrigin | ForEach-Object { $_.source_turn_ids } | Where-Object { $_ } | Select-Object -Unique)
    }
    words_from_listening_in_ledger = $leaks
}

# Plain scalars, computed before the literal: Windows PowerShell 5.1 fails with "Argument types do not match" when a
# pipeline result from the ledger records is indexed inside this hashtable literal.
$mindName = $null; foreach ($r in $records) { if (($r.type -eq "stream.started" -or $r.type -eq "session.started") -and $r.data.mind) { $mindName = [string]$r.data.mind; break } }
$plannerName = $null; foreach ($r in $records) { if ($r.type -eq "task.created" -and $r.data.planner) { $plannerName = [string]$r.data.planner; break } }
Write-Json "run_manifest.json" @{
    suite = @{ id = $suiteDoc.suite_id; schema_version = $suiteDoc.schema_version; file = (Resolve-Path -LiteralPath $Suite).Path }
    scenario = @{ id = $sc.id; title = $sc.title; branch = $branchId; mode = $mode }
    test_mode = "deterministic"
    started = $runStarted.ToString("o"); finished = (Get-Date).ToUniversalTime().ToString("o")
    app = @{ exe = $exe.FullName; built = $exe.LastWriteTime.ToUniversalTime().ToString("o"); commit = $commit }
    mind = $mindName
    planner = $plannerName
    data_root = $dataRoot; project_folder = $projects; copied_to = @{ data_root = "data-root"; projects = "projects" }
    # .ToArray(), not @($list): Windows PowerShell 5.1's ConvertTo-Json fails with "Argument types do not match" on @() around a List[object].
    setup = @{ projects_created = $projectNames.ToArray(); seeded_facts = $seedFacts.ToArray(); ledger_records_before_input = $setupRecords }
    input = @{ turns = $typedTurns.Count; entry = "capture surface extended through UI Automation, one chunk per turn (as dictation delivers it)"; typographic_characters_normalized = $true; speaker_labels_typed = $false }
    screenshots = $shots.ToArray()
    artifacts = @("run_manifest.json", "input_transcript.json", "detected_tasks.json", "state_transitions.jsonl", "tool_calls.jsonl", "approval_events.jsonl", "evidence.json", "mutations.jsonl", "final_output.json", "relay-ledger.jsonl", "report.txt")
    problems = $problems
}
}
catch {
    $problems++
    Log "  ERROR while writing artifacts: $($_.Exception.Message)"
    Log "  $($_.ScriptStackTrace)"
}

$verdict = $(if ($problems -eq 0) { "RUN COMPLETE" } else { "RUN COMPLETE WITH $problems PROBLEM(S)" })
Log ""; Log "== $verdict ==  artifacts: $OutDir"
Set-Content -Path (Join-Path $OutDir "report.txt") -Value $report -Encoding UTF8
if (-not $Keep) { Remove-Item -Recurse -Force $dataRoot, $projects -ErrorAction SilentlyContinue } else { Log "kept data root $dataRoot and project folder $projects" }
exit $(if ($problems -eq 0) { 0 } else { 1 })
