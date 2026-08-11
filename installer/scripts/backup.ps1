<#
.SYNOPSIS
    Nightly pg_dump with retention. Run silently by Task Scheduler; can
    also be run manually. Pass -Register once (done by the installer) to
    create the scheduled task itself.

.NOTES
    BUILD_PLAN.md item #2. A dump on the same physical disk as the live
    database is not a backup - it dies with the drive. The setup screen
    should steer the owner toward a second drive or a USB stick for
    -BackupDir; this script does not enforce that, it just writes wherever
    it's told.
#>

[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\ProgramData\Shabana',
    [string]$BackupDir = (Join-Path $InstallDir 'backups'),
    [int]$RetentionDays = 14,
    [switch]$Register
)

$ErrorActionPreference = 'Stop'

if ($Register) {
    $action  = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -InstallDir `"$InstallDir`" -BackupDir `"$BackupDir`" -RetentionDays $RetentionDays"
    $trigger = New-ScheduledTaskTrigger -Daily -At 3:00AM
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopOnIdleEnd

    Register-ScheduledTask -TaskName 'ShabanaNightlyBackup' `
        -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
        -Description 'Nightly database backup for Shabana Inventory (PC edition)' `
        -Force | Out-Null

    Write-Host 'Nightly backup task registered (03:00 daily).'
    return
}

if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null }

$pgBin = Join-Path $InstallDir 'bin\pg\bin'
$dbPassword = Get-Content (Join-Path $InstallDir 'config\db-password.key') -Raw
$pgPortFile = Join-Path $InstallDir 'config\pg-port.txt'
$pgPort = if (Test-Path $pgPortFile) { (Get-Content $pgPortFile -Raw).Trim() } else { '5432' }
$stamp = (Get-Date).ToString('yyyy-MM-dd_HHmmss')
$outFile = Join-Path $BackupDir "shabana-$stamp.dump"

$env:PGPASSWORD = $dbPassword
try {
    & (Join-Path $pgBin 'pg_dump.exe') -U postgres -h 127.0.0.1 -p $pgPort -Fc -f $outFile postgres
    if ($LASTEXITCODE -ne 0) { throw 'pg_dump failed' }
} finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

# A dump the app can't read is worse than no dump - it's a false sense of
# safety. Record success/failure so the in-app "last backup" badge can warn
# when the job silently stops working.
$sizeOk = (Get-Item $outFile).Length -gt 1024

# --- Off-site copy ---------------------------------------------------------
# The dump above lives on the same machine as the database it came from, so it
# survives a bad migration and nothing else. This sends an encrypted copy
# somewhere the fire cannot reach. Configured by setup-offsite.ps1; absent
# until someone does that, and its absence must never fail the local backup.
#
# One direction only. This is a backup, not a sync: nothing is ever read back
# automatically, and the copy at the far end is never authoritative.
$offsite = [ordered]@{ configured = $false; success = $null; target = $null; error = $null }
$offsiteConfig = Join-Path $InstallDir 'config\offsite.json'
if ($sizeOk -and (Test-Path $offsiteConfig)) {
    $offsite.configured = $true
    try {
        $cfg = Get-Content $offsiteConfig -Raw | ConvertFrom-Json
        $offsite.target = $cfg.target
        $passphrase = (Get-Content (Join-Path $InstallDir 'config\offsite.key') -Raw).Trim()
        if (-not $passphrase) { throw 'offsite.key is empty - re-run setup-offsite.ps1.' }

        . (Join-Path $PSScriptRoot 'lib-crypto.ps1')
        $encFile = "$outFile.enc"
        Protect-ShbFile -InFile $outFile -OutFile $encFile -Passphrase $passphrase

        $encName = Split-Path -Leaf $encFile
        if ($cfg.is_rclone) {
            & rclone.exe copyto $encFile "$($cfg.target)/$encName" 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "rclone copy to $($cfg.target) failed (exit $LASTEXITCODE)" }
            # --min-age prunes by the remote's own timestamps; no local list needed.
            & rclone.exe delete $cfg.target --include 'shabana-*.dump.enc' --min-age "$($cfg.keep_days)d" 2>&1 | Out-Null
        } else {
            # Copy to a .part name and rename, so a run interrupted by the PC
            # being switched off cannot leave a truncated file that looks whole.
            $dest = Join-Path $cfg.target $encName
            Copy-Item -Path $encFile -Destination "$dest.part" -Force
            Move-Item -Path "$dest.part" -Destination $dest -Force
            Get-ChildItem -Path $cfg.target -Filter 'shabana-*.dump.enc' -ErrorAction SilentlyContinue |
                Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$cfg.keep_days) } |
                Remove-Item -Force -ErrorAction SilentlyContinue
        }

        # The encrypted copy is a transport artefact; the local dump stays
        # plaintext so restore.ps1 needs no passphrase on this machine.
        Remove-Item $encFile -Force -ErrorAction SilentlyContinue
        $offsite.success = $true
    } catch {
        # Deliberately not fatal. A dead internet connection or an unplugged NAS
        # must not throw away a good local backup - but it MUST be visible, so
        # it lands in the status file the dashboard badge reads.
        $offsite.success = $false
        $offsite.error = "$_"
        Remove-Item "$outFile.enc" -Force -ErrorAction SilentlyContinue
        Write-Host "Off-site copy FAILED: $_"
    }
}

# The status files are written as ASCII (see the note below), and both the
# destination path and an rclone error message can legitimately contain Arabic
# or other non-ASCII text. Fold it down here rather than betting on how this
# PowerShell version escapes it - a mangled byte in this file makes the
# dashboard's fetch().json() throw, and the badge then reports nothing at all.
function ConvertTo-AsciiSafe {
    param([string]$Text)
    if (-not $Text) { return $Text }
    return ($Text -replace '[^\x20-\x7E]', '?')
}
$offsite.target = ConvertTo-AsciiSafe $offsite.target
$offsite.error  = ConvertTo-AsciiSafe $offsite.error

$status = [ordered]@{
    timestamp = (Get-Date).ToString('o')
    file      = ConvertTo-AsciiSafe $outFile
    success   = $sizeOk
    offsite   = $offsite
} | ConvertTo-Json -Depth 5

# Two copies. One next to the dumps (for a human browsing the backup folder),
# and one at a FIXED, Caddy-served path under InstallDir\public that the
# Dashboard fetches at /pc/last-backup.json - fixed because BackupDir may be a
# USB stick the browser can't reach, and because it must survive an upgrade
# that replaces www\.
# ASCII, NOT `-Encoding UTF8`: PS 5.1 would write a BOM, and the browser's
# fetch().json() on /pc/last-backup.json throws on a leading BOM. The JSON is
# ASCII (timestamp, bool, an ASCII install path).
Set-Content -Path (Join-Path $BackupDir 'last-backup-status.json') -Value $status -Encoding ASCII
$publicDir = Join-Path $InstallDir 'public'
if (-not (Test-Path $publicDir)) { New-Item -ItemType Directory -Path $publicDir -Force | Out-Null }
Set-Content -Path (Join-Path $publicDir 'last-backup.json') -Value $status -Encoding ASCII

if (-not $sizeOk) { throw "Backup file suspiciously small - treating as failed: $outFile" }

# Retention: delete dumps older than $RetentionDays.
Get-ChildItem -Path $BackupDir -Filter 'shabana-*.dump' |
    Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$RetentionDays) } |
    Remove-Item -Force

Write-Host "Backup complete: $outFile"
