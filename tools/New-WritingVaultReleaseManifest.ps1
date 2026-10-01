[CmdletBinding()]
param(
    [string] $Output = 'artifacts\release-manifest.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$outputPath = if ([IO.Path]::IsPathRooted($Output)) {
    [IO.Path]::GetFullPath($Output)
} else {
    [IO.Path]::GetFullPath((Join-Path $projectRoot $Output))
}
if (-not $outputPath.StartsWith($artifactRoot.TrimEnd('\') + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The release manifest must be written under the ignored artifacts directory.'
}

Push-Location $projectRoot
try {
    $previousErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $commitOutput = & git rev-parse --verify HEAD 2>$null }
    finally { $ErrorActionPreference = $previousErrorAction }
    $commit = if ($commitOutput) { ([string]$commitOutput).Trim() } else { '' }
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
        throw 'Create and review an immutable Git commit before building a Release manifest.'
    }
    $ErrorActionPreference = 'Continue'
    try { $changes = @(& git status --porcelain --untracked-files=all 2>$null) }
    finally { $ErrorActionPreference = $previousErrorAction }
    if ($LASTEXITCODE -ne 0 -or $changes.Count -gt 0) {
        throw 'The source checkout must be clean before building Release artifacts.'
    }

    & dotnet build WritingVault.slnx -c Release --no-restore -v:q
    if ($LASTEXITCODE -ne 0) { throw 'The Release solution build failed.' }
    $ErrorActionPreference = 'Continue'
    try {
        $builtCommit = & git rev-parse --verify HEAD 2>$null
        $postBuildChanges = @(& git status --porcelain --untracked-files=all 2>$null)
        $postBuildGitExit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousErrorAction }
    if ($postBuildGitExit -ne 0 -or $builtCommit -ne $commit -or
        $postBuildChanges.Count -gt 0) {
        throw 'The source checkout changed during the Release build; discard the candidate.'
    }

    $templatePath = Join-Path $projectRoot 'docs\RELEASE_MANIFEST_TEMPLATE.json'
    $manifest = Get-Content -LiteralPath $templatePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $migrationSource = [IO.File]::ReadAllText((Join-Path $projectRoot 'Infrastructure\Access\Schema\AccessSchemaDefinition.cs'))
    $migration = [regex]::Match($migrationSource,
        'public const string MigrationId\s*=\s*"(?<name>[^"\r\n]+)"')
    if (-not $migration.Success) { throw 'Could not identify the compiled schema migration.' }
    $manifest.status = 'candidate-unverified'
    $manifest.sourceCommit = $commit
    $manifest.builtAtUtc = [DateTime]::UtcNow.ToString('O')
    $manifest.schemaMigrationId = $migration.Groups['name'].Value
    foreach ($entry in $manifest.artifacts.PSObject.Properties) {
        $relative = [string]$entry.Value.relativePath
        if ([IO.Path]::IsPathRooted($relative) -or
            $relative.Split([char[]]@('/', '\')) -contains '..') {
            throw "Unsafe artifact path in the manifest template: $relative"
        }
        $path = [IO.Path]::GetFullPath((Join-Path $projectRoot $relative))
        if (-not $path.StartsWith($projectRoot.TrimEnd('\') + '\',
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "The Release artifact is missing: $relative"
        }
        $entry.Value.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    # Inventory all executable dependencies and served static assets as well
    # as the named entry points in the template.
    $buildRoots = @('bin/Release/net10.0-windows',
        'WritingVault.Client/bin/Release/net10.0-windows',
        'WritingVault.Web/bin/Release/net10.0-windows',
        'WritingVault.Tray/bin/Release/net10.0-windows')
    $buildFiles = foreach ($relativeRoot in $buildRoots) {
        $absoluteRoot = Join-Path $projectRoot $relativeRoot
        if (-not (Test-Path -LiteralPath $absoluteRoot -PathType Container)) {
            throw "Release output directory is missing: $relativeRoot"
        }
        Get-ChildItem -LiteralPath $absoluteRoot -File -Recurse | ForEach-Object {
            [ordered]@{
                relativePath = ($_.FullName.Substring($projectRoot.TrimEnd('\').Length + 1) -replace '\\', '/')
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        }
    }
    $manifest | Add-Member -NotePropertyName buildFiles -NotePropertyValue @($buildFiles | Sort-Object relativePath)
    New-Item -ItemType Directory -Path (Split-Path -Parent $outputPath) -Force | Out-Null
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $outputPath -Encoding UTF8
    Write-Output ([ordered]@{ manifest = $outputPath; sourceCommit = $commit;
        schemaMigrationId = $manifest.schemaMigrationId;
        artifactCount = @($manifest.artifacts.PSObject.Properties).Count;
        buildFileCount = @($manifest.buildFiles).Count } |
        ConvertTo-Json -Compress)
}
finally { Pop-Location }
