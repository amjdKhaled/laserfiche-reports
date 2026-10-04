param(
    [string]$TenantId,
    [string]$EnvFile,
    [ValidateSet("127.0.0.1", "localhost", "::1")]
    [string]$HostName = "127.0.0.1",
    [int]$Port = 5432,
    [string]$Database = "postgres",
    [switch]$DirectConnection
)
$ErrorActionPreference = "Stop"
if ($Port -lt 1 -or $Port -gt 65535) { throw "Invalid PostgreSQL port." }
# Do not guess the tenant, change Docker settings, or create/migrate a database.
$envPassword = $null
if ($EnvFile) {
    if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) { throw "Supabase .env file not found." }
    $values = @{}
    foreach ($line in [IO.File]::ReadAllLines((Resolve-Path -LiteralPath $EnvFile).Path)) {
        if ($line -match '^\s*(POOLER_TENANT_ID|POSTGRES_PASSWORD|POSTGRES_DB|POSTGRES_PORT)\s*=\s*(.*)$') {
            $key = $Matches[1]; $value = $Matches[2].Trim()
            if ($value.Length -ge 2 -and (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'")))) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            if ($value.Contains('${')) { throw "Expanded .env variables are unsupported. Enter the actual connection settings interactively." }
            $values[$key] = $value
        }
    }
    if (-not $PSBoundParameters.ContainsKey('TenantId')) { $TenantId = $values['POOLER_TENANT_ID'] }
    if (-not $PSBoundParameters.ContainsKey('Database') -and $values['POSTGRES_DB']) { $Database = $values['POSTGRES_DB'] }
    if (-not $PSBoundParameters.ContainsKey('Port') -and $values['POSTGRES_PORT']) { $Port = [int]$values['POSTGRES_PORT'] }
    $envPassword = $values['POSTGRES_PASSWORD']
    $values.Clear()
    if (-not $envPassword) { throw "POSTGRES_PASSWORD is missing in the selected Supabase .env file." }
    if (-not $DirectConnection -and -not $TenantId) { throw "POOLER_TENANT_ID is missing in the selected Supabase .env file." }
}
if ($Port -lt 1 -or $Port -gt 65535) { throw "Invalid PostgreSQL port." }
if ($DirectConnection) { $username = "postgres" }
else {
    if ([string]::IsNullOrWhiteSpace($TenantId)) {
        $TenantId = Read-Host "Actual POOLER_TENANT_ID from the Supabase .env file"
    }
    if ([string]::IsNullOrWhiteSpace($TenantId) -or $TenantId -match "YOUR_|CHANGE_ME|[;\r\n]") {
        throw "Enter the actual tenant ID, not a placeholder."
    }
    $username = "postgres.$($TenantId.Trim())"
}
foreach ($value in @($HostName, $Database)) {
    if ([string]::IsNullOrWhiteSpace($value) -or $value -match "[;\r\n]") { throw "Invalid connection setting." }
}
$settingsPath = Join-Path $PSScriptRoot "..\src\LaserficheReports.Web\appsettings.Local.json"
if (-not (Test-Path $settingsPath)) {
    $settings = [PSCustomObject]@{}
}
else { $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json }
$securePassword = if ($envPassword) { ConvertTo-SecureString $envPassword -AsPlainText -Force }
else { Read-Host "Actual POSTGRES_PASSWORD (input hidden)" -AsSecureString }
$envPassword = $null
$passwordPtr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
try {
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPtr)
    # ADO.NET quoting preserves semicolons, quotes and whitespace in passwords.
    $quote = { param([string]$Value) '"' + $Value.Replace('"', '""') + '"' }
    $connection = "Host=$HostName;Port=$Port;Database=$(& $quote $Database);Username=$(& $quote $username);Password=$(& $quote $password);SSL Mode=Disable"
    if (-not $settings.PSObject.Properties["Supabase"]) {
        $settings | Add-Member -NotePropertyName Supabase -NotePropertyValue ([PSCustomObject]@{})
    }
    $settings.Supabase | Add-Member -NotePropertyName PostgresConnectionString -NotePropertyValue $connection -Force
    # Existing settings, including Laserfiche and OCR, remain present in the JSON.
    $json = $settings | ConvertTo-Json -Depth 50
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($settingsPath), $json, [Text.UTF8Encoding]::new($false))
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPtr)
    $securePassword.Dispose()
    $password = $null
    $connection = $null
    $json = $null
}
Write-Host "Saved database connection in appsettings.Local.json. Restart the web application and check /api/database/status."
if ($env:Supabase__PostgresConnectionString) {
    Write-Warning "Supabase__PostgresConnectionString overrides the file. Update or remove that environment override in the application terminal."
}
