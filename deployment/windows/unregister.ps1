# Unregister only this user's Reports task. Never remove data or other services.
$ErrorActionPreference = 'Stop'
$taskName = 'LaserficheReports-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($task) {
    $root = $task.Actions[0].WorkingDirectory
    if ($root -and (Test-Path (Join-Path $root 'supervise.py'))) {
        New-Item -ItemType File -Force -Path (Join-Path $root '.stop') | Out-Null
        for ($i = 0; $i -lt 30; $i++) {
            if ((Get-ScheduledTask -TaskName $taskName).State -ne 'Running') { break }
            Start-Sleep -Seconds 1
        }
    }
    Stop-ScheduledTask -TaskName $taskName
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}
Write-Host 'Reports startup task removed. Application files, settings, models and all Laserfiche data are preserved.'
