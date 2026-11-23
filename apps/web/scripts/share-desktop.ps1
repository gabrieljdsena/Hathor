# Publishes the persistent desktop update folder as an SMB share so a build
# PC can copy release zips without RDP. Run elevated on the Hathor server.
#
#   .\scripts\share-desktop.ps1 -Account 'WORKPC\gabriel'
#   .\scripts\share-desktop.ps1 -Account 'WORKPC\gabriel' -ShareName HathorDesktop
#
# Only the given account gets access: the package is the desktop app, and the
# server reads the same folder from disk as LocalSystem.
param(
    [Parameter(Mandatory = $true)]
    [string]$Account,
    [string]$ShareName = 'HathorDesktop',
    [string]$PackageDirectory = (Join-Path $env:PROGRAMDATA 'Hathor\desktop')
)

$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Path $PackageDirectory -Force | Out-Null

$share = Get-SmbShare -Name $ShareName -ErrorAction SilentlyContinue
if ($share) {
    if ($share.Path -ne $PackageDirectory) {
        throw "Share '$ShareName' already exists at '$($share.Path)', expected '$PackageDirectory'. Remove it first or pass another -ShareName."
    }
    Write-Host "[skip] share '$ShareName' already exists"
}
else {
    New-SmbShare -Name $ShareName -Path $PackageDirectory -FullAccess $Account | Out-Null
    Write-Host "[ok] share '$ShareName' created"
}

$grant = Get-SmbShareAccess -Name $ShareName |
    Where-Object { $_.AccountName -eq $Account }
if (-not $grant) {
    Grant-SmbShareAccess -Name $ShareName -AccountName $Account -AccessRight Full -Force | Out-Null
    Write-Host "[ok] granted Full to $Account"
}

Write-Host "[ok] drop desktop releases in:"
Write-Host "     $PackageDirectory"
Write-Host "     \\$env:COMPUTERNAME\$ShareName"
