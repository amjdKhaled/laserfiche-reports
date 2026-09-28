param([Parameter(Mandatory = $true)][int[]]$EntryIds)
$ErrorActionPreference = "Stop"
$python = Join-Path $PSScriptRoot ".venv\Scripts\python.exe"
if (-not (Test-Path $python)) { throw "Run tools\reports-graph\setup.ps1 first." }
& $python (Join-Path $PSScriptRoot "sync.py") @EntryIds
if ($LASTEXITCODE -ne 0) { throw "Some documents failed to ingest." }
