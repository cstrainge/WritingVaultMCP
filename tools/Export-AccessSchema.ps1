[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $DatabasePath,

    [Parameter(Mandatory)]
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'

function Get-OptionalComProperty {
    param(
        [Parameter(Mandatory)] $Object,
        [Parameter(Mandatory)] [string] $Name
    )

    try {
        $value = $Object.$Name
        if ($null -eq $value) {
            return $null
        }

        return $value
    }
    catch {
        return $null
    }
}

function Get-DaoTypeName {
    param([int] $Type)

    switch ($Type) {
        1 { 'Boolean' }
        2 { 'Byte' }
        3 { 'Integer' }
        4 { 'Long' }
        5 { 'Currency' }
        6 { 'Single' }
        7 { 'Double' }
        8 { 'DateTime' }
        9 { 'Binary' }
        10 { 'Text' }
        11 { 'LongBinary' }
        12 { 'LongText' }
        15 { 'Guid' }
        16 { 'BigInt' }
        17 { 'VarBinary' }
        18 { 'Char' }
        19 { 'Numeric' }
        20 { 'Decimal' }
        21 { 'Float' }
        22 { 'Time' }
        23 { 'Timestamp' }
        default { "Unknown($Type)" }
    }
}

$resolvedDatabase = (Resolve-Path -LiteralPath $DatabasePath).Path
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutput
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$engine = New-Object -ComObject DAO.DBEngine.120
$database = $null

try {
    $database = $engine.OpenDatabase($resolvedDatabase, $false, $true)
    $tables = @()

    foreach ($tableDefinition in $database.TableDefs) {
        if ($tableDefinition.Name -like 'MSys*') {
            continue
        }

        $fields = @()
        $ordinal = 0
        foreach ($field in $tableDefinition.Fields) {
            $fields += [ordered]@{
                Ordinal          = $ordinal
                Name             = [string] $field.Name
                DaoType          = [int] $field.Type
                Type             = Get-DaoTypeName ([int] $field.Type)
                Size             = [int] $field.Size
                Required         = [bool] (Get-OptionalComProperty $field 'Required')
                AllowZeroLength  = Get-OptionalComProperty $field 'AllowZeroLength'
                Attributes       = [int] $field.Attributes
                DefaultValue     = Get-OptionalComProperty $field 'DefaultValue'
                ValidationRule   = Get-OptionalComProperty $field 'ValidationRule'
                ValidationText   = Get-OptionalComProperty $field 'ValidationText'
            }
            $ordinal++
        }

        $indexes = @()
        foreach ($index in $tableDefinition.Indexes) {
            $indexFields = @()
            $fieldOrdinal = 0
            foreach ($indexField in $index.Fields) {
                $indexFields += [ordered]@{
                    Ordinal    = $fieldOrdinal
                    Name       = [string] $indexField.Name
                    Attributes = [int] $indexField.Attributes
                }
                $fieldOrdinal++
            }

            $indexes += [ordered]@{
                Name        = [string] $index.Name
                Primary     = [bool] $index.Primary
                Unique      = [bool] $index.Unique
                Required    = [bool] $index.Required
                IgnoreNulls = [bool] $index.IgnoreNulls
                Foreign     = [bool] $index.Foreign
                Fields      = $indexFields
            }
        }

        $escapedTableName = $tableDefinition.Name.Replace(']', ']]')
        $recordset = $database.OpenRecordset("SELECT Count(*) AS [RowCount] FROM [$escapedTableName]")
        try {
            $rowCount = [int64] $recordset.Fields['RowCount'].Value
        }
        finally {
            $recordset.Close()
            [void] [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($recordset)
        }

        $tables += [ordered]@{
            Name           = [string] $tableDefinition.Name
            Attributes     = [int] $tableDefinition.Attributes
            ValidationRule = Get-OptionalComProperty $tableDefinition 'ValidationRule'
            ValidationText = Get-OptionalComProperty $tableDefinition 'ValidationText'
            RowCount       = $rowCount
            Fields         = $fields
            Indexes        = $indexes
        }
    }

    $relations = @()
    foreach ($relation in $database.Relations) {
        if ($relation.Table -like 'MSys*' -or $relation.ForeignTable -like 'MSys*') {
            continue
        }

        $attributes = [int] $relation.Attributes
        $relationFields = @()
        $ordinal = 0
        foreach ($relationField in $relation.Fields) {
            $relationFields += [ordered]@{
                Ordinal          = $ordinal
                PrincipalColumn  = [string] $relationField.Name
                DependentColumn  = [string] $relationField.ForeignName
            }
            $ordinal++
        }

        $relations += [ordered]@{
            Name             = [string] $relation.Name
            PrincipalTable   = [string] $relation.Table
            DependentTable   = [string] $relation.ForeignTable
            Attributes       = $attributes
            Enforced         = (($attributes -band 2) -eq 0)
            CascadeUpdate    = (($attributes -band 256) -ne 0)
            CascadeDelete    = (($attributes -band 4096) -ne 0)
            Fields           = $relationFields
        }
    }

    $snapshot = [ordered]@{
        FormatVersion    = 1
        CapturedAtUtc    = [DateTime]::UtcNow.ToString('o')
        DatabaseFileName = [System.IO.Path]::GetFileName($resolvedDatabase)
        DatabaseBytes    = (Get-Item -LiteralPath $resolvedDatabase).Length
        DatabaseSha256   = (Get-FileHash -LiteralPath $resolvedDatabase -Algorithm SHA256).Hash
        Tables           = @($tables | Sort-Object { $_.Name })
        Relations        = @($relations | Sort-Object { $_.DependentTable }, { $_.Name })
    }

    $snapshot | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $resolvedOutput -Encoding utf8
    $snapshot
}
finally {
    if ($null -ne $database) {
        $database.Close()
        [void] [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    }

    [void] [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($engine)
}
