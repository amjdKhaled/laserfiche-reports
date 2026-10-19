# Turns on LFDS single sign-on for Reports the same way as the Dashboard:
#   1. allows https://<server>:5188/login/Callback in the Repository API (WhitelistedRedirectUris)
#      and recycles its application pool,
#   2. switches the IIS-hosted Reports to Laserfiche:AuthenticationMode = LfdsSso,
#   3. redeploys the Web Client Reports button, which opens Reports on the open repository.
# Run from an Administrator PowerShell after install-iis.ps1. Every changed file is backed up first.
# -Disable restores the password form only; the Repository API entry is harmless and stays.
param(
    [string]$SitePath = "$env:SystemDrive\LaserficheReports\web",
    [ValidateRange(1024, 65535)][int]$Port = 5188,
    [string]$LfdsBaseUrl = '',
    [string]$ApiSettingsPath = "$env:ProgramData\Laserfiche\API Server\LFRepositoryAPI\appsettings.json",
    [string]$ApiAppPool = 'LFRepositoryAPI',
    [switch]$Disable
)
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script from an Administrator PowerShell.' }
Import-Module WebAdministration

# Names, not 127.0.0.1/localhost: the LFDS cookie and the certificate are bound to the server name.
$hostName = $env:COMPUTERNAME.ToLowerInvariant()
$baseUrl = "https://${hostName}:$Port"
$callback = "$baseUrl/login/Callback"
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$utf8 = New-Object System.Text.UTF8Encoding($false)

$settingsPath = Join-Path $SitePath 'appsettings.Local.json'
if (!(Test-Path $settingsPath)) { throw "$settingsPath was not found; run install-iis.ps1 first." }
$settings = [IO.File]::ReadAllText($settingsPath, $utf8) | ConvertFrom-Json
if (!$settings.Laserfiche) { throw "$settingsPath has no Laserfiche section." }
$lf = $settings.Laserfiche
if (!$LfdsBaseUrl) {
    if (!$lf.ServerUrl) { throw 'Laserfiche:ServerUrl is empty. Pass -LfdsBaseUrl, e.g. https://server/LFDSSTS' }
    $LfdsBaseUrl = $lf.ServerUrl.TrimEnd('/') + '/LFDSSTS'
}

if (!$Disable) {
    # The Repository API redirects only to listed callbacks; the entry must match exactly.
    if (!(Test-Path $ApiSettingsPath)) { throw "Repository API settings were not found: $ApiSettingsPath" }
    $apiText = [IO.File]::ReadAllText($ApiSettingsPath, $utf8)
    $pattern = '("WhitelistedRedirectUris"\s*:\s*")([^"]*)(")'
    $found = [regex]::Matches($apiText, $pattern)
    if ($found.Count -ne 1) { throw "Expected one WhitelistedRedirectUris entry in $ApiSettingsPath; found $($found.Count). Add $callback manually." }
    $current = $found[0].Groups[2].Value
    $uris = @($current.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($uris -ccontains $callback) {
        Write-Host "Repository API already allows $callback"
    } else {
        Copy-Item $ApiSettingsPath "$ApiSettingsPath.bak-$stamp"
        $updated = ($uris + $callback) -join ','
        $apiText = [regex]::Replace($apiText, $pattern, { param($m) $m.Groups[1].Value + $updated + $m.Groups[3].Value })
        [IO.File]::WriteAllText($ApiSettingsPath, $apiText, $utf8)
        Restart-WebAppPool -Name $ApiAppPool
        Write-Host "Repository API now allows $callback (backup: $ApiSettingsPath.bak-$stamp); pool $ApiAppPool recycled."
    }
}

Copy-Item $settingsPath "$settingsPath.bak-$stamp"
$lf | Add-Member -NotePropertyName AuthenticationMode -NotePropertyValue $(if ($Disable) { 'RepositoryPassword' } else { 'LfdsSso' }) -Force
$lf | Add-Member -NotePropertyName ApplicationBaseUrl -NotePropertyValue $baseUrl -Force
if (!$lf.Sso) { $lf | Add-Member -NotePropertyName Sso -NotePropertyValue ([pscustomobject]@{}) }
$lf.Sso | Add-Member -NotePropertyName LfdsBaseUrl -NotePropertyValue $LfdsBaseUrl -Force
[IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 20), $utf8)
Write-Host "Reports AuthenticationMode = $($lf.AuthenticationMode) (backup: $settingsPath.bak-$stamp). Reports reloads it without a restart."

if (!$Disable) { & "$PSScriptRoot\deploy-webclient-button.ps1" -ReportsUrl "$baseUrl/" }

Start-Sleep -Seconds 2
$status = Invoke-RestMethod "$baseUrl/api/session/status" -TimeoutSec 15
Write-Host "Reports SSO enabled: $($status.sso)"
if ($status.sso -eq $Disable.IsPresent) { throw 'Reports did not report the expected sign-in mode. Check appsettings.Local.json.' }
