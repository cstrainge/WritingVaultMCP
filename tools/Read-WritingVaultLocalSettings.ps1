function Get-WritingVaultLocalSettings {
    param([Parameter(Mandatory)][string] $ProjectRoot)

    $path = Join-Path $ProjectRoot 'appsettings.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw 'Local appsettings.json is missing. Copy appsettings.example.json and set DatabasePath and BackupRoot.'
    }
    try { $settings = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'Local appsettings.json is not valid JSON.' }
    $vaultProperty = $settings.PSObject.Properties['WritingVault']
    if ($null -eq $vaultProperty -or $null -eq $vaultProperty.Value) { throw 'Local appsettings.json has no WritingVault section.' }
    $vault = $vaultProperty.Value
    $databaseProperty = $vault.PSObject.Properties['DatabasePath']
    $backupProperty = $vault.PSObject.Properties['BackupRoot']
    $database = if ($null -eq $databaseProperty) { '' } else { [string]$databaseProperty.Value }
    $backup = if ($null -eq $backupProperty) { '' } else { [string]$backupProperty.Value }
    if ([string]::IsNullOrWhiteSpace($database) -or [string]::IsNullOrWhiteSpace($backup) -or
        -not [IO.Path]::IsPathRooted($database) -or -not [IO.Path]::IsPathRooted($backup)) {
        throw 'Local DatabasePath and BackupRoot must be absolute paths.'
    }
    return [pscustomobject]@{ DatabasePath = $database; BackupRoot = $backup }
}
