<#
.SYNOPSIS
    Resets a user's password directly in auth.users. Run locally on the
    server PC only - there is no email-based reset path (no SMTP on a
    shop PC with no guaranteed internet), so this local script running
    with physical access to the machine IS the account-recovery story.

.NOTES
    BUILD_PLAN.md item #4. Must exist before the first customer install -
    an admin lockout with no recovery path locks a shop out of its own
    books.
#>

[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\ProgramData\Shabana',
    [Parameter(Mandatory = $true)][string]$Email
)

$ErrorActionPreference = 'Stop'

$pgBin = Join-Path $InstallDir 'bin\pg\bin'
$dbPassword = Get-Content (Join-Path $InstallDir 'config\db-password.key') -Raw

$securePw  = Read-Host "New password for $Email" -AsSecureString
$confirmPw = Read-Host "Confirm new password" -AsSecureString

$plain1 = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePw))
$plain2 = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($confirmPw))

if ($plain1 -ne $plain2) { throw "Passwords do not match." }
if ($plain1.Length -lt 8) { throw "Password must be at least 8 characters." }

$env:PGPASSWORD = $dbPassword
# A typed password may contain non-ASCII; pin client encoding to UTF8 so it
# reaches the DB intact.
$env:PGCLIENTENCODING = 'UTF8'
try {
    # GoTrue hashes passwords with bcrypt via its own crypt() call using
    # pgcrypto - matching that exactly here (rather than reimplementing
    # bcrypt in PowerShell) so the row this writes is indistinguishable
    # from one GoTrue wrote itself.
    $sql = @"
update auth.users
set encrypted_password = crypt('$($plain1.Replace("'", "''"))', gen_salt('bf')),
    updated_at = now()
where email = '$($Email.Replace("'", "''"))';
"@
    $result = & (Join-Path $pgBin 'psql.exe') -U postgres -h 127.0.0.1 -d postgres -c $sql -t
    if ($LASTEXITCODE -ne 0) { throw "Failed to update password." }
} finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    $plain1 = $null; $plain2 = $null
}

Write-Host "Password updated for $Email. They can sign in with the new password immediately."
