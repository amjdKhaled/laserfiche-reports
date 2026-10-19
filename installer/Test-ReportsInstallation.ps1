[CmdletBinding()]
param([Parameter(Mandatory)][string]$MsiPath, [switch]$DisposableMachine)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -and !$DisposableMachine) { throw 'Run only on a disposable Windows test machine.' }
$msi = (Resolve-Path $MsiPath).Path
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$logs = Join-Path $repo 'artifacts\acceptance'; New-Item -ItemType Directory -Force -Path $logs | Out-Null
$root = Join-Path $env:ProgramFiles 'LaserficheReports'
$data = Join-Path $env:ProgramData 'LaserficheReports'
$outside = Join-Path $env:TEMP ('Reports-Unrelated-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $outside | Out-Null
$sentinel = Join-Path $outside 'Laserfiche-sentinel.txt'; 'unrelated document' | Set-Content $sentinel
$sentinelHash = (Get-FileHash $sentinel).Hash
# Independent fake Laserfiche libraries/data on a disposable runner. Hash them
# after every rejected operation and install/repair/upgrade/uninstall action.
$lfSentinels = @{}
foreach ($parent in @($env:ProgramFiles,$env:ProgramData)) {
    $fixture = Join-Path $parent ('Laserfiche\ReportsSafetyFixture-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $fixture | Out-Null
    foreach ($leaf in @('RepositoryAccess.dll','DocumentServices.dll','repository-data.dat')) {
        $file = Join-Path $fixture $leaf; 'unrelated Laserfiche fixture' | Set-Content $file
        $lfSentinels[$file] = (Get-FileHash $file).Hash
    }
}
# Verify the shipped MSI includes both native configuration pages and AI selection.
$wi = New-Object -ComObject WindowsInstaller.Installer
$db = $wi.OpenDatabase($msi,0)
foreach ($dialog in @('ReportsConnectionDlg','ReportsAiDlg')) {
    $view = $db.OpenView("SELECT ``Dialog`` FROM ``Dialog`` WHERE ``Dialog``='$dialog'")
    $view.Execute(); if (!$view.Fetch()) { throw "Missing MSI settings dialog: $dialog" }; $view.Close()
}
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($db)
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($wi)
$profile = @{ServerUrl='https://target-server.invalid';ApiBasePath='/LFRepositoryAPI';ApiVersion='v2';RepositoryId='TargetRepo';
    WebClientBaseUrl='https://target-server.invalid/laserfiche';Port=54323;GraphPort=54324;Model='qwen2.5:14b';OllamaUrl='http://127.0.0.1:11434'}
function Encoded-Profile { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($profile | ConvertTo-Json -Compress))) }
function Run-Msi([string]$Name,[string[]]$Arguments,[bool]$Success=$true,[string]$ExpectedReason='') {
    $log = Join-Path $logs "$Name.log"
    $process = Start-Process msiexec.exe -ArgumentList ($Arguments + @('/qn','/norestart','/L*v',('"'+$log+'"'))) -PassThru
    if (!$process.WaitForExit(180000)) { throw "MSI $Name timed out; $log" }
    if ($Success -and $process.ExitCode -notin @(0,3010)) { throw "MSI $Name failed ($($process.ExitCode)); $log" }
    if (!$Success -and $process.ExitCode -in @(0,3010)) { throw "MSI accepted unsafe operation $Name" }
    if ($ExpectedReason -and (Get-Content $log -Raw) -notmatch [regex]::Escape($ExpectedReason)) { throw "MSI $Name failed for an unexpected reason; $log" }
    if ((Get-FileHash $sentinel).Hash -ne $sentinelHash) { throw 'Unrelated file changed.' }
    foreach ($file in $lfSentinels.Keys) {
        if (!(Test-Path $file) -or (Get-FileHash $file).Hash -ne $lfSentinels[$file]) { throw "Laserfiche fixture changed: $file" }
    }
}
function Require-Web {
    $status=$null
    for ($attempt=0;$attempt -lt 40;$attempt++) {
        try { $status=Invoke-RestMethod 'http://127.0.0.1:54323/api/app/status' -TimeoutSec 2; break } catch { Start-Sleep -Seconds 1 }
    }
    if (!$status) { throw 'Installed Windows service did not serve the web app.' }
    $expected=Get-Content (Join-Path (Split-Path $msi) 'build-info.json') -Raw | ConvertFrom-Json
    if ($status.build.SourceCommit -ne $expected.SourceCommit) { throw 'Installed app SourceCommit mismatch.' }
    if ($status.build.Version -ne $expected.Version) { throw 'Installed app version mismatch.' }
    $response=Invoke-WebRequest 'http://127.0.0.1:54323/brand/font/ReadexPro-Regular.ttf' -UseBasicParsing
    if ($response.StatusCode -ne 200) { throw 'Installed font unavailable.' }
    $planner=$null
    try { $planner=Invoke-WebRequest 'http://127.0.0.1:54324/health' -UseBasicParsing -TimeoutSec 5 } catch {
        if ([int]$_.Exception.Response.StatusCode -ne 503) { throw 'Frozen planner did not start.' }
    }
    $status | ConvertTo-Json -Depth 8 | Set-Content "$logs\installed-build.json"
}
# Refuse a redirected program directory before modifying any payload.
Run-Msi 'reject-foreign-path' @('/i',('"'+$msi+'"'),"INSTALLFOLDER=$outside",('REPORTS_PROFILE='+(Encoded-Profile))) $false 'Reports isolation rejected'
if (Test-Path $root) { throw 'Expected a clean disposable test machine.' }
New-Item -ItemType Junction -Path $root -Target $outside | Out-Null
try { Run-Msi 'reject-junction' @('/i',('"'+$msi+'"'),('REPORTS_PROFILE='+(Encoded-Profile))) $false 'Reports isolation rejected' }
finally { [IO.Directory]::Delete($root) }
New-Item -ItemType Directory -Force -Path "$root\web" | Out-Null
$linked=Join-Path $root 'web\LaserficheReports.Web.exe'
New-Item -ItemType HardLink -Path $linked -Target $sentinel | Out-Null
try { Run-Msi 'reject-hardlink' @('/i',('"'+$msi+'"'),('REPORTS_PROFILE='+(Encoded-Profile))) $false 'Reports isolation rejected' }
finally { [IO.File]::Delete($linked) }
# The native MSI UI supplies these same secure properties; no EXE/profile required.
$msiSettings = @('REPORTS_CONFIGURE=1','REPORTS_SERVER=https://target-server.invalid','REPORTS_API_PATH=/LFRepositoryAPI',
    'REPORTS_API_VERSION=v2','REPORTS_REPOSITORY=TargetRepo','REPORTS_WEBCLIENT=https://target-server.invalid/laserfiche',
    'REPORTS_PORT=54323','REPORTS_GRAPH_PORT=54324','REPORTS_MODEL=qwen2.5:14b','REPORTS_OLLAMA=http://127.0.0.1:11434')
