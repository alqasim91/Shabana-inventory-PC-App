<#
.SYNOPSIS
    Restore the database from a backup dump. Interactive by default —
    lists available backups, confirms before doing anything destructive.

.NOTES
    BUILD_PLAN.md item #2. This is the script a shop owner runs after a
    bad upgrade or a corrupted drive, with no one else around. It must be
    unambiguous about what it's about to overwrite.
#>

[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\ProgramData\Shabana',
    [string]$BackupDir = (Join-Path $InstallDir 'backups'),
    [string]$DumpFile,
    # For automated rollback callers only (migrate.ps1) — skips the
    # interactive confirmation, which would otherwise hang forever waiting
    # for input that will never come on an unattended failure. Never pass
    # this when a human is running the script by hand.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$pgBin = Join-Path $InstallDir 'bin\pg\bin'
$pgCtl = Join-Path $pgBin 'pg_ctl.exe'

if (-not $DumpFile) {
    $dumps = Get-ChildItem -Path $BackupDir -Filter 'shabana-*.dump' | Sort-Object LastWriteTime -Descending
    if ($dumps.Count -eq 0) { throw "No backups found in $BackupDir" }

    Write-Host "Available backups (most recent first):"
    for ($i = 0; $i -lt $dumps.Count; $i++) {
        Write-Host "  [$i] $($dumps[$i].Name)  ($($dumps[$i].LastWriteTime))"
    }
    $choice = Read-Host "Enter number to restore (default 0 = most recent), or Ctrl+C to cancel"
    if ([string]::IsNullOrWhiteSpace($choice)) { $choice = 0 }
    $DumpFile = $dumps[[int]$choice].FullName
}

Write-Host ""
Write-Host "This will REPLACE ALL CURRENT DATA with the contents of:"
Write-Host "  $DumpFile"
Write-Host "Everything recorded since that backup was made will be lost."
if (-not $Force) {
    $confirm = Read-Host "Type YES (in capitals) to continue"
    if ($confirm -ne 'YES') {
        Write-Host "Cancelled — no changes made."
        return
    }
} else {
    Write-Host "(-Force: proceeding without confirmation — automated rollback)"
}

Write-Host "Stopping application services..."
Stop-Service -Name 'ShabanaCaddy', 'ShabanaGoTrue', 'ShabanaPostgREST' -ErrorAction SilentlyContinue

$dbPassword = Get-Content (Join-Path $InstallDir 'config\db-password.key') -Raw
$env:PGPASSWORD = $dbPassword
try {
    Write-Host "Dropping and recreating database..."
    & (Join-Path $pgBin 'psql.exe') -U postgres -h 127.0.0.1 -d postgres -c "select pg_terminate_backend(pid) from pg_stat_activity where datname = 'postgres' and pid <> pg_backend_pid();"
    & (Join-Path $pgBin 'dropdb.exe') -U postgres -h 127.0.0.1 postgres --if-exists
    & (Join-Path $pgBin 'createdb.exe') -U postgres -h 127.0.0.1 postgres

    Write-Host "Restoring from dump..."
    & (Join-Path $pgBin 'pg_restore.exe') -U postgres -h 127.0.0.1 -d postgres $DumpFile
    if ($LASTEXITCODE -ne 0) { throw "pg_restore reported errors — check output above before trusting this restore." }
} finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

Write-Host "Restarting application services..."
Start-Service -Name 'ShabanaPostgREST', 'ShabanaGoTrue', 'ShabanaCaddy'

Write-Host "Restore complete from: $DumpFile"
