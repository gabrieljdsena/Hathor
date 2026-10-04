# Hathor web production build: frontend -> API wwwroot -> publish folder.
# Then restart the Windows service (see scripts/install-service.ps1):
#   Restart-Service Hathor
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts\publish'
$wwwroot = Join-Path $root 'src\Hathor.Api\wwwroot'

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
Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\src\Hathor.Api\Hathor.Api.csproj" -c Release -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "backend publish failed (exit $LASTEXITCODE)" }

Write-Host "[build] done."
Write-Host "  Restart the service: Restart-Service Hathor"
Write-Host "  UI + API then serve same-origin (no vite/nginx needed)."
