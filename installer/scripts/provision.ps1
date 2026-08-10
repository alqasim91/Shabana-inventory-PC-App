<#
.SYNOPSIS
    First-run provisioning: initdb, platform bootstrap, app migrations,
    secret generation, config writing. Run once by the installer, and
    again (idempotently, where possible) if the customer needs to rebuild
    the database from scratch.

.NOTES
    Layout under $InstallDir (default C:\ProgramData\Shabana):
      bin\pg\ bin\postgrest\ bin\gotrue\ bin\caddy\   -- vendored binaries
      data\pg\                                        -- initdb target
      config\                                          -- generated, per-machine
      www\                                              -- built frontend
      backups\                                          -- pg_dump output
      logs\

    Install path is fixed under ProgramData, never a user profile - Windows
    usernames on these machines are often Arabic, and PostgreSQL on Windows
    does not reliably handle non-ASCII data directory paths. See
    BUILD_PLAN.md item #7.
#>

[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\ProgramData\Shabana'
)

$ErrorActionPreference = 'Stop'

$pgBin      = Join-Path $InstallDir 'bin\pg\bin'
$dataDir    = Join-Path $InstallDir 'data\pg'
$configDir  = Join-Path $InstallDir 'config'
$logsDir    = Join-Path $InstallDir 'logs'
$migrationsDir = Join-Path $PSScriptRoot '..\..\supabase\migrations'
$bootstrapSql  = Join-Path $PSScriptRoot '..\..\supabase\platform-bootstrap.sql'

foreach ($dir in @($dataDir, $configDir, $logsDir, (Join-Path $InstallDir 'backups'), (Join-Path $InstallDir 'public'))) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
}

# --- Secrets first ---------------------------------------------------------
# Must run before initdb: the generated DB password becomes the `postgres`
# superuser's own initial password (via --pwfile below), and every script
# in this repo (backup/restore/reset-admin/migrate) assumes ONE password,
# read from config\db-password.key, works for -U postgres everywhere.
# Generating it after initdb would mean either a second, undocumented
# password for the superuser, or a separate ALTER ROLE step that's easy to
# forget. See BUILD_PLAN.md item #1.

Write-Host 'Generating per-machine secrets and config...'
& (Join-Path $PSScriptRoot 'generate-secrets.ps1') -InstallDir $InstallDir

$dbPasswordFile = Join-Path $configDir 'db-password.key'
# The Postgres port generate-secrets chose (5432 unless it was in a Windows
# reserved range). postgresql.conf and every psql call below must use it.
$pgPort = (Get-Content (Join-Path $configDir 'pg-port.txt') -Raw).Trim()

# --- initdb -------------------------------------------------------------
# Fixed UTF8/C locale and explicit Cairo timezone at creation time - see
# BUILD_PLAN.md items #3 and #7. Never inherit whatever locale/timezone
# Windows happens to be set to.

if (-not (Test-Path (Join-Path $dataDir 'PG_VERSION'))) {
    Write-Host 'Running initdb...'
    & (Join-Path $pgBin 'initdb.exe') `
        --pgdata=$dataDir `
        --encoding=UTF8 `
        --locale=C `
        --username=postgres `
        --auth=scram-sha-256 `
        --pwfile=$dbPasswordFile
    if ($LASTEXITCODE -ne 0) { throw 'initdb failed' }
} else {
    Write-Host 'Data directory already initialized, skipping initdb.'
}

# Apply our Postgres settings ALWAYS (not only right after initdb): a data
# dir left behind by a failed earlier install already has PG_VERSION, so
# gating this on initdb would skip it and Postgres would keep trying the old
# (unbindable) port. Append the block only if there's no active `port =`
# line yet, so re-provisioning doesn't stack duplicates. Postgres uses the
# last value of any setting, so a stray duplicate would be harmless anyway.
$pgConf = Join-Path $dataDir 'postgresql.conf'
if (-not (Select-String -Path $pgConf -Pattern '^\s*port\s*=' -Quiet)) {
    Add-Content -Path $pgConf -Value @"

# --- Shabana PC provisioning ---
timezone = 'Africa/Cairo'
listen_addresses = '127.0.0.1'
port = $pgPort
"@
    Write-Host "Postgres configured on port $pgPort."
} else {
    Write-Host "postgresql.conf already has a port setting; leaving it."
}

