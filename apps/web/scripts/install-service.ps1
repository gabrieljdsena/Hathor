# Registers Hathor web (API + SPA, same origin) as a Windows service.
# Run from an elevated PowerShell. HTTP only (LAN use); no cert handling.
#   .\scripts\install-service.ps1
#   .\scripts\install-service.ps1 -Uninstall
param(
    [string]$ServiceName = 'Hathor',
    [int]$Port = 5051,
    # The service runs as LocalSystem, whose %APPDATA% is NOT yours — without
    # these the API would serve an empty library from the SYSTEM profile.
    # Defaults to this user's desktop folders (same ones the desktop app uses).
    [string]$SongsPath = (Join-Path $env:APPDATA 'musicPlayer'),
    [string]$PodcastsPath = (Join-Path $env:APPDATA 'musicPlayerPodcasts'),
    [string]$StorageRoot = (Join-Path $env:PROGRAMDATA 'Hathor'),
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

    # NOTE: never write ASPNETCORE_URLS at Machine scope — it is inherited
    # by EVERY .NET service on the box (it once hijacked Vroid Remote
    # Control from :5050 to Hathor's port). The bind address lives on this
    # service's own command line instead (--urls), refreshed below on every
    # install.

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

    $binPath = "dotnet `"$dll`" --urls http://0.0.0.0:$Port"
    New-Service -Name $ServiceName -BinaryPathName $binPath -StartupType Automatic -DisplayName 'Hathor Music' | Out-Null

    Write-Host "[ok] service '$ServiceName' registered (http://localhost:$Port)"
}
else {
    Write-Host "[skip] service '$ServiceName' already registered"
    # Refresh the bind address on every install (port moves without this).
    $binPath = "dotnet `"$dll`" --urls http://0.0.0.0:$Port"
    sc.exe config $ServiceName binPath= $binPath | Out-Null
    Write-Host "[ok] service '$ServiceName' bind refreshed (http://localhost:$Port)"
}

# Library paths refresh on every install (not just first registration):
# the service runs as LocalSystem, whose %APPDATA% is NOT yours — without
# these the API serves an empty library from the SYSTEM profile.
foreach ($dir in @($SongsPath, $PodcastsPath, $StorageRoot)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}
[Environment]::SetEnvironmentVariable('Library__SongsPath', $SongsPath, 'Machine')
[Environment]::SetEnvironmentVariable('Library__PodcastsPath', $PodcastsPath, 'Machine')
[Environment]::SetEnvironmentVariable('Database__StorageRoot', $StorageRoot, 'Machine')
Write-Host "[ok] library paths pinned as machine env:"
Write-Host "     songs=$SongsPath"
Write-Host "     podcasts=$PodcastsPath"
Write-Host "     storage=$StorageRoot"

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
