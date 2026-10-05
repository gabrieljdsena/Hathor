# Hathor web production build: frontend -> API wwwroot -> publish folder.
# Stops the Windows service first (it locks the publish output), restarts
# it at the end unless -NoRestart. Then the UI + API serve same-origin:
#   Restart-Service Hathor
param([switch]$NoRestart)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts\publish'
$wwwroot = Join-Path $root 'src\Hathor.Api\wwwroot'
$serviceWasRunning = $false

Write-Host "[build] frontend (npm run build)"
Push-Location "$root\frontend\hathor-web"
npm run build
if ($LASTEXITCODE -ne 0) { throw "frontend build failed (exit $LASTEXITCODE)" }
Pop-Location

Write-Host "[build] refreshing API wwwroot (drop stale hashed assets)"
Remove-Item "$wwwroot\*" -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null
Copy-Item "$root\frontend\hathor-web\dist\*" $wwwroot -Recurse -Force

Write-Host "[build] backend publish -> $publishDir"
$svc = Get-Service Hathor -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq 'Running') {
    Write-Host "[build] stopping Hathor service (releases publish locks)"
    Stop-Service Hathor -Force
    $serviceWasRunning = $true
}
Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\src\Hathor.Api\Hathor.Api.csproj" -c Release -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "backend publish failed (exit $LASTEXITCODE)" }

if ($serviceWasRunning -and -not $NoRestart) {
    Start-Service Hathor
    Write-Host "[build] service restarted."
}
Write-Host "[build] done."
Write-Host "  UI + API then serve same-origin (no vite/nginx needed)."
