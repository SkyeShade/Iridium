[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string] $Version = "dev"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $repositoryRoot "release"
$stagingRoot = Join-Path $releaseRoot ".staging-linux-x64"
$serverOutput = Join-Path $stagingRoot "server"
$webPublishOutput = Join-Path $releaseRoot ".web-publish-linux-x64"
$webOutput = Join-Path $stagingRoot "web"
$archivePath = Join-Path $releaseRoot "iridium-linux-x64.tar.gz"
$archiveVerificationRoot = Join-Path $releaseRoot ".verify-linux-x64"
$legacySourceMediaManifest = Join-Path $repositoryRoot "Iridium.Web/wwwroot/media-build.json"
$mediaBuildId = [Guid]::NewGuid().ToString("N")

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    for ($attempt = 1; $attempt -le 2; $attempt++) {
        & $FilePath @Arguments
        if ($LASTEXITCODE -eq 0) { return }
        if ($attempt -eq 1 -and $FilePath -eq "dotnet") {
            Write-Warning "dotnet publish exited with $LASTEXITCODE; retrying once after the build host settles."
            Start-Sleep -Seconds 1
            continue
        }
        throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($Arguments -join ' ')"
    }
}

function Get-AssemblyMediaBuildId {
    param([Parameter(Mandatory)][string] $AssemblyPath)

    $verifierProject = Join-Path $repositoryRoot "scripts/MediaBuildVerifier/MediaBuildVerifier.csproj"
    $output = & dotnet run --project $verifierProject -c Release -- $AssemblyPath
    if ($LASTEXITCODE -ne 0) {
        throw "Media build verifier failed with exit code $LASTEXITCODE."
    }
    $lines = @($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($lines.Count -eq 0) { throw "The WASM assembly media build verifier returned no value." }
    $value = $lines[-1].Trim()
    if ([string]::IsNullOrWhiteSpace($value)) { throw "The WASM assembly media build identifier is empty." }
    return $value
}

function Assert-PublishedMediaBuild {
    param(
        [Parameter(Mandatory)][string] $WebRoot,
        [Parameter(Mandatory)][string] $ExpectedBuildId,
        [Parameter()][string] $AssemblyBuildId
    )

    $manifestPath = Join-Path $WebRoot "media-build.json"
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "The published web output does not contain media-build.json."
    }
    $manifestBuildId = (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).buildId
    $voicePath = Join-Path $WebRoot "js/voiceCall.js"
    $communityPath = Join-Path $WebRoot "js/communityVoiceMedia.js"
    $voiceModule = if (Test-Path -LiteralPath $voicePath) { Get-Content -LiteralPath $voicePath -Raw } else { "" }
    $communityModule = if (Test-Path -LiteralPath $communityPath) { Get-Content -LiteralPath $communityPath -Raw } else { "" }
    $voiceUsesRuntimeId = $voiceModule -match "requireMatchingMediaBuild\(mediaBuildId\)" -and
        $voiceModule -notmatch "screen-v1"
    $communityUsesRuntimeId = $communityModule -match "requireMatchingMediaBuild\(mediaBuildId\)" -and
        $communityModule -notmatch "screen-v1"

    $mismatch = $manifestBuildId -ne $ExpectedBuildId -or
        (-not [string]::IsNullOrWhiteSpace($AssemblyBuildId) -and $AssemblyBuildId -ne $ExpectedBuildId) -or
        -not $voiceUsesRuntimeId -or -not $communityUsesRuntimeId
    if ($mismatch) {
        Write-Host "Expected media build ID:       $ExpectedBuildId"
        Write-Host "WASM assembly media build ID:  $(if ($AssemblyBuildId) { $AssemblyBuildId } else { '<verified before archive>' })"
        Write-Host "media-build.json ID:           $manifestBuildId"
        Write-Host "voiceCall.js ID mechanism:     $(if ($voiceUsesRuntimeId) { 'runtime MediaBuildInfo ID' } else { 'INVALID' })"
        Write-Host "communityVoiceMedia.js:        $(if ($communityUsesRuntimeId) { 'runtime MediaBuildInfo ID' } else { 'INVALID' })"
        throw "Published media build identifiers are inconsistent."
    }
    return $manifestBuildId
}

