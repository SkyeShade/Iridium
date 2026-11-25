[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Version,

    # Uploads the installer, its blockmap and latest.yml to the GitHub release v$Version, which must exist.
    [switch] $Upload
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$desktopRoot = Join-Path $repositoryRoot "Iridium.Desktop"
$distRoot = Join-Path $desktopRoot "dist"

function Invoke-Checked {
    param([Parameter(Mandatory)] [string] $FilePath, [Parameter(Mandatory)] [string[]] $Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($Arguments -join ' ')" }
}

Push-Location $desktopRoot
try {
    if (-not (Test-Path (Join-Path $desktopRoot "node_modules"))) {
        Write-Host "[Iridium Desktop] Installing dependencies..."
        Invoke-Checked "npm" @("ci", "--no-fund", "--no-audit")
    }
    Write-Host "[Iridium Desktop] Running tests..."
    Invoke-Checked "npm" @("test", "--silent")
    if (Test-Path $distRoot) { Remove-Item -Recurse -Force $distRoot }
    Write-Host "[Iridium Desktop] Building installer $Version..."
    # The release version is injected here so package.json doesn't need a commit per release.
    Invoke-Checked "npx" @("electron-builder", "--win", "--publish", "never", "-c.extraMetadata.version=$Version")
}
finally { Pop-Location }

$assets = @(
    (Join-Path $distRoot "Iridium-Setup-$Version.exe"),
    (Join-Path $distRoot "Iridium-Setup-$Version.exe.blockmap"),
    (Join-Path $distRoot "latest.yml")
)
foreach ($asset in $assets) {
    if (-not (Test-Path $asset)) { throw "Expected build output is missing: $asset" }
}
Write-Host "[Iridium Desktop] Installer ready: $($assets[0])"

if ($Upload) {
    Write-Host "[Iridium Desktop] Uploading to GitHub release v$Version..."
    Invoke-Checked "gh" (@("release", "upload", "v$Version") + $assets + @("--clobber", "-R", "SkyeShade/Iridium"))
}
