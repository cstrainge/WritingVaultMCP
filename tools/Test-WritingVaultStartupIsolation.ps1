[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$webTask = "Writing Vault Phase 8 Probe Web $suffix"
$tunnelTask = "Writing Vault Phase 8 Probe Tunnel $suffix"
$trayTask = "Writing Vault Phase 12 Probe Tray $suffix"
$probeRoot = Join-Path ([IO.Path]::GetTempPath()) "writing-vault-startup-probe-$suffix"
$probeScript = Join-Path $probeRoot 'probe.ps1'
$powershell = Join-Path $PSHOME 'powershell.exe'
$userId = [Security.Principal.WindowsIdentity]::GetCurrent().Name

function Wait-Until([scriptblock] $Condition, [int] $Seconds, [string] $Failure) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

function Read-Integer([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return 0 }
    $value = 0
    if ([int]::TryParse((Get-Content -LiteralPath $Path -Raw).Trim(), [ref]$value)) { return $value }
    return 0
}

function New-ProbeTask([string] $Name) {
    $arguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$probeScript`" -Name `"$Name`" -StateRoot `"$probeRoot`""
    $action = New-ScheduledTaskAction -Execute $powershell -Argument $arguments -WorkingDirectory $probeRoot
    $logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
    $watchdogTrigger = New-ScheduledTaskTrigger -Once -At ([DateTime]::Now.AddMinutes(1)) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration (New-TimeSpan -Days 3650)
    $principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -Hidden -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)
    New-ScheduledTask -Action $action -Trigger @($logonTrigger, $watchdogTrigger) -Principal $principal -Settings $settings -Description 'Disposable Writing Vault Phase 8 restart-isolation probe.'
}

function Paths([string] $Name) {
    $safe = $Name.Replace(' ', '-')
    return @{
        Count = Join-Path $probeRoot "$safe.count"
        Pid = Join-Path $probeRoot "$safe.pid"
        Heartbeat = Join-Path $probeRoot "$safe.heartbeat"
        Crash = Join-Path $probeRoot "$safe.crash"
    }
}

New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
@'
param([Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][string] $StateRoot)
$safe = $Name.Replace(' ', '-')
$countPath = Join-Path $StateRoot "$safe.count"
$pidPath = Join-Path $StateRoot "$safe.pid"
$heartbeatPath = Join-Path $StateRoot "$safe.heartbeat"
$crashPath = Join-Path $StateRoot "$safe.crash"
$count = 0
if (Test-Path -LiteralPath $countPath) { [void][int]::TryParse((Get-Content -LiteralPath $countPath -Raw).Trim(), [ref]$count) }
($count + 1) | Set-Content -LiteralPath $countPath -Encoding Ascii
$PID | Set-Content -LiteralPath $pidPath -Encoding Ascii
while ($true) {
    [DateTime]::UtcNow.Ticks | Set-Content -LiteralPath $heartbeatPath -Encoding Ascii
    if (Test-Path -LiteralPath $crashPath) { Remove-Item -LiteralPath $crashPath -Force; exit 23 }
    Start-Sleep -Seconds 1
}
'@ | Set-Content -LiteralPath $probeScript -Encoding UTF8

