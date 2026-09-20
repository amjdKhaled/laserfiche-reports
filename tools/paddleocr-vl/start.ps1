param(
    [ValidateSet("cpu", "gpu")]
    [string]$Device = "cpu",
    [string]$PipelineVersion = "v1.6",
    [int]$Port = 8765
)

$ErrorActionPreference = "Stop"
$python = Join-Path $PSScriptRoot ".venv\Scripts\python.exe"
$server = Join-Path $PSScriptRoot "server.py"

if (-not (Test-Path $python)) {
    throw "PaddleOCR-VL is not installed. Run tools\paddleocr-vl\setup.ps1 first."
}

& $python $server `
    --host "127.0.0.1" `
    --port $Port `
    --device $Device `
    --pipeline-version $PipelineVersion