Run-Msi 'reject-invalid-settings' (@('/i',('"'+$msi+'"')) + $msiSettings + @('REPORTS_GRAPH_PORT=54323')) $false 'Web and planner ports must differ'
Run-Msi 'install' (@('/i',('"'+$msi+'"')) + $msiSettings)
Require-Web
# A busy planner port must stop this host without terminating the unrelated owner.
Stop-Service LaserficheReports
$listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,54324)
$listener.Start()
try {
    try { Start-Service LaserficheReports }
    catch { Write-Host 'Service startup correctly rejected an occupied planner port.' }
    $stopped=$false
    for ($attempt=0;$attempt -lt 20;$attempt++) {
        if ((Get-Service LaserficheReports).Status -eq 'Stopped') { $stopped=$true; break }
        Start-Sleep -Seconds 1
    }
    if (!$stopped -or !$listener.Server.IsBound) { throw 'Busy planner port was accepted or the unrelated owner was stopped.' }
} finally { $listener.Stop() }
Start-Service LaserficheReports
Require-Web
$unknown=Join-Path $root 'unrelated-document.txt'; 'keep unknown app file' | Set-Content $unknown
$unknownData=Join-Path $data 'unrelated-document.txt'; 'keep unknown data file' | Set-Content $unknownData
$runtime=Join-Path $data 'laserfiche.runtime.json'; '{"Laserfiche":{"TimeoutSeconds":300}}' | Set-Content $runtime
$runtimeHash=(Get-FileHash $runtime).Hash
$config=Join-Path $data 'laserfiche.config.json'; $configHash=(Get-FileHash $config).Hash
$hosting=Join-Path $data 'hosting.config.json'; $hostingHash=(Get-FileHash $hosting).Hash
# An explicit configuration change must override stale runtime connection values
# while preserving unrelated runtime settings, then ordinary repair preserves bytes.
'{"Laserfiche":{"ServerUrl":"https://old-host.invalid","RepositoryId":"OldRepo","DetectedApiVersion":"v1","TimeoutSeconds":300},"Unrelated":{"Keep":true}}' | Set-Content $runtime
Run-Msi 'reconfigure' (@('/i',('"'+$msi+'"'),'REINSTALL=ALL','REINSTALLMODE=amus') + $msiSettings)
$effectiveRuntime = Get-Content $runtime -Raw | ConvertFrom-Json
if ($effectiveRuntime.Laserfiche.PSObject.Properties.Name -contains 'ServerUrl' -or
    $effectiveRuntime.Laserfiche.PSObject.Properties.Name -contains 'RepositoryId' -or
    $effectiveRuntime.Laserfiche.PSObject.Properties.Name -contains 'DetectedApiVersion' -or
    $effectiveRuntime.Laserfiche.TimeoutSeconds -ne 300 -or !$effectiveRuntime.Unrelated.Keep) { throw 'Stale runtime override remained or unrelated settings were lost.' }
