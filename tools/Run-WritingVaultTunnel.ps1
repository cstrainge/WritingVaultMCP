[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $TunnelId,

    [switch] $ForgetApiKey,

    [switch] $Background,

    [switch] $DebugBuild,

    [string] $Database
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$configuration = if ($DebugBuild) { 'Debug' } else { 'Release' }
if ([string]::IsNullOrWhiteSpace($TunnelId)) {
    $TunnelId = if ($DebugBuild) { $env:WRITING_VAULT_DEBUG_TUNNEL_ID } else { $env:WRITING_VAULT_TUNNEL_ID }
}
$tunnelClient = Join-Path $projectRoot 'mcp-tunnel\tunnel-client.exe'
$serverDll = Join-Path $projectRoot "bin\$configuration\net10.0-windows\WritingVaultMcp.dll"
$profileDirectory = Join-Path $projectRoot '.tunnel-client'
$profileName = if ($DebugBuild) { 'writing-vault-debug' } else { 'writing-vault' }
$profileFile = Join-Path $profileDirectory "$profileName.yaml"
$credentialDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WritingVaultMCP'
$apiKeyFile = Join-Path $credentialDirectory $(if ($DebugBuild) { 'openai-tunnel-api-key-debug.dpapi' } else { 'openai-tunnel-api-key.dpapi' })
$runtimeDirectory = Join-Path $credentialDirectory "Runtime\$configuration"
$healthUrlFile = Join-Path $runtimeDirectory 'tunnel-health.url'
$pidFile = Join-Path $runtimeDirectory 'tunnel.pid'
$ownsApiKeyEnvironment = $false
$apiKeyToPersist = $null
$tunnelMutex = $null
$ownsTunnelMutex = $false
$failureExitCode = 1

function Assert-FileExists {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $Description,

        [Parameter(Mandatory)]
        [int] $ExitCode
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $script:failureExitCode = $ExitCode
        throw "$Description was not found: $Path"
    }
}

function Invoke-TunnelClient {
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments,

        [Parameter(Mandatory)]
        [string] $FailureMessage,

        [Parameter(Mandatory)]
        [int] $ExitCode
    )

    & $script:tunnelClient @Arguments
    if ($LASTEXITCODE -ne 0) {
        $script:failureExitCode = $ExitCode
        throw "$FailureMessage (exit code $LASTEXITCODE)."
    }
}

