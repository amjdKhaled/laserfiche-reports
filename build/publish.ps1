[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$OutputDirectory = '',
    [string]$PythonExecutable = 'python',
    [switch]$AllowDirty
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'The complete EXE/MSI must be built on Windows x64.' }
$source = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (!$OutputDirectory) { $OutputDirectory = Join-Path $source 'artifacts' }
$release = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "LaserficheReports-$Version"
if (Test-Path $release) { throw "Release directory already exists. Use a new version: $release" }
$parts = $Version.Split('.') | ForEach-Object { [int]$_ }
if ($parts[0] -gt 255 -or $parts[1] -gt 255 -or $parts[2] -gt 65535) { throw 'Version exceeds MSI limits.' }
function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE. No successful release was produced." }
}
function Escape-Xml([string]$Value) { [Security.SecurityElement]::Escape($Value) }
function Get-WixId([string]$Prefix, [string]$Value) {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $hash = $hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())); return $Prefix + ([BitConverter]::ToString($hash).Replace('-','').Substring(0,32)) }
    finally { $hasher.Dispose() }
}
function New-PayloadFragment([string]$Root, [string]$Destination) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $files = @(Get-ChildItem $rootPath -Recurse -File | Sort-Object FullName)
    if (!$files.Count) { throw 'Empty payload.' }
    $xml = New-Object Text.StringBuilder
    [void]$xml.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"><Fragment>')
    $directories = @{ '' = 'INSTALLFOLDER'; 'web' = 'WEBFOLDER'; 'planner' = 'PLANNERFOLDER' }
    # Enumerate parents before children; stable hashed IDs tolerate spaces and Arabic filenames.
    foreach ($directory in (Get-ChildItem $rootPath -Directory -Recurse | Sort-Object { $_.FullName.Length })) {
        $relative = $directory.FullName.Substring($rootPath.Length + 1)
        if ($directories.ContainsKey($relative)) { continue }
        $parentRelative = [IO.Path]::GetDirectoryName($relative)
        if ($null -eq $parentRelative) { $parentRelative = '' }
        $id = Get-WixId 'D' $relative
        $directories[$relative] = $id
        [void]$xml.AppendLine(('<DirectoryRef Id="{0}"><Directory Id="{1}" Name="{2}" /></DirectoryRef>' -f $directories[$parentRelative],$id,(Escape-Xml $directory.Name)))
    }
    [void]$xml.AppendLine('<ComponentGroup Id="ReportsPayload">')
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($rootPath.Length + 1)
        if ($relative -eq 'LaserficheReports.Service.exe') { continue }
        if ($relative -match '(^|\\)(appsettings\.Local\.json|laserfiche\.(config|runtime)\.json|credentials|logs|__pycache__)(\\|$)') { throw "Private runtime content in payload: $relative" }
        $parentRelative = [IO.Path]::GetDirectoryName($relative)
        if ($null -eq $parentRelative) { $parentRelative = '' }
        $componentId = Get-WixId 'C' $relative; $fileId = Get-WixId 'F' $relative
        [void]$xml.AppendLine(('<Component Id="{0}" Directory="{1}" Guid="*" Bitness="always64"><File Id="{2}" Source="{3}" KeyPath="yes" /></Component>' -f $componentId,$directories[$parentRelative],$fileId,(Escape-Xml $file.FullName)))
    }
    [void]$xml.AppendLine('</ComponentGroup></Fragment></Wix>')
    [IO.File]::WriteAllText($Destination,$xml.ToString(),(New-Object Text.UTF8Encoding($false)))
    [xml](Get-Content $Destination -Raw) | Out-Null
}
Push-Location $source
try {
    Get-Command dotnet,git,$PythonExecutable -ErrorAction Stop | Out-Null
    Invoke-Checked $PythonExecutable @('-c','import sys; assert sys.version_info[:2] == (3,11) and sys.maxsize > 2**32, "Build requires Python 3.11 x64"')
    $commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify SourceCommit.' }
    $dirty = [bool](& git status --porcelain)
    if ($dirty -and !$AllowDirty) { throw 'Commit changes before building a release, or use -AllowDirty for testing.' }
    $branch = (& git branch --show-current).Trim()
    $stage = Join-Path $source ('.build-cache\reports-' + [Guid]::NewGuid().ToString('N'))
    $payload = Join-Path $stage 'payload'; $baStage = Join-Path $stage 'BA'
    New-Item -ItemType Directory -Force -Path "$payload\web","$payload\planner",$baStage,$release | Out-Null
    foreach ($project in @('Reports.BA','Reports.SafetyActions')) {
        # Delete only known intermediate directories in this checkout, never deployment folders.
        foreach ($leaf in @('bin','obj')) {
            $intermediate = Join-Path "$source\installer\$project" $leaf
            if (Test-Path $intermediate) { if ((Get-Item $intermediate).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked build directory rejected.' }; Remove-Item $intermediate -Recurse -Force }
        }
    }
    Invoke-Checked dotnet @('tool','restore')
    foreach ($extension in @('WixToolset.Util.wixext','WixToolset.Bal.wixext')) {
        & dotnet tool run wix extension add "$extension/4.0.5"
        if ($LASTEXITCODE -ne 0) {
            $cached = & dotnet tool run wix extension list
            if ($LASTEXITCODE -ne 0 -or !(($cached | Out-String) -match ([regex]::Escape($extension)+'.*4\.0\.5'))) {
                throw "WiX extension $extension/4.0.5 could not be installed or verified."
            }
        }
    }
    Invoke-Checked dotnet @('publish',"$source\src\LaserficheReports.Web",'-c','Release','-r','win-x64','--self-contained','true','-o',"$payload\web","-p:Version=$Version")
    Invoke-Checked dotnet @('publish',"$source\installer\Reports.Service",'-c','Release','-r','win-x64','--self-contained','true','-o',$payload,"-p:Version=$Version")
    foreach ($project in @('Reports.BA','Reports.SafetyActions')) { Invoke-Checked dotnet @('build',"$source\installer\$project",'-c','Release') }
    Copy-Item "$source\installer\Reports.BA\bin\Release\net48\*" $baStage -Recurse -Force
    foreach ($name in @('Reports.BA.dll','WixToolset.Mba.Core.dll','mbanative.dll','WixToolset.Mba.Host.config')) {
        $built = "$source\installer\Reports.BA\bin\Release\net48\$name"
        if (!(Test-Path "$baStage\$name") -or (Get-FileHash $built).Hash -ne (Get-FileHash "$baStage\$name").Hash) { throw "Missing/stale bootstrapper payload: $name" }
    }
    # Freeze a fresh Python 3.11 runtime and dependencies on Windows; never transfer a venv.
    $pythonBuild = Join-Path $stage 'python-build'
    Invoke-Checked $PythonExecutable @('-m','venv',$pythonBuild)
    $python = Join-Path $pythonBuild 'Scripts\python.exe'
    Invoke-Checked $python @('-m','pip','install','-r',"$source\tools\reports-graph\requirements.txt",'pyinstaller==6.14.2')
    Invoke-Checked $python @('-m','pip','freeze')
    & $python -m pip freeze | Set-Content "$release\python-dependencies.txt" -Encoding UTF8
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record Python dependencies.' }
    Invoke-Checked $python @('-m','PyInstaller','--noconfirm','--clean','--onedir','--name','LaserficheReports.Planner',
        '--distpath',"$stage\frozen",'--workpath',"$stage\pyinstaller-work",'--specpath',$stage,
        '--paths',"$source\tools\reports-graph",'--collect-all','langgraph','--collect-all','langchain_core',
        '--collect-all','langchain_ollama','--copy-metadata','langchain-core','--copy-metadata','langchain-ollama',
        "$source\tools\reports-graph\server.py")
    Copy-Item "$stage\frozen\LaserficheReports.Planner\*" "$payload\planner" -Recurse -Force
    # These can be produced by a developer publish. Exclude them before harvesting.
    foreach ($private in @("$payload\web\appsettings.Local.json","$payload\web\config")) {
        if (Test-Path $private) { Remove-Item $private -Recurse -Force }
    }
    Copy-Item "$source\installer\Open-Reports.ps1" $payload
    $manifest = @{ Version=$Version; SourceCommit=$commit; Branch=$branch; Dirty=$dirty; BuiltAtUtc=[DateTime]::UtcNow.ToString('O'); Hosting='WindowsService'; Python='frozen-3.11' }
    $manifest | ConvertTo-Json | Set-Content "$payload\web\build-info.json" -Encoding UTF8
    Copy-Item "$payload\web\build-info.json" "$release\build-info.json"
    foreach ($required in @('LaserficheReports.Service.exe','web\LaserficheReports.Web.exe','web\appsettings.json','web\wwwroot\brand\font\ReadexPro-Regular.ttf','planner\LaserficheReports.Planner.exe')) {
        if (!(Test-Path "$payload\$required")) { throw "Missing packaged runtime: $required" }
    }
    # Run the frozen executable, not the build venv, before making an MSI.
    Invoke-Checked "$payload\planner\LaserficheReports.Planner.exe" @('--help')
    $harvest = Join-Path $stage 'Payload.wxs'; New-PayloadFragment $payload $harvest
    $actions = "$source\installer\Reports.SafetyActions\bin\Release\net48\Reports.SafetyActions.CA.dll"
    if (!(Test-Path $actions)) { throw 'Missing native custom action package.' }
    $msi = Join-Path $release 'LaserficheReports.msi'
    Invoke-Checked dotnet @('tool','run','wix','build',"$source\installer\Reports.Installer\Product.wxs",$harvest,'-arch','x64',
        '-ext','WixToolset.Util.wixext/4.0.5','-d',"ProductVersion=$Version",'-d',"PayloadRoot=$payload",'-d',"SafetyActionsPath=$actions",'-o',$msi)
    $setup = Join-Path $release 'LaserficheReports-Setup.exe'
    $netfx = Join-Path $stage 'ndp48-web.exe'
    Invoke-WebRequest 'https://go.microsoft.com/fwlink/?LinkId=2085155' -OutFile $netfx -UseBasicParsing
    $signature = Get-AuthenticodeSignature $netfx
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'Untrusted .NET Framework prerequisite.' }
    Invoke-Checked dotnet @('tool','run','wix','build',"$source\installer\Reports.Bundle\Bundle.wxs",'-ext','WixToolset.Bal.wixext/4.0.5',
        '-ext','WixToolset.Util.wixext/4.0.5','-d',"NetFxInstaller=$netfx",'-d',"ProductVersion=$Version",'-d',"MsiPath=$msi",'-d',"BaRoot=$baStage",'-o',$setup)
    Copy-Item "$source\docs\windows-installer.md" "$release\README.md"
    $hashes = Get-ChildItem $release -File | Where-Object Extension -in @('.exe','.msi') | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash,$_.Name }
    $hashes | Set-Content "$release\SHA256SUMS.txt" -Encoding ASCII
    if (!(Test-Path $setup) -or !(Test-Path $msi)) { throw 'Final package missing.' }
    Write-Host "BUILD COMPLETE: $setup (Version=$Version SourceCommit=$commit). Run Windows acceptance before distributing."
} finally { Pop-Location }
