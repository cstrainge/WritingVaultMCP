[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$controller = Join-Path $PSScriptRoot 'Configure-WritingVaultStartup.ps1'
$debugPlan = & $controller plan -Configuration Debug | ConvertFrom-Json
$releasePlan = & $controller plan -Configuration Release | ConvertFrom-Json
if ($debugPlan.Count -ne 3 -or $releasePlan.Count -ne 3) { throw 'Each build must have exactly three startup tasks.' }
if (@($debugPlan.name | Where-Object { $releasePlan.name -contains $_ }).Count -ne 0) { throw 'Debug and Release task names overlap.' }
if ($debugPlan[0].address -ne 'http://127.0.0.1:5285' -or $releasePlan[0].address -ne 'http://127.0.0.1:5284') {
    throw 'Viewer URLs are not isolated.'
}
if ($debugPlan[1].profile -ne 'writing-vault-debug' -or $releasePlan[1].profile -ne 'writing-vault') {
    throw 'Tunnel profiles are not isolated.'
}
foreach ($task in $debugPlan[0..1]) {
    if ($task.arguments -notmatch '(?:^| )-DebugBuild(?:$| )') { throw "Debug task omits -DebugBuild: $($task.name)" }
}
foreach ($task in $releasePlan[0..1]) {
    if ($task.arguments -match '(?:^| )-DebugBuild(?:$| )') { throw "Release task includes -DebugBuild: $($task.name)" }
}
if ($debugPlan[2].executable -notmatch '\\Debug\\' -or $releasePlan[2].executable -notmatch '\\Release\\') {
    throw 'Tray executables are not build-specific.'
}

. (Join-Path $PSScriptRoot 'Read-WritingVaultLocalSettings.ps1')
$settings = Get-WritingVaultLocalSettings -ProjectRoot $projectRoot
$production = $settings.DatabasePath
$webOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Run-WritingVaultWeb.ps1') -DebugBuild -Database $production -BackupRoot (Join-Path $settings.BackupRoot 'Debug') 2>&1
if ($LASTEXITCODE -eq 0 -or (@($webOutput) -join ' ') -notmatch 'separate from the Release production database') {
    throw 'Debug viewer accepted the Release database.'
}
$tunnelOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Run-WritingVaultTunnel.ps1') -DebugBuild -Database $production 2>&1
if ($LASTEXITCODE -eq 0 -or (@($tunnelOutput) -join ' ') -notmatch 'separate from the Release production database') {
    throw 'Debug tunnel accepted the Release database.'
}
Write-Host 'Debug and Release startup plans and database guards are isolated.' -ForegroundColor Green
