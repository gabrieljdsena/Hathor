# Publishes the persistent APK drop folder as an SMB share so a build PC can
# copy sideload releases without RDP. Run elevated on the Hathor server.
#
#   .\scripts\share-apk.ps1 -Account 'WORKPC\gabriel'
#   .\scripts\share-apk.ps1 -Account 'WORKPC\gabriel' -ShareName HathorAPK
#
# Only the given account gets access (the APK embeds the build-time API
# token, so do not share it with Everyone). The Hathor service itself reads
# the folder from disk as LocalSystem and does not need the share.
param(
    [Parameter(Mandatory = $true)]
    [string]$Account,
    [string]$ShareName = 'HathorAPK',
    [string]$ApkDirectory = (Join-Path $env:PROGRAMDATA 'Hathor\apk')
)

$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Path $ApkDirectory -Force | Out-Null

$share = Get-SmbShare -Name $ShareName -ErrorAction SilentlyContinue
if ($share) {
    if ($share.Path -ne $ApkDirectory) {
        throw "Share '$ShareName' already exists at '$($share.Path)', expected '$ApkDirectory'. Remove it first or pass another -ShareName."
    }
    Write-Host "[skip] share '$ShareName' already exists"
}
else {
    New-SmbShare -Name $ShareName -Path $ApkDirectory -FullAccess $Account | Out-Null
    Write-Host "[ok] share '$ShareName' created"
}

$grant = Get-SmbShareAccess -Name $ShareName |
    Where-Object { $_.AccountName -eq $Account }
if (-not $grant) {
    Grant-SmbShareAccess -Name $ShareName -AccountName $Account -AccessRight Full -Force | Out-Null
    Write-Host "[ok] granted Full to $Account"
}

Write-Host "[ok] drop releases in:"
Write-Host "     $ApkDirectory"
Write-Host "     \\$env:COMPUTERNAME\$ShareName"
