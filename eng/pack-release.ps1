param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.+][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/SubReel.Launcher.App/SubReel.Launcher.App.csproj'
$artifactRoot = Join-Path $repoRoot 'artifacts/velopack'
$publishDir = Join-Path $artifactRoot "publish/$Runtime"
$releaseDir = Join-Path $artifactRoot 'releases'

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw 'Velopack CLI is missing. Install the matching vpk tool with: dotnet tool install -g vpk --version 1.2.161'
}

New-Item -ItemType Directory -Force $publishDir, $releaseDir | Out-Null

dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

vpk pack --packId SubReel.Launcher --packVersion $Version `
    --packDir $publishDir --mainExe SubReel.Launcher.App.exe `
    --packTitle 'SubReel Launcher' --packAuthors SubReel `
    --runtime $Runtime --outputDir $releaseDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE" }

Write-Host "Release $Version is packaged in $releaseDir"
