#Requires -Version 5.1
[CmdletBinding()]
param([switch]$Restart, [ValidateRange(1024,65535)][int]$Port = 5187)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $projectRoot 'src\LaserficheReports.Web'
$url = "http://127.0.0.1:$Port"
$listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique)
if ($listeners.Count -gt 0) {
    foreach ($ownerId in $listeners) {
        $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId=$ownerId"
        $belongsToProject = $processInfo -and (
            ($processInfo.ExecutablePath -and $processInfo.ExecutablePath.StartsWith($project + '\', [StringComparison]::OrdinalIgnoreCase)) -or
            ($processInfo.Name -eq 'dotnet.exe' -and $processInfo.CommandLine -and
             $processInfo.CommandLine.IndexOf((Join-Path $project 'bin\'), [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
             $processInfo.CommandLine -match 'LaserficheReports\.Web\.dll'))
        if (-not $belongsToProject) {
            throw "Port $Port is used by $($processInfo.Name) (PID $ownerId). No process was stopped. Inspect that process or use -Port with another port."
        }
        if (-not $Restart) {
            Write-Host "Reports is already running: $url (PID $ownerId). Use -Restart to rebuild and restart it."
            return
        }
        Stop-Process -Id $ownerId -Force
        Wait-Process -Id $ownerId -Timeout 10 -ErrorAction SilentlyContinue
    }
}
# Also release DLL locks from a prior instance of this project on another port.
if ($Restart) {
    Get-CimInstance Win32_Process -Filter "Name='LaserficheReports.Web.exe'" | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($project + '\', [StringComparison]::OrdinalIgnoreCase)
    } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Wait-Process -Id $_.ProcessId -Timeout 10 -ErrorAction SilentlyContinue }
}
Push-Location $projectRoot
try {
    dotnet build $project
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. Reports was not started.' }
    Write-Host "Starting Reports: $url"
    dotnet run --no-build --no-launch-profile --project $project -- --urls $url
    if ($LASTEXITCODE -ne 0) { throw 'Reports stopped with an error. Check the displayed message and local application logs.' }
} finally { Pop-Location }
