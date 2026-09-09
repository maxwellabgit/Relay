<#
.SYNOPSIS
  Scores a real model as RELAY0 - the model judge and the model planner as the primary tools - against the
  authored evaluation sets and the live listening scenario, and keeps the reports.

.DESCRIPTION
  The deterministic suite (`dotnet test`) scores the rule grammar and the heuristic judge. This runner turns
  on the live tests instead, which put a model in both seats with nothing in front of it:

    EvaluationTests.LiveModelJudgeAndPlannerAreScoredAsThePrimaryTools
        every case in tests\Relay.Tests\Evaluation (unseen + failure) and tests\Relay.Tests\Evaluation\model
        (multi-segment windows, project attribution, grounding, selectivity, delegation to workers and
        external profiles) scored by the ModelJudge and the ModelOrchestrator.
    EvaluationTests.LiveModelJudgeRaisesObservedTasksAndTheModelPlannerActsOnThem
        the live chain: words overheard -> model judge -> observed tasks -> model planner -> policy -> approval,
        then a direct research ask delegated to a named external profile (scripted counterparty).

  The model is any OpenAI-compatible chat endpoint. The default is the README's reference model, Ministral 8B
  Instruct (Q4_K_M), served locally by Ollama on the loopback interface; the runner starts Ollama and pulls
  the model when needed. Nothing here is run by CI: the live tests return immediately unless
  RELAY_LIVE_MODEL_KEY is set, which is what this script does for the duration of the run.

  Output (default %TEMP%\relay-live-eval\<timestamp>):
    evaluation-live.json / .txt     the EvaluationReport: every case, pass/fail, reasons, what the model produced
    listening-live.txt              the scenario transcript: segments, judge passes, tasks, tool calls, proposals
    listening-live.ledger.jsonl     the ledger of that session (fingerprints, never the words)
    dotnet-test.log                 the test runner's output
    run.json                        endpoint, model, timings, exit code

.PARAMETER Endpoint
  Chat-completions URL. Default: http://127.0.0.1:11434/v1/chat/completions (Ollama). Remote endpoints must be https and need -Key.
.PARAMETER Model
  Model name as the endpoint knows it. Default: hf.co/bartowski/Ministral-8B-Instruct-2410-GGUF:Q4_K_M.
.PARAMETER Key
  API key for a remote endpoint. Ignored for loopback (the gateway sends none); any value switches the live tests on.
.PARAMETER ContextTokens
  Context window for an Ollama model. Ollama serves every model with 2048 tokens of context unless told otherwise and
  silently drops the middle of a longer prompt - which is the mind's system prompt. The runner derives a model with
  PARAMETER num_ctx set to this value (once; "relay-<model>:ctx<n>") and runs against that. Default 8192. 0 uses the model as is.
.PARAMETER OutDir
  Where the reports go. Default: %TEMP%\relay-live-eval\<timestamp>.
.PARAMETER Filter
  dotnet test filter. Default: both live tests. Use "FullyQualifiedName~LiveModelJudgeAndPlanner" for the evaluation set only.
.PARAMETER SkipBuild
  Use the existing Debug build.

.EXAMPLE
  .\tools\live-eval.ps1
  .\tools\live-eval.ps1 -Endpoint https://api.openai.com/v1/chat/completions -Model gpt-4o-mini -Key $env:OPENAI_API_KEY
#>
[CmdletBinding()]
param(
    [string]$Endpoint = "http://127.0.0.1:11434/v1/chat/completions",
    [string]$Model = "hf.co/bartowski/Ministral-8B-Instruct-2410-GGUF:Q4_K_M",
    [string]$Key = "",
    [string]$OutDir = "",
    [string]$Filter = "FullyQualifiedName~EvaluationTests.LiveModel",
    [int]$ContextTokens = 8192,
    [switch]$SkipBuild
)

