[CmdletBinding()]
param(
    [switch] $Background,
    [switch] $DebugBuild,
    [string] $Database,
    [string] $BackupRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$configuration = if ($DebugBuild) { 'Debug' } else { 'Release' }
$webDll = Join-Path $projectRoot "WritingVault.Web\bin\$configuration\net10.0-windows\WritingVault.Web.dll"
$serverDll = Join-Path $projectRoot "bin\$configuration\net10.0-windows\WritingVaultMcp.dll"
$logRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WritingVaultMCP\Logs'
$logPath = Join-Path $logRoot "viewer-$($configuration.ToLowerInvariant()).log"
$pidPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "WritingVaultMCP\Runtime\$configuration\viewer.pid"
$port = if ($DebugBuild) { 5285 } else { 5284 }
$mutex = $null
$ownsMutex = $false

try {
    . (Join-Path $PSScriptRoot 'Read-WritingVaultLocalSettings.ps1')
    $localSettings = Get-WritingVaultLocalSettings -ProjectRoot $projectRoot
    if ([string]::IsNullOrWhiteSpace($Database) -or [string]::IsNullOrWhiteSpace($BackupRoot)) {
        if ([string]::IsNullOrWhiteSpace($Database)) {
            $Database = if ($DebugBuild) { Join-Path $projectRoot 'usability\WritingVault.Usability.accdb' } else { $localSettings.DatabasePath }
        }
        if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
            $BackupRoot = if ($DebugBuild) { Join-Path $localSettings.BackupRoot 'Debug' } else { $localSettings.BackupRoot }
        }
    }
    foreach ($item in @(@($webDll, 'Web application'), @($serverDll, 'MCP server'), @($Database, 'database'))) {
        if (-not (Test-Path -LiteralPath $item[0] -PathType Leaf)) { throw "$($item[1]) was not found: $($item[0])" }
    }
    if ($DebugBuild -and [IO.Path]::GetFullPath($Database) -eq [IO.Path]::GetFullPath($localSettings.DatabasePath)) {
        throw 'Debug viewer database must be separate from the Release production database.'
    }
    $mutex = [Threading.Mutex]::new($false, "Local\WritingVaultMCP-Web-$port")
    try { $ownsMutex = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { Write-Host 'The Writing Vault viewer launcher is already running.' -ForegroundColor Yellow; exit 0 }

    $arguments = @($webDll, '--server', $serverDll, '--database', $Database, '--backup-root', $BackupRoot, '--listen', "http://127.0.0.1:$port")
    if ($Background) {
        . (Join-Path $PSScriptRoot 'BoundedProcess.ps1')
        $exitCode = Invoke-WritingVaultBoundedProcess -FilePath 'dotnet' -ArgumentList $arguments -LogPath $logPath -PidPath $pidPath
        exit $exitCode
    }
    Write-Host "Writing Vault $configuration viewer: http://127.0.0.1:$port" -ForegroundColor Green
    Write-Host 'Press Ctrl+C to stop it.' -ForegroundColor DarkGray
    & dotnet @arguments
    exit $LASTEXITCODE
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    if ($ownsMutex -and $null -ne $mutex) { $mutex.ReleaseMutex() }
    if ($null -ne $mutex) { $mutex.Dispose() }
}
