param(
    [string]$InstallPath = "$env:LOCALAPPDATA\LaserficheReports",
    [string]$Model = 'gpt-oss:20b',
    [ValidateRange(1024, 65535)][int]$Port = 5187,
    [string]$LaserficheServerUrl = ''
)
$ErrorActionPreference = 'Stop'
$InstallPath = [IO.Path]::GetFullPath($InstallPath)
if ($InstallPath.Contains('"') -or $Model.Contains('"')) { throw 'Invalid installation arguments.' }
Get-Command py -ErrorAction Stop | Out-Null
$ollama = (Get-Command ollama -ErrorAction Stop).Source
if (!(Test-Path "$PSScriptRoot\web\LaserficheReports.Web.exe")) { throw 'Extract the entire transfer archive first.' }
$taskName = 'LaserficheReports-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    throw 'Reports is already running. Run unregister.ps1 first; settings will be preserved.'
}
New-Item -ItemType Directory -Force -Path $InstallPath, "$InstallPath\web", "$InstallPath\graph", "$InstallPath\logs" | Out-Null
if (!(Test-Path "$InstallPath\web\appsettings.Local.json")) {
    if (!$LaserficheServerUrl) { $LaserficheServerUrl = Read-Host 'Laserfiche server URL (example: https://laserfiche-server)' }
    $serverUri = $null
    if (![Uri]::TryCreate($LaserficheServerUrl, [UriKind]::Absolute, [ref]$serverUri) -or
        $serverUri.Scheme -notin @('http','https') -or $serverUri.UserInfo) { throw 'Enter a valid Laserfiche server URL.' }
    @{ Laserfiche = @{ ServerUrl = $serverUri.AbsoluteUri.TrimEnd('/'); ApiBasePath = '/LFRepositoryAPI'; ApiVersion = 'Auto' } } |
        ConvertTo-Json -Depth 4 | Set-Content "$InstallPath\web\appsettings.Local.json" -Encoding UTF8
}
# Copy shipped defaults; preserve the target machine's local connection settings.
Copy-Item "$PSScriptRoot\web\*" "$InstallPath\web" -Recurse -Force
Copy-Item "$PSScriptRoot\graph\*" "$InstallPath\graph" -Recurse -Force
Copy-Item "$PSScriptRoot\supervise.py" $InstallPath -Force
& py -3.11 -m venv "$InstallPath\python"
if ($LASTEXITCODE -ne 0) { throw 'Install Python 3.11 x64, then run Install.cmd again.' }
& "$InstallPath\python\Scripts\python.exe" -m pip install -r "$InstallPath\graph\requirements.txt"
if ($LASTEXITCODE -ne 0) { throw 'Python dependency installation failed.' }
# Reuse Ollama when available; start it without taking ownership of other apps.
try { Invoke-RestMethod 'http://127.0.0.1:11434/api/tags' -TimeoutSec 5 | Out-Null }
catch {
    Start-Process -FilePath $ollama -ArgumentList 'serve' -WindowStyle Hidden
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        try { Invoke-RestMethod 'http://127.0.0.1:11434/api/tags' -TimeoutSec 2 | Out-Null; $ready = $true; break }
        catch { Start-Sleep -Seconds 1 }
    }
    if (!$ready) { throw 'Ollama did not start. Open Ollama and retry.' }
}
& $ollama pull $Model
if ($LASTEXITCODE -ne 0) { throw 'Model download failed. Check Ollama and internet access, then retry.' }
# Actually load the selected model and check structured final output before startup.
$body = @{ model=$Model; stream=$false; think='low'; format='json';
    messages=@(@{role='user';content='Return exactly {"ready":true} as JSON.'});
    options=@{num_ctx=8192;num_predict=8192;temperature=0} } | ConvertTo-Json -Depth 8
if ($Model -notlike 'gpt-oss:*') { $body = ($body | ConvertFrom-Json); $body.PSObject.Properties.Remove('think'); $body = $body | ConvertTo-Json -Depth 8 }
$response = Invoke-RestMethod 'http://127.0.0.1:11434/api/chat' -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 0
if (!(($response.message.content | ConvertFrom-Json).ready)) { throw 'The model did not return valid final JSON.' }
$userId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute "$InstallPath\python\Scripts\pythonw.exe" -Argument "`"$InstallPath\supervise.py`" --root `"$InstallPath`" --ollama `"$ollama`" --model `"$Model`" --port $Port" -WorkingDirectory $InstallPath
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
$principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null
@('[InternetShortcut]', "URL=http://127.0.0.1:$Port") | Set-Content (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Laserfiche Reports.url') -Encoding ASCII
Start-ScheduledTask -TaskName $taskName
$webReady = $false
for ($i = 0; $i -lt 30; $i++) {
    try { Invoke-RestMethod "http://127.0.0.1:$Port/api/app/status" -TimeoutSec 2 | Out-Null; $webReady = $true; break }
    catch { Start-Sleep -Seconds 1 }
}
if ($webReady) { Start-Process "http://127.0.0.1:$Port" }
else { throw "Startup did not complete. Read $InstallPath\logs\services.log. Do not start another copy." }
Write-Host 'Installed. Sign in to your repository. Connection settings are in web\appsettings.Local.json.'
Write-Host "Logs: $InstallPath\logs\services.log"
