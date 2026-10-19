param(
    [string]$InstallPath = "$env:LOCALAPPDATA\LaserficheReports",
    [string]$Model = "qwen3-vl:8b-instruct",
    [int]$Port = 5187,
    [int]$GraphPort = 5050,
    [string]$PythonVersion = ''
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$InstallPath = [IO.Path]::GetFullPath($InstallPath)
if ($InstallPath.Contains('"') -or $Model.Contains('"') -or $Port -lt 1024 -or $Port -gt 65535 -or $GraphPort -lt 1024 -or $GraphPort -gt 65535 -or $GraphPort -eq $Port) { throw 'Invalid installation arguments.' }
Get-Command dotnet -ErrorAction Stop | Out-Null
Get-Command py -ErrorAction Stop | Out-Null
if (!$PythonVersion) {
    $installedPythons = (& py -0p) -join "`n"
    $PythonVersion = @('3.11', '3.12', '3.13') | Where-Object { $installedPythons -match "(?m)^\s*-(V:)?$([regex]::Escape($_))\b" } | Select-Object -First 1
    if (!$PythonVersion) { throw 'Python 3.11, 3.12 or 3.13 (64-bit) is required.' }
}
$ollama = (Get-Command ollama -ErrorAction Stop).Source
$taskName = 'LaserficheReports-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing) { throw "Reports is already installed. Use deployment\windows\unregister.ps1 to stop and unregister it before updating. Files and settings are preserved." }
New-Item -ItemType Directory -Force -Path $InstallPath, "$InstallPath\web", "$InstallPath\graph", "$InstallPath\logs" | Out-Null
& dotnet publish "$source\src\LaserficheReports.Web" -c Release -r win-x64 --self-contained true -o "$InstallPath\web"
if ($LASTEXITCODE -ne 0) { throw 'Web publish failed; nothing was registered for startup.' }
Copy-Item "$source\tools\reports-graph\*.py" "$InstallPath\graph" -Force
Copy-Item "$source\tools\reports-graph\requirements.txt" "$InstallPath\graph" -Force
Copy-Item "$source\deployment\supervise.py" $InstallPath -Force
& py "-$PythonVersion" -m venv "$InstallPath\python"
if ($LASTEXITCODE -ne 0) { throw "Python $PythonVersion x64 virtual environment creation failed." }
& "$InstallPath\python\Scripts\python.exe" -m pip install -r "$InstallPath\graph\requirements.txt"
if ($LASTEXITCODE -ne 0) { throw 'Python dependencies failed to install.' }
$localConfig = "$source\src\LaserficheReports.Web\appsettings.Local.json"
if ((Test-Path $localConfig) -and !(Test-Path "$InstallPath\web\appsettings.Local.json")) { Copy-Item $localConfig "$InstallPath\web" }
& $ollama pull $Model
if ($LASTEXITCODE -ne 0) { throw 'Model download failed. Open Ollama, check internet access and retry; startup has not been registered.' }
$userId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute "$InstallPath\python\Scripts\pythonw.exe" -Argument "`"$InstallPath\supervise.py`" --root `"$InstallPath`" --ollama `"$ollama`" --model `"$Model`" --port $Port --graph-port $GraphPort" -WorkingDirectory $InstallPath
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
$principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Laserfiche Reports local web and AI services for this user.' | Out-Null
$desktop = [Environment]::GetFolderPath('Desktop')
@("[InternetShortcut]", "URL=http://127.0.0.1:$Port") | Set-Content (Join-Path $desktop 'Laserfiche Reports.url') -Encoding ASCII
Start-ScheduledTask -TaskName $taskName
Write-Host "Installed. Opens at http://127.0.0.1:$Port and starts automatically when this Windows user signs in."
Write-Host "Service logs: $InstallPath\logs\services.log. Existing Laserfiche/Supabase services and models must remain available."
