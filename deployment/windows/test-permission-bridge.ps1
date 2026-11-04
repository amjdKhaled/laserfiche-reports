param([Parameter(Mandatory)][string]$WebPath)
$ErrorActionPreference = 'Stop'
if (![Environment]::Is64BitOperatingSystem) { throw 'Windows x64 is required.' }
$release = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction Stop).Release
if ($release -lt 528040) { throw '.NET Framework 4.8 is required.' }
foreach ($name in @('LaserficheReports.PermissionBridge.exe', 'LaserficheReports.PermissionBridge.exe.config', 'Laserfiche.RepositoryAccess.dll', 'Laserfiche-SDK-EULA.md')) {
    $path = Join-Path $WebPath "permission-bridge\$name"
    if (!(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "Missing bridge payload: $name" }
}
Write-Host 'Deployment preflight passed. This is not a live SDK, trustee or permission acceptance test.'
