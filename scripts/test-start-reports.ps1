$ErrorActionPreference = 'Stop'
$global:taskProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\LaserficheReports.Web'))
$global:taskCommands = @()
$global:taskStopped = @()
$global:taskOwnerName = 'LaserficheReports.Web.exe'
$global:taskOwnerPath = Join-Path $global:taskProject 'bin\Debug\net8.0\LaserficheReports.Web.exe'
function Get-NetTCPConnection { param($LocalPort, $State, $ErrorAction) [pscustomobject]@{ OwningProcess = 98765 } }
function Get-CimInstance {
    param($ClassName, $Filter)
    if ($Filter -like 'ProcessId=*') {
        [pscustomobject]@{ Name = $global:taskOwnerName; ExecutablePath = $global:taskOwnerPath; CommandLine = ''; ProcessId = 98765 }
    }
}
function Stop-Process { param($Id, [switch]$Force) $global:taskStopped += $Id }
function Wait-Process { param($Id, $Timeout, $ErrorAction) }
function dotnet { $global:taskCommands += ($args -join ' '); $global:LASTEXITCODE = 0 }

& (Join-Path $PSScriptRoot 'start-reports.ps1') | Out-Null
if ($global:taskCommands.Count -ne 0 -or $global:taskStopped.Count -ne 0) { throw 'Existing instance was not reused.' }

& (Join-Path $PSScriptRoot 'start-reports.ps1') -Restart | Out-Null
if ($global:taskStopped.Count -ne 1 -or $global:taskStopped[0] -ne 98765) { throw 'Restart stopped an unexpected process.' }
if ($global:taskCommands.Count -ne 2 -or $global:taskCommands[0] -notlike 'build *' -or $global:taskCommands[1] -notlike 'run --no-build --no-launch-profile *') { throw 'Restart did not build and launch correctly.' }

$global:taskOwnerName = 'unrelated.exe'; $global:taskOwnerPath = 'C:\OtherApp\unrelated.exe'
$global:taskCommands = @(); $global:taskStopped = @(); $caught = $false
try { & (Join-Path $PSScriptRoot 'start-reports.ps1') -Restart | Out-Null }
catch { $caught = $_.Exception.Message -like 'Port * is used by *' }
if (-not $caught -or $global:taskCommands.Count -ne 0 -or $global:taskStopped.Count -ne 0) { throw 'Unrelated listener was not protected.' }
Write-Host 'PASS startup checks: reuse existing instance, scoped restart, preserve unrelated process.'
