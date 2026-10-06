param([string]$BaseUrl = "http://127.0.0.1:5187")
$ErrorActionPreference = "Stop"
$checks = @(
    @{ Name = "Web"; Url = "$BaseUrl/api/app/status" },
    @{ Name = "Local AI"; Url = "$BaseUrl/api/ai/status" },
    @{ Name = "Laserfiche"; Url = "$BaseUrl/api/laserfiche/status" }
)
$failed = $false
foreach ($check in $checks) {
    try {
        $result = Invoke-RestMethod -Uri $check.Url -TimeoutSec 12
        $ready = $true
        if ($result.status -eq "unavailable" -or $result.isConnected -eq $false -or
            $result.authenticationSucceeded -eq $false) { $ready = $false }
        if ($ready) {
            if ($check.Name -eq "Web" -and $result.processId) {
                Write-Host "Web: ready (PID $($result.processId), started $($result.startedAtUtc))" -ForegroundColor Green
            } else {
                Write-Host "$($check.Name): ready" -ForegroundColor Green
            }
        } else {
            Write-Host "$($check.Name): unavailable" -ForegroundColor Yellow
            $failed = $true
        }
    } catch {
        $failed = $true
        $detail = $_.Exception.Message
        if ($_.ErrorDetails.Message) { $detail = $_.ErrorDetails.Message }
        Write-Host "$($check.Name): $detail" -ForegroundColor Red
    }
}
if ($failed) { exit 1 }
