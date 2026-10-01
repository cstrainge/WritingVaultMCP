[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('install', 'status', 'start', 'stop', 'restart', 'remove', 'plan',
        'start-viewer', 'stop-viewer', 'restart-viewer',
        'start-tunnel', 'stop-tunnel', 'restart-tunnel', 'disable-tray', 'start-tray')]
    [string] $Action = 'status',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$prefix = if ($Configuration -eq 'Debug') { 'Writing Vault Debug' } else { 'Writing Vault' }
$viewerTask = "$prefix Viewer"
$tunnelTask = "$prefix ChatGPT Tunnel"
$trayTask = "$prefix Tray"
$database = $null
$webDll = Join-Path $projectRoot "WritingVault.Web\bin\$Configuration\net10.0-windows\WritingVault.Web.dll"
$serverDll = Join-Path $projectRoot "bin\$Configuration\net10.0-windows\WritingVaultMcp.dll"
$trayExe = Join-Path $projectRoot "WritingVault.Tray\bin\$Configuration\net10.0-windows\WritingVault.Tray.exe"
$webLauncher = Join-Path $PSScriptRoot 'Run-WritingVaultWeb.ps1'
$tunnelLauncher = Join-Path $PSScriptRoot 'Run-WritingVaultTunnel.ps1'
$tunnelClient = Join-Path $projectRoot 'mcp-tunnel\tunnel-client.exe'
$profileName = if ($Configuration -eq 'Debug') { 'writing-vault-debug' } else { 'writing-vault' }
$profile = Join-Path $projectRoot ".tunnel-client\$profileName.yaml"
$credentialName = if ($Configuration -eq 'Debug') { 'openai-tunnel-api-key-debug.dpapi' } else { 'openai-tunnel-api-key.dpapi' }
$credential = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "WritingVaultMCP\$credentialName"
$runtimeRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "WritingVaultMCP\Runtime\$Configuration"
$viewerPidPath = Join-Path $runtimeRoot 'viewer.pid'
$tunnelPidPath = Join-Path $runtimeRoot 'tunnel.pid'
$viewerPort = if ($Configuration -eq 'Debug') { 5285 } else { 5284 }
$powershell = Join-Path $PSHOME 'powershell.exe'
$userId = [Security.Principal.WindowsIdentity]::GetCurrent().Name

function Task-Arguments([string] $Script) {
    $buildSwitch = if ($Configuration -eq 'Debug') { ' -DebugBuild' } else { '' }
    return "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$Script`" -Background$buildSwitch"
}

function Get-Plan {
    @(
        [ordered]@{ name = $viewerTask; executable = $powershell; arguments = Task-Arguments $webLauncher; address = "http://127.0.0.1:$viewerPort"; hidden = $true; restartCount = 100; restartIntervalSeconds = 60 },
        [ordered]@{ name = $tunnelTask; executable = $powershell; arguments = Task-Arguments $tunnelLauncher; profile = $profileName; hidden = $true; restartCount = 100; restartIntervalSeconds = 60 },
        [ordered]@{ name = $trayTask; executable = $trayExe; arguments = ''; hidden = $true; restartCount = 100; restartIntervalSeconds = 60 }
    )
}

