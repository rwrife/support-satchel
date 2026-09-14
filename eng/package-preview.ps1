param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-z0-9-]+$')]
    [string] $PlatformLabel,

    [string] $Configuration = 'Release',

    [string] $OutputDirectory = 'artifacts'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = if ([IO.Path]::IsPathFullyQualified($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
$bundleRoot = Join-Path $artifactRoot "support-satchel-preview-$PlatformLabel"
$archivePath = "$bundleRoot.zip"
$checksumPath = "$archivePath.sha256"

if (Test-Path $bundleRoot) {
    Remove-Item $bundleRoot -Recurse -Force
}
if (Test-Path $archivePath) {
    Remove-Item $archivePath -Force
}
if (Test-Path $checksumPath) {
    Remove-Item $checksumPath -Force
}

New-Item (Join-Path $bundleRoot 'app') -ItemType Directory -Force | Out-Null
New-Item (Join-Path $bundleRoot 'cli') -ItemType Directory -Force | Out-Null

& dotnet publish (Join-Path $repoRoot 'src/SupportSatchel.App/SupportSatchel.App.csproj') `
    --configuration $Configuration --no-restore --no-build `
    --output (Join-Path $bundleRoot 'app')
if ($LASTEXITCODE -ne 0) {
    throw 'Desktop preview publish failed.'
}

& dotnet publish (Join-Path $repoRoot 'src/SupportSatchel.Cli/SupportSatchel.Cli.csproj') `
    --configuration $Configuration --no-restore --no-build `
    --output (Join-Path $bundleRoot 'cli')
if ($LASTEXITCODE -ne 0) {
    throw 'CLI preview publish failed.'
}

$cliDll = Join-Path $bundleRoot 'cli/SupportSatchel.Cli.dll'
& dotnet $cliDll --version
if ($LASTEXITCODE -ne 0) {
    throw 'Published CLI smoke failed.'
}

$required = @(
    (Join-Path $bundleRoot 'app/SupportSatchel.App.dll'),
    (Join-Path $bundleRoot 'app/SupportSatchel.Core.dll'),
    (Join-Path $bundleRoot 'cli/SupportSatchel.Cli.dll'),
    (Join-Path $bundleRoot 'cli/SupportSatchel.Core.dll')
)
foreach ($file in $required) {
    if (-not (Test-Path $file -PathType Leaf)) {
        throw "Published preview is incomplete: $([IO.Path]::GetFileName($file))"
    }
}

Compress-Archive -Path (Join-Path $bundleRoot '*') -DestinationPath $archivePath
$hash = (Get-FileHash $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($archivePath))" |
    Set-Content -Path $checksumPath -Encoding ascii -NoNewline

Write-Host "Preview archive: $archivePath"
Write-Host "SHA-256: $hash"
