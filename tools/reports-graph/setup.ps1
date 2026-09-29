param([string]$PythonVersion = "3.11")
$ErrorActionPreference = "Stop"
$venv = Join-Path $PSScriptRoot ".venv"
$python = Join-Path $venv "Scripts\python.exe"
if (-not (Test-Path $python)) {
    & py "-$PythonVersion" -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw "Install Python $PythonVersion (64-bit) first." }
}
& $python -m pip install --upgrade pip
if ($LASTEXITCODE -ne 0) { throw "pip upgrade failed." }
& $python -m pip install -r (Join-Path $PSScriptRoot "requirements.txt")
if ($LASTEXITCODE -ne 0) { throw "LangGraph installation failed." }
Write-Host "LangGraph environment is ready."
