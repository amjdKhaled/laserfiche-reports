param(
    [ValidateSet("cpu", "gpu")]
    [string]$Device = "cpu",
    [string]$OcrVersion = "PP-OCRv5",
    [string]$Language = "ar",
    [string]$RecognitionModel = "arabic_PP-OCRv5_mobile_rec",
    [ValidateRange(0.0, 1.0)]
    [double]$MinimumScore = 0.35,
    [ValidateRange(1600, 4000)]
    [int]$TextDetectionMaxSideLength = 3000,
    [int]$Port = 8765
)

$ErrorActionPreference = "Stop"
$python = Join-Path $PSScriptRoot ".venv\Scripts\python.exe"
$server = Join-Path $PSScriptRoot "server.py"

if (-not (Test-Path $python)) {
    throw "PaddleOCR is not installed. Run tools\paddleocr-vl\setup.ps1 first."
}

& $python $server `
    --host "127.0.0.1" `
    --port $Port `
    --device $Device `
    --ocr-version $OcrVersion `
    --language $Language `
    --recognition-model $RecognitionModel `
    --minimum-score $MinimumScore `
    --text-det-limit-side-len $TextDetectionMaxSideLength
