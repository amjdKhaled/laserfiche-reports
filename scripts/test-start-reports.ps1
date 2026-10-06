$ErrorActionPreference = 'Stop'
$script:taskProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\LaserficheReports.Web'))
$script:taskCommands = @()
$script:taskStopped = @()
$script:taskOwnerName = 'LaserficheReports.Web.exe'
$script:taskOwnerPath = Join-Path $script:taskProject 'bin\Debug\net8.0\LaserficheReports.Web.exe'
function Get-NetTCPConnection { param($LocalPort, $State, $ErrorAction) [pscustomobject]@{ OwningProcess = 98765 } }
function Get-CimInstance {
    param($ClassName, $Filter)
    if ($Filter -like 'ProcessId=*') {
        [pscustomobject]@{ Name = $script:taskOwnerName; ExecutablePath = $script:taskOwnerPath; CommandLine = ''; ProcessId = 98765 }
    }
}
function Stop-Process { param($Id, [switch]$Force) $script:taskStopped += $Id }
function Wait-Process { param($Id, $Timeout, $ErrorAction) }
function dotnet { $script:taskCommands += ($args -join ' '); $global:LASTEXITCODE = 0 }

& (Join-Path $PSScriptRoot 'start-reports.ps1') | Out-Null
if ($script:taskCommands.Count -ne 0 -or $script:taskStopped.Count -ne 0) { throw 'Existing instance was not reused.' }

& (Join-Path $PSScriptRoot 'start-reports.ps1') -Restart | Out-Null
if ($script:taskStopped.Count -ne 1 -or $script:taskStopped[0] -ne 98765) { throw 'Restart stopped an unexpected process.' }
if ($script:taskCommands.Count -ne 2 -or $script:taskCommands[0] -notlike 'build *' -or $script:taskCommands[1] -notlike 'run --no-build --no-launch-profile *') { throw 'Restart did not build and launch correctly.' }

$script:taskOwnerName = 'unrelated.exe'; $script:taskOwnerPath = 'C:\OtherApp\unrelated.exe'
$script:taskCommands = @(); $script:taskStopped = @(); $caught = $false
try { & (Join-Path $PSScriptRoot 'start-reports.ps1') -Restart | Out-Null }
catch { $caught = $_.Exception.Message -like 'Port * is used by *' }
if (-not $caught -or $script:taskCommands.Count -ne 0 -or $script:taskStopped.Count -ne 0) { throw 'Unrelated listener was not protected.' }
Write-Host 'PASS startup checks: reuse existing instance, scoped restart, preserve unrelated process.'
