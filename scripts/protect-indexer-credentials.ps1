[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Repository,
      [System.Management.Automation.PSCredential]$Credential = (Get-Credential))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$directory = Join-Path $env:ProgramData 'LaserficheReports\realtime\credentials'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
# Machine-bound DPAPI + ACL, not portable plaintext. Re-create on each target computer.
& icacls $directory /inheritance:r /grant '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)R' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not secure service credential directory.' }
$sha = [Security.Cryptography.SHA256]::Create()
try { $key = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Repository.ToLowerInvariant())))).Replace('-','') }
finally { $sha.Dispose() }
$payload = @{ Username=$Credential.UserName; Password=$Credential.GetNetworkCredential().Password } | ConvertTo-Json -Compress
$bytes = [Text.Encoding]::UTF8.GetBytes($payload)
try {
    $encrypted = [Security.Cryptography.ProtectedData]::Protect($bytes,$null,[Security.Cryptography.DataProtectionScope]::LocalMachine)
    [IO.File]::WriteAllBytes((Join-Path $directory ($key + '.bin')),$encrypted)
} finally { [Array]::Clear($bytes,0,$bytes.Length); $payload=$null }
Write-Host "Protected credentials for repository $Repository. No password was stored in application settings or service arguments."
