# Hosts the Reports web app in IIS over HTTPS with the server certificate (the same way as the Dashboard site),
# then adds the Reports button to the Laserfiche Web Client.
# Run from an Administrator PowerShell as the Windows user who ran install.ps1: that user's scheduled task
# keeps running the AI graph and Ollama, while IIS now serves the web app.
param(
    [string]$InstallPath = "$env:LOCALAPPDATA\LaserficheReports",
    [string]$SitePath = "$env:SystemDrive\LaserficheReports\web",
    [string]$SiteName = 'LaserficheReports',
    [ValidateRange(1024, 65535)][int]$Port = 5188,
    [string]$CertificateThumbprint = '',
    [switch]$SkipWebClientButton
)
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script from an Administrator PowerShell.' }
$ancmInstalled = (Test-Path 'HKLM:\SOFTWARE\Microsoft\IIS Extensions\IIS AspNetCore Module V2') -or
    (Test-Path "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll") -or (Test-Path "$env:windir\System32\inetsrv\aspnetcorev2.dll")
if (!$ancmInstalled) { throw 'Install the ASP.NET Core Hosting Bundle (IIS module) first.' }
Import-Module WebAdministration
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$source = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$hostName = $env:COMPUTERNAME.ToLowerInvariant()
$siteUrl = "https://${hostName}:$Port/"

# Reuse the certificate IIS already serves for Laserfiche on port 443 (the Dashboard uses it too);
# otherwise the newest CN=<computer name> certificate with a private key.
$https443 = Get-Item 'IIS:\SslBindings\0.0.0.0!443' -ErrorAction SilentlyContinue
$cert = if ($CertificateThumbprint) { Get-Item "Cert:\LocalMachine\My\$CertificateThumbprint" }
    elseif ($https443 -and $https443.Thumbprint) { Get-Item "Cert:\LocalMachine\My\$($https443.Thumbprint)" }
    else { Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq "CN=$env:COMPUTERNAME" -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1 }
Write-Host "Certificate: $($cert.Subject) $($cert.Thumbprint)"
if (!$cert) { throw "No valid certificate CN=$env:COMPUTERNAME was found in LocalMachine\My. Pass -CertificateThumbprint." }

$existingSite = Get-Website | Where-Object { $_.Name -eq $SiteName }
$portOwner = Get-Website | Where-Object { $_.Name -ne $SiteName -and ($_.Bindings.Collection | Where-Object { $_.bindingInformation -match ":${Port}:" }) }
if ($portOwner) { throw "IIS site '$($portOwner.Name)' already uses port $Port. Choose another -Port." }

$dotnet = @('dotnet', "${env:ProgramFiles(x86)}\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe") |
    Where-Object { (Get-Command $_ -ErrorAction SilentlyContinue) -and ((& $_ --list-sdks) -match '^8\.') } | Select-Object -First 1
if (!$dotnet) { throw 'A .NET 8 SDK is required to publish the web app.' }

if ($existingSite) { Stop-Website -Name $SiteName }
if ((Test-Path "IIS:\AppPools\$SiteName") -and (Get-WebAppPoolState -Name $SiteName).Value -ne 'Stopped') {
    Stop-WebAppPool -Name $SiteName
    for ($i = 0; $i -lt 30 -and (Get-WebAppPoolState -Name $SiteName).Value -ne 'Stopped'; $i++) { Start-Sleep -Seconds 1 }
}
New-Item -ItemType Directory -Force -Path $SitePath | Out-Null
& $dotnet publish "$source\src\LaserficheReports.Web" -c Release -r win-x64 --self-contained true -o $SitePath -nologo
if ($LASTEXITCODE -ne 0) { throw 'Web publish failed; IIS was not changed.' }

