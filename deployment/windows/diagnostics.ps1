param([string]$InstallPath = "$env:LOCALAPPDATA\LaserficheReports")
$ErrorActionPreference = 'Stop'
$report = [ordered]@{ generatedAt = (Get-Date).ToUniversalTime().ToString('o'); graph = $null; services = @(); planner = @() }
try { $report.graph = Invoke-RestMethod 'http://127.0.0.1:5050/health' -TimeoutSec 10 }
catch { $report.graph = @{ status = 'unavailable'; error = $_.Exception.Message } }
$servicePath = Join-Path $InstallPath 'logs\services.log'
$tracePath = Join-Path $InstallPath 'logs\planner-trace.jsonl'
if (Test-Path $servicePath) { $report.services = @(Get-Content $servicePath -Encoding UTF8 -Tail 150) }
if (Test-Path $tracePath) {
    $report.planner = @(Get-Content $tracePath -Encoding UTF8 -Tail 30 | ForEach-Object {
        try { $_ | ConvertFrom-Json } catch { @{ unreadableTraceLine = $true } }
    })
}
$target = Join-Path $InstallPath ('logs\diagnostics-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
$report | ConvertTo-Json -Depth 100 | Set-Content $target -Encoding UTF8
Write-Host "Saved local diagnostics: $target"
Write-Host 'Contains questions, field names and model replies. Review before sharing.'
