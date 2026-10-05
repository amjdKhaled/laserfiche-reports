$ErrorActionPreference='Stop'
$env:Realtime__Enabled='false'
$env:Realtime__StateDirectory=Join-Path $env:RUNNER_TEMP 'reports-smoke-state'
$env:Laserfiche__ServerUrl='http://127.0.0.1:9'
$env:Laserfiche__ApiVersion='v2'
$root=Join-Path $PSScriptRoot '..\src\LaserficheReports.Web'
$out=Join-Path $env:RUNNER_TEMP 'reports-smoke.log'
$err=Join-Path $env:RUNNER_TEMP 'reports-smoke-error.log'
$process=Start-Process -FilePath 'dotnet' -ArgumentList @('run','--no-build','--configuration','Release','--project',"`"$root`"",'--no-launch-profile','--','--urls','http://127.0.0.1:15987') -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
try {
    $ready=$false
    for($i=0;$i -lt 100;$i++) {
        if($process.HasExited){throw "Web host exited: $(Get-Content $err -Raw) $(Get-Content $out -Raw)"}
        try {$status=Invoke-RestMethod 'http://127.0.0.1:15987/api/app/status';if($status.application -eq 'Laserfiche Reports'){$ready=$true;break}}catch {Start-Sleep -Milliseconds 200}
    }
    if(-not $ready){throw 'Web host did not start.'}
    $html=(Invoke-WebRequest 'http://127.0.0.1:15987/' -UseBasicParsing).Content
    if($html -notmatch 'tab-docs' -or $html -notmatch 'حالة المزامنة'){throw 'Current UI was not served.'}
    foreach($path in @('/api/reports/sync/status','/api/reports/documents','/api/reports/files/unknown')) {
        $code=(Invoke-WebRequest ('http://127.0.0.1:15987'+$path) -SkipHttpErrorCheck).StatusCode
        if($code -ne 401){throw "Unauthenticated $path returned $code; expected 401."}
    }
    Write-Host 'Web host started, served UI/status and enforced authentication on report endpoints.'
} finally {Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue}