function Assert-InstallReady {
    . (Join-Path $PSScriptRoot 'Read-WritingVaultLocalSettings.ps1')
    $localSettings = Get-WritingVaultLocalSettings -ProjectRoot $projectRoot
    $productionDatabase = $localSettings.DatabasePath
    $script:database = if ($Configuration -eq 'Debug') { Join-Path $projectRoot 'usability\WritingVault.Usability.accdb' } else { $productionDatabase }
    $backupRoot = if ($Configuration -eq 'Debug') { Join-Path $localSettings.BackupRoot 'Debug' } else { $localSettings.BackupRoot }
    if ($Configuration -eq 'Debug' -and [IO.Path]::GetFullPath($database) -eq [IO.Path]::GetFullPath($productionDatabase)) {
        throw 'Debug and Release cannot use the same database.'
    }
    foreach ($item in @(
        @($webDll, "$Configuration web application"), @($serverDll, "$Configuration MCP server"),
        @($trayExe, "$Configuration tray application"),
        @($database, "$Configuration database"), @($webLauncher, 'web launcher'),
        @($tunnelLauncher, 'tunnel launcher'), @($tunnelClient, 'tunnel client'),
        @($profile, 'tunnel profile'), @($credential, 'Windows-encrypted tunnel credential')
    )) {
        if (-not (Test-Path -LiteralPath $item[0] -PathType Leaf)) { throw "$($item[1]) was not found: $($item[0])" }
    }
    if ([IO.Path]::GetExtension($database) -ne '.accdb') { throw 'The database must be an .accdb file.' }
    $profileText = Get-Content -LiteralPath $profile -Raw
    if ($profileText -notmatch '(?m)^\s*tunnel_id:\s*"?tunnel_[A-Za-z0-9_-]+') { throw 'The writing-vault tunnel profile has no valid tunnel ID.' }
    if ($Configuration -eq 'Debug') {
        $releaseProfile = Join-Path $projectRoot '.tunnel-client\writing-vault.yaml'
        if (Test-Path -LiteralPath $releaseProfile -PathType Leaf) {
            $releaseText = Get-Content -LiteralPath $releaseProfile -Raw
            $debugId = [regex]::Match($profileText, '(?m)^\s*tunnel_id:\s*"?([^"\s#]+)')
            $releaseId = [regex]::Match($releaseText, '(?m)^\s*tunnel_id:\s*"?([^"\s#]+)')
            if ($debugId.Success -and $releaseId.Success -and $debugId.Groups[1].Value -eq $releaseId.Groups[1].Value) {
                throw 'The Debug and Release tunnel profiles use the same tunnel ID.'
            }
        }
    }
    if ($profileText.IndexOf($serverDll.Replace('\', '/'), [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        $profileText.IndexOf($database.Replace('\', '/'), [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        $profileText.IndexOf($backupRoot.Replace('\', '/'), [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        $profileText.IndexOf('--tool-surface v4', [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "The $Configuration tunnel profile does not target its own server, database, and backup root. Refresh it with Run-WritingVaultTunnel.ps1."
    }
    try {
        $secure = Get-Content -LiteralPath $credential -Raw | ConvertTo-SecureString
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try {
            $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
            if ([string]::IsNullOrWhiteSpace($plain)) { throw 'empty credential' }
        }
        finally { if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) } }
    }
    catch { throw 'The saved tunnel API key cannot be decrypted by the current Windows user.' }

    $client = [Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.ConnectAsync('127.0.0.1', $viewerPort)
        if ($connect.Wait(300) -and $client.Connected) { throw "Port $viewerPort is already occupied. Stop the current listener before installing startup tasks." }
    }
    catch [AggregateException] { }
    finally { $client.Dispose() }
}

function New-VaultTask([string] $Name, [string] $Executable, [string] $Arguments) {
    $taskAction = if ([string]::IsNullOrWhiteSpace($Arguments)) {
        New-ScheduledTaskAction -Execute $Executable -WorkingDirectory $projectRoot
    }
    else {
        New-ScheduledTaskAction -Execute $Executable -Argument $Arguments -WorkingDirectory $projectRoot
    }
    $logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
    $watchdogTrigger = New-ScheduledTaskTrigger -Once -At ([DateTime]::Now.AddMinutes(1)) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration (New-TimeSpan -Days 3650)
    $principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -Hidden -StartWhenAvailable -RestartCount 100 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)
    return New-ScheduledTask -Action $taskAction -Trigger @($logonTrigger, $watchdogTrigger) -Principal $principal -Settings $settings -Description "$Name for the current Windows user. Managed by Writing Vault."
}

function Expected-TaskAction([string] $Name) {
    if ($Name -eq $viewerTask) { return @{ Executable = $powershell; Arguments = (Task-Arguments $webLauncher) } }
    if ($Name -eq $tunnelTask) { return @{ Executable = $powershell; Arguments = (Task-Arguments $tunnelLauncher) } }
    if ($Name -eq $trayTask) { return @{ Executable = $trayExe; Arguments = '' } }
    throw "Unknown Writer's Vault task name."
}

