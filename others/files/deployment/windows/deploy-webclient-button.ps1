# Adds the "Reports" button to the Laserfiche Web Client navbar. Run from an Administrator PowerShell;
# install-iis.ps1 runs it automatically.
# Browse.aspx is backed up first. A Laserfiche Web Client upgrade can replace Browse.aspx; re-run this script afterwards.
param(
    [string]$ReportsUrl = "https://$($env:COMPUTERNAME.ToLowerInvariant()):5188/",
    [string]$WebFiles = 'C:\Program Files\Laserfiche\Web Access\Web Files'
)
$ErrorActionPreference = 'Stop'
$uri = $null
if (![Uri]::TryCreate($ReportsUrl, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin @('http', 'https') -or
    $uri.UserInfo -or $ReportsUrl.IndexOfAny([char[]]"'`"\<>") -ge 0) { throw 'Enter a valid Reports URL, e.g. https://desktop-lr7l5jt:5188/' }
$browse = Join-Path $WebFiles 'Browse.aspx'
if (!(Test-Path $browse)) { throw "Browse.aspx was not found in $WebFiles." }

$source = Join-Path $PSScriptRoot '..\webclient\lf-reports-button.js'
$target = Join-Path $WebFiles 'Assets\custom\lf-reports-button.js'
New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
$script = [IO.File]::ReadAllText($source, $utf8).Replace('__REPORTS_URL__', $uri.AbsoluteUri)
[IO.File]::WriteAllText($target, $script, $utf8)
Write-Host "Button script deployed: $target"

$tag = '<script src="assets/custom/lf-reports-button.js"></script>'
$page = [IO.File]::ReadAllText($browse, $utf8)
if ($page.IndexOf('lf-reports-button.js', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    Write-Host 'Browse.aspx already loads the Reports button.'
} else {
    $head = $page.IndexOf('</head>', [StringComparison]::OrdinalIgnoreCase)
    if ($head -lt 0) { throw 'Browse.aspx has no </head>; it was not changed.' }
    $backup = "$browse.bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Copy-Item $browse $backup
    [IO.File]::WriteAllText($browse, $page.Insert($head, $tag), $utf8)
    Write-Host "Browse.aspx updated. Backup: $backup"
}
Write-Host 'Refresh the Web Client with Ctrl+F5. An IIS restart is not required.'
