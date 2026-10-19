param(
    [ValidateRange(1, 100000)][int]$Count = 100000,
    [switch]$Resume,
    [string]$OutputPath = "$env:LOCALAPPDATA\LaserficheReports\logs\qa-language.jsonl"
)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'LaserficheReports'
$python = Join-Path $root 'python\Scripts\python.exe'
$script = Join-Path $root 'graph\qa_language.py'
if (!(Test-Path $python) -or !(Test-Path $script)) {
    throw 'Update the installed Reports application first using deployment\windows\install.ps1.'
}
$qaArgs = @($script, '--count', $Count, '--output', $OutputPath)
if ($Resume) { $qaArgs += '--resume' }
& $python @qaArgs
exit $LASTEXITCODE
