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

# This dedicated worker uses CAMeL's morphology analyzer only. Installing the
# package with every optional NLP dependency would also pull PyTorch,
# Transformers, pandas, and scikit-learn into the Paddle environment. Install
# the small, audited runtime subset needed by morphology and camel_data instead.
$camelMorphologyDependencies = @(
    "cachetools>=6.0.0",
    "docopt",
    "emoji",
    "future",
    "muddler",
    "pyrsistent",
    "requests",
    "six",
    "tabulate",
    "tqdm"
)
& $venvPython -m pip install --upgrade "opencv-python-headless==4.10.0.84" @camelMorphologyDependencies
& $venvPython -m pip install --upgrade --no-deps "camel-tools==1.6.0"

$camelData = Join-Path $venvDirectory "Scripts\camel_data.exe"
if (-not (Test-Path $camelData)) {
    throw "CAMeL Tools installed without camel_data.exe. Recreate tools\paddleocr-vl\.venv and run setup.ps1 again."
}
& $camelData -i morphology-db-msa-r13
if ($LASTEXITCODE -ne 0) {
    throw "CAMeL Tools could not install the morphology-db-msa-r13 dataset. Check Internet access, then run setup.ps1 again."
}

& $venvPython -c "import cv2; from camel_tools.morphology.database import MorphologyDB; MorphologyDB.builtin_db('calima-msa-r13'); print('OpenCV and CAMeL Tools verified.')"
if ($LASTEXITCODE -ne 0) {
    throw "OpenCV or CAMeL Tools verification failed. Recreate tools\paddleocr-vl\.venv and run setup.ps1 again."
}

Write-Host "Arabic PaddleOCR, OpenCV, and CAMeL Tools setup completed. Start it with:"
Write-Host "powershell -ExecutionPolicy Bypass -File .\tools\paddleocr-vl\start.ps1"
