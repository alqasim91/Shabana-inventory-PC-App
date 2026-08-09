<#
.SYNOPSIS
    Registers Postgres (native) and PostgREST/GoTrue/Caddy (via NSSM, since
    they're plain console executables, not Windows-service-aware) so the
    whole stack starts automatically on boot with no one logged in.

.NOTES
    NSSM (nssm.exe) is vendored alongside the other binaries by the CI
    build — see .github/workflows/build-installer.yml. It's the standard,
    long-established tool for this (wraps any console exe as a proper
    Windows service, handles restart-on-crash).
#>

[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\ProgramData\Shabana'
)

$ErrorActionPreference = 'Stop'

$pgBin    = Join-Path $InstallDir 'bin\pg\bin'
$dataDir  = Join-Path $InstallDir 'data\pg'
$logsDir  = Join-Path $InstallDir 'logs'
$configDir = Join-Path $InstallDir 'config'
$nssm     = Join-Path $InstallDir 'bin\nssm\nssm.exe'

function Install-NssmService {
    param(
        [string]$Name,
        [string]$Exe,
        [string]$Args,
        [string]$WorkingDir,
        [string]$LogFile
    )
    $existing = & $nssm status $Name 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Service $Name already registered, updating..."
        & $nssm set $Name Application $Exe | Out-Null
        & $nssm set $Name AppParameters $Args | Out-Null
    } else {
        & $nssm install $Name $Exe $Args
    }
    & $nssm set $Name AppDirectory $WorkingDir | Out-Null
    & $nssm set $Name AppStdout $LogFile | Out-Null
    & $nssm set $Name AppStderr $LogFile | Out-Null
    & $nssm set $Name Start SERVICE_AUTO_START | Out-Null
    & $nssm set $Name AppRestartDelay 3000 | Out-Null
}

# --- Postgres: native Windows service support, no NSSM needed -----------

$pgCtl = Join-Path $pgBin 'pg_ctl.exe'
$pgRegistered = (Get-Service -Name 'ShabanaPostgres' -ErrorAction SilentlyContinue)
if (-not $pgRegistered) {
    & $pgCtl register -N ShabanaPostgres -D $dataDir -w `
        -o "-o `"-c config_file=$dataDir\postgresql.conf`""
}
Set-Service -Name 'ShabanaPostgres' -StartupType Automatic

# --- PostgREST, GoTrue, Caddy: via NSSM ----------------------------------

Install-NssmService -Name 'ShabanaPostgREST' `
    -Exe (Join-Path $InstallDir 'bin\postgrest\postgrest.exe') `
    -Args (Join-Path $configDir 'postgrest.conf') `
    -WorkingDir (Join-Path $InstallDir 'bin\postgrest') `
    -LogFile (Join-Path $logsDir 'postgrest.log')

Install-NssmService -Name 'ShabanaGoTrue' `
    -Exe (Join-Path $InstallDir 'bin\gotrue\gotrue.exe') `
    -Args '' `
    -WorkingDir (Join-Path $InstallDir 'bin\gotrue') `
    -LogFile (Join-Path $logsDir 'gotrue.log')
# GoTrue reads its config from env vars — NSSM's AppEnvironmentExtra loads
# them from the generated .env file at service start.
& $nssm set ShabanaGoTrue AppEnvironmentExtra (Get-Content (Join-Path $configDir 'gotrue.env') -Raw) | Out-Null

Install-NssmService -Name 'ShabanaCaddy' `
    -Exe (Join-Path $InstallDir 'bin\caddy\caddy.exe') `
    -Args "run --config `"$configDir\Caddyfile`" --adapter caddyfile" `
    -WorkingDir (Join-Path $InstallDir 'bin\caddy') `
    -LogFile (Join-Path $logsDir 'caddy.log')

# --- Start everything, in dependency order -------------------------------

Write-Host 'Starting services...'
Start-Service -Name 'ShabanaPostgres'
Start-Sleep -Seconds 3   # give Postgres a moment to accept connections
Start-Service -Name 'ShabanaPostgREST'
Start-Service -Name 'ShabanaGoTrue'
Start-Service -Name 'ShabanaCaddy'

Write-Host 'All services registered and started.'
