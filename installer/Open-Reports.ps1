$ErrorActionPreference = 'Stop'
$settings = Get-Content (Join-Path $env:ProgramData 'LaserficheReports\hosting.config.json') -Raw | ConvertFrom-Json
Start-Process "http://127.0.0.1:$($settings.Port)"
