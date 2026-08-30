<#
.SYNOPSIS
    Builds the distributable zip that gets attached to a GitHub release.

.DESCRIPTION
    Publishes Consolify self-contained for win-x64 as a single file, so the download is one
    exe plus the ui folder and nothing has to be installed first -- no .NET runtime, no
    unpacking a folder of two hundred DLLs. The WebView2 runtime is the one thing that cannot
    be bundled; Windows 11 ships it, and the app says so plainly if it is missing.

    Output: dist\Consolify-v<version>-win-x64.zip

.PARAMETER Version
    Overrides the version baked into the exe and the zip name. Defaults to <Version> in the
    csproj. Pass the tag you are about to push, without the "v".

.EXAMPLE
    .\tools\package.ps1
    .\tools\package.ps1 -Version 1.1.0
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'Consolify\Consolify.csproj'
$staging = Join-Path $repo "artifacts\$Runtime"
$dist = Join-Path $repo 'dist'

if (-not $Version) {
    $csproj = [xml](Get-Content $project)
    $Version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw 'No <Version> in the csproj and none passed with -Version.' }

# A stale staging folder would ship files that are no longer part of the build.
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null
New-Item -ItemType Directory -Path $dist -Force | Out-Null

Write-Host "Publishing Consolify $Version ($Runtime, self-contained)..." -ForegroundColor Cyan
dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $staging `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# The UI is loaded off disk through a WebView2 virtual host mapping, so ui\ has to sit next to
# the exe -- it is the one thing single-file publishing does not swallow. Fail loudly if the
# csproj ever stops copying it, rather than shipping a zip that opens to a black screen.
foreach ($required in 'Consolify.exe', 'ui\index.html', 'ui\app.js', 'ui\app.css', 'ui\radial.js') {
    if (-not (Test-Path (Join-Path $staging $required))) { throw "Missing from the publish output: $required" }
}

# Nothing here is any use to someone running the app.
Get-ChildItem $staging -Include *.pdb, *.xml -Recurse | Remove-Item -Force

# Apache-2.0 section 4 wants the license and the NOTICE to travel with the distribution, not
# just sit in the repo.
foreach ($doc in "README.md", "LICENSE", "NOTICE", "CONTRIBUTING.md") {
    $path = Join-Path $repo $doc
    if (Test-Path $path) { Copy-Item $path $staging -Force }
}

$zip = Join-Path $dist "Consolify-v$Version-$Runtime.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal

$size = '{0:N1} MB' -f ((Get-Item $zip).Length / 1MB)
Write-Host ''
Write-Host "  $zip  ($size)" -ForegroundColor Green
Write-Host ''
Write-Host 'SHA-256:' (Get-FileHash $zip -Algorithm SHA256).Hash
