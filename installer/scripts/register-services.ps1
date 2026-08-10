<#
.SYNOPSIS
    Registers Postgres (native) and PostgREST/GoTrue/Caddy (via NSSM, since
    they're plain console executables, not Windows-service-aware) so the
    whole stack starts automatically on boot with no one logged in.

.NOTES
    NSSM (nssm.exe) is vendored alongside the other binaries by the CI
    build - see .github/workflows/build-installer.yml. It's the standard,
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
        # NOT named $Args: that is a PowerShell AUTOMATIC variable (the
        # function's own argument array). A parameter of that name is shadowed
        # by it, so this always resolved to empty and EVERY service was
        # registered with no command line at all - Caddy printed its help text
        # and exited, PostgREST ran with no config file and threw, and GoTrue
        # only survived because it takes no arguments and reads its
        # configuration from the environment.
        [string]$Arguments,
        [string]$WorkingDir,
        [string]$LogFile
    )
    # Same trap as in provision.ps1: `2>` promotes a native command's stderr to
    # PowerShell ErrorRecords, which $ErrorActionPreference = 'Stop' turns into
    # a terminating error. nssm writes to stderr for a service that does not
    # exist yet - true for every service on every FRESH install - so this line
    # would kill provisioning at the point where it registers services.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $nssm status $Name 2>&1 | Out-Null } catch { }
    $ErrorActionPreference = $prevEap
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Service $Name already registered, updating..."
        & $nssm set $Name Application $Exe | Out-Null
    } else {
        # Install with the program ONLY. Passing the arguments here as well
        # means PowerShell hands nssm one string that it re-parses, and any
        # embedded quotes get escaped along the way - Caddy received a mangled
        # command line, printed its help text and exited, which surfaced as a
        # service that "started" and immediately stopped with a log full of
        # usage instructions. Setting AppParameters explicitly avoids that
        # round trip.
        & $nssm install $Name $Exe | Out-Null
    }
    & $nssm set $Name AppParameters $Arguments | Out-Null
    # Read it back. If the arguments ever get mangled again, this line says so
    # in the provisioning window instead of leaving a service that silently
    # runs the wrong command.
    # Read it back and PROVE it stuck. nssm emits UTF-16, which PowerShell
    # surfaces with NULs between the characters, so strip those before
    # comparing. A service registered with the wrong command line otherwise
    # fails in a way that looks nothing like its cause - Caddy printing its
    # help text into a log nobody reads.
    $stored = (((& $nssm get $Name AppParameters) -join ' ') -replace "`0", '').Trim()
    Write-Host ("  {0} args: {1}" -f $Name, $(if ($stored) { $stored } else { '(none)' }))
    if ($Arguments -and -not $stored) {
        throw "nssm did not store a command line for $Name (expected '$Arguments'). The service would run with no arguments."
    }
    & $nssm set $Name AppDirectory $WorkingDir | Out-Null
    & $nssm set $Name AppStdout $LogFile | Out-Null
    & $nssm set $Name AppStderr $LogFile | Out-Null
    # 4 = append rather than truncate. Without it a service that restart-loops
    # overwrites its own log on every attempt, so the first (real) error is
    # gone by the time anyone reads it.
    & $nssm set $Name AppStdoutCreationDisposition 4 | Out-Null
    & $nssm set $Name AppStderrCreationDisposition 4 | Out-Null
    & $nssm set $Name Start SERVICE_AUTO_START | Out-Null
    & $nssm set $Name AppRestartDelay 3000 | Out-Null
}

# --- Postgres: native Windows service support, no NSSM needed -----------

# pg_ctl register creates a service that runs as NT AUTHORITY\NetworkService
# (its documented default). That account did not create the data directory and
# has no rights to it, so the service registers fine and then fails to start
# with a bare "Cannot start service" and nothing useful in any log. Grant it
# access before registering. Also the logs directory, which Postgres writes to.
$pgAccount = 'NT AUTHORITY\NetworkService'
foreach ($dir in @($dataDir, $logsDir)) {
    & icacls $dir /grant "$($pgAccount):(OI)(CI)F" /T /Q | Out-Null
}

$pgCtl = Join-Path $pgBin 'pg_ctl.exe'
$pgRegistered = (Get-Service -Name 'ShabanaPostgres' -ErrorAction SilentlyContinue)
if (-not $pgRegistered) {
    # No -o: the previous version passed `-o "-o \"-c config_file=...\""`, a
    # doubled -o that reached postgres as a malformed option. The config file
    # lives in the data directory, which is where postgres looks by default,
    # so it never needed overriding at all.
    & $pgCtl register -N ShabanaPostgres -D $dataDir -S auto
    if ($LASTEXITCODE -ne 0) { throw "pg_ctl register failed with exit code $LASTEXITCODE" }
}
Set-Service -Name 'ShabanaPostgres' -StartupType Automatic

