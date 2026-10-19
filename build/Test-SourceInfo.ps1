[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'source-info.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Reports-GitFixture-' + [Guid]::NewGuid().ToString('N'))
$previousHeadRef = $env:GITHUB_HEAD_REF
$previousRefName = $env:GITHUB_REF_NAME
function Git-Fixture([string[]]$Arguments) {
    & git @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Git fixture setup failed.' }
}
New-Item -ItemType Directory -Path $fixture | Out-Null
Push-Location $fixture
try {
    $env:GITHUB_HEAD_REF = ''; $env:GITHUB_REF_NAME = ''
    Git-Fixture @('init','--quiet','--initial-branch=fixture-main')
    'fixture' | Set-Content source.txt
    Git-Fixture @('add','source.txt')
    Git-Fixture @('-c','user.name=Reports Fixture','-c','user.email=reports-fixture@example.invalid','commit','--quiet','-m','fixture')
    $attached = Get-ReportsSourceInformation
    if ($attached.Branch -ne 'fixture-main' -or $attached.DetachedHead -or $attached.Dirty) { throw 'Attached checkout identity is incorrect.' }

    Git-Fixture @('checkout','--quiet','--detach','HEAD')
    $detached = Get-ReportsSourceInformation
    if (!$detached.DetachedHead -or $detached.Branch -ne 'detached' -or $detached.SourceCommit -ne $attached.SourceCommit -or $detached.Dirty) {
        throw 'Detached checkout without GitHub environment is incorrect.'
    }
    $env:GITHUB_HEAD_REF = 'fix/fixture-pr'; $env:GITHUB_REF_NAME = '123/merge'
    $pullRequest = Get-ReportsSourceInformation
    if ($pullRequest.Branch -ne 'fix/fixture-pr' -or !$pullRequest.DetachedHead -or $pullRequest.SourceCommit -ne $attached.SourceCommit) {
        throw 'Pull request branch label changed the checked-out commit identity.'
    }
    $env:GITHUB_HEAD_REF = ''; $env:GITHUB_REF_NAME = 'main'
    $push = Get-ReportsSourceInformation
    if ($push.Branch -ne 'main' -or !$push.DetachedHead) { throw 'Detached push checkout label is incorrect.' }
    'uncommitted' | Add-Content source.txt
    if (!(Get-ReportsSourceInformation).Dirty) { throw 'Dirty source was not detected.' }
    Write-Host 'Passed: attached, detached, pull request, explicit commit and dirty Git source identity.'
} finally {
    $env:GITHUB_HEAD_REF = $previousHeadRef; $env:GITHUB_REF_NAME = $previousRefName
    Pop-Location
    # Only this newly created, uniquely named disposable fixture is removed.
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
