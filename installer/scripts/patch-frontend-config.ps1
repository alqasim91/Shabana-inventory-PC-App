<#
.SYNOPSIS
    Patches this machine's real Supabase URL and anon key into the already-
    built frontend, in place of the placeholder tokens CI baked in.

.NOTES
    Why this exists instead of editing alqasim91/Shabana-Inventory: CI
    builds ONE installer that every customer downloads, but each customer
    needs a UNIQUE anon key (see BUILD_PLAN.md item #1 - a shared key would
    let one customer forge tokens against another's tunnel URL). A Vite
    build bakes VITE_SUPABASE_URL/VITE_SUPABASE_ANON_KEY into the JS as
    literal strings at build time, so per-customer values can't come from
    that build.

    Solution: CI builds the frontend with obvious placeholder strings
    instead of real values (see .github/workflows/build-installer.yml) -
    this requires no change to the frontend's source, just different env
    var VALUES passed to a build it already supports. This script then
    does a plain text substitution across the built .js files at install
    time, once, on each customer's own machine. The cloud repo is never
    touched.

    Why plain string replacement is safe here: Vite replaces
    `import.meta.env.VITE_X` with the literal value as a quoted JS string
    constant at build time - the placeholder text ends up verbatim in the
    minified output, character-for-character, and JWTs/URLs contain no
    characters that need escaping inside a JS string literal. Minifiers
    may dedupe identical string literals but never split or transform
    their contents, so a whole-file text replace is safe. Placeholder
    tokens are unusual enough that they cannot plausibly collide with
    anything else in the bundle.
#>

[CmdletBinding()]
param(
    # The PRISTINE build shipped by the installer, still carrying the
    # placeholder tokens. Read-only: never patched, never served.
    [Parameter(Mandatory = $true)][string]$SourceDir,
    # The directory Caddy serves. Regenerated from $SourceDir on every run.
    [Parameter(Mandatory = $true)][string]$WwwDir,
    [Parameter(Mandatory = $true)][string]$SupabaseUrl,
    [Parameter(Mandatory = $true)][string]$AnonKey
)

$ErrorActionPreference = 'Stop'

# Regenerate the served copy from the pristine one FIRST. This is what makes
# the script re-runnable: patching consumes the placeholder tokens, so a
# script that patched the served copy in place worked exactly once and then
# failed forever after with "placeholder tokens not found" - including on a
# re-provision, a repair install, or an upgrade. Starting from the untouched
# source every time means the tokens are always there to replace, and the
# served copy always matches the shipped build plus this machine's values.
if (-not (Test-Path $SourceDir)) {
    throw "Pristine frontend not found at $SourceDir - the installer did not lay down www-src\."
}
if (Test-Path $WwwDir) { Remove-Item -Path $WwwDir -Recurse -Force }
New-Item -ItemType Directory -Path $WwwDir -Force | Out-Null
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $WwwDir -Recurse -Force

# Must match the placeholder values passed as VITE_SUPABASE_URL /
# VITE_SUPABASE_ANON_KEY in .github/workflows/build-installer.yml's
# frontend build step - if these ever drift out of sync, the replace
# below silently matches nothing and every install ships pointing at a
# placeholder that goes nowhere. Worth a sanity check after this runs.
$urlToken = '__SHABANA_RUNTIME_SUPABASE_URL__'
$keyToken = '__SHABANA_RUNTIME_ANON_KEY__'

$targets = Get-ChildItem -Path $WwwDir -Recurse -Include '*.js', '*.html' -File
if ($targets.Count -eq 0) {
    throw "No .js/.html files found under $WwwDir - frontend build missing or www\ path wrong."
}

$patchedUrl = 0
$patchedKey = 0

foreach ($file in $targets) {
    $content = Get-Content -Raw -Path $file.FullName -Encoding UTF8
    $original = $content

    if ($content.Contains($urlToken)) {
        $content = $content.Replace($urlToken, $SupabaseUrl)
        $patchedUrl++
    }
    if ($content.Contains($keyToken)) {
        $content = $content.Replace($keyToken, $AnonKey)
        $patchedKey++
    }

    if ($content -ne $original) {
        # UTF-8 WITHOUT BOM. The bundle contains UTF-8 (Arabic labels), so
        # ASCII would destroy it - but Windows PowerShell 5.1's
        # `Set-Content -Encoding UTF8` PREPENDS a BOM, and a BOM at the top of
        # the entry JS/HTML can break how the browser parses it. .NET's
        # UTF8Encoding($false) writes UTF-8 with no BOM, which is what we want.
        [System.IO.File]::WriteAllText($file.FullName, $content, (New-Object System.Text.UTF8Encoding($false)))
    }
}

if ($patchedUrl -eq 0 -or $patchedKey -eq 0) {
    throw "Placeholder tokens not found in the pristine frontend at $SourceDir (url matches: $patchedUrl, " +
          "key matches: $patchedKey). Either the frontend wasn't built with the placeholder env vars, or the " +
          "tokens have drifted out of sync with .github/workflows/build-installer.yml. Refusing to continue " +
          "with an unpatched frontend."
}

Write-Host "Frontend config patched: URL in $patchedUrl file(s), anon key in $patchedKey file(s)."
