#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$WebClientPath = 'C:\Program Files\Laserfiche\Web Access\Web Files',
    [string]$ReportsUrl = 'http://localhost:5187/',
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$browsePath = Join-Path $WebClientPath 'Browse.aspx'
$customPath = Join-Path $WebClientPath 'assets\custom'
$scriptPath = Join-Path $customPath 'lf-reports-button.js'
$templatePath = Join-Path $PSScriptRoot '..\integrations\laserfiche-webclient\lf-reports-button.js'
if (-not (Test-Path -LiteralPath $browsePath -PathType Leaf)) {
    throw "Browse.aspx was not found at $browsePath. Supply -WebClientPath from the IIS application's physical path."
}
if (-not $Remove) {
    $uri = $null
    if (-not [Uri]::TryCreate($ReportsUrl, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -notin @('http', 'https') -or $uri.UserInfo) {
        throw 'ReportsUrl must be an absolute HTTP/HTTPS URL without credentials.'
    }
    if (-not (Test-Path -LiteralPath $templatePath)) { throw "Missing template: $templatePath" }
    $template = [IO.File]::ReadAllText($templatePath)
    $script = $template.Replace('__LF_REPORTS_URL_JSON__', (ConvertTo-Json -InputObject $uri.AbsoluteUri -Compress))
}
# Preserve the detected text encoding and keep a byte-for-byte backup before changing ASPX.
$reader = [IO.StreamReader]::new($browsePath, [Text.Encoding]::UTF8, $true)
try { $original = $reader.ReadToEnd(); $encoding = $reader.CurrentEncoding }
finally { $reader.Dispose() }
$pattern = '(?is)<script\b[^>]*\bsrc\s*=\s*["''][^"'']*assets/custom/lf-reports-button\.js(?:\?[^"'']*)?["''][^>]*>\s*</script>'
$updated = [regex]::Replace($original, $pattern, '')
if (-not $Remove) {
    if ($updated -notmatch '(?i)</body\s*>') { throw 'No closing body tag in Browse.aspx; no files were modified.' }
    $newline = if ($original.Contains("`r`n")) { "`r`n" } else { "`n" }
    $tag = '<script src="assets/custom/lf-reports-button.js?v=' + [Guid]::NewGuid().ToString('N') + '"></script>'
    $updated = [regex]::Replace($updated, '(?i)</body\s*>', ($tag + $newline + '</body>'))
}
$suffix = '.reports-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
if ($updated -ne $original) { Copy-Item -LiteralPath $browsePath -Destination ($browsePath + $suffix) }
if (Test-Path -LiteralPath $scriptPath) { Copy-Item -LiteralPath $scriptPath -Destination ($scriptPath + $suffix) }
if ($Remove) {
    if ($updated -ne $original) { [IO.File]::WriteAllText($browsePath, $updated, $encoding) }
    if (Test-Path -LiteralPath $scriptPath) { Remove-Item -LiteralPath $scriptPath }
    Write-Host 'Reports button removed. Refresh the Laserfiche Web Client with Ctrl+F5.'
} else {
    New-Item -ItemType Directory -Path $customPath -Force | Out-Null
    [IO.File]::WriteAllText($scriptPath, $script, [Text.UTF8Encoding]::new($false))
    try { [IO.File]::WriteAllText($browsePath, $updated, $encoding) }
    catch {
        if (Test-Path -LiteralPath ($scriptPath + $suffix)) {
            Copy-Item -LiteralPath ($scriptPath + $suffix) -Destination $scriptPath -Force
        } else { Remove-Item -LiteralPath $scriptPath -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath ($browsePath + $suffix)) {
            Copy-Item -LiteralPath ($browsePath + $suffix) -Destination $browsePath -Force
        }
        throw
    }
    Write-Host "Reports button installed: $ReportsUrl"
    Write-Host 'Refresh the Laserfiche Web Client with Ctrl+F5.'
}
