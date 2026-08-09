<#
.SYNOPSIS
    Nightly pg_dump with retention. Run silently by Task Scheduler; can
    also be run manually. Pass -Register once (done by the installer) to
    create the scheduled task itself.

.NOTES
    BUILD_PLAN.md item #2. A dump on the same physical disk as the live
    database is not a backup — it dies with the drive. The setup screen
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
        -Description 'Nightly database backup for مخزون شبانة (PC edition)' `
        -Force | Out-Null

    Write-Host 'Nightly backup task registered (03:00 daily).'
    return
}

if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null }

$pgBin = Join-Path $InstallDir 'bin\pg\bin'
$dbPassword = Get-Content (Join-Path $InstallDir 'config\db-password.key') -Raw
$stamp = (Get-Date).ToString('yyyy-MM-dd_HHmmss')
$outFile = Join-Path $BackupDir "shabana-$stamp.dump"

$env:PGPASSWORD = $dbPassword
try {
    & (Join-Path $pgBin 'pg_dump.exe') -U postgres -h 127.0.0.1 -Fc -f $outFile postgres
    if ($LASTEXITCODE -ne 0) { throw 'pg_dump failed' }
} finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

# A dump the app can't read is worse than no dump — it's a false sense of
# safety. Record success/failure where the dashboard's "last backup" badge
# (see task: last-backup indicator) can find it.
$statusFile = Join-Path $BackupDir 'last-backup-status.json'
$sizeOk = (Get-Item $outFile).Length -gt 1024
$status = @{
    timestamp = (Get-Date).ToString('o')
    file      = $outFile
    success   = $sizeOk
} | ConvertTo-Json
Set-Content -Path $statusFile -Value $status -Encoding UTF8

if (-not $sizeOk) { throw "Backup file suspiciously small — treating as failed: $outFile" }

# Retention: delete dumps older than $RetentionDays.
Get-ChildItem -Path $BackupDir -Filter 'shabana-*.dump' |
    Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$RetentionDays) } |
    Remove-Item -Force

Write-Host "Backup complete: $outFile"
