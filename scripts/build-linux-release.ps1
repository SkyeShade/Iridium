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

New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
foreach ($path in @($stagingRoot, $webPublishOutput)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

try {
    New-Item -ItemType Directory -Force -Path $serverOutput, $webOutput | Out-Null

    Write-Host "[Iridium] Publishing Blazor WebAssembly client..."
    Invoke-Checked -FilePath dotnet -Arguments @(
        "publish", (Join-Path $repositoryRoot "Iridium.Web/Iridium.Web.csproj"),
        "-c", "Release", "-m:1", "-p:DebugType=None", "-p:DebugSymbols=false",
        "-o", $webPublishOutput)

    $publishedWebRoot = Join-Path $webPublishOutput "wwwroot"
    if (-not (Test-Path -LiteralPath (Join-Path $publishedWebRoot "index.html"))) {
        throw "The Blazor publish output does not contain wwwroot/index.html."
    }
    $mediaManifestPath = Join-Path $publishedWebRoot "media-build.json"
    if (-not (Test-Path -LiteralPath $mediaManifestPath)) {
        throw "The Blazor publish output does not contain media-build.json."
    }
    $mediaBuildId = (Get-Content -LiteralPath $mediaManifestPath -Raw | ConvertFrom-Json).buildId
    if ([string]::IsNullOrWhiteSpace($mediaBuildId)) {
        throw "The generated media build identifier is empty."
    }
    $assemblyInfo = Join-Path $repositoryRoot "Iridium.Web/obj/Release/net10.0/Iridium.Web.AssemblyInfo.cs"
    if (-not (Test-Path -LiteralPath $assemblyInfo) -or
        -not (Select-String -LiteralPath $assemblyInfo -SimpleMatch "IridiumMediaBuildId`", `"$mediaBuildId" -Quiet)) {
        throw "The WASM assembly metadata does not match media-build.json."
    }
    $voiceModule = Get-Content -LiteralPath (Join-Path $publishedWebRoot "js/voiceCall.js") -Raw
    if ($voiceModule -notmatch "requireMatchingMediaBuild\(mediaBuildId\)" -or $voiceModule -match "screen-v1") {
        throw "The published voice module is not using the generated media build identifier."
    }
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
    Write-Host "[Iridium] Release ready: $archivePath"
}
finally {
    foreach ($path in @($stagingRoot, $webPublishOutput)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}