# --- PostgREST, GoTrue, Caddy: via NSSM ----------------------------------

Install-NssmService -Name 'ShabanaPostgREST' `
    -Exe (Join-Path $InstallDir 'bin\postgrest\postgrest.exe') `
    -Arguments (Join-Path $configDir 'postgrest.conf') `
    -WorkingDir (Join-Path $InstallDir 'bin\postgrest') `
    -LogFile (Join-Path $logsDir 'postgrest.log')

Install-NssmService -Name 'ShabanaGoTrue' `
    -Exe (Join-Path $InstallDir 'bin\gotrue\gotrue.exe') `
    -Arguments '' `
    -WorkingDir (Join-Path $InstallDir 'bin\gotrue') `
    -LogFile (Join-Path $logsDir 'gotrue.log')
# GoTrue reads its config from env vars. NSSM's AppEnvironmentExtra wants each
# setting as a SEPARATE argument of the form KEY=VALUE - handing it the whole
# file as one string fails with "Environment should comprise strings of the
# form KEY=VALUE", and the service then starts with no configuration at all.
# The generated file also carries comments and blank lines, which have to go.
$gotrueEnv = @(
    Get-Content (Join-Path $configDir 'gotrue.env') |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -ne '' -and -not $_.StartsWith('#') -and $_.Contains('=') }
)
if ($gotrueEnv.Count -eq 0) { throw 'gotrue.env produced no KEY=VALUE settings.' }
& $nssm set ShabanaGoTrue AppEnvironmentExtra @gotrueEnv | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Failed to set GoTrue environment (nssm exit $LASTEXITCODE)" }

# Validate the Caddyfile BEFORE registering. A config error otherwise shows up
# only as "Failed to start service ShabanaCaddy" with an empty caddy.log,
# because the process dies before NSSM ever attaches its output streams.
$caddyExe = Join-Path $InstallDir 'bin\caddy\caddy.exe'
$caddyFile = Join-Path $configDir 'Caddyfile'
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$caddyCheck = & $caddyExe validate --config $caddyFile --adapter caddyfile 2>&1 | Out-String
$caddyOk = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $prevEap
if (-not $caddyOk) {
    throw "Caddy rejected its configuration - it would fail to start with no log. Output:`n$caddyCheck"
}
Write-Host 'Caddy configuration validated.'

Install-NssmService -Name 'ShabanaCaddy' `
    -Exe (Join-Path $InstallDir 'bin\caddy\caddy.exe') `
    -Arguments "run --config $configDir\Caddyfile --adapter caddyfile" `
    -WorkingDir (Join-Path $InstallDir 'bin\caddy') `
    -LogFile (Join-Path $logsDir 'caddy.log')

# --- Start everything, in dependency order -------------------------------

# Start each one WITHOUT throwing on failure. A throw here aborts provisioning
# with nothing but "Cannot start service X", killing it before the health check
# in provision.ps1 - which prints service states and every log tail - ever gets
# to run. Report what happened and let that diagnostic do its job.
Write-Host 'Starting services...'
$startFailures = @()
foreach ($svc in @('ShabanaPostgres', 'ShabanaPostgREST', 'ShabanaGoTrue', 'ShabanaCaddy')) {
    try {
        Start-Service -Name $svc -ErrorAction Stop
        Write-Host "  started $svc"
    } catch {
        $startFailures += $svc
        Write-Host "  FAILED to start $svc : $($_.Exception.Message)"
    }
    # Postgres has to be accepting connections before PostgREST and GoTrue
    # try to connect, or they exit immediately and NSSM restart-loops them.
    if ($svc -eq 'ShabanaPostgres') { Start-Sleep -Seconds 3 }
}

if ($startFailures.Count -gt 0) {
    Write-Host ''
    Write-Host "Services that did not start: $($startFailures -join ', ')"
    # Windows records the real reason here when a service dies before it can
    # write its own log - the single most useful place to look, and one no
    # customer would ever think to check.
    Get-WinEvent -LogName System -MaxEvents 40 -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'Shabana' } |
        Select-Object -First 5 TimeCreated, Message |
        Format-List | Out-String | Write-Host
}

Write-Host 'All services registered and started.'
