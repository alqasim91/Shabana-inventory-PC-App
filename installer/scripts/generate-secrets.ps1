<#
.SYNOPSIS
    Generates a fresh, machine-unique JWT secret, DB password, and derived
    anon/service_role JWTs, and writes every config file that needs them.

.NOTES
    SECURITY-CRITICAL - flagged in BUILD_PLAN.md item #1 for a dedicated
    review pass before this ships to a real customer.

    Every value here MUST be unique per install. If two customers ever
    share a JWT secret, either one can forge a service_role token - which
    bypasses every RLS policy - against the OTHER customer's tunnel URL.
    This script must never read a secret from anywhere but its own fresh
    random generation, and its output must never be committed to git,
    logged, or reused by copying files between installs.

    JWT construction follows Supabase's own self-hosting documentation:
    HS256, shared secret, standard `role` claim consumed by PostgREST's
    `db-anon-role` / RLS `current_setting('request.jwt.claims')`. Nothing
    exotic - but "not exotic" is not the same as "verified," hence the
    review flag above.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDir
)

$ErrorActionPreference = 'Stop'

function New-RandomBase64Secret {
    param([int]$ByteLength = 40)
    $bytes = [byte[]]::new($ByteLength)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($bytes)
}

function New-RandomPassword {
    param([int]$Length = 32)
    # Alphanumeric only - this password is interpolated into postgres
    # connection strings and .env files; avoiding punctuation sidesteps a
    # whole class of quoting/escaping bugs across three different config
    # formats (env file, libpq URI, Caddyfile).
    $chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    $bytes = [byte[]]::new($Length)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

function ConvertTo-Base64Url {
    param([byte[]]$Bytes)
    ([Convert]::ToBase64String($Bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function New-SupabaseJwt {
    param(
        [Parameter(Mandatory = $true)][string]$Secret,
        [Parameter(Mandatory = $true)][string]$Role
    )
    $iat = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $exp = $iat + (10 * 365 * 24 * 60 * 60)  # 10 years - see BUILD_PLAN.md upgrade notes on rotation

    $header  = '{"alg":"HS256","typ":"JWT"}'
    $payload = "{`"role`":`"$Role`",`"iss`":`"shabana-pc`",`"iat`":$iat,`"exp`":$exp}"

    $headerB64  = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($header))
    $payloadB64 = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($payload))
    $signingInput = "$headerB64.$payloadB64"

    $hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($Secret))
    try {
        $sig = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($signingInput))
    } finally {
        $hmac.Dispose()
    }
    $sigB64 = ConvertTo-Base64Url $sig

    return "$signingInput.$sigB64"
}

function Set-FromTemplate {
    param(
        [Parameter(Mandatory = $true)][string]$TemplatePath,
        [Parameter(Mandatory = $true)][string]$OutPath,
        [Parameter(Mandatory = $true)][hashtable]$Replacements
    )
    $content = Get-Content -Raw -Path $TemplatePath -Encoding UTF8
    foreach ($key in $Replacements.Keys) {
        $content = $content.Replace("{{$key}}", [string]$Replacements[$key])
    }
    $outDir = Split-Path -Parent $OutPath
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
    # ASCII, NOT `-Encoding UTF8`: in Windows PowerShell 5.1 `Set-Content
    # -Encoding UTF8` prepends a BOM. A BOM in these config files is at best
    # ugly and at worst breaks the reader (Caddy/PostgREST/GoTrue). All
    # generated content here is ASCII (base64/JWT/alphanumeric/ASCII paths),
    # so ASCII is exact and BOM-free.
    Set-Content -Path $OutPath -Value $content -NoNewline -Encoding ASCII
}

function Get-BindablePort {
    # Returns a port we can ACTUALLY bind on loopback, starting from
    # $Preferred. This is not just "is it in use" - on Windows, Hyper-V /
    # WSL2 / Docker reserve blocks of TCP ports (netsh excludedportrange),
    # and binding one fails with "Permission denied" even though nothing is
    # listening. Postgres hit exactly this on 5432. TcpListener.Start()
    # throws for both in-use AND reserved ports, so a successful Start/Stop
    # is proof the real service can bind it too.
    param([int]$Preferred, [int[]]$Exclude = @())
    $candidates = @($Preferred)
    $candidates += ($Preferred + 1)..($Preferred + 40)   # near the preferred first (nicer logs)
    $candidates += 55100..55400                            # then a high fallback block
    foreach ($p in $candidates) {
        if ($p -lt 1 -or $p -gt 65535 -or ($Exclude -contains $p)) { continue }
        $listener = $null
        try {
            $listener = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback, $p)
            $listener.Start()
            $listener.Stop()
            return $p
        } catch {
            if ($listener) { try { $listener.Stop() } catch { } }
            continue
        }
    }
    throw "Could not find a bindable TCP port near $Preferred (all candidates in use or in a Windows reserved range)."
}

# --- Generate ---------------------------------------------------------
# REUSE existing secrets on a re-provision. This is not just tidiness: the DB
# superuser password is baked into the data dir by initdb and can only be
# changed by connecting (which needs the current password). Since initdb is
# skipped when a data dir already exists, minting a NEW password here would
# leave provision unable to connect at all. Reusing the stored value keeps it
# matched to the running data dir. The JWT secret is reused for the same
# reason applied to sessions - a new one would silently invalidate everyone's
# login on every reinstall. A truly fresh install has no config\ yet, so both
# are generated.
$configDir = Join-Path $InstallDir 'config'
$jwtSecretFile = Join-Path $configDir 'jwt-secret.key'
$dbPasswordFile2 = Join-Path $configDir 'db-password.key'

