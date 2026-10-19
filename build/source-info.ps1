# GitHub pull-request checkouts and explicit commit checkouts have no active branch.
# Keep the exact checked-out commit as the release identity; branch is a label only.
function Get-ReportsSourceInformation {
    [CmdletBinding()]
    param()

    $commitOutput = & git rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify SourceCommit from this checkout.' }
    if ([string]::IsNullOrWhiteSpace($commitOutput)) { throw 'Git returned an empty SourceCommit.' }
    $commit = $commitOutput.Trim()
    if ($commit -notmatch '^[0-9a-fA-F]{40,64}$') { throw 'Git returned an invalid SourceCommit.' }

    $statusOutput = @(& git status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot check for uncommitted source changes.' }
    $dirty = [bool]($statusOutput.Count)

    $branchOutput = & git branch --show-current
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read Git checkout information.' }
    # A successful git command with no stdout becomes $null in PowerShell.
    $branch = ''
    if (![string]::IsNullOrWhiteSpace($branchOutput)) { $branch = $branchOutput.Trim() }
    $detached = [string]::IsNullOrWhiteSpace($branch)
    if ($detached) {
        $branch = if (![string]::IsNullOrWhiteSpace($env:GITHUB_HEAD_REF)) { $env:GITHUB_HEAD_REF }
                  elseif (![string]::IsNullOrWhiteSpace($env:GITHUB_REF_NAME)) { $env:GITHUB_REF_NAME }
                  else { 'detached' }
    }
    [pscustomobject]@{ SourceCommit = $commit; Branch = $branch; DetachedHead = $detached; Dirty = $dirty }
}
