[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$tunnelClient = Join-Path $projectRoot 'mcp-tunnel\tunnel-client.exe'
$serverDll = Join-Path $projectRoot 'bin\Debug\net10.0-windows\WritingVaultMcp.dll'
$profileDirectory = Join-Path $projectRoot '.tunnel-client'
$debugProfile = Join-Path $profileDirectory 'writing-vault-debug.yaml'
$probeProfileName = 'writing-vault-v4-image-probe'
$credentialFile = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WritingVaultMCP\openai-tunnel-api-key-debug.dpapi'
$evidenceFile = Join-Path $projectRoot 'artifacts\contracts\v4\image-ingress-evidence.jsonl'
$mutex = $null
$ownsMutex = $false
$ownsApiKeyEnvironment = $false

function ConvertTo-PlainText {
    param([Parameter(Mandatory)][Security.SecureString] $SecureValue)
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Invoke-TunnelClient {
    param([Parameter(Mandatory)][string[]] $Arguments, [Parameter(Mandatory)][string] $FailureMessage)
    & $script:tunnelClient @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FailureMessage (exit code $LASTEXITCODE)." }
}

try {
    foreach ($required in @($tunnelClient, $serverDll, $debugProfile, $credentialFile)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required file was not found: $required" }
    }

    $mutex = [Threading.Mutex]::new($false, 'Local\WritingVaultMCP-OpenAITunnel-writing-vault-debug')
    try { $ownsMutex = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'The Debug Writing Vault tunnel is running. Stop it before starting the temporary image probe.' }

    $running = Get-CimInstance Win32_Process -Filter "Name='tunnel-client.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '(?i)(?:^|\s)run(?:\s|$)' -and
            $_.CommandLine -match '(?i)(?:^|\s)--profile(?:=|\s+)["'']?writing-vault-debug["'']?(?=\s|$)' } |
        Select-Object -First 1
    if ($null -ne $running) { throw "A Writing Vault tunnel process is already running (PID $($running.ProcessId)). Stop it before the probe." }

    $profileText = Get-Content -LiteralPath $debugProfile -Raw
    $match = [regex]::Match($profileText, '(?m)^\s*tunnel_id:\s*"?([^"\s#]+)')
    if (-not $match.Success) { throw 'The existing Debug tunnel profile has no tunnel_id.' }
    $tunnelId = $match.Groups[1].Value

    $saved = Get-Content -LiteralPath $credentialFile -Raw | ConvertTo-SecureString
    $env:CONTROL_PLANE_API_KEY = ConvertTo-PlainText -SecureValue $saved
    $ownsApiKeyEnvironment = $true
    if ([string]::IsNullOrWhiteSpace($env:CONTROL_PLANE_API_KEY)) { throw 'The saved tunnel API key decrypted to an empty value.' }

    $commandDll = $serverDll.Replace('\', '/')
    $commandEvidence = $evidenceFile.Replace('\', '/')
    $mcpCommand = 'dotnet "{0}" contract image-probe --evidence-output "{1}"' -f $commandDll, $commandEvidence
    Invoke-TunnelClient -Arguments @(
        'init', '--sample', 'sample_mcp_stdio_local',
        '--profile', $probeProfileName, '--profile-dir', $profileDirectory,
        '--tunnel-id', $tunnelId, '--health-listen-addr', '127.0.0.1:0',
        '--mcp-command', $mcpCommand, '--force'
    ) -FailureMessage 'Probe tunnel profile creation failed'
    Invoke-TunnelClient -Arguments @(
        'doctor', '--profile', $probeProfileName, '--profile-dir', $profileDirectory, '--explain'
    ) -FailureMessage 'Probe tunnel configuration check failed'

    Write-Host 'Starting the temporary Debug image-ingress probe on the existing tunnel ID.' -ForegroundColor Green
    Write-Host 'Refresh the ChatGPT MCP declaration, call image_ingress_probe with a real PNG, then press Ctrl+C.' -ForegroundColor Cyan
    Write-Host "Evidence: $evidenceFile" -ForegroundColor DarkGray
    Invoke-TunnelClient -Arguments @(
        'run', '--profile', $probeProfileName, '--profile-dir', $profileDirectory, '--log.format', 'struct-text'
    ) -FailureMessage 'Probe tunnel client failed'
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    if ($ownsApiKeyEnvironment) { Remove-Item Env:CONTROL_PLANE_API_KEY -ErrorAction SilentlyContinue }
    if ($ownsMutex -and $null -ne $mutex) { $mutex.ReleaseMutex() }
    if ($null -ne $mutex) { $mutex.Dispose() }
}