function Get-OwnedTask([string] $Name) {
    $task = Get-ScheduledTask -TaskName $Name -TaskPath '\' -ErrorAction SilentlyContinue
    if ($null -eq $task) { return $null }
    $expected = Expected-TaskAction $Name
    $action = @($task.Actions)
    $principalIsCurrentUser = $false
    try {
        # Task Scheduler may normalize DOMAIN\user to user when registering a
        # local account, or return a SID, so compare account identities.
        $principalSid = if ($task.Principal.UserId -match '^S-\d+(?:-\d+)+$') {
            [Security.Principal.SecurityIdentifier]::new($task.Principal.UserId)
        } else {
            ([Security.Principal.NTAccount]::new($task.Principal.UserId)).Translate([Security.Principal.SecurityIdentifier])
        }
        $principalIsCurrentUser = $principalSid.Value -eq [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    }
    catch { }
    if ($action.Count -ne 1 -or
        -not [string]::Equals($action[0].Execute, $expected.Executable, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$action[0].Arguments, $expected.Arguments, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$action[0].WorkingDirectory, $projectRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not $principalIsCurrentUser) {
        throw "The scheduled task named '$Name' is not owned by this Writer's Vault installation."
    }
    return $task
}

function Test-ManagedProcess([string] $PidPath, [string] $ExpectedPath) {
    if (-not (Test-Path -LiteralPath $PidPath -PathType Leaf)) { return $false }
    $number = 0
    if (-not [int]::TryParse((Get-Content -LiteralPath $PidPath -Raw).Trim(), [ref]$number) -or $number -le 0) { return $false }
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$number" -ErrorAction SilentlyContinue
    return $null -ne $process -and $null -ne $process.CommandLine -and
        $process.CommandLine.IndexOf($ExpectedPath, [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Test-ElectedBackendForConfiguration {
    . (Join-Path $PSScriptRoot 'Read-WritingVaultLocalSettings.ps1')
    $productionPath = (Get-WritingVaultLocalSettings -ProjectRoot $projectRoot).DatabasePath
    $selectedPath = if ($Configuration -eq 'Debug') { Join-Path $projectRoot 'usability\WritingVault.Usability.accdb' } else { $productionPath }
    $alternatePath = $selectedPath.Replace('\', '/')
    $running = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='WritingVaultMcp.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $command = [string]$_.CommandLine
            $command.IndexOf($serverDll, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $command.IndexOf('backend', [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            ($command.IndexOf($selectedPath, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
             $command.IndexOf($alternatePath, [StringComparison]::OrdinalIgnoreCase) -ge 0)
        }
    return @($running).Count -gt 0
}

function Stop-VaultTasks([string[]] $Names, [bool] $WaitBackend = $false) {
    foreach ($name in $Names) {
        if (Get-OwnedTask $name) {
            # A disabled task cannot be immediately relaunched by its watchdog.
            Disable-ScheduledTask -TaskName $name -TaskPath '\' | Out-Null
            Stop-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction SilentlyContinue
        }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $viewerRunning = $Names -contains $viewerTask -and (Test-ManagedProcess $viewerPidPath $webDll)
        $tunnelRunning = $Names -contains $tunnelTask -and (Test-ManagedProcess $tunnelPidPath $tunnelClient)
        $backendRunning = $WaitBackend -and (Test-ElectedBackendForConfiguration)
        if (-not $viewerRunning -and -not $tunnelRunning -and -not $backendRunning) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The managed $Configuration service or elected backend did not drain within 45 seconds. Other MCP clients may still be connected."
}

function Start-VaultTasks([string[]] $Names) {
    foreach ($name in $Names) {
        if (-not (Get-OwnedTask $name)) { throw "Scheduled task is not installed: $name" }
        Enable-ScheduledTask -TaskName $name -TaskPath '\' | Out-Null
        Start-ScheduledTask -TaskName $name -TaskPath '\'
    }
}

switch ($Action) {
    'plan' {
        Get-Plan | ConvertTo-Json -Depth 4
    }
    'status' {
        $items = foreach ($name in @($viewerTask, $tunnelTask, $trayTask)) {
            $task = Get-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction SilentlyContinue
            $state = if ($null -eq $task) { 'Missing' }
                else {
                    try { [void](Get-OwnedTask $name); $task.State.ToString() }
                    catch { 'Foreign' }
                }
            $processRunning = if ($name -eq $viewerTask) { Test-ManagedProcess $viewerPidPath $webDll }
                elseif ($name -eq $tunnelTask) { Test-ManagedProcess $tunnelPidPath $tunnelClient }
                else { $state -eq 'Running' }
            [ordered]@{ name = $name; installed = $null -ne $task; state = $state; processRunning = [bool]$processRunning }
        }
        [ordered]@{ configuration = $Configuration; viewerUrl = "http://127.0.0.1:$viewerPort"; tasks = @($items) } | ConvertTo-Json -Depth 4
    }
    'install' {
        Assert-InstallReady
        $backups = @{}
        foreach ($name in @($viewerTask, $tunnelTask, $trayTask)) {
            if (Get-OwnedTask $name) { $backups[$name] = Export-ScheduledTask -TaskName $name -TaskPath '\' }
        }
        try {
            Register-ScheduledTask -TaskName $viewerTask -TaskPath '\' -InputObject (New-VaultTask $viewerTask $powershell (Task-Arguments $webLauncher)) -Force | Out-Null
            Register-ScheduledTask -TaskName $tunnelTask -TaskPath '\' -InputObject (New-VaultTask $tunnelTask $powershell (Task-Arguments $tunnelLauncher)) -Force | Out-Null
            Register-ScheduledTask -TaskName $trayTask -TaskPath '\' -InputObject (New-VaultTask $trayTask $trayExe '') -Force | Out-Null
        }
        catch {
            foreach ($name in @($viewerTask, $tunnelTask, $trayTask)) {
                Unregister-ScheduledTask -TaskName $name -TaskPath '\' -Confirm:$false -ErrorAction SilentlyContinue
                if ($backups.ContainsKey($name)) { Register-ScheduledTask -TaskName $name -TaskPath '\' -Xml $backups[$name] | Out-Null }
            }
            throw
        }
        Write-Host 'Installed independent current-user startup tasks for the viewer, ChatGPT tunnel, and tray.' -ForegroundColor Green
    }
    'start' { Start-VaultTasks @($tunnelTask, $viewerTask) }
    'stop' { Stop-VaultTasks @($tunnelTask, $viewerTask) $true }
    'restart' {
        Stop-VaultTasks @($tunnelTask, $viewerTask) $true
        Start-VaultTasks @($tunnelTask, $viewerTask)
    }
    'start-viewer' { Start-VaultTasks @($viewerTask) }
    'stop-viewer' { Stop-VaultTasks @($viewerTask) }
    'restart-viewer' {
        Stop-VaultTasks @($viewerTask)
        Start-VaultTasks @($viewerTask)
    }
    'start-tunnel' { Start-VaultTasks @($tunnelTask) }
    'stop-tunnel' { Stop-VaultTasks @($tunnelTask) }
    'restart-tunnel' {
        Stop-VaultTasks @($tunnelTask)
        Start-VaultTasks @($tunnelTask)
    }
    'disable-tray' {
        if (Get-OwnedTask $trayTask) { Disable-ScheduledTask -TaskName $trayTask -TaskPath '\' | Out-Null }
    }
    'start-tray' { Start-VaultTasks @($trayTask) }
    'remove' {
        Stop-VaultTasks @($tunnelTask, $viewerTask) $true
        if (Get-OwnedTask $trayTask) {
            Disable-ScheduledTask -TaskName $trayTask -TaskPath '\' | Out-Null
            Stop-ScheduledTask -TaskName $trayTask -TaskPath '\' -ErrorAction SilentlyContinue
        }
        foreach ($name in @($viewerTask, $tunnelTask, $trayTask)) {
            if (Get-OwnedTask $name) { Unregister-ScheduledTask -TaskName $name -TaskPath '\' -Confirm:$false }
        }
    }
}
