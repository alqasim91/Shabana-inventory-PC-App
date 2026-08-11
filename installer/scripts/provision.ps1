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

# Inno runs this in its own PowerShell window. Without this trap, any failure
# exits immediately and the window - the ONLY place the reason is ever shown -
# vanishes before it can be read, leaving the customer with a broken install
# and nothing to report but "it didn't work". Hold the window open on failure.
trap {
    Write-Host ''
    Write-Host '============================================================'
    Write-Host 'SETUP FAILED - the application was NOT installed correctly.'
    Write-Host ''
    Write-Host $_.Exception.Message
    Write-Host ''
    Write-Host "Logs are in: $InstallDir\logs"
    Write-Host 'Send them to support (Start Menu > Export problem report).'
    Write-Host '============================================================'
    if ($env:SHABANA_NONINTERACTIVE -ne '1') {
        Write-Host 'Press Enter to close this window...'
        try { Read-Host | Out-Null } catch { Start-Sleep -Seconds 120 }
    }
    exit 1
}

# Service command lines are handed to NSSM unquoted, because quoting them gets
# mangled on the way through - it is what made Caddy launch with no arguments,
# print its help text and exit. The installer fixes the path to
# C:\ProgramData\Shabana, which has no spaces, so this only fires if someone
# provisions by hand against a different location - and it says why, instead of
# producing a service that silently runs the wrong command.
if ($InstallDir -match '\s') {
    throw "InstallDir must not contain spaces (got '$InstallDir'). Service arguments are passed unquoted."
}

$pgBin      = Join-Path $InstallDir 'bin\pg\bin'
$dataDir    = Join-Path $InstallDir 'data\pg'
$configDir  = Join-Path $InstallDir 'config'
$logsDir    = Join-Path $InstallDir 'logs'
$migrationsDir = Join-Path $PSScriptRoot '..\..\supabase\migrations'
$bootstrapSql  = Join-Path $PSScriptRoot '..\..\supabase\platform-bootstrap.sql'

