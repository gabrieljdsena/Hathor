<#
.SYNOPSIS
    Runs the Hathor mobile test suites.

.DESCRIPTION
    Without parameters: JVM unit tests (Robolectric) - no emulator needed.
    With -Device: also runs the Compose instrumentation tests.

    Device selection:
      -Serial <adb-serial>   Use an already-running device (default: emulator-5554).
      -Avd <name>            Boot that AVD first if it is not running.

    BlueStacks works for manual app installs (`adb install`), but its install
    hook kills the emulator when an instrumentation APK is installed, so the
    connected suite should use an AVD or a physical phone.

.EXAMPLE
    ./scripts/test.ps1
    ./scripts/test.ps1 -Device
    ./scripts/test.ps1 -Device -Avd hathor_api34
    ./scripts/test.ps1 -Device -Serial 127.0.0.1:5555   # BlueStacks (may crash)
#>
param(
    [switch]$Device,
    [string]$Serial = $env:ANDROID_SERIAL,
    [string]$Avd
)

$ErrorActionPreference = 'Continue'
$mobileRoot = Split-Path -Parent $PSScriptRoot
Push-Location $mobileRoot
try {
    $gradlew = if ($IsWindows -or $env:OS -eq 'Windows_NT') { '.\gradlew.bat' } else { './gradlew' }

    Write-Host '==> JVM unit tests (Robolectric)' -ForegroundColor Cyan
    $oldEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $gradlew testDebugUnitTest --console=plain 2>&1 | Out-Host
    $code = $LASTEXITCODE
    $ErrorActionPreference = $oldEap
    if ($code -ne 0) { exit $code }

    if (-not $Device) {
        Write-Host 'Unit tests passed. Use -Device for Compose tests on an emulator/phone.' -ForegroundColor Green
        return
    }

    $sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } elseif ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } else { $null }
    if (-not $sdk) { throw 'Set ANDROID_HOME (or ANDROID_SDK_ROOT) to your Android SDK path.' }
    $adb = Join-Path $sdk 'platform-tools\adb.exe'
    if (-not (Test-Path $adb)) { $adb = Join-Path $sdk 'platform-tools/adb' }
    if (-not (Test-Path $adb)) { throw "adb not found under $sdk\platform-tools" }

    if (-not $Serial) { $Serial = 'emulator-5554' }
    & $adb start-server 2>&1 | Out-Null
    & $adb connect $Serial 2>&1 | Out-Null
    $state = (& $adb -s $Serial get-state 2>&1 | Out-String).Trim()

    if ($state -ne 'device' -and $Avd) {
        $emulator = Join-Path $sdk 'emulator\emulator.exe'
        if (-not (Test-Path $emulator)) { $emulator = Join-Path $sdk 'emulator/emulator' }
        if (-not (Test-Path $emulator)) { throw "emulator not found under $sdk\emulator" }
        Write-Host "==> Booting AVD $Avd" -ForegroundColor Cyan
        Start-Process -FilePath $emulator -ArgumentList @(
            '-avd', $Avd, '-no-window', '-no-audio', '-no-boot-anim',
            '-gpu', 'swiftshader_indirect', '-no-snapshot'
        ) | Out-Null
        for ($i = 0; $i -lt 90; $i++) {
            Start-Sleep -Seconds 4
            $state = (& $adb -s $Serial get-state 2>&1 | Out-String).Trim()
            if ($state -eq 'device') {
                $booted = (& $adb -s $Serial shell getprop sys.boot_completed 2>&1 | Out-String).Trim()
                if ($booted -eq '1') { break }
            }
        }
    }

    if ($state -ne 'device') {
        throw "No usable device at $Serial (state: $state). Boot an AVD (-Avd name) or connect a phone/BlueStacks."
    }

    $env:ANDROID_SERIAL = $Serial
    Write-Host "==> Instrumented tests on $Serial" -ForegroundColor Cyan
    $oldEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $gradlew connectedDebugAndroidTest --console=plain 2>&1 | Out-Host
    $code = $LASTEXITCODE
    $ErrorActionPreference = $oldEap
    if ($code -ne 0) { exit $code }
    Write-Host 'All tests passed.' -ForegroundColor Green
}
finally {
    Pop-Location
}
