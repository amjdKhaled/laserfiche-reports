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
Run-Msi 'install' @('/i',('"'+$msi+'"'),('REPORTS_PROFILE='+(Encoded-Profile)))
Require-Web
$unknown=Join-Path $root 'unrelated-document.txt'; 'keep unknown app file' | Set-Content $unknown
$unknownData=Join-Path $data 'unrelated-document.txt'; 'keep unknown data file' | Set-Content $unknownData
$runtime=Join-Path $data 'laserfiche.runtime.json'; '{"Laserfiche":{"TimeoutSeconds":300}}' | Set-Content $runtime
$runtimeHash=(Get-FileHash $runtime).Hash
$config=Join-Path $data 'laserfiche.config.json'; $configHash=(Get-FileHash $config).Hash
$hosting=Join-Path $data 'hosting.config.json'; $hostingHash=(Get-FileHash $hosting).Hash
Run-Msi 'repair' @('/fa',('"'+$msi+'"'))
if ((Get-FileHash $config).Hash -ne $configHash -or (Get-FileHash $hosting).Hash -ne $hostingHash -or (Get-FileHash $runtime).Hash -ne $runtimeHash) { throw 'Repair changed saved settings.' }
Require-Web
# A denied second write proves the journal restores the first configuration file.
$profile.ServerUrl='https://rollback-target.invalid'
$locked=[IO.File]::Open($hosting,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try { Run-Msi 'rollback' @('/i',('"'+$msi+'"'),'REINSTALL=ALL','REINSTALLMODE=amus',('REPORTS_PROFILE='+(Encoded-Profile))) $false }
finally { $locked.Dispose() }
if ((Get-FileHash $config).Hash -ne $configHash -or (Get-FileHash $hosting).Hash -ne $hostingHash) { throw 'Failed repair did not restore settings.' }
# Repackage the same payload with a higher product version to exercise MajorUpgrade.
$upgrade=Join-Path $logs 'upgrade-fixture.msi'; Copy-Item $msi $upgrade
$installer=New-Object -ComObject WindowsInstaller.Installer; $database=$installer.OpenDatabase($upgrade,1)
$productCode='{'+[Guid]::NewGuid().ToString().ToUpperInvariant()+'}'
$view=$database.OpenView("UPDATE ``Property`` SET ``Value`` = '$productCode' WHERE ``Property`` = 'ProductCode'"); $view.Execute(); $view.Close()
$expected=Get-Content (Join-Path (Split-Path $msi) 'build-info.json') -Raw | ConvertFrom-Json
$version=[version]$expected.Version; $upgradeVersion="{0}.{1}.{2}" -f $version.Major,$version.Minor,($version.Build+1)
$view=$database.OpenView("UPDATE ``Property`` SET ``Value`` = '$upgradeVersion' WHERE ``Property`` = 'ProductVersion'"); $view.Execute(); $view.Close(); $database.Commit()
$view=$database.OpenView("UPDATE ``Upgrade`` SET ``VersionMax`` = '$upgradeVersion' WHERE ``ActionProperty`` = 'WIX_UPGRADE_DETECTED'"); $view.Execute(); $view.Close()
$view=$database.OpenView("UPDATE ``Upgrade`` SET ``VersionMin`` = '$upgradeVersion' WHERE ``ActionProperty`` = 'WIX_DOWNGRADE_DETECTED'"); $view.Execute(); $view.Close(); $database.Commit()
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) | Out-Null
$summary=$installer.SummaryInformation($upgrade,20)
$summary.GetType().InvokeMember('Property',[Reflection.BindingFlags]::SetProperty,$null,$summary,@(9,('{'+[Guid]::NewGuid().ToString().ToUpperInvariant()+'}'))) | Out-Null
$summary.Persist(); [Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) | Out-Null
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null
Run-Msi 'upgrade' @('/i',('"'+$upgrade+'"'))
if ((Get-FileHash $config).Hash -ne $configHash -or (Get-FileHash $runtime).Hash -ne $runtimeHash) { throw 'Upgrade changed settings.' }
Require-Web
Run-Msi 'uninstall' @('/x',('"'+$upgrade+'"'))
if (Get-Service LaserficheReports -ErrorAction SilentlyContinue) { throw 'Reports service still registered.' }
if (!(Test-Path $unknown) -or !(Test-Path $unknownData) -or !(Test-Path $runtime) -or !(Test-Path $config)) { throw 'Uninstall removed unknown files or saved settings.' }
if (Test-Path "$root\LaserficheReports.Service.exe") { throw 'MSI-owned service binary remained.' }
'Passed: path isolation, install, real HTTP hosting, installed commit/fonts, frozen planner, repair, rollback, major upgrade, uninstall, unknown-file and settings preservation.' | Set-Content "$logs\acceptance.txt"
Write-Host (Get-Content "$logs\acceptance.txt")
