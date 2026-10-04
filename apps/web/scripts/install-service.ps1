# Registers Hathor web (API + SPA, same origin) as a Windows service.
# Run from an elevated PowerShell. HTTP only (LAN use); no cert handling.
#   .\scripts\install-service.ps1
#   .\scripts\install-service.ps1 -Uninstall
param(
    [string]$ServiceName = 'Hathor',
    [int]$Port = 5050,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts\publish'
$dll = Join-Path $publishDir 'Hathor.Api.dll'

if ($Uninstall) {
    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
        sc.exe delete $ServiceName | Out-Null
        Write-Host "[ok] service '$ServiceName' removed"
    }
    else {
        Write-Host "[skip] service '$ServiceName' not installed"
    }
    return
}

if (-not (Test-Path $dll)) {
    throw "Published output not found. Run .\scripts\build.ps1 first (missing: $dll)"
}

if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Production', 'Machine')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', "http://0.0.0.0:$Port", 'Machine')

    $jwt = [Environment]::GetEnvironmentVariable('JWT_KEY', 'User')
    if (-not $jwt) { $jwt = [Environment]::GetEnvironmentVariable('JWT_KEY', 'Process') }
    if (-not $jwt) {
        $jwt = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 48 | ForEach-Object { [char]$_ })
        Write-Host "[ok] generated random JWT_KEY (old sessions/tokens are void)"
    }
    [Environment]::SetEnvironmentVariable('JWT_KEY', $jwt, 'Machine')
    Write-Host "[ok] JWT_KEY pinned as machine env"

    $dbPassword = [Environment]::GetEnvironmentVariable('HATHOR_DB_PASSWORD', 'User')
    if (-not $dbPassword) { $dbPassword = [Environment]::GetEnvironmentVariable('HATHOR_DB_PASSWORD', 'Process') }
    if ($dbPassword) {
        [Environment]::SetEnvironmentVariable('HATHOR_DB_PASSWORD', $dbPassword, 'Machine')
        Write-Host "[ok] DB password pinned as machine env (HATHOR_DB_PASSWORD)"
    }
    else {
        Write-Host "[warn] HATHOR_DB_PASSWORD not found - service may fail to reach Postgres"
    }

    $binPath = "dotnet `"$dll`""
    New-Service -Name $ServiceName -BinaryPathName $binPath -StartupType Automatic -DisplayName 'Hathor Music' | Out-Null

    Write-Host "[ok] service '$ServiceName' registered (http://localhost:$Port)"
}
else {
    Write-Host "[skip] service '$ServiceName' already registered"
}

# LAN access: the service binds 0.0.0.0, but Windows Firewall drops inbound
# LAN traffic by default. Profile Any (same as the MyHomeLab 443/8080 rules):
# the host's interfaces run Public, so a Private-only rule would not apply.
# Idempotent — refreshes the rule on every install.
if (-not (Get-NetFirewallRule -DisplayName 'Hathor' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName 'Hathor' -Direction Inbound `
        -Protocol TCP -LocalPort $Port -Action Allow -Profile Any | Out-Null
}
else {
    Set-NetFirewallRule -DisplayName 'Hathor' -Protocol TCP -LocalPort $Port `
        -Action Allow -Profile Any
}
Write-Host "[ok] firewall open for TCP $Port (any profile)"

Start-Service $ServiceName -ErrorAction SilentlyContinue
Write-Host "[ok] service '$ServiceName' started"
