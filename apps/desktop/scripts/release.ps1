# Build the Hathor desktop onedir app, package it for the in-app updater,
# and optionally publish it to the server's SMB drop folder.
#
#   .\scripts\release.ps1
#   .\scripts\release.ps1 -Destination \\DELL-SERVER1\HathorDesktop
#
# version.json is the single source of truth for the desktop update channel.
# Bump versionCode for every published build (the server picks the highest).
param(
    [string]$Destination = "",
    [switch]$SkipTests,
    [switch]$SkipTailwind
)

# Native tools (npx/PyInstaller) write warnings to stderr; PowerShell's
# Stop preference turns those into terminating errors even on success.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $versionPath = Join-Path $root 'version.json'
    if (-not (Test-Path $versionPath)) { throw "Missing version.json: $versionPath" }
    $version = Get-Content $versionPath -Raw | ConvertFrom-Json
    $versionCode = [int]$version.versionCode
    $versionName = [string]$version.versionName
    if ($versionCode -le 0) { throw "versionCode must be a positive integer." }
    if ($versionName -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') {
        throw "versionName may only contain letters, digits, dots, underscores and dashes."
    }

    if (-not $SkipTests) {
        Write-Host "==> Running desktop and JS tests"
        & python -m pytest -q
        if ($LASTEXITCODE -ne 0) { throw "Python tests failed." }
        & npm run test:js
        if ($LASTEXITCODE -ne 0) { throw "JS tests failed." }
    }

    if (-not $SkipTailwind) {
        Write-Host "==> Building Tailwind CSS"
        & npx --yes @tailwindcss/cli@4.3.3 -i "ui/input.css" -o "ui/output.css"
        if ($LASTEXITCODE -ne 0) { throw "Tailwind build failed." }
    }

    Write-Host "==> Building desktop executable"
    $dist = Join-Path $root 'dist'
    $appDir = Join-Path $dist 'Hathor'
    if (Test-Path $appDir) {
        Remove-Item -LiteralPath $appDir -Recurse -Force -ErrorAction Stop
    }
    & python -m PyInstaller music_player.spec --noconfirm --clean
    if ($LASTEXITCODE -ne 0) { throw "PyInstaller build failed." }
    $exe = Join-Path $appDir 'Hathor.exe'
    if (-not (Test-Path $exe)) { throw "Built executable not found: $exe" }

    # The installed copy reads version.json next to Hathor.exe. PyInstaller's
    # bundled copy lives under _internal as a fallback.
    Copy-Item -LiteralPath $versionPath -Destination (Join-Path $appDir 'version.json') -Force -ErrorAction Stop
    foreach ($forbidden in @('music_player.db', '.env', 'hathor.log')) {
        if (Test-Path (Join-Path $appDir $forbidden)) {
            throw "Refusing to package user data: $forbidden"
        }
    }

    $fileName = "hathor-desktop-$versionCode-$versionName.zip"
    $out = Join-Path $dist $fileName
    if (Test-Path $out) { Remove-Item -LiteralPath $out -Force -ErrorAction Stop }
    Write-Host "==> Packing $fileName"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $appDir,
        $out,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )

    $sha = (Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = (Get-Item -LiteralPath $out).Length

    Write-Host ""
    Write-Host "Package:   $out"
    Write-Host "Version:   $versionCode / $versionName"
    Write-Host "Size:      $size bytes"
    Write-Host "SHA-256:   $sha"
    Write-Host "File name: $fileName"

    if ($Destination) {
        if ($Destination.StartsWith('\\') -and -not (Test-Path $Destination)) {
            Write-Warning "SMB share '$Destination' is not available."
            Write-Warning "Create it on the server with apps/web/scripts/share-desktop.ps1."
            Write-Host "Package left in: $out"
        } else {
            try {
                if (-not (Test-Path $Destination)) {
                    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
                }
                $published = Join-Path $Destination $fileName
                Copy-Item -LiteralPath $out -Destination $published -Force
                Write-Host "Published: $published"
            } catch {
                Write-Warning "Could not publish to '$Destination': $($_.Exception.Message)"
                Write-Host "Package left in: $out"
            }
        }
    }
} finally {
    Pop-Location
}
