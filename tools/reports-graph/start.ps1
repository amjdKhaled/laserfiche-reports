param([string]$Model = "", [int]$TimeoutSeconds = 600,
      [int]$PlannerTimeoutSeconds = 120, [int]$PlannerOutputTokens = 1536)
$ErrorActionPreference = "Stop"
$python = Join-Path $PSScriptRoot ".venv\Scripts\python.exe"
if (-not (Test-Path $python)) { throw "Run tools\reports-graph\setup.ps1 first." }
$graphArgs = @((Join-Path $PSScriptRoot "server.py"), "--model-timeout-seconds", $TimeoutSeconds,
    "--planner-timeout-seconds", $PlannerTimeoutSeconds, "--planner-output-tokens", $PlannerOutputTokens)
if ($Model) { $graphArgs += @("--model", $Model) }
& $python @graphArgs