# Native tools (ollama, dotnet) write progress to stderr; with "Stop" PowerShell 5.1 would turn that into a terminating error.
$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
if ($OutDir -eq "") { $OutDir = Join-Path $env:TEMP ("relay-live-eval\" + $stamp) }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Log($text) { Write-Host $text }

# ---- dotnet ---------------------------------------------------------------------------------------
$dotnet = $null
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd) { $dotnet = $cmd.Source }
foreach ($candidate in @((Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"), (Join-Path $env:ProgramFiles "dotnet\dotnet.exe"))) {
    if (-not $dotnet -and (Test-Path $candidate)) { $dotnet = $candidate }
}
if (-not $dotnet) { throw "dotnet SDK not found (PATH, %LOCALAPPDATA%\Microsoft\dotnet, %ProgramFiles%\dotnet)." }

# ---- the model endpoint -----------------------------------------------------------------------------
$uri = [Uri]$Endpoint
$isLoopback = $uri.IsLoopback
if (-not $isLoopback -and $uri.Scheme -ne "https") { throw "A remote endpoint must be https (the gateway allows plain http on loopback only)." }
if (-not $isLoopback -and $Key -eq "") { throw "A remote endpoint needs -Key." }
if ($Key -eq "") { $Key = "loopback" }   # the switch that turns the live tests on; the gateway sends no key to a loopback server

if ($isLoopback -and $uri.Port -eq 11434) {
    $ollama = Get-Command ollama -ErrorAction SilentlyContinue
    $ollamaExe = if ($ollama) { $ollama.Source } else { Join-Path $env:LOCALAPPDATA "Programs\Ollama\ollama.exe" }
    $base = "$($uri.Scheme)://$($uri.Host):$($uri.Port)"
    $up = $false
    try { $v = Invoke-RestMethod -Uri "$base/api/version" -TimeoutSec 3 -ErrorAction Stop; $up = $true; Log "Ollama $($v.version) is up at $base" } catch { }
    if (-not $up) {
        if (-not (Test-Path $ollamaExe)) { throw "Nothing answers at $base and Ollama is not installed; start a server there or pass -Endpoint." }
        Log "Starting Ollama..."
        Start-Process -FilePath $ollamaExe -ArgumentList "serve" -WindowStyle Hidden | Out-Null
        $deadline = (Get-Date).AddSeconds(30)
        while (-not $up -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500; try { Invoke-RestMethod -Uri "$base/api/version" -TimeoutSec 2 -ErrorAction Stop | Out-Null; $up = $true } catch { } }
        if (-not $up) { throw "Ollama did not come up at $base within 30 s." }
    }
    $tags = Invoke-RestMethod -Uri "$base/api/tags" -TimeoutSec 10 -ErrorAction Stop
    $have = @($tags.models | ForEach-Object { $_.name })
    if ($have -notcontains $Model) {
        Log "Model '$Model' is not pulled yet (have: $($have -join ', ')). Pulling..."
        & $ollamaExe pull $Model
        if ($LASTEXITCODE -ne 0) { throw "ollama pull failed for '$Model'." }
    }
    if ($ContextTokens -gt 0) {
        # Ollama's default context is 2048 tokens and a longer prompt is truncated from the middle without any error.
        # A derived model carries num_ctx; the base model is untouched.
        $short = (($Model -replace '^hf\.co/[^/]+/', '') -replace '[^A-Za-z0-9._-]', '-').ToLower()
        $derived = "relay-" + $short + ":ctx$ContextTokens"
        if ($have -notcontains $derived) {
            Log "Deriving '$derived' from '$Model' with num_ctx $ContextTokens..."
            $modelfile = Join-Path $OutDir "Modelfile"
            @("FROM $Model", "PARAMETER num_ctx $ContextTokens") | Set-Content -Path $modelfile -Encoding ASCII
            & $ollamaExe create $derived -f $modelfile
            if ($LASTEXITCODE -ne 0) { throw "ollama create failed for '$derived'." }
        }
        $Model = $derived
    }
    # Load it now so the first case is not charged the model load.
    Log "Loading the model..."
    $warm = @{ model = $Model; messages = @(@{ role = "user"; content = "Reply with the single word ready." }); max_tokens = 5; temperature = 0 } | ConvertTo-Json -Depth 5
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-RestMethod -Uri $Endpoint -Method Post -ContentType "application/json" -Body $warm -TimeoutSec 600 -ErrorAction Stop
    Log ("  loaded in {0:N1} s: {1}" -f $sw.Elapsed.TotalSeconds, ($r.choices[0].message.content -replace '\s+', ' ').Trim())
}

# ---- build -----------------------------------------------------------------------------------------
$tests = Join-Path $repo "tests\Relay.Tests\Relay.Tests.csproj"
if (-not $SkipBuild) {
    Log "Building..."
    & $dotnet build $tests -c Debug --nologo -v q 2>&1 | Where-Object { $_ -match "error|Build succeeded" } | ForEach-Object { Log "  $_" }
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

# ---- run -------------------------------------------------------------------------------------------
$env:RELAY_LIVE_MODEL_KEY = $Key
$env:RELAY_LIVE_MODEL_ENDPOINT = $Endpoint
$env:RELAY_LIVE_MODEL = $Model
$env:RELAY_LIVE_REPORT_DIR = $OutDir
$started = Get-Date
Log ""
Log "== live run: $Model at $Endpoint =="
Log "   filter: $Filter"
Log "   output: $OutDir"
Log ""
$log = Join-Path $OutDir "dotnet-test.log"
& $dotnet test $tests -c Debug --no-build --nologo --filter $Filter --blame-hang-timeout 45m 2>&1 | Tee-Object -FilePath $log | ForEach-Object {
    if ($_ -match "Passed!|Failed!|\[FAIL\]|\[PASS\]|Error Message|Assert|Expected|Actual|transcript") { Log "  $_" }
}
$exit = $LASTEXITCODE
$finished = Get-Date
Remove-Item Env:RELAY_LIVE_MODEL_KEY, Env:RELAY_LIVE_MODEL_ENDPOINT, Env:RELAY_LIVE_MODEL, Env:RELAY_LIVE_REPORT_DIR -ErrorAction SilentlyContinue

@{
    endpoint = $Endpoint; model = $Model; filter = $Filter; started = $started.ToString("o"); finished = $finished.ToString("o")
    seconds = [math]::Round(($finished - $started).TotalSeconds, 1); exitCode = $exit; outDir = $OutDir
} | ConvertTo-Json | Set-Content -Path (Join-Path $OutDir "run.json") -Encoding UTF8

# ---- report ----------------------------------------------------------------------------------------
$report = Join-Path $OutDir "evaluation-live.txt"
if (Test-Path $report) {
    Log ""
    Log "== evaluation report =="
    Get-Content $report | ForEach-Object { Log $_ }
}
$transcript = Join-Path $OutDir "listening-live.txt"
if (Test-Path $transcript) { Log ""; Log "transcript: $transcript ($((Get-Content $transcript).Count) lines)" }
Log ""
Log ("== done in {0:N0} s, dotnet test exit {1}; reports in {2} ==" -f ($finished - $started).TotalSeconds, $exit, $OutDir)
exit $exit
