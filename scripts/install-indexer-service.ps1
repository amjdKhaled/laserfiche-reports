[CmdletBinding()]
param(
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'LaserficheReports'),
    [string]$Url = 'http://127.0.0.1:5187',
    [switch]$Start
)
$ErrorActionPreference = 'Stop'
$serviceName = 'LaserficheReportsIndexer'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run PowerShell as Administrator.' }
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw 'Service already exists. Stop it before upgrading its published files; this script does not replace a running installation.' }
$uri = [Uri]$Url
if ($uri.Scheme -ne 'http' -or -not $uri.IsLoopback) { throw 'Use a loopback HTTP URL; remote access is not enabled by this application.' }
$project = Join-Path $PSScriptRoot '..\src\LaserficheReports.Web\LaserficheReports.Web.csproj'
# Self-contained publish avoids requiring the .NET runtime on the target machine.
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $InstallPath
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
$exe = Join-Path $InstallPath 'LaserficheReports.Web.exe'
$state = Join-Path $env:ProgramData 'LaserficheReports\realtime'
New-Item -ItemType Directory -Path $state -Force | Out-Null
# The default LocalService identity does not inherit the interactive user's DPAPI credentials.
# Configure service credentials explicitly via protect-indexer-credentials.ps1 before starting.
& icacls $state /inheritance:r /grant '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)M' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not secure indexer state directory.' }
$command = '"' + $exe + '" --contentRoot "' + $InstallPath + '" --urls "' + $Url + '"'
& sc.exe create $serviceName binPath= $command start= auto obj= 'NT AUTHORITY\LocalService' DisplayName= 'Laserfiche Reports AI Indexer'
if ($LASTEXITCODE -ne 0) { throw 'Could not install Windows service.' }
& sc.exe description $serviceName 'Laserfiche Reports: live API and event-driven content indexing.' | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
if ($Start) { Start-Service -Name $serviceName }
Write-Host "Installed $serviceName. Configure Laserfiche, SDK path and AI/database settings in appsettings.Local.json, protect service credentials, then Start-Service $serviceName."
