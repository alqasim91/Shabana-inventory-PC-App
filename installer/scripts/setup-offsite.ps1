<#
.SYNOPSIS
    Configure the off-site backup: where encrypted copies go, and the
    passphrase that unlocks them.

.DESCRIPTION
    The local nightly dump protects you from a bad migration or a mistake. It
    does NOT protect you from the drive dying, the PC being stolen, or a fire -
    because it is on that same PC. On this edition the PC holds the only copy
    of the business's records, so that gap is the largest single risk in the
    whole design. This closes it.

    Copies are encrypted on this machine before they leave it (AES-256, see
    lib-crypto.ps1), so the storage provider stores bytes it cannot read.

.PARAMETER Target
    Where to put encrypted copies. Either:
      - a folder path: another drive, a NAS, a mapped drive, a UNC share
        (\\server\backups). Needs no extra software.
      - an rclone remote in the form  remote:path  (gdrive:shabana,
        onedrive:backups, b2:bucket/shabana). Needs rclone on PATH -
        https://rclone.org/downloads/

.PARAMETER Passphrase
    Optional. If omitted a strong one is generated and shown once.

.PARAMETER KeepDays
    How long to keep encrypted copies at the destination. Default 30.
#>

[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\ProgramData\Shabana',
    [Parameter(Mandatory)][string]$Target,
    [string]$Passphrase,
    [int]$KeepDays = 30,
    [switch]$Disable
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib-crypto.ps1')

$configDir  = Join-Path $InstallDir 'config'
$configFile = Join-Path $configDir 'offsite.json'
$keyFile    = Join-Path $configDir 'offsite.key'

if ($Disable) {
    Remove-Item $configFile -ErrorAction SilentlyContinue
    Write-Host 'Off-site backup disabled. Local nightly backups continue.'
    Write-Host "The passphrase file is left in place ($keyFile) so existing copies stay readable."
    return
}

$isRclone = $Target -match '^[A-Za-z0-9_-]+:(?![\\/])'
if ($isRclone) {
    if (-not (Get-Command rclone.exe -ErrorAction SilentlyContinue)) {
        throw "Target looks like an rclone remote ('$Target') but rclone.exe is not on PATH. Install it from https://rclone.org/downloads/ and run 'rclone config' first."
    }
    & rclone.exe lsd $Target 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "rclone cannot reach '$Target'. Run 'rclone config' and check the remote name." }
} else {
    if (-not (Test-Path $Target)) {
        New-Item -ItemType Directory -Path $Target -Force | Out-Null
    }
    # Prove it is writable NOW, not at 3am in an unattended job nobody watches.
    $probe = Join-Path $Target ".shabana-write-test"
    try { Set-Content -Path $probe -Value 'ok' -Encoding ASCII; Remove-Item $probe -Force }
    catch { throw "Cannot write to '$Target': $_" }

    # A destination on the same physical disk is not off-site. It is worth
    # saying out loud rather than letting someone believe they are covered.
    $targetRoot = [System.IO.Path]::GetPathRoot((Resolve-Path $Target))
    $installRoot = [System.IO.Path]::GetPathRoot($InstallDir)
    if ($targetRoot -and $targetRoot -eq $installRoot) {
        Write-Host ''
        Write-Host '  WARNING: that folder is on the SAME DRIVE as the database.'
        Write-Host '  It protects you from mistakes, but not from the drive failing,'
        Write-Host '  the PC being stolen, or a fire. Prefer another drive or a cloud remote.'
        Write-Host ''
    }
}

$generated = $false
if (-not $Passphrase) {
    # 32 chars from a 32-symbol alphabet with no look-alikes (no O/0, I/l/1) -
    # this gets read off a screen and written on paper.
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $bytes = New-Object byte[] 32; $rng.GetBytes($bytes)
        $Passphrase = -join ($bytes | ForEach-Object { $alphabet[$_ % 32] })
    } finally { $rng.Dispose() }
    $generated = $true
}

Set-Content -Path $keyFile -Value $Passphrase -NoNewline -Encoding ASCII
# Readable only by SYSTEM and Administrators - the nightly task runs as SYSTEM.
try {
    $acl = Get-Acl $keyFile
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($who in @('SYSTEM', 'Administrators')) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $who, 'FullControl', 'Allow')))
    }
    Set-Acl -Path $keyFile -AclObject $acl
} catch {
    Write-Host "Note: could not tighten permissions on $keyFile ($_)"
}

@{ target = $Target; keep_days = $KeepDays; is_rclone = [bool]$isRclone } |
    ConvertTo-Json | Set-Content -Path $configFile -Encoding ASCII

Write-Host ''
Write-Host '============================================================'
Write-Host ' Off-site backup is configured.'
Write-Host "   Destination : $Target"
Write-Host "   Keep        : $KeepDays days"
Write-Host ''
if ($generated) {
    Write-Host ' RECOVERY PASSPHRASE - write this down NOW:'
    Write-Host ''
    Write-Host "     $Passphrase"
    Write-Host ''
}
Write-Host ' This passphrase is stored on THIS PC. If the PC is lost in the'
Write-Host ' same event that makes you need the backup - fire, theft, a dead'
Write-Host ' drive - the copies in the cloud CANNOT BE DECRYPTED without it.'
Write-Host ' Keep it somewhere that is not this building.'
Write-Host '============================================================'
Write-Host ''
Write-Host 'The next nightly backup will send its first encrypted copy.'
Write-Host "To send one now:  backup.ps1 -InstallDir `"$InstallDir`""
