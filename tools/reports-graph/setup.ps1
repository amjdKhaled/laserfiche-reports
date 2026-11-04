param([string]$PythonVersion = "")
$ErrorActionPreference = "Stop"
if (!$PythonVersion) {
    $installedPythons = (& py -0p) -join "`n"
    $PythonVersion = @('3.11', '3.12', '3.13') | Where-Object { $installedPythons -match "(?m)^\s*-(V:)?$([regex]::Escape($_))\b" } | Select-Object -First 1
    if (!$PythonVersion) { throw 'Python 3.11, 3.12 or 3.13 (64-bit) is required.' }
}
$venv = Join-Path $PSScriptRoot ".venv"
$python = Join-Path $venv "Scripts\python.exe"
# A virtual environment copied from another machine points at a missing base interpreter.
$config = Join-Path $venv "pyvenv.cfg"
$baseHome = if (Test-Path $config) { (Get-Content $config | Where-Object { $_ -match '^home\s*=' }) -replace '^home\s*=\s*', '' }
if (-not (Test-Path $python) -or -not $baseHome -or -not (Test-Path $baseHome)) {
    & py "-$PythonVersion" -m venv --clear $venv
    if ($LASTEXITCODE -ne 0) { throw "Install Python $PythonVersion (64-bit) first." }
}
& $python -m pip install --upgrade pip
if ($LASTEXITCODE -ne 0) { throw "pip upgrade failed." }
& $python -m pip install -r (Join-Path $PSScriptRoot "requirements.txt")
if ($LASTEXITCODE -ne 0) { throw "LangGraph installation failed." }
Write-Host "LangGraph environment is ready."
