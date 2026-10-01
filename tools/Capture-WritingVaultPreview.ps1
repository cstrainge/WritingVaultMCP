[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Output,
    [int] $Width = 1440,
    [int] $Height = 1000,
    [ValidateRange(1, 30)][int] $WaitSeconds = 5,
    [ValidateRange(0, 30)][int] $AfterClickWaitSeconds = 1,
    [ValidatePattern('^[A-Za-z0-9#._-]+$')][string] $ClickSelector,
    [string] $ProbeSetupExpression,
    [string] $ReadyFile,
    [string] $ProbeExpression,
    [string] $ScriptFileToEvaluate,
    [string] $ScriptToCompile,
    [string] $ProfileDirectory,
    [ValidateSet('Tab', 'Enter', 'Escape')][string[]] $KeySequence = @(),
    [switch] $AccessibilitySummary,
    [string] $Url = 'http://127.0.0.1:5285/c/Lostville%20Preview'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
if (-not (Test-Path -LiteralPath $chrome -PathType Leaf)) { throw 'Google Chrome was not found.' }
$port = Get-Random -Minimum 19000 -Maximum 29000
$persistentProfile = -not [string]::IsNullOrWhiteSpace($ProfileDirectory)
$profile = if ($persistentProfile) { [IO.Path]::GetFullPath($ProfileDirectory) }
    else { Join-Path ([IO.Path]::GetTempPath()) "writing-vault-capture-$([Guid]::NewGuid().ToString('N'))" }
if ($persistentProfile) {
    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts')).TrimEnd('\') + '\'
    if (-not $profile.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Persistent capture profiles must be inside the workspace artifacts directory.'
    }
    if ((Test-Path -LiteralPath $profile -PathType Container) -and
        -not (Test-Path -LiteralPath (Join-Path $profile '.writing-vault-capture-profile') -PathType Leaf)) {
        throw 'The requested persistent capture profile was not created by this tool.'
    }
}
$process = $null
$socket = $null

function Receive-Message([Net.WebSockets.ClientWebSocket] $Client) {
    $buffer = New-Object byte[] 65536
    $stream = [IO.MemoryStream]::new()
    try {
        do {
            $segment = [ArraySegment[byte]]::new($buffer)
            $received = $Client.ReceiveAsync($segment, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
            if ($received.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Close) { throw 'Chrome DevTools closed before returning the screenshot.' }
            $stream.Write($buffer, 0, $received.Count)
        } while (-not $received.EndOfMessage)
        return [Text.Encoding]::UTF8.GetString($stream.ToArray())
    }
    finally { $stream.Dispose() }
}

function Send-Command([Net.WebSockets.ClientWebSocket] $Client, [int] $Id, [string] $Method, [hashtable] $Parameters = @{}) {
    $json = @{ id = $Id; method = $Method; params = $Parameters } | ConvertTo-Json -Compress -Depth 5
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    [void]$Client.SendAsync([ArraySegment[byte]]::new($bytes), [Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
}

try {
    New-Item -ItemType Directory -Path $profile -Force | Out-Null
    if ($persistentProfile) { Set-Content -LiteralPath (Join-Path $profile '.writing-vault-capture-profile') -Value 'Writing Vault disposable capture profile' }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $chrome
    # The disposable headless capture runs inside a restricted Windows test sandbox.
    # Disable Chrome's nested sandbox and software GPU process for this local fixture only.
    $start.Arguments = "--headless=new --disable-gpu --disable-gpu-sandbox --disable-software-rasterizer --no-sandbox --no-first-run --no-default-browser-check --remote-debugging-port=$port --user-data-dir=`"$profile`" --window-size=$Width,$Height `"$Url`""
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $process = [Diagnostics.Process]::Start($start)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $target = $null
    while ($null -eq $target -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 200
        try {
            $targets = Invoke-RestMethod -Uri "http://127.0.0.1:$port/json/list" -TimeoutSec 2
            $target = $targets | Where-Object { $_.type -eq 'page' -and $_.url -like "$Url*" } | Select-Object -First 1
        }
        catch { }
    }
    if ($null -eq $target) { throw 'Chrome did not expose the Writing Vault page for capture.' }
    Start-Sleep -Seconds $WaitSeconds
    $socket = [Net.WebSockets.ClientWebSocket]::new()
    [void]$socket.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $captureId = 1
    if (-not [string]::IsNullOrWhiteSpace($ClickSelector)) {
        $selectorJson = ConvertTo-Json $ClickSelector -Compress
        Send-Command $socket 1 'Runtime.evaluate' @{ expression = "document.querySelector($selectorJson).click()"; returnByValue = $true }
        do { $response = Receive-Message $socket | ConvertFrom-Json } while ($response.id -ne 1)
        Start-Sleep -Seconds $AfterClickWaitSeconds
        $captureId = 2
    }
    if (-not [string]::IsNullOrWhiteSpace($ProbeSetupExpression)) {
        Send-Command $socket 98 'Runtime.evaluate' @{ expression = $ProbeSetupExpression; returnByValue = $true; awaitPromise = $true }
        do { $setup = Receive-Message $socket | ConvertFrom-Json } while ($setup.id -ne 98)
        if ($setup.result.PSObject.Properties['exceptionDetails']) {
            throw "Browser setup failed: $($setup.result.exceptionDetails.text) $($setup.result.exceptionDetails.exception.description)"
        }
        $setup.result.result.value | ConvertTo-Json -Compress -Depth 8
    }
    if (-not [string]::IsNullOrWhiteSpace($ReadyFile)) {
        $fullReady = [IO.Path]::GetFullPath($ReadyFile)
        New-Item -ItemType Directory -Path (Split-Path -Parent $fullReady) -Force | Out-Null
        [IO.File]::WriteAllText($fullReady, 'ready')
    }
    $keyId = 200
    foreach ($key in $KeySequence) {
        $virtualKey = switch ($key) { 'Tab' { 9 } 'Enter' { 13 } 'Escape' { 27 } }
        foreach ($type in @('keyDown', 'keyUp')) {
            Send-Command $socket $keyId 'Input.dispatchKeyEvent' @{
                type = $type; key = $key; code = $key
                windowsVirtualKeyCode = $virtualKey; nativeVirtualKeyCode = $virtualKey
            }
            do { $keyResult = Receive-Message $socket | ConvertFrom-Json } while ($keyResult.id -ne $keyId)
            if ($keyResult.PSObject.Properties['error']) { throw "Chrome rejected the $key keyboard probe." }
            $keyId++
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($ScriptFileToEvaluate)) {
        $source = [IO.File]::ReadAllText([IO.Path]::GetFullPath($ScriptFileToEvaluate))
        Send-Command $socket 99 'Runtime.evaluate' @{ expression = $source; returnByValue = $true }
        do { $loaded = Receive-Message $socket | ConvertFrom-Json } while ($loaded.id -ne 99)
        if ($loaded.PSObject.Properties['error'] -or $loaded.result.PSObject.Properties['exceptionDetails']) {
            throw 'The browser diagnostic script failed to load.'
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($ProbeExpression)) {
        Send-Command $socket 100 'Runtime.evaluate' @{ expression = $ProbeExpression; returnByValue = $true; awaitPromise = $true }
        do { $probe = Receive-Message $socket | ConvertFrom-Json } while ($probe.id -ne 100)
        if ($probe.result.PSObject.Properties['exceptionDetails']) {
            throw "Browser probe failed: $($probe.result.exceptionDetails.text) $($probe.result.exceptionDetails.exception.description)"
        }
        if (-not $probe.result.PSObject.Properties['result'] -or -not $probe.result.result.PSObject.Properties['value']) {
            throw 'Browser probe did not return a serializable value.'
        }
        $probe.result.result.value | ConvertTo-Json -Compress -Depth 8
    }
    if (-not [string]::IsNullOrWhiteSpace($ScriptToCompile)) {
        Send-Command $socket 102 'Runtime.enable'
        do { $enabled = Receive-Message $socket | ConvertFrom-Json } while (-not $enabled.PSObject.Properties['id'] -or $enabled.id -ne 102)
        $source = [IO.File]::ReadAllText([IO.Path]::GetFullPath($ScriptToCompile))
        Send-Command $socket 101 'Runtime.compileScript' @{ expression = $source; sourceURL = 'app.js'; persistScript = $false }
        do { $compile = Receive-Message $socket | ConvertFrom-Json } while (-not $compile.PSObject.Properties['id'] -or $compile.id -ne 101)
        if ($compile.PSObject.Properties['error']) { $compile.error | ConvertTo-Json -Compress -Depth 8 }
        elseif ($compile.result.PSObject.Properties['exceptionDetails']) { $compile.result.exceptionDetails | ConvertTo-Json -Compress -Depth 8 }
        else { 'Script compilation succeeded.' }
    }
    if ($AccessibilitySummary) {
        Send-Command $socket 103 'Accessibility.getFullAXTree'
        do { $tree = Receive-Message $socket | ConvertFrom-Json } while ($tree.id -ne 103)
        if ($tree.PSObject.Properties['error']) { throw 'Chrome could not return the accessibility tree.' }
        $meaningful = @($tree.result.nodes | Where-Object {
            $_.role.value -in @('main', 'navigation', 'heading', 'button', 'link', 'table', 'columnheader', 'row', 'group') -and
            -not $_.ignored
        } | Select-Object -First 90 | ForEach-Object {
            [ordered]@{ role = $_.role.value; name = $_.name.value }
        })
        @{ totalNodes = @($tree.result.nodes).Count; meaningful = $meaningful } | ConvertTo-Json -Compress -Depth 8
    }
    Send-Command $socket $captureId 'Page.captureScreenshot' @{ format = 'png'; fromSurface = $true; captureBeyondViewport = $false }
    do { $response = Receive-Message $socket | ConvertFrom-Json } while ($response.id -ne $captureId)
    if ($null -eq $response.result.data) { throw 'Chrome returned no screenshot data.' }
    $fullOutput = [IO.Path]::GetFullPath($Output)
    New-Item -ItemType Directory -Path (Split-Path -Parent $fullOutput) -Force | Out-Null
    [IO.File]::WriteAllBytes($fullOutput, [Convert]::FromBase64String($response.result.data))
    Get-Item -LiteralPath $fullOutput | Select-Object FullName, Length
}
finally {
    if ($persistentProfile -and $null -ne $socket) {
        try {
            Send-Command $socket 999 'Browser.close'
            if ($null -ne $process) { [void]$process.WaitForExit(3000) }
        } catch { }
    }
    if ($null -ne $socket) { $socket.Dispose() }
    if ($null -ne $process -and -not $process.HasExited) { $process.Kill() }
    Start-Sleep -Milliseconds 300
    Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*$profile*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if (-not $persistentProfile -and (Test-Path -LiteralPath $profile -PathType Container)) {
        $resolvedProfile = (Resolve-Path -LiteralPath $profile).Path
        $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedProfile.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to remove a Chrome capture profile outside the temporary directory.' }
        Remove-Item -LiteralPath $resolvedProfile -Recurse -Force -ErrorAction SilentlyContinue
    }
}
