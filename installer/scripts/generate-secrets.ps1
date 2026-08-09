<#
.SYNOPSIS
    Generates a fresh, machine-unique JWT secret, DB password, and derived
    anon/service_role JWTs, and writes every config file that needs them.

.NOTES
    SECURITY-CRITICAL — flagged in BUILD_PLAN.md item #1 for a dedicated
    review pass before this ships to a real customer.

    Every value here MUST be unique per install. If two customers ever
    share a JWT secret, either one can forge a service_role token — which
    bypasses every RLS policy — against the OTHER customer's tunnel URL.
    This script must never read a secret from anywhere but its own fresh
    random generation, and its output must never be committed to git,
    logged, or reused by copying files between installs.

    JWT construction follows Supabase's own self-hosting documentation:
    HS256, shared secret, standard `role` claim consumed by PostgREST's
    `db-anon-role` / RLS `current_setting('request.jwt.claims')`. Nothing
    exotic — but "not exotic" is not the same as "verified," hence the
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
    # Alphanumeric only — this password is interpolated into postgres
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
    $exp = $iat + (10 * 365 * 24 * 60 * 60)  # 10 years — see BUILD_PLAN.md upgrade notes on rotation

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
    $content = Get-Content -Raw -Path $TemplatePath
    foreach ($key in $Replacements.Keys) {
        $content = $content.Replace("{{$key}}", [string]$Replacements[$key])
    }
    $outDir = Split-Path -Parent $OutPath
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
    Set-Content -Path $OutPath -Value $content -NoNewline -Encoding UTF8
}

# --- Generate ---------------------------------------------------------

$jwtSecret  = New-RandomBase64Secret -ByteLength 40
$dbPassword = New-RandomPassword -Length 32

$anonKey        = New-SupabaseJwt -Secret $jwtSecret -Role 'anon'
$serviceRoleKey = New-SupabaseJwt -Secret $jwtSecret -Role 'service_role'

$ports = @{
    HTTP_PORT      = 8000
    PG_PORT        = 5432
    POSTGREST_PORT = 3001
    GOTRUE_PORT    = 9999
}

$replacements = @{
    JWT_SECRET  = $jwtSecret
    DB_PASSWORD = $dbPassword
    ANON_KEY    = $anonKey
    INSTALL_DIR = $InstallDir
} + $ports

$templateDir = Join-Path $PSScriptRoot '..\config'
$configDir   = Join-Path $InstallDir 'config'

Set-FromTemplate -TemplatePath (Join-Path $templateDir 'Caddyfile.template')          -OutPath (Join-Path $configDir 'Caddyfile')           -Replacements $replacements
Set-FromTemplate -TemplatePath (Join-Path $templateDir 'gotrue.env.template')          -OutPath (Join-Path $configDir 'gotrue.env')           -Replacements $replacements
Set-FromTemplate -TemplatePath (Join-Path $templateDir 'postgrest.conf.template')      -OutPath (Join-Path $configDir 'postgrest.conf')       -Replacements $replacements
Set-FromTemplate -TemplatePath (Join-Path $templateDir 'runtime-config.json.template') -OutPath (Join-Path $configDir 'runtime-config.json')  -Replacements $replacements

# service_role key is not embedded in any served file (it must never reach
# a browser). It's written once, locally, for admin scripts (backup /
# restore / reset-admin / migrate) to read when they need elevated access.
Set-Content -Path (Join-Path $configDir 'service-role.key') -Value $serviceRoleKey -NoNewline -Encoding UTF8

# DB password alone, for scripts that shell out to psql/pg_dump without
# parsing gotrue.env.
Set-Content -Path (Join-Path $configDir 'db-password.key') -Value $dbPassword -NoNewline -Encoding UTF8

Write-Host "Secrets generated and config files written to $configDir"
Write-Host "JWT secret and DB password are unique to this machine — do not copy config\ between installs."