New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
foreach ($path in @($stagingRoot, $webPublishOutput, $archiveVerificationRoot)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
if (Test-Path -LiteralPath $legacySourceMediaManifest) {
    Remove-Item -LiteralPath $legacySourceMediaManifest -Force
}

try {
    New-Item -ItemType Directory -Force -Path $serverOutput, $webOutput | Out-Null

    Write-Host "[Iridium] Media build ID: $mediaBuildId"
    Write-Host "[Iridium] Publishing Blazor WebAssembly client..."
    Invoke-Checked -FilePath dotnet -Arguments @(
        "publish", (Join-Path $repositoryRoot "Iridium.Web/Iridium.Web.csproj"),
        "-c", "Release", "-m:1", "-p:DebugType=None", "-p:DebugSymbols=false",
        "-p:IridiumMediaBuildId=$mediaBuildId",
        "-o", $webPublishOutput)

    $publishedWebRoot = Join-Path $webPublishOutput "wwwroot"
    if (-not (Test-Path -LiteralPath (Join-Path $publishedWebRoot "index.html"))) {
        throw "The Blazor publish output does not contain wwwroot/index.html."
    }
    $webAssembly = Join-Path $repositoryRoot "Iridium.Web/bin/Release/net10.0/Iridium.Web.dll"
    $assemblyBuildId = Get-AssemblyMediaBuildId -AssemblyPath $webAssembly
    Assert-PublishedMediaBuild -WebRoot $publishedWebRoot -ExpectedBuildId $mediaBuildId `
        -AssemblyBuildId $assemblyBuildId | Out-Null
    Get-ChildItem -LiteralPath $publishedWebRoot -Force | Copy-Item -Destination $webOutput -Recurse -Force

    Write-Host "[Iridium] Publishing self-contained Linux x64 server..."
    Invoke-Checked -FilePath dotnet -Arguments @(
        "publish", (Join-Path $repositoryRoot "Iridium.Server/Iridium.Server.csproj"),
        "-c", "Release", "-r", "linux-x64", "--self-contained", "true",
        "-m:1", "-p:DebugType=None", "-p:DebugSymbols=false", "-o", $serverOutput)

    $developmentSettings = Join-Path $serverOutput "appsettings.Development.json"
    if (Test-Path -LiteralPath $developmentSettings) {
        Remove-Item -LiteralPath $developmentSettings -Force
    }

    [ordered]@{
        version = $Version
        runtime = "linux-x64"
        serverPublish = "self-contained"
        mediaBuildId = $mediaBuildId
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stagingRoot "release.json") -Encoding utf8

    if (-not (Test-Path -LiteralPath (Join-Path $serverOutput "Iridium.Server"))) {
        throw "The server publish output does not contain Iridium.Server."
    }

    Write-Host "[Iridium] Creating $archivePath..."
    Invoke-Checked -FilePath tar -Arguments @("-czf", $archivePath, "-C", $stagingRoot, ".")
    New-Item -ItemType Directory -Force -Path $archiveVerificationRoot | Out-Null
    Invoke-Checked -FilePath tar -Arguments @("-xzf", $archivePath, "-C", $archiveVerificationRoot)
    $packagedWebRoot = Join-Path $archiveVerificationRoot "web"
    Assert-PublishedMediaBuild -WebRoot $packagedWebRoot -ExpectedBuildId $mediaBuildId `
        -AssemblyBuildId $assemblyBuildId | Out-Null
    $packagedRelease = Get-Content -LiteralPath (Join-Path $archiveVerificationRoot "release.json") -Raw |
        ConvertFrom-Json
    if ($packagedRelease.mediaBuildId -ne $mediaBuildId) {
        throw "Packaged release.json media build ID '$($packagedRelease.mediaBuildId)' does not match '$mediaBuildId'."
    }
    if (@(Get-ChildItem -LiteralPath (Join-Path $packagedWebRoot "_framework") -Filter "Iridium.Web*.wasm").Count -ne 1) {
        throw "The archive does not contain exactly one fingerprinted Iridium.Web WASM assembly."
    }
    Write-Host "[Iridium] Verified packaged media build ID: $mediaBuildId"
    Write-Host "[Iridium] Release ready: $archivePath"
}
finally {
    foreach ($path in @($stagingRoot, $webPublishOutput, $archiveVerificationRoot)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}
