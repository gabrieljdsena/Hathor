# Build, sign, copy and optionally publish a Hathor Android release.
#
#   .\scripts\release.ps1
#   .\scripts\release.ps1 -Destination \\192.168.15.22\HathorAPK
#
# The release keystore and passwords live in apps/mobile/keystore.properties
# (gitignored). The server selects the highest versionCode from its APK folder.
param(
    [string]$Destination = ""
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host "==> Building release APK"
    & .\gradlew.bat :app:assembleRelease --console=plain
    if ($LASTEXITCODE -ne 0) { throw "Gradle release build failed." }

    $gradleFile = Join-Path $root 'app\build.gradle.kts'
    $gradle = Get-Content $gradleFile -Raw
    $codeMatch = [regex]::Match($gradle, 'versionCode\s*=\s*(\d+)')
    $nameMatch = [regex]::Match($gradle, 'versionName\s*=\s*"([^"]+)"')
    if (-not $codeMatch.Success -or -not $nameMatch.Success) {
        throw "Could not read versionCode/versionName from $gradleFile"
    }
    $versionCode = $codeMatch.Groups[1].Value
    $versionName = $nameMatch.Groups[1].Value

    $apk = Join-Path $root 'app\build\outputs\apk\release\app-release.apk'
    if (-not (Test-Path $apk)) { throw "Release APK not found: $apk" }

    $sdk = $env:ANDROID_HOME
    if (-not $sdk) { $sdk = $env:ANDROID_SDK_ROOT }
    if (-not $sdk) { $sdk = 'C:\Android\Sdk' }
    $buildTools = Get-ChildItem (Join-Path $sdk 'build-tools') -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if ($buildTools) {
        $apksigner = Join-Path $buildTools.FullName 'apksigner.bat'
        if (Test-Path $apksigner) {
            Write-Host "==> Verifying signature"
            & $apksigner verify --print-certs $apk
            if ($LASTEXITCODE -ne 0) { throw "APK signature verification failed." }
        }
    } else {
        Write-Warning "Android build-tools not found; skipping apksigner verification."
    }

    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Force -Path $dist -ErrorAction Stop | Out-Null
    $fileName = "hathor-$versionCode-$versionName.apk"
    $out = Join-Path $dist $fileName
    Copy-Item -Force $apk $out -ErrorAction Stop
    $sha = (Get-FileHash $out -Algorithm SHA256).Hash.ToLowerInvariant()

    Write-Host ""
    Write-Host "APK:       $out"
    Write-Host "Version:   $versionCode / $versionName"
    Write-Host "Size:      $((Get-Item $out).Length) bytes"
    Write-Host "SHA-256:   $sha"
    Write-Host "File name: $fileName"

    if ($Destination) {
        if ($Destination.StartsWith('\\') -and -not (Test-Path $Destination)) {
            Write-Warning "SMB share '$Destination' is not available."
            Write-Warning "Create it on the server with apps/web/scripts/share-apk.ps1, then rerun with -Destination."
            Write-Host "APK left in: $out"
        } else {
            try {
                if (-not (Test-Path $Destination)) {
                    New-Item -ItemType Directory -Force -Path $Destination -ErrorAction Stop | Out-Null
                }
                $published = Join-Path $Destination $fileName
                Copy-Item -Force $out $published -ErrorAction Stop
                Write-Host "Published: $published"
            } catch {
                Write-Warning "Could not publish to '$Destination': $($_.Exception.Message)"
                Write-Host "APK left in: $out"
            }
        }
    }
} finally {
    Pop-Location
}