# NOTE: data\pg itself is deliberately NOT created here - only its parent.
# initdb wants to set permissions on its target directory, and it fails with
# "could not change permissions of directory ... Permission denied" when it is
# handed a directory someone else created under ACLs it cannot alter. Letting
# initdb create the directory itself avoids depending on the parent's ACLs.
foreach ($dir in @((Split-Path -Parent $dataDir), $configDir, $logsDir, (Join-Path $InstallDir 'backups'), (Join-Path $InstallDir 'public'))) {
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

# Stop anything still running from a previous install before touching its
# files. Provisioning rewrites config\, regenerates www\ from www-src\, and may
# move data\pg aside - all of which the running services hold open, and on
# Windows an open handle makes a delete or rename fail outright.
#
# ShabanaPostgres is in this list, and leaving it out was a real bug: on a
# reinstall over a SUCCESSFUL install the old service kept running, kept
# holding data\pg, and the temporary `pg_ctl start` below then failed with
# "could not start server" - because one was already running on that very
# directory. It also occupied 5432, so the port probe moved to 5433 and every
# generated config disagreed with postgresql.conf.
#
# It used to be stopped only on the "previous attempt never finished" path,
# which is the one case where the database is disposable - i.e. everywhere
# except where it mattered.
Stop-Service -Name 'ShabanaCaddy', 'ShabanaGoTrue', 'ShabanaPostgREST', 'ShabanaPostgres' -ErrorAction SilentlyContinue

# Stopping the service does not guarantee the postmaster is gone - NSSM returns
# as soon as it has signalled it. Wait for the data directory's lock to clear,
# or pg_ctl races it and fails intermittently, which is worse than failing
# every time.
$pgLock = Join-Path $InstallDir 'data\pg\postmaster.pid'
for ($i = 0; $i -lt 30 -and (Test-Path $pgLock); $i++) { Start-Sleep -Milliseconds 500 }

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

# A data directory from an ATTEMPT THAT NEVER FINISHED is worse than no data
# directory: provisioning is not fully idempotent (platform-bootstrap creates
# roles and schemas, and the app migrations are one-shot), so re-running over a
# half-built database fails on whatever the previous run already managed to
# create - and the customer is left having to delete a folder by hand from an
# elevated prompt, which is not something a shop owner should ever be asked to
# do. The marker below is written only after migrations fully succeed, so its
# ABSENCE is proof the database was never completed and holds nothing worth
# keeping.
#
# The old directory is RENAMED, never deleted. If this logic is ever wrong
# about what it is looking at, the data is still on disk and recoverable.
$markerFile = Join-Path $configDir 'provision-complete.marker'
$dbExists   = Test-Path (Join-Path $dataDir 'PG_VERSION')

if ($dbExists -and -not (Test-Path $markerFile)) {
    Write-Host 'Found a database from an earlier attempt that never completed.'
    # Anything still holding the folder open has to let go before a rename.
    Stop-Service -Name 'ShabanaCaddy', 'ShabanaGoTrue', 'ShabanaPostgREST', 'ShabanaPostgres' -ErrorAction SilentlyContinue
    # `2>` on a native command turns its stderr into PowerShell ErrorRecords,
    # and with $ErrorActionPreference = 'Stop' that TERMINATES the script. This
    # pg_ctl is a best-effort "stop it if it happens to be running", and it
    # writes to stderr whenever it is NOT running (no PID file) - which is the
    # normal case here. Suspend the preference around it and ignore the result.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & (Join-Path $pgBin 'pg_ctl.exe') stop -D $dataDir -m immediate -w 2>&1 | Out-Null } catch { }
    $ErrorActionPreference = $prevEap
    Start-Sleep -Seconds 2

    $quarantine = Join-Path (Split-Path -Parent $dataDir) ("pg.failed-" + (Get-Date).ToString('yyyy-MM-dd_HHmmss'))
    Rename-Item -Path $dataDir -NewName (Split-Path -Leaf $quarantine)
    Write-Host "Moved it aside to: $quarantine"
    Write-Host 'Starting from a clean database.'
    $dbExists = $false
}

if (-not $dbExists) {
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

# Two different accounts touch this data directory, and that is what broke the
# first real reinstall:
#
#   - initdb and this script run as the INSTALLING ADMINISTRATOR (Inno runs us
#     elevated as that user), and initdb on Windows locks the data directory
#     down to whoever created it.
#   - ShabanaPostgres runs as LocalSystem, because that is what NSSM registers
#     with no ObjectName. Every WAL segment the service creates after that is
#     therefore owned by SYSTEM.
#
# Reinstall, and the temporary Postgres above starts as the admin, reaches a
# WAL segment SYSTEM created, and dies with:
#
#     FATAL: could not open file "pg_wal/000000010000000000000002": Permission denied
#
# The message names a file, so it reads like corruption. It is an ACL.
#
# Granting SYSTEM and Administrators inheritable full control is what a normal
# PostgreSQL-on-Windows install looks like anyway, and it lets both accounts
# operate the same cluster. Done only when a data directory already exists -
# a fresh initdb needs nothing.
#
# SIDs, not names: these machines are frequently Arabic-localised, where the
# groups are "NT AUTHORITY\SYSTEM" and "Administrators" under different names.
# S-1-5-18 = SYSTEM, S-1-5-32-544 = Administrators.
if (Test-Path (Join-Path $dataDir 'PG_VERSION')) {
    Write-Host 'Normalising data directory permissions...'
    & icacls.exe $dataDir /grant "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" /T /C /Q 2>&1 | Out-Null
    # Best effort: a failure here is not fatal on its own, and the Postgres
    # start below gives a far clearer error than icacls does.
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  (icacls returned $LASTEXITCODE - continuing; Postgres will report if it still cannot read its files.)"
    }
    $global:LASTEXITCODE = 0
}

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
    if ($dbExists) {
        # Reached only when the marker says this database was fully provisioned
        # before (an upgrade re-running this script). Re-running the bootstrap
        # over a live database risks resetting grants under a running system,
        # and re-running every migration would be worse - upgrades apply only
        # what is pending, which is migrate.ps1's job.
        Write-Host 'Database already provisioned; applying only pending migrations.'
        & (Join-Path $PSScriptRoot 'migrate.ps1') -InstallDir $InstallDir
    }
    else {
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

        # PC compatibility layer. Must sit between the platform bootstrap and the
        # application migrations: it pre-creates things those migrations assume the
        # cloud already had (the storage schema; order-number sequences that can
        # legally be setval'd to 0 on an empty database). Without it, 0017 and 0022
        # abort provisioning outright and no service is ever registered. See the
        # file's own header for the full reasoning.
        $preludeSql = Join-Path $PSScriptRoot '..\..\supabase\pc-prelude.sql'
        if (-not (Test-Path $preludeSql)) {
            throw "pc-prelude.sql not found - the application migrations cannot apply without it."
        }
        Write-Host 'Applying PC compatibility prelude...'
        & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -v ON_ERROR_STOP=1 -f $preludeSql
        if ($LASTEXITCODE -ne 0) { throw 'PC prelude failed' }

        # pc_local_auth is DEFERRED until after GoTrue has started once - see
        # the "auth schema" section further down for why. Everything else
        # applies here, against the temporary Postgres.
        #
        # Matched by NAME, not by number. This filter used to read '0033_*',
        # which silently became wrong the moment the cloud shipped its own
        # 0033 (currency/timezone): that one would have been deferred and the
        # auth layer applied too early. PC-only migrations now live at 0100+,
        # well clear of the cloud's range, and this matches what it means.
        Write-Host 'Applying application migrations...'
        Get-ChildItem -Path $migrationsDir -Filter '*.sql' | Sort-Object Name |
            Where-Object { $_.Name -notlike '*pc_local_auth*' } | ForEach-Object {
            Write-Host "  -> $($_.Name)"
            & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -v ON_ERROR_STOP=1 -f $_.FullName
            if ($LASTEXITCODE -ne 0) { throw "Migration failed: $($_.Name)" }
        }

        # Record what's applied, for migrate.ps1 on future upgrades.
        & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -c "create table if not exists shabana_migrations (filename text primary key, applied_at timestamptz not null default now());"
        Get-ChildItem -Path $migrationsDir -Filter '*.sql' | Sort-Object Name |
            Where-Object { $_.Name -notlike '*pc_local_auth*' } | ForEach-Object {
            & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -c "insert into shabana_migrations (filename) values ('$($_.Name)') on conflict do nothing;"
        }
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

# --- The auth schema, and why pc_local_auth comes last ------------------------------
# GoTrue runs its OWN migrations against the auth schema the first time the
# service starts. Its 00_init_auth_schema does `create or replace function
# auth.uid()` with a body that reads the per-claim GUC `request.jwt.claim.sub`
# - a GUC PostgREST stopped setting years ago. 0100 replaces those functions
# with versions that read the `request.jwt.claims` JSON, and EVERY RLS policy
# depends on that: with GoTrue's version in place, auth.uid() is always null
# and every policy fails closed.
#
# So the order is forced: GoTrue must migrate FIRST, then 0100 must have the
# last word. Applying 0100 before starting the services (what we did) meant
# GoTrue overwrote it - except it never even got that far, because the
# functions were owned by the bootstrapping superuser and GoTrue connects as
# supabase_auth_admin, so it died on "must be owner of function uid" and
# restart-looped forever. pc-prelude.sql now hands it ownership; this section
# applies 0100 once GoTrue is done.
#
# It is also what makes pc_first_run_bootstrap work at all: that function
# inserts into auth.users columns (email_confirmed_at and friends) that only
# exist after GoTrue has migrated.

Write-Host 'Waiting for GoTrue to migrate the auth schema...'
$psql = Join-Path $pgBin 'psql.exe'
$env:PGPASSWORD = Get-Content $dbPasswordFile -Raw
$env:PGCLIENTENCODING = 'UTF8'
try {
    $authReady = $false
    for ($i = 0; $i -lt 45; $i++) {
        $probe = & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -t -A -c `
            "select count(*) from information_schema.columns where table_schema='auth' and table_name='users' and column_name='email_confirmed_at';"
        if ($LASTEXITCODE -eq 0 -and ("$probe".Trim() -eq '1')) { $authReady = $true; break }
        Start-Sleep -Seconds 2
    }
    if (-not $authReady) {
        throw "GoTrue did not migrate the auth schema within 90 seconds - see logs\gotrue.log. Without it there is no login."
    }

    Write-Host 'Applying PC auth layer...'
    $authMigrations = @(Get-ChildItem -Path $migrationsDir -Filter '*pc_local_auth*.sql' | Sort-Object Name)
    # Assert rather than quietly skip. If this ever matches nothing, GoTrue's
    # own auth.uid() stays in place, every RLS policy fails closed, and the
    # install looks fine right up until the owner logs in and sees no data.
    if ($authMigrations.Count -eq 0) {
        throw "No pc_local_auth migration found in $migrationsDir - refusing to finish an install whose RLS would fail closed."
    }
    $authMigrations | ForEach-Object {
        Write-Host "  -> $($_.Name)"
        & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -v ON_ERROR_STOP=1 -f $_.FullName
        if ($LASTEXITCODE -ne 0) { throw "Migration failed: $($_.Name)" }
        & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -c "insert into shabana_migrations (filename) values ('$($_.Name)') on conflict do nothing;"
    }

    # PostgREST builds its schema cache once, at startup - and it started
    # BEFORE 0100 ran, so it has never seen pc_needs_setup or
    # pc_first_run_bootstrap. Calling them returns PGRST202 "could not find the
    # function in the schema cache", which reads like the migration failed when
    # it applied perfectly. NOTIFY is the documented way to make it re-read;
    # the restart afterwards is belt and braces, since a stack that cannot
    # answer its own setup call is not worth leaving to chance.
    Write-Host 'Refreshing the PostgREST schema cache...'
    & $psql -U postgres -h 127.0.0.1 -p $pgPort -d postgres -c "notify pgrst, 'reload schema';" | Out-Null
    Restart-Service -Name 'ShabanaPostgREST' -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

# Only NOW is the database a complete, usable one. Everything above this line
# can be safely thrown away and redone; everything after it cannot. A future
# run that finds this marker missing will quarantine the data directory and
# start over - see the check near initdb.
Set-Content -Path $markerFile -Encoding ASCII -NoNewline `
    -Value ("provisioned " + (Get-Date).ToString('o'))

# --- Shortcut target -------------------------------------------------------
# The Start Menu / desktop shortcuts setup.iss creates point at THIS file
# rather than at a literal URL, because the HTTP port is chosen at
# provisioning time (see generate-secrets.ps1) and setup.iss has no way to
# know it. Rewriting the .url here retargets every shortcut at once.
$httpPort = (Get-Content (Join-Path $configDir 'http-port.txt') -Raw).Trim()
$appUrl = "http://localhost:$httpPort"
Set-Content -Path (Join-Path $InstallDir 'Shabana.url') -Encoding ASCII -Value @"
[InternetShortcut]
URL=$appUrl
"@

# --- Health check ----------------------------------------------------------
# NSSM reports a service as "started" as soon as the process launches, even
# if that process exits immediately - so Start-Service succeeding proves
# nothing. The only real evidence the stack is up is an HTTP response on the
# port the customer is about to open. Check it here, while the installer
# window is still on screen and the logs are one line away.
Write-Host ''
Write-Host "Checking that the application is responding on $appUrl ..."
# Asking Caddy for the home page proves almost nothing: it serves that from
# static files whether or not the database is reachable. An install where
# PostgREST is dead answers this check happily and then hands the customer a
# login screen that cannot work. So check the API the app actually depends on -
# a real RPC through PostgREST, and GoTrue's health endpoint.
$anonKey = (Get-Content (Join-Path $configDir 'anon.key') -Raw).Trim()
$apiHeaders = @{ apikey = $anonKey; Authorization = "Bearer $anonKey"; 'Content-Type' = 'application/json' }
$ok = $false
for ($i = 0; $i -lt 20; $i++) {
    try {
        Invoke-WebRequest -Uri $appUrl -UseBasicParsing -TimeoutSec 3 | Out-Null
        Invoke-WebRequest -Uri "$appUrl/auth/v1/health" -UseBasicParsing -TimeoutSec 3 | Out-Null
        Invoke-RestMethod -Method Post -Uri "$appUrl/rest/v1/rpc/pc_needs_setup" `
            -Headers $apiHeaders -Body '{}' -TimeoutSec 5 | Out-Null
        $ok = $true
        break
    } catch { }
    Start-Sleep -Seconds 2
}

if ($ok) {
    Write-Host ''
    Write-Host "SUCCESS: Shabana Inventory is running at $appUrl"
} else {
    # Not a `throw`: aborting here would roll the installer back and take the
    # logs with it. Print everything needed to diagnose instead, and let the
    # install finish so the customer can send us the log folder.
    Write-Host ''
    Write-Host '============================================================'
    Write-Host "PROBLEM: nothing answered on $appUrl after 40 seconds."
    Write-Host 'Service state:'
    Get-Service -Name 'ShabanaPostgres', 'ShabanaPostgREST', 'ShabanaGoTrue', 'ShabanaCaddy' -ErrorAction SilentlyContinue |
        Format-Table Name, Status -AutoSize | Out-String | Write-Host
    foreach ($log in @('caddy.log', 'postgrest.log', 'gotrue.log', 'pg-provision.log')) {
        $path = Join-Path $logsDir $log
        Write-Host "--- last 15 lines of $log ---"
        if (Test-Path $path) { Get-Content $path -Tail 15 | Write-Host } else { Write-Host '(not created)' }
    }
    Write-Host "Full logs: $logsDir"
    Write-Host '============================================================'
}

# Take one backup right now, rather than leaving the first restore point until
# 3am tomorrow. A shop that loses its disk on day one currently loses
# everything it entered on day one; this makes that window minutes instead of
# hours. It also creates public\last-backup.json, which the dashboard badge
# fetches - without it every page load logs a 404 until the nightly job first
# runs. Best effort: a shop with a working database and no backup yet is still
# a successful install, so a failure here is reported, not fatal.
Write-Host 'Taking an initial backup...'
try {
    & (Join-Path $PSScriptRoot 'backup.ps1') -InstallDir $InstallDir
} catch {
    Write-Host "Initial backup did not run: $($_.Exception.Message)"
    Write-Host '(The nightly job is still scheduled; this is not fatal.)'
}

Write-Host 'Provisioning complete.'