# --- Start Postgres temporarily to run bootstrap + migrations ----------

Write-Host 'Starting Postgres (temporary, for provisioning)...'
$pgCtl = Join-Path $pgBin 'pg_ctl.exe'
& $pgCtl start -D $dataDir -l (Join-Path $logsDir 'pg-provision.log') -w
if ($LASTEXITCODE -ne 0) { throw 'Postgres failed to start for provisioning' }

$dbPassword = Get-Content $dbPasswordFile -Raw
$env:PGPASSWORD = $dbPassword
# The SQL files (platform-bootstrap + migrations) contain Arabic string
# literals. Without this, psql on Windows infers client_encoding from the
# console codepage (often WIN1252), so the UTF-8 bytes in those files reach
# the UTF-8 database mis-decoded - mangled Arabic, or an encoding error. The
# DB is UTF8 (initdb --encoding=UTF8), so pin the client to match.
$env:PGCLIENTENCODING = 'UTF8'

try {
    $psql = Join-Path $pgBin 'psql.exe'

    # Platform bootstrap: base roles (anon/authenticated/service_role/
    # authenticator/supabase_auth_admin), auth/storage schemas, required
    # extensions. Cloud Supabase provisions this automatically; a
    # self-host must do it explicitly, once, before the app's own
    # migrations run. Vendored from Supabase's own postgres repo - see
    # supabase/platform-bootstrap.sql header for provenance and the one
    # deliberate addition (the supabase_admin shim).
    if (-not (Test-Path $bootstrapSql)) {
        throw "platform-bootstrap.sql not found - see BUILD_PLAN.md. Provisioning cannot safely continue without it."
    }
    Write-Host 'Applying platform bootstrap...'
    & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -v ON_ERROR_STOP=1 -f $bootstrapSql
    if ($LASTEXITCODE -ne 0) { throw 'Platform bootstrap failed' }

    # PostgREST connects as `authenticator`, GoTrue as `supabase_auth_admin`
    # (see installer/config/postgrest.conf.template and gotrue.env.template)
    # - the bootstrap above creates both roles but sets no password on
    # either. Reusing the one generated DB password here keeps every script
    # in this repo (backup/restore/migrate/reset-admin) working off a
    # single value read from config\db-password.key, rather than tracking
    # a separate secret per role.
    Write-Host 'Setting role passwords...'
    $escapedPw = $dbPassword.Replace("'", "''")
    & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -v ON_ERROR_STOP=1 -c "alter role authenticator password '$escapedPw';"
    & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -v ON_ERROR_STOP=1 -c "alter role supabase_auth_admin password '$escapedPw';"
    & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -v ON_ERROR_STOP=1 -c "alter role supabase_storage_admin password '$escapedPw';"
    if ($LASTEXITCODE -ne 0) { throw 'Failed to set role passwords' }

    Write-Host 'Applying application migrations...'
    Get-ChildItem -Path $migrationsDir -Filter '*.sql' | Sort-Object Name | ForEach-Object {
        Write-Host "  -> $($_.Name)"
        & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -v ON_ERROR_STOP=1 -f $_.FullName
        if ($LASTEXITCODE -ne 0) { throw "Migration failed: $($_.Name)" }
    }

    # Record what's applied, for migrate.ps1 on future upgrades.
    & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -c "create table if not exists shabana_migrations (filename text primary key, applied_at timestamptz not null default now());"
    Get-ChildItem -Path $migrationsDir -Filter '*.sql' | Sort-Object Name | ForEach-Object {
        & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -c "insert into shabana_migrations (filename) values ('$($_.Name)') on conflict do nothing;"
    }
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    Write-Host 'Stopping temporary Postgres...'
    & $pgCtl stop -D $dataDir -m fast -w
}

# --- Services --------------------------------------------------------------

Write-Host 'Registering Windows services...'
& (Join-Path $PSScriptRoot 'register-services.ps1') -InstallDir $InstallDir

Write-Host 'Provisioning complete.'