function ConvertTo-PlainText {
    param(
        [Parameter(Mandatory)]
        [Security.SecureString] $SecureValue
    )

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function Find-RunningTunnel {
    try {
        $profilePattern = '(?i)(?:^|\s)--profile(?:=|\s+)["'']?' +
            [regex]::Escape($script:profileName) + '["'']?(?=\s|$)'
        return Get-CimInstance Win32_Process -Filter "Name='tunnel-client.exe'" -ErrorAction Stop |
            Where-Object {
                $_.CommandLine -match '(?i)(?:^|\s)run(?:\s|$)' -and
                $_.CommandLine -match $profilePattern
            } |
            Select-Object -First 1
    }
    catch {
        return $null
    }
}

try {
    . (Join-Path $PSScriptRoot 'Read-WritingVaultLocalSettings.ps1')
    $localSettings = Get-WritingVaultLocalSettings -ProjectRoot $projectRoot
    $backupRoot = if ($DebugBuild) { Join-Path $localSettings.BackupRoot 'Debug' } else { $localSettings.BackupRoot }
    if ([string]::IsNullOrWhiteSpace($Database)) {
        $Database = if ($DebugBuild) { Join-Path $projectRoot 'usability\WritingVault.Usability.accdb' } else { $localSettings.DatabasePath }
    }
    if ($DebugBuild -and [IO.Path]::GetFullPath($Database) -eq [IO.Path]::GetFullPath($localSettings.DatabasePath)) {
        throw 'Debug tunnel database must be separate from the Release production database.'
    }
    Assert-FileExists -Path $tunnelClient -Description 'Tunnel client' -ExitCode 10
    Assert-FileExists -Path $serverDll -Description "Writing Vault $configuration build" -ExitCode 11
    Assert-FileExists -Path $Database -Description "Writing Vault $configuration database" -ExitCode 12

    $tunnelMutex = [Threading.Mutex]::new($false, "Local\WritingVaultMCP-OpenAITunnel-$profileName")
    try {
        $ownsTunnelMutex = $tunnelMutex.WaitOne(0)
    }
    catch [Threading.AbandonedMutexException] {
        $ownsTunnelMutex = $true
    }
    if (-not $ownsTunnelMutex) {
        Write-Host 'The Writing Vault ChatGPT tunnel launcher is already running.' -ForegroundColor Yellow
        exit 0
    }

    $runningTunnel = Find-RunningTunnel
    if ($null -ne $runningTunnel) {
        Write-Host "The Writing Vault ChatGPT tunnel is already running (process $($runningTunnel.ProcessId))." -ForegroundColor Yellow
        Write-Host 'Use the existing tunnel window; starting a duplicate would split connection-local session state.' -ForegroundColor DarkGray
        exit 0
    }

    if ($Background -and $ForgetApiKey) {
        $failureExitCode = 22
        throw '-Background cannot be combined with -ForgetApiKey.'
    }
    if ($ForgetApiKey -and (Test-Path -LiteralPath $apiKeyFile -PathType Leaf)) {
        Remove-Item -LiteralPath $apiKeyFile -Force
        Write-Host 'Removed the saved OpenAI tunnel API key.' -ForegroundColor DarkGray
    }

    if ($Background) {
        $failureExitCode = 21
        if (-not (Test-Path -LiteralPath $apiKeyFile -PathType Leaf)) {
            throw "Background mode requires the saved Windows-encrypted API key: $apiKeyFile"
        }
        try {
            $savedApiKey = Get-Content -LiteralPath $apiKeyFile -Raw | ConvertTo-SecureString
            $env:CONTROL_PLANE_API_KEY = ConvertTo-PlainText -SecureValue $savedApiKey
            $ownsApiKeyEnvironment = $true
        }
        catch {
            throw 'The saved OpenAI tunnel API key could not be decrypted for this Windows user.'
        }
        if ([string]::IsNullOrWhiteSpace($env:CONTROL_PLANE_API_KEY)) {
            throw 'The saved OpenAI tunnel API key decrypted to an empty value.'
        }
    }
    elseif ([string]::IsNullOrWhiteSpace($env:CONTROL_PLANE_API_KEY)) {
        if (Test-Path -LiteralPath $apiKeyFile -PathType Leaf) {
            try {
                $savedApiKey = Get-Content -LiteralPath $apiKeyFile -Raw | ConvertTo-SecureString
                $env:CONTROL_PLANE_API_KEY = ConvertTo-PlainText -SecureValue $savedApiKey
                $ownsApiKeyEnvironment = $true
                Write-Host 'Loaded the saved OpenAI tunnel API key for this Windows user.' -ForegroundColor DarkGray
            }
            catch {
                Write-Warning 'The saved OpenAI tunnel API key could not be decrypted. Enter a replacement key.'
            }
        }

        if ([string]::IsNullOrWhiteSpace($env:CONTROL_PLANE_API_KEY)) {
            $apiKeyToPersist = Read-Host 'OpenAI tunnel runtime API key' -AsSecureString
            $env:CONTROL_PLANE_API_KEY = ConvertTo-PlainText -SecureValue $apiKeyToPersist
            $ownsApiKeyEnvironment = $true
        }

        if ([string]::IsNullOrWhiteSpace($env:CONTROL_PLANE_API_KEY)) {
            throw 'A runtime API key is required.'
        }
    }

    $profileExists = Test-Path -LiteralPath $profileFile -PathType Leaf
    if ($profileExists -and [string]::IsNullOrWhiteSpace($TunnelId)) {
        $profileText = Get-Content -LiteralPath $profileFile -Raw
        $tunnelIdMatch = [regex]::Match($profileText, '(?m)^\s*tunnel_id:\s*"?([^"\s#]+)')
        if ($tunnelIdMatch.Success) {
            $TunnelId = $tunnelIdMatch.Groups[1].Value
        }
    }

    if ([string]::IsNullOrWhiteSpace($TunnelId)) {
        if ($Background) { $failureExitCode = 20; throw 'Background mode requires an existing profile containing a tunnel ID.' }
        $TunnelId = Read-Host 'OpenAI tunnel ID (tunnel_...)'
    }

    if ($TunnelId -notmatch '^tunnel_[A-Za-z0-9_-]+$') {
        $failureExitCode = 22
        throw 'The tunnel ID must begin with tunnel_ and contain only letters, numbers, underscores, or hyphens.'
    }
    if ($DebugBuild) {
        $releaseProfile = Join-Path $profileDirectory 'writing-vault.yaml'
        if (Test-Path -LiteralPath $releaseProfile -PathType Leaf) {
            $releaseProfileText = Get-Content -LiteralPath $releaseProfile -Raw
            $releaseTunnelId = [regex]::Match($releaseProfileText, '(?m)^\s*tunnel_id:\s*"?([^"\s#]+)')
            if ($releaseTunnelId.Success -and $TunnelId -eq $releaseTunnelId.Groups[1].Value) {
                throw 'The Debug tunnel must use a different tunnel ID from Release.'
            }
        }
    }

    if ($Background -and -not $profileExists) {
        $failureExitCode = 20
        throw "Background mode requires an existing tunnel profile: $profileFile"
    }
    New-Item -ItemType Directory -Path $profileDirectory -Force | Out-Null

    # tunnel-client tokenizes this command using shell-style escaping. Forward
    # slashes prevent Windows path backslashes from being consumed as escapes.
    $serverCommandPath = $serverDll.Replace('\', '/')
    $databaseCommandPath = $Database.Replace('\', '/')
    $backupCommandPath = $backupRoot.Replace('\', '/')
    $mcpCommand = 'dotnet "{0}" serve --database "{1}" --backup-root "{2}" --client-label "ChatGPT Tunnel {3}" --tool-surface v4' -f $serverCommandPath, $databaseCommandPath, $backupCommandPath, $configuration
    if ($Background) {
        $savedProfile = Get-Content -LiteralPath $profileFile -Raw
        if ($savedProfile.IndexOf($serverCommandPath, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
            $savedProfile.IndexOf($databaseCommandPath, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
            $savedProfile.IndexOf($backupCommandPath, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            $failureExitCode = 20
            throw "The $configuration tunnel profile does not target its own server and database. Run the launcher interactively to refresh it."
        }
    }
    $initArguments = @(
            'init',
            '--sample', 'sample_mcp_stdio_local',
            '--profile', $profileName,
            '--profile-dir', $profileDirectory,
            '--tunnel-id', $TunnelId,
            '--health-listen-addr', '127.0.0.1:0',
            '--mcp-command', $mcpCommand
    )

    if (-not $Background -and $profileExists) {
        $initArguments += '--force'
        Write-Host "Refreshing tunnel profile: $profileFile" -ForegroundColor DarkGray
    }
    elseif (-not $Background) {
        Write-Host 'Creating the Writing Vault tunnel profile...' -ForegroundColor Cyan
    }

    if (-not $Background) {
        Invoke-TunnelClient -Arguments $initArguments -FailureMessage 'Tunnel profile creation failed' -ExitCode 30
    }

    if (-not $Background) {
        Write-Host 'Checking the tunnel and local MCP configuration...' -ForegroundColor Cyan
        Invoke-TunnelClient -Arguments @(
            'doctor',
            '--profile', $profileName,
            '--profile-dir', $profileDirectory,
            '--explain'
        ) -FailureMessage 'Tunnel configuration check failed' -ExitCode 31
    }

    if ($null -ne $apiKeyToPersist) {
        New-Item -ItemType Directory -Path $credentialDirectory -Force | Out-Null
        ConvertFrom-SecureString -SecureString $apiKeyToPersist |
            Set-Content -LiteralPath $apiKeyFile -Encoding Ascii -NoNewline
        Write-Host "Saved the API key with Windows user encryption: $apiKeyFile" -ForegroundColor DarkGray
    }

    if (-not $Background) { Write-Host ''; Write-Host 'Starting the Writing Vault tunnel. Press Ctrl+C to stop it.' -ForegroundColor Green }
    New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
    foreach ($stale in @($healthUrlFile, $pidFile)) {
        if (Test-Path -LiteralPath $stale -PathType Leaf) { Remove-Item -LiteralPath $stale -Force }
    }
    $runArguments = @(
        'run',
        '--profile', $profileName,
        '--profile-dir', $profileDirectory,
        '--health.url-file', $healthUrlFile,
        '--pid.file', $pidFile,
        '--log.format', 'struct-text'
    )
    if (-not $Background) { $runArguments += '--open-web-ui' }
    if ($Background) {
        . (Join-Path $PSScriptRoot 'BoundedProcess.ps1')
        $logRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WritingVaultMCP\Logs'
        $exitCode = Invoke-WritingVaultBoundedProcess -FilePath $tunnelClient -ArgumentList $runArguments -LogPath (Join-Path $logRoot "tunnel-$($configuration.ToLowerInvariant()).log")
        if ($exitCode -ne 0) { $failureExitCode = 40; throw "Tunnel client failed (exit code $exitCode)." }
    }
    else {
        Invoke-TunnelClient -Arguments $runArguments -FailureMessage 'Tunnel client failed' -ExitCode 40
    }
}
catch {
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit $failureExitCode
}
finally {
    if ($ownsApiKeyEnvironment) {
        Remove-Item Env:CONTROL_PLANE_API_KEY -ErrorAction SilentlyContinue
    }
    if ($ownsTunnelMutex -and $null -ne $tunnelMutex) {
        $tunnelMutex.ReleaseMutex()
    }
    if ($null -ne $tunnelMutex) {
        $tunnelMutex.Dispose()
    }
}
