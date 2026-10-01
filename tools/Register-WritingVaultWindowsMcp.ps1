[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$server = Join-Path $projectRoot 'bin\Release\net10.0-windows\WritingVaultMcp.exe'
$manifest = Join-Path (Split-Path -Parent $server) 'manifest.json'
$generator = Join-Path $PSScriptRoot 'New-WritingVaultWindowsMcpManifest.py'

$windows = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$build = [int]$windows.CurrentBuildNumber
$revision = [int]$windows.UBR
if ($build -lt 26220 -or ($build -eq 26220 -and $revision -lt 7262)) {
    throw "Windows build $build.$revision does not support the documented ODR MCP registration flow (minimum: 26220.7262)."
}
$odr = Get-Command odr.exe -ErrorAction SilentlyContinue
if ($null -eq $odr) { throw 'odr.exe is unavailable. Check the installed Windows MCP/ODR feature before registering.' }
if (-not (Test-Path -LiteralPath $server -PathType Leaf)) { throw "Release MCP server not found: $server" }

& py -3 $generator --server $server
if ($LASTEXITCODE -ne 0) { throw 'Could not generate the Windows MCP manifest from the Release server.' }

$validator = Join-Path $projectRoot 'artifacts\windows-mcp\tools\mcpb.exe'
if (Test-Path -LiteralPath $validator -PathType Leaf) {
    & $validator validate $manifest
    if ($LASTEXITCODE -ne 0) { throw 'MCP bundle manifest validation failed.' }
}

& $odr.Source mcp add $manifest
if ($LASTEXITCODE -ne 0) { throw "Windows ODR rejected the Writer's Vault MCP manifest." }
& $odr.Source mcp list
if ($LASTEXITCODE -ne 0) { throw 'Windows ODR registered the manifest but its list command failed.' }
