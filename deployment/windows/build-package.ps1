param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (!$OutputDirectory) { $OutputDirectory = Join-Path $source 'artifacts' }
Get-Command dotnet -ErrorAction Stop | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$package = Join-Path $OutputDirectory "LaserficheReports-$stamp"
New-Item -ItemType Directory -Force -Path "$package\web", "$package\graph" | Out-Null
& dotnet publish "$source\src\LaserficheReports.Web" -c Release -r win-x64 --self-contained true -o "$package\web"
if ($LASTEXITCODE -ne 0) { throw 'Publish failed. No transfer archive was produced.' }
# Never export local passwords, runtime settings, sessions or source data.
Remove-Item "$package\web\appsettings.Local.json" -ErrorAction SilentlyContinue
Remove-Item "$package\web\config" -Recurse -ErrorAction SilentlyContinue
Copy-Item "$source\tools\reports-graph\*.py" "$package\graph"
Copy-Item "$source\tools\reports-graph\requirements.txt" "$package\graph"
Copy-Item "$source\deployment\supervise.py" $package
Copy-Item "$PSScriptRoot\install-package.ps1" "$package\install.ps1"
Copy-Item "$PSScriptRoot\install-package.cmd" "$package\Install.cmd"
Copy-Item "$PSScriptRoot\unregister.ps1" $package
Copy-Item "$PSScriptRoot\diagnostics.ps1" $package
Copy-Item "$source\deployment\TRANSFER.md" "$package\README.md"
$archive = "$package.zip"
Compress-Archive -Path "$package\*" -DestinationPath $archive
Write-Host "Transfer archive: $archive"