$web = Paths $webTask
$tunnel = Paths $tunnelTask
$tray = Paths $trayTask
try {
    Register-ScheduledTask -TaskName $webTask -InputObject (New-ProbeTask $webTask) | Out-Null
    Register-ScheduledTask -TaskName $tunnelTask -InputObject (New-ProbeTask $tunnelTask) | Out-Null
    Register-ScheduledTask -TaskName $trayTask -InputObject (New-ProbeTask $trayTask) | Out-Null
    Start-ScheduledTask -TaskName $webTask
    Start-ScheduledTask -TaskName $tunnelTask
    Start-ScheduledTask -TaskName $trayTask
    Wait-Until { (Read-Integer $web.Count) -eq 1 -and (Read-Integer $tunnel.Count) -eq 1 -and (Read-Integer $tray.Count) -eq 1 } 20 'The disposable startup probe tasks did not start.'

    $tunnelHeartbeatBefore = [long](Get-Content -LiteralPath $tunnel.Heartbeat -Raw)
    $trayHeartbeatBefore = [long](Get-Content -LiteralPath $tray.Heartbeat -Raw)
    'forced failure' | Set-Content -LiteralPath $web.Crash -Encoding Ascii
    Wait-Until { (Read-Integer $web.Count) -ge 2 } 80 'Task Scheduler did not restart the crashed web probe.'
    $tunnelHeartbeatAfter = [long](Get-Content -LiteralPath $tunnel.Heartbeat -Raw)
    $trayHeartbeatAfter = [long](Get-Content -LiteralPath $tray.Heartbeat -Raw)
    if ((Read-Integer $tunnel.Count) -ne 1 -or $tunnelHeartbeatAfter -le $tunnelHeartbeatBefore) { throw 'The tunnel probe did not remain independently alive during the web restart.' }
    if ((Read-Integer $tray.Count) -ne 1 -or $trayHeartbeatAfter -le $trayHeartbeatBefore) { throw 'The tray probe did not remain independently alive during the web restart.' }

    $webCountBefore = Read-Integer $web.Count
    $webHeartbeatBefore = [long](Get-Content -LiteralPath $web.Heartbeat -Raw)
    $trayHeartbeatBefore = [long](Get-Content -LiteralPath $tray.Heartbeat -Raw)
    'forced failure' | Set-Content -LiteralPath $tunnel.Crash -Encoding Ascii
    Wait-Until { (Read-Integer $tunnel.Count) -ge 2 } 80 'Task Scheduler did not restart the crashed tunnel probe.'
    $webHeartbeatAfter = [long](Get-Content -LiteralPath $web.Heartbeat -Raw)
    $trayHeartbeatAfter = [long](Get-Content -LiteralPath $tray.Heartbeat -Raw)
    if ((Read-Integer $web.Count) -ne $webCountBefore -or $webHeartbeatAfter -le $webHeartbeatBefore) { throw 'The web probe did not remain independently alive during the tunnel restart.' }
    if ((Read-Integer $tray.Count) -ne 1 -or $trayHeartbeatAfter -le $trayHeartbeatBefore) { throw 'The tray probe did not remain independently alive during the tunnel restart.' }

    $webCountBefore = Read-Integer $web.Count
    $tunnelCountBefore = Read-Integer $tunnel.Count
    $webHeartbeatBefore = [long](Get-Content -LiteralPath $web.Heartbeat -Raw)
    $tunnelHeartbeatBefore = [long](Get-Content -LiteralPath $tunnel.Heartbeat -Raw)
    'forced failure' | Set-Content -LiteralPath $tray.Crash -Encoding Ascii
    Wait-Until { (Read-Integer $tray.Count) -ge 2 } 80 'Task Scheduler did not restart the crashed tray probe.'
    if ((Read-Integer $web.Count) -ne $webCountBefore -or [long](Get-Content -LiteralPath $web.Heartbeat -Raw) -le $webHeartbeatBefore) {
        throw 'The web probe did not remain independently alive during the tray restart.'
    }
    if ((Read-Integer $tunnel.Count) -ne $tunnelCountBefore -or [long](Get-Content -LiteralPath $tunnel.Heartbeat -Raw) -le $tunnelHeartbeatBefore) {
        throw 'The tunnel probe did not remain independently alive during the tray restart.'
    }

    [ordered]@{
        webRestartCount = Read-Integer $web.Count
        tunnelRestartCount = Read-Integer $tunnel.Count
        trayRestartCount = Read-Integer $tray.Count
        webStayedAvailableDuringTunnelRestart = $true
        tunnelStayedConnectedDuringWebRestart = $true
        servicesStayedAvailableDuringTrayRestart = $true
    } | ConvertTo-Json
}
finally {
    foreach ($name in @($webTask, $tunnelTask, $trayTask)) {
        Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 1
    if (Test-Path -LiteralPath $probeRoot -PathType Container) {
        $resolvedRoot = (Resolve-Path -LiteralPath $probeRoot).Path
        $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedRoot.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to remove startup probe data outside the temporary directory.' }
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
