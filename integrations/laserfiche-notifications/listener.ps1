# Windows PowerShell 5.1 / .NET Framework bridge for the installed RepositoryAccess SDK.
# Official methods: Session.LogIn, NotificationManager.Subscribe/WaitForNotification,
# ActivityLogReader.Read/Item/Reset. Every required type/enum is checked at runtime.
# stdin ACK means the event and cursor were durably committed by the .NET backend.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
function Send($message, [bool]$ack = $false) {
    [Console]::WriteLine(($message | ConvertTo-Json -Compress -Depth 8))
    [Console]::Out.Flush()
    if ($ack -and [Console]::ReadLine() -ne 'ACK') { throw 'Backend did not acknowledge durable event.' }
}
$session = $null
try {
    $path = $env:LF_SYNC_SDK
    if (-not $path -or -not (Test-Path -LiteralPath $path)) {
        $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }
        $candidates = @($roots | ForEach-Object {
            $root = Join-Path $_ 'Laserfiche'
            if (Test-Path -LiteralPath $root) { Get-ChildItem -LiteralPath $root -Filter 'Laserfiche.RepositoryAccess.dll' -Recurse -ErrorAction SilentlyContinue }
        })
        if ($candidates.Count -ne 1) { throw 'Set Realtime:SdkAssemblyPath to the installed SDK assembly (missing or ambiguous installation).' }
        $path = $candidates[0].FullName
    }
    $assembly = [Reflection.Assembly]::LoadFrom($path)
    foreach ($name in @('Session','RepositoryRegistration','NotificationManager','NotificationActivities','NotificationSubscriptionOptions','ActivityLogReader','SortDirection')) {
        if (-not $assembly.GetType("Laserfiche.RepositoryAccess.$name")) { throw "Installed SDK lacks $name." }
    }
    $activityType = $assembly.GetType('Laserfiche.RepositoryAccess.NotificationActivities')
    $names = [Enum]::GetNames($activityType)
    foreach ($required in @('CreateEntry','ReleaseEntry','DeleteEntry','RestoreEntry')) {
        if ($names -notcontains $required) { throw "Installed SDK lacks required notification: $required." }
    }
    $session = New-Object Laserfiche.RepositoryAccess.Session
    $session.IsSecure = $env:LF_SYNC_SECURE -eq 'true'
    $registration = New-Object Laserfiche.RepositoryAccess.RepositoryRegistration($env:LF_SYNC_SERVER, $env:LF_SYNC_REPOSITORY)
    $session.LogIn($env:LF_SYNC_USERNAME, $env:LF_SYNC_PASSWORD, $registration)
    # Credentials do not appear in command arguments or protocol/log output.
    Remove-Item Env:LF_SYNC_PASSWORD -ErrorAction SilentlyContinue
    $manager = New-Object Laserfiche.RepositoryAccess.NotificationManager($session)
    $manager.Connect()
    $subscriptions = @($names | Where-Object { $_ -match 'Entry|Field|Template' -and $_ -notmatch 'All|None' })
    foreach ($name in $subscriptions) {
        $manager.Subscribe([Enum]::Parse($activityType, $name), [Laserfiche.RepositoryAccess.NotificationSubscriptionOptions]::OtherSessionsOnly)
    }
    $script:cursor = [long]$env:LF_SYNC_CURSOR
    function Emit-Record($record) {
        $sequence = [long]$record.SequenceNumber
        if ($sequence -le $script:cursor) { return }
        $kind = $record.ActivityType.ToString()
        $entry = -1
        if ($record.PSObject.Properties['EntryId']) { $entry = [int]$record.EntryId }
        $changes = @($kind)
        if ($kind -eq 'ReleaseEntry') { $changes = @($record.GetActivities() | ForEach-Object { $_.ToString() }) }
        # Only explicitly metadata-only activities skip page/content embedding.
        # Unknown activities conservatively refresh content; no guessed SDK enum values.
        $change = 'Content'
        if ($changes.Count -gt 0 -and @($changes | Where-Object { $_ -notmatch 'Rename|Move|Field|Template|Name|Path' }).Count -eq 0) { $change = 'Metadata' }
        if ($entry -le 0 -and ($kind -match 'Entry|Field|Template|Security|Access')) { $entry = 0; $change = 'Reconcile' }
        Send @{ entryId=$entry; change=$change; sequence=$sequence; label=($changes -join ', '); type='event' } $true
        $script:cursor = $sequence
    }
    function Catch-Up {
        $log = New-Object Laserfiche.RepositoryAccess.ActivityLogReader([Laserfiche.RepositoryAccess.SortDirection]::Descending, $session)
        try {
            $latest = 0L; $oldest = 0L
            if ($log.Read()) { $latest = [long]$log.Item.SequenceNumber }
            $log.Reset()
            if ($log.Read()) { $oldest = [long]$log.Item.SequenceNumber }
            if ($script:cursor -eq 0 -or $script:cursor -gt $latest -or ($oldest -gt 0 -and $script:cursor -lt ($oldest - 1))) {
                # Rollover/server sequence reset: durable metadata reconciliation, not a full OCR rebuild.
                Send @{ type='event'; entryId=0; change='Reconcile'; sequence=$latest; reset=$true; label='Activity-log gap: reconcile current metadata' } $true
                $script:cursor = $latest
            }
        } finally { $log.Dispose() }
        $range = New-Object Laserfiche.RepositoryAccess.ActivityLogReader($script:cursor, -1, $session)
        try { while ($range.Read()) { Emit-Record $range.Item } } finally { $range.Dispose() }
    }
    # Subscribe first, then catch up: changes during startup stay in the notification stream/log.
    Catch-Up
    Send @{ type='ready'; sdkVersion=$assembly.GetName().Version.ToString(); subscriptions=$subscriptions }
    while ($true) {
        $notification = $manager.WaitForNotification(30000)
        if ($null -ne $notification) {
            # Ordered range read only when notified, never a periodic repository poll.
            Catch-Up
            Emit-Record ($notification.GetActivityRecord())
        }
        Send @{ type='heartbeat' }
    }
} catch {
    # Detailed exception goes to protected host logs, never a user-facing stack trace or secret.
    [Console]::Error.WriteLine($_.Exception.GetType().FullName + ': ' + $_.Exception.Message)
    exit 1
} finally { if ($null -ne $session) { $session.Dispose() } }
