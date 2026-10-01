[CmdletBinding()]
param([Parameter(Mandatory)][string] $DatabasePath)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data
$resolved = (Resolve-Path -LiteralPath $DatabasePath).Path
$connection = [System.Data.OleDb.OleDbConnection]::new(
    "Provider=Microsoft.ACE.OLEDB.12.0;Data Source=$resolved;Persist Security Info=False;OLE DB Services=-4;")
$results = [System.Collections.Generic.List[object]]::new()

function Invoke-Sql([string] $Sql) {
    $command = $connection.CreateCommand()
    $command.Transaction = $script:transaction
    $command.CommandText = $Sql
    try { return $command.ExecuteNonQuery() } finally { $command.Dispose() }
}

function Get-Identity {
    $command = $connection.CreateCommand()
    $command.Transaction = $script:transaction
    $command.CommandText = 'SELECT @@IDENTITY'
    try { return [int]$command.ExecuteScalar() } finally { $command.Dispose() }
}

function Expect-Rejection([string] $Name, [string] $Sql) {
    try {
        Invoke-Sql $Sql | Out-Null
        $results.Add([pscustomobject]@{ Probe=$Name; Passed=$false; Detail='Insert was accepted.' })
    }
    catch [System.Data.OleDb.OleDbException] {
        $results.Add([pscustomobject]@{ Probe=$Name; Passed=$true; Detail='ACE rejected the invalid write.' })
    }
}

try {
    $connection.Open()
    $script:transaction = $connection.BeginTransaction()
    try {
        Invoke-Sql "INSERT INTO [Continuities] ([Name],[NormalizedName],[DefaultTimeZoneId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ('Probe Canon','probe canon','UTC',Now(),Now())" | Out-Null
        $continuityId = Get-Identity

        Expect-Rejection 'null-audit' "INSERT INTO [Continuities] ([Name],[NormalizedName],[DefaultTimeZoneId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ('Bad Null','bad null','UTC',Null,Now())"
        Expect-Rejection 'blank-required-text' "INSERT INTO [Continuities] ([Name],[NormalizedName],[DefaultTimeZoneId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (' ','blank','UTC',Now(),Now())"
        Expect-Rejection 'orphan-tag-entity' 'INSERT INTO [EntityTags] ([EntityId],[TagId]) VALUES (999999,999998)'

        Invoke-Sql "INSERT INTO [Tags] ([Name],[NormalizedName],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ('Mystery','mystery',Now(),Now())" | Out-Null
        Expect-Rejection 'duplicate-normalized-tag' "INSERT INTO [Tags] ([Name],[NormalizedName],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (' MYSTERY ','mystery',Now(),Now())"

        Invoke-Sql "INSERT INTO [CanonEntities] ([ContinuityId],[EntityType],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ($continuityId,'Location',Now(),Now())" | Out-Null
        $locationId = Get-Identity
        Expect-Rejection 'self-parent-location' "INSERT INTO [Locations] ([EntityId],[Name],[ParentLocationId]) VALUES ($locationId,'Loop',$locationId)"

        Invoke-Sql "INSERT INTO [CanonEntities] ([ContinuityId],[EntityType],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ($continuityId,'WorldEvent',Now(),Now())" | Out-Null
        $eventId = Get-Identity
        Expect-Rejection 'reversed-story-interval' "INSERT INTO [WorldEvents] ([EntityId],[Title],[EventKind],[EventLowerBound],[EventUpperBound]) VALUES ($eventId,'Backwards','Range',#2030-01-01#,#2020-01-01#)"
        Expect-Rejection 'delete-referenced-continuity' "DELETE FROM [Continuities] WHERE [Id]=$continuityId"

        $failed = @($results | Where-Object { -not $_.Passed }).Count
        [pscustomobject]@{ Valid=($failed -eq 0); Failed=$failed; Probes=$results } | ConvertTo-Json -Depth 5
        if ($failed -ne 0) { exit 2 }
    }
    finally {
        $script:transaction.Rollback()
        $script:transaction.Dispose()
    }
}
finally {
    $connection.Close()
    $connection.Dispose()
}
