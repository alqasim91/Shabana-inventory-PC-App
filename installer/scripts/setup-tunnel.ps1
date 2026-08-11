<#
.SYNOPSIS
    Turn the public link on or off (Tailscale Funnel).

.DESCRIPTION
    Publishes this installation at an HTTPS address reachable from any device,
    so the owner can open the app from a phone or another PC while this machine
    is running.

    WHY TAILSCALE FUNNEL AND NOT CLOUDFLARE TUNNEL
    Funnel's relays forward the TLS stream by SNI and never decrypt it; the
    certificate lives on THIS machine and TLS terminates here. A vendor who
    proxies for you therefore cannot read the invoices passing through. A
    tunnel that terminates TLS at the provider can. Visitors also need no
    client installed - it is an ordinary https:// address in any browser.

    WHAT IT DOES NOT DO
    The link is only up while this PC is on, awake and online. It is remote
    ACCESS to the one database on this machine - not a second copy, not a
    backup, and nothing syncs. If the PC is off, the link is down. Backups are
    a separate thing entirely; see backup.ps1.

    SECURITY
    Funnel points at a loopback-only Caddy site (TUNNEL_PORT) that refuses
    /setup and pc_first_run_bootstrap outright - claiming the install stays
    physically local. See installer/config/Caddyfile.template for why that is a
    separate site block rather than an IP check.

    Everything else is reachable, so ANYONE WITH THE LINK REACHES THE LOGIN
    PAGE. It is a login page, not open data - but it is exposed to the internet,
    so weak passwords now matter in a way they did not on a LAN-only install.

.PARAMETER Disable
    Take the link down. The install keeps working locally.

.PARAMETER InstallDir
    Where Shabana is installed.
#>

[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\ProgramData\Shabana',
    [switch]$Disable
)

$ErrorActionPreference = 'Stop'

$configDir  = Join-Path $InstallDir 'config'
$publicDir  = Join-Path $InstallDir 'public'
$urlFile    = Join-Path $configDir 'public-url.txt'
$statusFile = Join-Path $publicDir 'tunnel.json'

function Write-TunnelStatus {
    param([string]$Url, [string]$State)
    if (-not (Test-Path $publicDir)) { New-Item -ItemType Directory -Path $publicDir -Force | Out-Null }
    # Served at /pc/tunnel.json so the app can show the owner their own link
    # instead of making them find it in a config folder.
    $payload = [ordered]@{
        state      = $State
        url        = $Url
        checked_at = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json -Compress
    Set-Content -Path $statusFile -Value $payload -Encoding UTF8
}

# --- Find Tailscale --------------------------------------------------------
# Not bundled with the installer: it ships its own service and needs the owner
# to sign in to their own account, which an unattended MSI cannot do for them.
$tailscale = $null
foreach ($candidate in @(
    (Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Tailscale\tailscale.exe')
)) {
    if ($candidate -and (Test-Path $candidate)) { $tailscale = $candidate; break }
}
if (-not $tailscale) {
    $cmd = Get-Command tailscale.exe -ErrorAction SilentlyContinue
    if ($cmd) { $tailscale = $cmd.Source }
}

if (-not $tailscale) {
    Write-Host ''
    Write-Host 'Tailscale is not installed on this machine.'
    Write-Host ''
    Write-Host '  1. Install it from  https://tailscale.com/download/windows'
    Write-Host '  2. Sign in (a free personal account is enough)'
    Write-Host '  3. Run this again'
    Write-Host ''
    throw 'Tailscale not found.'
}

# --- Disable ---------------------------------------------------------------
if ($Disable) {
    Write-Host 'Taking the public link down...'
    & $tailscale funnel --https=443 off
    if ($LASTEXITCODE -ne 0) { throw 'Could not disable the funnel. Is Tailscale signed in?' }
    Remove-Item $urlFile -ErrorAction SilentlyContinue
    Write-TunnelStatus -Url '' -State 'off'
    Write-Host 'The public link is off. The app still works on this machine.'
    return
}

# --- Enable ----------------------------------------------------------------
$tunnelPortFile = Join-Path $configDir 'tunnel-port.txt'
if (-not (Test-Path $tunnelPortFile)) {
    throw "tunnel-port.txt not found in $configDir - re-run provisioning before enabling the public link."
}
$tunnelPort = (Get-Content $tunnelPortFile -Raw).Trim()

# Fail here rather than publishing a link to a port nothing answers on.
try {
    $probe = Invoke-WebRequest -Uri "http://127.0.0.1:$tunnelPort/" -UseBasicParsing -TimeoutSec 10
    if ($probe.StatusCode -ne 200) { throw "status $($probe.StatusCode)" }
} catch {
    throw "The app is not answering on 127.0.0.1:$tunnelPort - start the Shabana services first. ($_)"
}

Write-Host "Publishing 127.0.0.1:$tunnelPort ..."
# --bg keeps it running (and persisted) after this window closes; without it
# the funnel lasts only as long as the foreground command.
& $tailscale funnel --bg "http://127.0.0.1:$tunnelPort"
if ($LASTEXITCODE -ne 0) {
    throw 'Could not enable the funnel. Sign in to Tailscale, and make sure Funnel is enabled for this tailnet (https://tailscale.com/kb/1223/funnel).'
}

# Ask Tailscale what name it actually published under rather than guessing it
# from the hostname - it lowercases, strips and de-duplicates names.
$dns = (& $tailscale status --json | ConvertFrom-Json).Self.DNSName
if ($dns) {
    $publicUrl = 'https://' + $dns.TrimEnd('.')
    Set-Content -Path $urlFile -Value $publicUrl -NoNewline -Encoding ASCII
    Write-TunnelStatus -Url $publicUrl -State 'on'
    Write-Host ''
    Write-Host '============================================================'
    Write-Host ' The public link is live:'
    Write-Host "   $publicUrl"
    Write-Host ''
    Write-Host ' It works only while this PC is on and connected.'
    Write-Host ' Anyone with the link reaches the login page - use strong'
    Write-Host ' passwords for every user.'
    Write-Host '============================================================'
} else {
    Write-TunnelStatus -Url '' -State 'unknown'
    Write-Host 'Funnel enabled, but Tailscale did not report a DNS name. Run: tailscale funnel status'
}
