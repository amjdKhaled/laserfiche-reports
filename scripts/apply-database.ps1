param(
    [string]$ContainerName = "supabase-db",
    [string]$Database = "postgres",
    [string]$Username = "postgres"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$migrationDirectory = Join-Path $projectRoot "database\migrations"
$migrations = Get-ChildItem -Path $migrationDirectory -Filter "*.sql" | Sort-Object Name

if (-not $migrations) {
    throw "No SQL migrations were found in $migrationDirectory"
}

$running = docker inspect -f "{{.State.Running}}" $ContainerName 2>$null
if ($running -ne "true") {
    throw "The local Supabase database container '$ContainerName' is not running."
}

foreach ($migration in $migrations) {
    Write-Host "Applying $($migration.Name)..."
    Get-Content -Raw $migration.FullName |
        docker exec -i $ContainerName psql -v ON_ERROR_STOP=1 -U $Username -d $Database

    if ($LASTEXITCODE -ne 0) {
        throw "Migration failed: $($migration.Name)"
    }
}

Write-Host "Verifying database objects..."
Get-Content -Raw (Join-Path $projectRoot "database\verify.sql") |
    docker exec -i $ContainerName psql -v ON_ERROR_STOP=1 -U $Username -d $Database

if ($LASTEXITCODE -ne 0) {
    throw "Database verification failed."
}

Write-Host "Laserfiche Reports database is ready."
