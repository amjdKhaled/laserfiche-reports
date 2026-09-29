param([int[]]$EntryIds = @(), [switch]$All)
$ErrorActionPreference = "Stop"
$python = Join-Path $PSScriptRoot ".venv\Scripts\python.exe"
if (-not (Test-Path $python)) { throw "Run tools\reports-graph\setup.ps1 first." }
if (-not $All -and $EntryIds.Count -eq 0) { throw "Provide -EntryIds or -All." }
$syncArgs = @()
if ($All) { $syncArgs += "--all" }
$syncArgs += $EntryIds
& $python (Join-Path $PSScriptRoot "sync.py") @syncArgs
if ($LASTEXITCODE -ne 0) { throw "Some documents failed to ingest." }
