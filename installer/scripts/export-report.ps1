<#
.SYNOPSIS
    Zips the service logs and recent database errors to the Desktop so the shop
    owner can send one file over WhatsApp when something breaks.

.NOTES
    BUILD_PLAN.md item #8. Delivered as a Start Menu shortcut, not an in-app
    button: a browser page can't read the machine's log files or run a local
    zip. Turns an unanswerable "it's broken" call into a file we can actually
    read — on a machine we can't reach, with no internet.

    Deliberately collects only logs + config FILENAMES (not contents) + recent
    Postgres errors. It never copies config\*.key, gotrue.env, postgrest.conf
    or any dump — those hold secrets or customer data and have no place in a
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

    # 2. An inventory of config — names and sizes only, so we can see WHAT
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
    if ((Test-Path (Join-Path $pgBin 'psql.exe')) -and (Test-Path $pwFile)) {
        $env:PGPASSWORD = Get-Content $pwFile -Raw
        try {
            & (Join-Path $pgBin 'psql.exe') -U postgres -h 127.0.0.1 -d postgres -c `
                "select filename, applied_at from shabana_migrations order by filename;" `
                *> (Join-Path $staging 'applied-migrations.txt')
        } catch {
            "Could not query database: $_" | Out-File (Join-Path $staging 'db-query-error.txt') -Encoding UTF8
        } finally {
            Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
        }
    }

    # 4. Service state.
    Get-Service -Name 'Shabana*' -ErrorAction SilentlyContinue |
        Select-Object Name, Status, StartType |
        Format-Table -AutoSize |
        Out-File -FilePath (Join-Path $staging 'services.txt') -Encoding UTF8

    $desktop = [Environment]::GetFolderPath('Desktop')
    $zipPath = Join-Path $desktop "شبانة-تقرير-المشكلة-$stamp.zip"
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -Force

    Write-Host ""
    Write-Host "تم إنشاء تقرير المشكلة على سطح المكتب:"
    Write-Host "  $zipPath"
    Write-Host ""
    Write-Host "أرسل هذا الملف للدعم الفني."
}
finally {
    Remove-Item -Path $staging -Recurse -Force -ErrorAction SilentlyContinue
}