$runtimeHash=(Get-FileHash $runtime).Hash
$configHash=(Get-FileHash $config).Hash
$hostingHash=(Get-FileHash $hosting).Hash
Run-Msi 'repair' @('/fa',('"'+$msi+'"'))
if ((Get-FileHash $config).Hash -ne $configHash -or (Get-FileHash $hosting).Hash -ne $hostingHash -or (Get-FileHash $runtime).Hash -ne $runtimeHash) { throw 'Repair changed saved settings.' }
Require-Web
# A denied second write proves the journal restores the first configuration file.
$profile.ServerUrl='https://rollback-target.invalid'
$profile.Model='qwen2.5:7b'
$locked=[IO.File]::Open($hosting,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try { Run-Msi 'rollback' @('/i',('"'+$msi+'"'),'REINSTALL=ALL','REINSTALLMODE=amus',('REPORTS_PROFILE='+(Encoded-Profile))) $false }
finally { $locked.Dispose() }
if ((Get-FileHash $config).Hash -ne $configHash -or (Get-FileHash $hosting).Hash -ne $hostingHash) { throw 'Failed repair did not restore settings.' }
# Repackage the same payload with a higher product version to exercise MajorUpgrade.
$upgrade=Join-Path $logs 'upgrade-fixture.msi'; Copy-Item $msi $upgrade
$expected=Get-Content (Join-Path (Split-Path $msi) 'build-info.json') -Raw | ConvertFrom-Json
$version=[version]$expected.Version; $upgradeVersion="{0}.{1}.{2}" -f $version.Major,$version.Minor,($version.Build+1)
$stage=Get-ChildItem "$repo\.build-cache" -Directory -Filter 'reports-*' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (!$stage) { throw 'The upgrade fixture requires staging from build/publish.ps1.' }
$payload=Join-Path $stage.FullName 'payload'; $harvest=Join-Path $stage.FullName 'Payload.wxs'
# Recompile the MSI so its Upgrade table contains the correct version bounds.
& dotnet tool run wix build "$repo\installer\Reports.Installer\Product.wxs" "$repo\installer\Reports.Installer\ReportsUI.wxs" $harvest -arch x64 `
    -ext 'WixToolset.Util.wixext/4.0.5' -ext 'WixToolset.UI.wixext/4.0.5' -d "ProductVersion=$upgradeVersion" -d "PayloadRoot=$payload" `
    -d "SafetyActionsPath=$repo\installer\Reports.SafetyActions\bin\Release\net48\Reports.SafetyActions.CA.dll" -o $upgrade
if ($LASTEXITCODE -ne 0) { throw 'Upgrade MSI fixture build failed.' }
Run-Msi 'upgrade' @('/i',('"'+$upgrade+'"'))
if ((Get-FileHash $config).Hash -ne $configHash -or (Get-FileHash $runtime).Hash -ne $runtimeHash) { throw 'Upgrade changed settings.' }
Require-Web
Run-Msi 'uninstall' @('/x',('"'+$upgrade+'"'))
if (Get-Service LaserficheReports -ErrorAction SilentlyContinue) { throw 'Reports service still registered.' }
if (!(Test-Path $unknown) -or !(Test-Path $unknownData) -or !(Test-Path $runtime) -or !(Test-Path $config)) { throw 'Uninstall removed unknown files or saved settings.' }
if (Test-Path "$root\LaserficheReports.Service.exe") { throw 'MSI-owned service binary remained.' }
'Passed: native MSI settings pages/properties, model configuration, runtime-override replacement, Laserfiche library/data preservation, path isolation, install, real HTTP hosting, installed commit/fonts, frozen planner, repair, rollback, major upgrade, uninstall, unknown-file and settings preservation.' | Set-Content "$logs\acceptance.txt"
Write-Host (Get-Content "$logs\acceptance.txt")