# Private connection settings are never published; reuse the installed copy, then allow the server host name.
$settingsPath = Join-Path $SitePath 'appsettings.Local.json'
if (!(Test-Path $settingsPath)) {
    $installed = Join-Path $InstallPath 'web\appsettings.Local.json'
    if (!(Test-Path $installed)) { $installed = "$source\src\LaserficheReports.Web\appsettings.Local.json" }
    if (!(Test-Path $installed)) { throw 'appsettings.Local.json was not found; run install.ps1 first.' }
    Copy-Item $installed $settingsPath
}
$utf8 = New-Object System.Text.UTF8Encoding($false)
$settings = [IO.File]::ReadAllText($settingsPath, $utf8) | ConvertFrom-Json
$settings | Add-Member -NotePropertyName AllowedHosts -NotePropertyValue "localhost;127.0.0.1;$hostName" -Force
[IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 20), $utf8)

# No managed CLR (ASP.NET Core in-process), keep sessions alive: no idle shutdown or scheduled recycle.
$poolPath = "IIS:\AppPools\$SiteName"
if (!(Test-Path $poolPath)) { New-WebAppPool -Name $SiteName | Out-Null }
Set-ItemProperty $poolPath -Name managedRuntimeVersion -Value ''
Set-ItemProperty $poolPath -Name processModel.loadUserProfile -Value $true
Set-ItemProperty $poolPath -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty $poolPath -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
$identity = "IIS AppPool\$SiteName"
& icacls $SitePath /grant "${identity}:(OI)(CI)RX" /T /Q | Out-Null
$programData = Join-Path $env:ProgramData 'LaserficheReports'
New-Item -ItemType Directory -Force -Path $programData | Out-Null
& icacls $programData /grant "${identity}:(OI)(CI)M" /Q | Out-Null

if (!$existingSite) {
    New-Website -Name $SiteName -PhysicalPath $SitePath -ApplicationPool $SiteName -Port $Port -Ssl | Out-Null
} else {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $SitePath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $SiteName
}
$sslBinding = "IIS:\SslBindings\0.0.0.0!$Port"
if (Test-Path $sslBinding) { Remove-Item $sslBinding }
New-Item -Path $sslBinding -Value $cert | Out-Null
# New pools and sites can start on their own; starting a started one is an error.
if ((Get-WebAppPoolState -Name $SiteName).Value -ne 'Started') { Start-WebAppPool -Name $SiteName }
if ((Get-WebsiteState -Name $SiteName).Value -ne 'Started') { Start-Website -Name $SiteName }

# The scheduled task keeps only the AI graph and Ollama; IIS serves the web app.
Copy-Item "$source\deployment\supervise.py" $InstallPath -Force
$taskName = 'LaserficheReports-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if (!$task) { throw 'The Reports scheduled task was not found; run install.ps1 first, then re-run this script.' }
$action = $task.Actions[0]
New-Item -ItemType File -Force -Path (Join-Path $InstallPath '.stop') | Out-Null
for ($i = 0; $i -lt 30 -and (Get-ScheduledTask -TaskName $taskName).State -eq 'Running'; $i++) { Start-Sleep -Seconds 1 }
if ($action.Arguments -notmatch '--no-web') {
    $graphOnly = New-ScheduledTaskAction -Execute $action.Execute -Argument ($action.Arguments + ' --no-web') -WorkingDirectory $action.WorkingDirectory
    Set-ScheduledTask -TaskName $taskName -Action $graphOnly | Out-Null
}
Start-ScheduledTask -TaskName $taskName

$ready = $false
for ($i = 0; $i -lt 60 -and !$ready; $i++) {
    try { Invoke-RestMethod "${siteUrl}api/app/status" -TimeoutSec 5 | Out-Null; $ready = $true } catch { Start-Sleep -Seconds 1 }
}
if (!$ready) { throw "IIS site did not answer on $siteUrl. Check the Windows Application event log (IIS AspNetCore Module V2)." }

@('[InternetShortcut]', "URL=$siteUrl") | Set-Content (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Laserfiche Reports.url') -Encoding ASCII
if (!$SkipWebClientButton) { & "$PSScriptRoot\deploy-webclient-button.ps1" -ReportsUrl $siteUrl }
Write-Host "Reports is served by IIS at $siteUrl"
