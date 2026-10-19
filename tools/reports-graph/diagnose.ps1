[CmdletBinding()]
param(
    [string]$TracePath = '.\logs\planner-trace.jsonl',
    [string]$OutputPath = '.\logs\planner-diagnostics.json',
    [string]$GraphUrl = 'http://127.0.0.1:8766'
)
$ErrorActionPreference = 'Stop'
$uri = [Uri]$GraphUrl
if ($uri.Scheme -ne 'http' -or -not $uri.IsLoopback -or $uri.UserInfo) {
    throw 'GraphUrl must be a local HTTP URL without credentials.'
}
if (-not (Test-Path -LiteralPath $TracePath -PathType Leaf)) {
    throw 'Planner trace is missing. Start the graph with -PlannerTracePath .\logs\planner-trace.jsonl and repeat the failed question.'
}
$records = [System.Collections.Generic.List[object]]::new()
foreach ($line in [IO.File]::ReadLines((Resolve-Path -LiteralPath $TracePath).Path)) {
    if (-not [string]::IsNullOrWhiteSpace($line)) {
        try { $records.Add(($line | ConvertFrom-Json)) }
        catch { Write-Warning 'Skipped an incomplete trace line.' }
    }
}
$lastInput = $records | Where-Object { $_.stage -eq 'route_input' } | Select-Object -Last 1
if (-not $lastInput) { throw 'No captured planning request exists in the trace.' }
# The latest request contains its original question, live catalog and individual
# model calls. No cookie, token, password or application settings are collected.
$lastIndex = $records.IndexOf($lastInput)
$latest = @($records | Select-Object -Skip $lastIndex | Where-Object { $_.requestId -eq $lastInput.requestId })
$health = $null
try { $health = Invoke-RestMethod -Uri ($GraphUrl.TrimEnd('/') + '/health') -TimeoutSec 10 }
catch { $health = @{ status = 'unavailable' } }
$commit = (& git rev-parse HEAD 2>$null | Out-String).Trim()
$diagnostic = @{ generatedAt = [DateTimeOffset]::UtcNow.ToString('o'); commit = $commit; graph = $health; request = $latest }
$fullPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullPath)) | Out-Null
[IO.File]::WriteAllText($fullPath, ($diagnostic | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
Write-Host "Saved planning diagnostics: $fullPath"