if (Test-Path $jwtSecretFile) {
    $jwtSecret = (Get-Content $jwtSecretFile -Raw).Trim()
} else {
    $jwtSecret = New-RandomBase64Secret -ByteLength 40
}
if (Test-Path $dbPasswordFile2) {
    $dbPassword = (Get-Content $dbPasswordFile2 -Raw).Trim()
} else {
    $dbPassword = New-RandomPassword -Length 32
}

$anonKey        = New-SupabaseJwt -Secret $jwtSecret -Role 'anon'
$serviceRoleKey = New-SupabaseJwt -Secret $jwtSecret -Role 'service_role'

# HTTP_PORT is FIXED at 8000: it's the only user-facing port (the desktop
# shortcut and the anon URL baked into the frontend both point at it), so it
# can't be chosen dynamically without also rewriting those. The three
# INTERNAL ports are probed for real bindability, because a Windows reserved
# range (Hyper-V/WSL/Docker) can make the preferred one unbindable - which is
# exactly what killed Postgres on 5432. Each is excluded from the next so
# they can't collide.
$httpPort = 8000
$pgPortFile = Join-Path (Join-Path $InstallDir 'config') 'pg-port.txt'
if (Test-Path $pgPortFile) {
    # Reuse the port a previous provision already baked into postgresql.conf,
    # so a reinstall/re-provision stays consistent with the existing data dir
    # instead of picking a new port the running Postgres isn't listening on.
    $pgPort = [int]((Get-Content $pgPortFile -Raw).Trim())
} else {
    $pgPort = Get-BindablePort -Preferred 5432 -Exclude @($httpPort)
}
$pgrPort  = Get-BindablePort -Preferred 3001 -Exclude @($httpPort, $pgPort)
$gotPort  = Get-BindablePort -Preferred 9999 -Exclude @($httpPort, $pgPort, $pgrPort)
$ports = @{
    HTTP_PORT      = $httpPort
    PG_PORT        = $pgPort
    POSTGREST_PORT = $pgrPort
    GOTRUE_PORT    = $gotPort
}
Write-Host "Ports: HTTP=$httpPort  Postgres=$pgPort  PostgREST=$pgrPort  GoTrue=$gotPort"

$replacements = @{
    JWT_SECRET  = $jwtSecret
    DB_PASSWORD = $dbPassword
    ANON_KEY    = $anonKey
    INSTALL_DIR = $InstallDir
} + $ports

$templateDir = Join-Path $PSScriptRoot '..\config'
# $configDir already set above (near the reuse logic).

Set-FromTemplate -TemplatePath (Join-Path $templateDir 'Caddyfile.template')     -OutPath (Join-Path $configDir 'Caddyfile')      -Replacements $replacements
Set-FromTemplate -TemplatePath (Join-Path $templateDir 'gotrue.env.template')     -OutPath (Join-Path $configDir 'gotrue.env')     -Replacements $replacements
Set-FromTemplate -TemplatePath (Join-Path $templateDir 'postgrest.conf.template') -OutPath (Join-Path $configDir 'postgrest.conf') -Replacements $replacements

# Patch this machine's real URL + anon key into the already-built frontend
# in place of the placeholder tokens CI baked in - see
# patch-frontend-config.ps1 for why this exists instead of a runtime-fetch
# endpoint or an edit to the frontend's source repo.
$wwwDir = Join-Path $InstallDir 'www'
if (Test-Path $wwwDir) {
    & (Join-Path $PSScriptRoot 'patch-frontend-config.ps1') `
        -WwwDir $wwwDir `
        -SupabaseUrl "http://localhost:$($ports.HTTP_PORT)" `
        -AnonKey $anonKey
} else {
    Write-Warning "www\ not found under $InstallDir - skipping frontend patch (expected during provisioning before [Files] copies it; not expected otherwise)."
}

# service_role key is not embedded in any served file (it must never reach
# a browser). It's written once, locally, for admin scripts (backup /
# restore / reset-admin / migrate) to read when they need elevated access.
# ASCII (no BOM) - a JWT is ASCII, and a BOM would corrupt it for any reader.
Set-Content -Path (Join-Path $configDir 'service-role.key') -Value $serviceRoleKey -NoNewline -Encoding ASCII

# DB password alone, for scripts that shell out to psql/pg_dump without
# parsing gotrue.env. CRITICAL that this is BOM-free: initdb reads it via
# --pwfile and does NOT strip a BOM, so a BOM here would make the postgres
# superuser password differ from what every psql call later sends (which
# Get-Content strips) - every DB connection would then fail auth. ASCII
# guarantees no BOM (the password is alphanumeric).
Set-Content -Path (Join-Path $configDir 'db-password.key') -Value $dbPassword -NoNewline -Encoding ASCII

# The JWT secret, stored so a re-provision can reuse it (see the reuse logic
# up top) instead of silently invalidating every session. Never served; only
# read back by this script. ASCII, no BOM.
Set-Content -Path (Join-Path $configDir 'jwt-secret.key') -Value $jwtSecret -NoNewline -Encoding ASCII

# The chosen Postgres port, so every maintenance script (provision, backup,
# restore, migrate, reset-admin, export-report) connects to the right place
# when it isn't the default 5432. ASCII, no BOM.
Set-Content -Path (Join-Path $configDir 'pg-port.txt') -Value ([string]$pgPort) -NoNewline -Encoding ASCII

Write-Host "Secrets generated and config files written to $configDir"
Write-Host "JWT secret and DB password are unique to this machine - do not copy config\ between installs."
