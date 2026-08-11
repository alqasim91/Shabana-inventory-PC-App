<#
.SYNOPSIS
    Turn an off-site .dump.enc back into a .dump that restore.ps1 can read.

.DESCRIPTION
    The counterpart to the encryption in backup.ps1. Run it on the file you
    downloaded from your cloud storage or copied off the NAS; hand the result
    to restore.ps1.

    THE CASE THIS IS FOR
    The shop PC is gone - dead drive, stolen, burnt - and all you have is the
    cloud copy and the passphrase you wrote down. So this script deliberately
    depends on NOTHING from an install: no config folder, no database, no
    services. Copy this file and lib-crypto.ps1 onto any Windows machine and it
    works.

    If the old PC is still alive, -Passphrase can be omitted and it is read
    from config\offsite.key.

.EXAMPLE
    .\decrypt-backup.ps1 -InFile D:\shabana-2026-08-11_030000.dump.enc
    # then, on a fresh install:
    .\restore.ps1 -DumpFile D:\shabana-2026-08-11_030000.dump
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InFile,
    [string]$OutFile,
    [string]$Passphrase,
    [string]$InstallDir = 'C:\ProgramData\Shabana'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib-crypto.ps1')

if (-not (Test-Path $InFile)) { throw "Not found: $InFile" }

if (-not $Passphrase) {
    $keyFile = Join-Path $InstallDir 'config\offsite.key'
    if (Test-Path $keyFile) {
        $Passphrase = (Get-Content $keyFile -Raw).Trim()
        Write-Host "Using the passphrase stored on this machine ($keyFile)."
    } else {
        $secure = Read-Host -Prompt 'Recovery passphrase' -AsSecureString
        $Passphrase = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    }
}
if (-not $Passphrase) { throw 'No passphrase given.' }

if (-not $OutFile) {
    $OutFile = if ($InFile -like '*.enc') { $InFile.Substring(0, $InFile.Length - 4) } else { "$InFile.dump" }
}
if (Test-Path $OutFile) {
    throw "$OutFile already exists - move it aside first. Refusing to overwrite a backup."
}

Write-Host "Decrypting $InFile ..."
# Unprotect-ShbFile verifies the MAC before writing anything, so a wrong
# passphrase or a corrupted download fails with no partial file left behind.
Unprotect-ShbFile -InFile $InFile -OutFile $OutFile -Passphrase $Passphrase

$size = (Get-Item $OutFile).Length
Write-Host ''
Write-Host "Decrypted to: $OutFile  ($([math]::Round($size / 1MB, 1)) MB)"
Write-Host 'Restore it with:'
Write-Host "  .\restore.ps1 -DumpFile `"$OutFile`""
