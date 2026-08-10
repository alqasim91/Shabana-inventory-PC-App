<#
.SYNOPSIS
    Applies any migrations from supabase/migrations/ not yet recorded in
    shabana_migrations, after taking a safety backup. Restores that backup
    automatically if any migration fails partway through.

.NOTES
    SECURITY/CORRECTNESS-CRITICAL - flagged in BUILD_PLAN.md item #6 for a
    dedicated review pass before this ships. This script runs unattended,
    on live customer financial data, on a machine with no one around to
    intervene if something goes wrong. The backup-then-restore-on-failure
    logic is the whole safety net; it needs to be verified to actually
    work (a restore tested once, by hand) before being trusted to run
    itself.

    Postgres major version is pinned deliberately (see BUILD_PLAN.md) -
    this script applies application migrations only, and must never
    attempt a pg_upgrade. If a future migration requires a Postgres major
    version bump, that is a deliberate, manually-supervised operation, not
    something this script should do on its own.
#>

[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\ProgramData\Shabana'
)

$ErrorActionPreference = 'Stop'

$pgBin = Join-Path $InstallDir 'bin\pg\bin'
$migrationsDir = Join-Path $PSScriptRoot '..\..\supabase\migrations'
$dbPassword = Get-Content (Join-Path $InstallDir 'config\db-password.key') -Raw
$pgPortFile = Join-Path $InstallDir 'config\pg-port.txt'
$pgPort = if (Test-Path $pgPortFile) { (Get-Content $pgPortFile -Raw).Trim() } else { '5432' }
$psql = Join-Path $pgBin 'psql.exe'

$env:PGPASSWORD = $dbPassword
# Migration files contain Arabic literals; pin the client encoding to the
# DB's UTF8 so psql doesn't mis-decode them via the console codepage. See
# provision.ps1 for the full rationale.
$env:PGCLIENTENCODING = 'UTF8'

function Get-AppliedMigrations {
    $rows = & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -t -A -c "select filename from shabana_migrations order by filename;"
    if ($LASTEXITCODE -ne 0) { throw "Could not read shabana_migrations - is the database reachable?" }
    return @($rows -split "`n" | Where-Object { $_.Trim() -ne '' })
}

try {
    $applied = Get-AppliedMigrations
    $allFiles = Get-ChildItem -Path $migrationsDir -Filter '*.sql' | Sort-Object Name
    $pending = $allFiles | Where-Object { $applied -notcontains $_.Name }

    if ($pending.Count -eq 0) {
        Write-Host "No pending migrations. Database is up to date."
        return
    }

    Write-Host "Pending migrations:"
    $pending | ForEach-Object { Write-Host "  - $($_.Name)" }

    # Safety backup BEFORE touching anything. If this fails, we abort -
    # applying migrations without a fresh restore point is not acceptable
    # on live customer data.
    Write-Host "Taking pre-upgrade backup..."
    & (Join-Path $PSScriptRoot 'backup.ps1') -InstallDir $InstallDir
    $preUpgradeBackups = Get-ChildItem -Path (Join-Path $InstallDir 'backups') -Filter 'shabana-*.dump' |
        Sort-Object LastWriteTime -Descending
    $safetyDump = $preUpgradeBackups[0].FullName
    Write-Host "Safety backup: $safetyDump"

    $failed = $false
    foreach ($file in $pending) {
        Write-Host "Applying $($file.Name)..."
        & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -v ON_ERROR_STOP=1 -f $file.FullName
        if ($LASTEXITCODE -ne 0) {
            Write-Host "MIGRATION FAILED: $($file.Name)"
            $failed = $true
            break
        }
        & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -c "insert into shabana_migrations (filename) values ('$($file.Name)');"
    }

    if ($failed) {
        Write-Host "Rolling back to pre-upgrade backup..."
        & (Join-Path $PSScriptRoot 'restore.ps1') -InstallDir $InstallDir -DumpFile $safetyDump -Force
        throw "Migration failed and was rolled back. Database is unchanged from before this upgrade. Do not retry without investigating the failed migration first."
    }

    Write-Host "All pending migrations applied successfully."
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}
