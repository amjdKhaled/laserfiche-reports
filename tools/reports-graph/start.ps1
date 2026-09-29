param([string]$Model = "qwen2.5:7b")
$ErrorActionPreference = "Stop"
$python = Join-Path $PSScriptRoot ".venv\Scripts\python.exe"
if (-not (Test-Path $python)) { throw "Run tools\reports-graph\setup.ps1 first." }
& $python (Join-Path $PSScriptRoot "server.py") --model $Model
