$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString())
try {
    $scripts = New-Item -ItemType Directory -Path (Join-Path $testRoot 'scripts')
    $web = New-Item -ItemType Directory -Path (Join-Path $testRoot 'src/LaserficheReports.Web')
    $scriptPath = Join-Path $scripts.FullName 'configure-database.ps1'
    Copy-Item (Join-Path $PSScriptRoot 'configure-database.ps1') $scriptPath
    $localSettings = Join-Path $web.FullName 'appsettings.Local.json'
    [IO.File]::WriteAllText($localSettings, '{"Laserfiche":{"RepositoryId":"OriginalRepo"},"Ocr":{"Enabled":false}}')
    $envFile = Join-Path $testRoot '.env'
    [IO.File]::WriteAllText($envFile, @'
POOLER_TENANT_ID="actual-local-tenant"
POSTGRES_PASSWORD='test;secret#value"quoted'
POSTGRES_DB=original_database
POSTGRES_PORT=5440
'@)
    $output = (& $scriptPath -EnvFile $envFile | Out-String)
    $settings = Get-Content $localSettings -Raw | ConvertFrom-Json
    if ($settings.Laserfiche.RepositoryId -ne 'OriginalRepo' -or $settings.Ocr.Enabled -ne $false) { throw 'Existing settings were changed.' }
    if ($settings.Supabase.PostgresConnectionString -notlike '*Username="postgres.actual-local-tenant"*') { throw 'Tenant username missing.' }
    if ($settings.Supabase.PostgresConnectionString -notlike '*Port=5440*') { throw 'Published port not imported.' }
    if (-not $settings.Supabase.PostgresConnectionString.Contains('Password="test;secret#value""quoted"')) { throw 'Password quoting damaged.' }
    if ($output.Contains('test;secret') -or $output.Contains('actual-local-tenant')) { throw 'Secrets were printed.' }
    & $scriptPath -EnvFile $envFile -TenantId explicit-tenant -Port 5550 | Out-Null
    $settings = Get-Content $localSettings -Raw | ConvertFrom-Json
    if ($settings.Supabase.PostgresConnectionString -notlike '*Username="postgres.explicit-tenant"*' -or $settings.Supabase.PostgresConnectionString -notlike '*Port=5550*') { throw 'Explicit settings did not take precedence.' }
    $before = Get-Content $localSettings -Raw
    [IO.File]::WriteAllText($envFile, 'POSTGRES_PASSWORD=another-test-secret')
    $rejected = $false
    try { & $scriptPath -EnvFile $envFile | Out-Null } catch { $rejected = $true }
    if (-not $rejected -or (Get-Content $localSettings -Raw) -ne $before) { throw 'Invalid tenant configuration was saved.' }
    [IO.File]::WriteAllText($envFile, "POOLER_TENANT_ID=your-tenant-id`nPOSTGRES_PASSWORD=test-secret")
    Remove-Item -LiteralPath $localSettings
    & $scriptPath -EnvFile $envFile | Out-Null
    $created = Get-Content $localSettings -Raw | ConvertFrom-Json
    if ($created.Supabase.PostgresConnectionString -notlike '*postgres.your-tenant-id*') { throw 'Missing local settings were not initialized from the actual tenant value.' }
    Write-Host 'Supabase configuration import tests passed.' 
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
