param(
    [string]$PythonVersion = "3.11"
)

$ErrorActionPreference = "Stop"
$toolDirectory = $PSScriptRoot
$venvDirectory = Join-Path $toolDirectory ".venv"
$venvPython = Join-Path $venvDirectory "Scripts\python.exe"

if (-not (Test-Path $venvPython)) {
    if (Get-Command py -ErrorAction SilentlyContinue) {
        & py "-$PythonVersion" -m venv $venvDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "Python $PythonVersion could not create the PaddleOCR virtual environment. Install Python $PythonVersion (64-bit), then run this script again."
        }
    }
    elseif (Get-Command python -ErrorAction SilentlyContinue) {
        & python -m venv $venvDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "Python could not create the PaddleOCR virtual environment. Install Python $PythonVersion (64-bit), then run this script again."
        }
    }
    else {
        throw "Python 3.9-3.13 is required. Install Python, then run this script again."
    }
}

if (-not (Test-Path $venvPython)) {
    throw "The PaddleOCR virtual environment was not created. Install Python $PythonVersion (64-bit) from python.org, then run this script again."
}

& $venvPython -m pip install --upgrade pip
& $venvPython -m pip install "paddlepaddle==3.2.1" -i "https://www.paddlepaddle.org.cn/packages/stable/cpu/"
& $venvPython -m pip install --upgrade "paddleocr[doc-parser]"

Write-Host "Arabic PP-StructureV3 setup completed. Start it with:"
Write-Host "powershell -ExecutionPolicy Bypass -File .\tools\paddleocr-vl\start.ps1"
