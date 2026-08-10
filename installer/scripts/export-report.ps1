<#
.SYNOPSIS
    Zips the service logs and recent database errors to the Desktop so the shop
    owner can send one file over WhatsApp when something breaks.

.NOTES
    BUILD_PLAN.md item #8. Delivered as a Start Menu shortcut, not an in-app
    button: a browser page can't read the machine's log files or run a local
    zip. Turns an unanswerable "it's broken" call into a file we can actually
    read - on a machine we can't reach, with no internet.

    Deliberately collects only logs + config FILENAMES (not contents) + recent
    Postgres errors. It never copies config\*.key, gotrue.env, postgrest.conf
    or any dump - those hold secrets or customer data and have no place in a
    support bundle.
#>

[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\ProgramData\Shabana'
)

$ErrorActionPreference = 'Stop'

$logsDir  = Join-Path $InstallDir 'logs'
$configDir = Join-Path $InstallDir 'config'
$stamp = (Get-Date).ToString('yyyy-MM-dd_HHmmss')
$staging = Join-Path $env:TEMP "shabana-report-$stamp"
New-Item -ItemType Directory -Path $staging -Force | Out-Null

try {
    # 1. Service logs (Postgres / PostgREST / GoTrue / Caddy).
    if (Test-Path $logsDir) {
        Copy-Item -Path (Join-Path $logsDir '*.log') -Destination $staging -ErrorAction SilentlyContinue
    }

    # 2. An inventory of config - names and sizes only, so we can see WHAT
    #    exists without ever exposing the secrets inside those files.
    if (Test-Path $configDir) {
        Get-ChildItem -Path $configDir -File |
            Select-Object Name, Length, LastWriteTime |
            Format-Table -AutoSize |
            Out-File -FilePath (Join-Path $staging 'config-inventory.txt') -Encoding UTF8
    }

    # 3. Recent Postgres errors, if the DB is reachable.
    $pgBin = Join-Path $InstallDir 'bin\pg\bin'
    $pwFile = Join-Path $configDir 'db-password.key'
    $pgPortFile = Join-Path $configDir 'pg-port.txt'
    $pgPort = if (Test-Path $pgPortFile) { (Get-Content $pgPortFile -Raw).Trim() } else { '5432' }
    if ((Test-Path (Join-Path $pgBin 'psql.exe')) -and (Test-Path $pwFile)) {
        $env:PGPASSWORD = Get-Content $pwFile -Raw
        try {
            & (Join-Path $pgBin 'psql.exe') -U postgres -h 127.0.0.1 -p $pgPort -d postgres -c `
                "select filename, applied_at from shabana_migrations order by filename;" `
                *> (Join-Path $staging 'applied-migrations.txt')
        } catch {
            "Could not query database: $_" | Out-File (Join-Path $staging 'db-query-error.txt') -Encoding UTF8
        } finally {
            Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
        }
    }

    # 4. Ports: the ones we chose, plus the ranges Windows has RESERVED.
    #    Hyper-V/WSL/Docker reserve blocks of TCP ports, and binding one fails
    #    with "Permission denied" even though nothing is listening - the single
    #    most confusing failure mode this stack has, and invisible without
    #    netsh. Port numbers are not secrets, so both are safe to include.
    $portInfo = @()
    foreach ($pf in @('http-port.txt', 'pg-port.txt')) {
        $full = Join-Path $configDir $pf
        $portInfo += if (Test-Path $full) { "$pf = $((Get-Content $full -Raw).Trim())" } else { "$pf = (missing)" }
    }
    $portInfo += ''
    $portInfo += '--- Windows reserved TCP port ranges ---'
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $portInfo += (& netsh int ipv4 show excludedportrange protocol=tcp 2>&1 | Out-String) } catch { }
    $ErrorActionPreference = $prevEap
    $portInfo | Out-File -FilePath (Join-Path $staging 'ports.txt') -Encoding UTF8

    # 5. Service state.
    Get-Service -Name 'Shabana*' -ErrorAction SilentlyContinue |
        Select-Object Name, Status, StartType |
        Format-Table -AutoSize |
        Out-File -FilePath (Join-Path $staging 'services.txt') -Encoding UTF8

    $desktop = [Environment]::GetFolderPath('Desktop')
    $zipPath = Join-Path $desktop "Shabana-Problem-Report-$stamp.zip"
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -Force

    Write-Host ""
    Write-Host "Problem report created on your Desktop:"
    Write-Host "  $zipPath"
    Write-Host ""
    Write-Host "Send this file to support."
}
finally {
    Remove-Item -Path $staging -Recurse -Force -ErrorAction SilentlyContinue
}
