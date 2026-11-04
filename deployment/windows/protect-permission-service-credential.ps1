# Run as the SAME Windows identity that runs Reports. No password argument or plaintext file.
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Windows DPAPI is required.' }
Add-Type -AssemblyName System.Security
$Path = [IO.Path]::GetFullPath($Path)
if (Test-Path -LiteralPath $Path) { throw 'Credential file already exists; choose a new path for rotation.' }
$directory = Split-Path -Parent $Path
if ((Test-Path -LiteralPath $directory) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -gt 0) { throw 'Use a new, dedicated empty directory; existing directory ACLs will not be changed.' }
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
# Use a dedicated directory; do not point this at a Laserfiche or shared data directory.
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$acl.SetOwner($owner)
foreach ($sid in @($owner, [Security.Principal.SecurityIdentifier]'S-1-5-18', [Security.Principal.SecurityIdentifier]'S-1-5-32-544')) {
    $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
}
Set-Acl -LiteralPath $directory -AclObject $acl
$credential = Get-Credential -Message 'Internal Laserfiche verification account (not an interactive Reports user)'
if (!$credential) { throw 'No credential supplied.' }
$clear = [Text.Encoding]::UTF8.GetBytes((@{ Username = $credential.UserName; Password = $credential.GetNetworkCredential().Password } | ConvertTo-Json -Compress))
try {
    $cipher = [Security.Cryptography.ProtectedData]::Protect($clear, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.File]::WriteAllBytes($Path, $cipher)
} finally { [Array]::Clear($clear, 0, $clear.Length); $credential = $null }
Write-Host 'Protected credential created. It can only be decrypted by this Windows service identity on this machine.'
