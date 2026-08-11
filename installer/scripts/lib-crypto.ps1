<#
.SYNOPSIS
    File encryption for off-site backups. Dot-sourced by backup.ps1 and
    decrypt-backup.ps1 - not meant to be run directly.

.NOTES
    WHY THIS EXISTS
    An off-site copy is handed to somebody else's disk - Google Drive,
    OneDrive, a Backblaze bucket. The dump is the entire business: every
    customer, every price, every payment. It leaves this machine encrypted or
    it does not leave.

    THE CONSTRUCTION
    AES-256-CBC, then HMAC-SHA256 over (salt || iv || ciphertext) - encrypt
    then MAC, verified before a single byte is decrypted. Two independent keys
    from one passphrase via PBKDF2-SHA256, 200k iterations, 32-byte random salt
    per file.

    Not AES-GCM, which would be the modern choice: AesGcm is .NET Core only and
    this has to run on the Windows PowerShell 5.1 that ships with Windows.
    Encrypt-then-MAC with separate keys is the correct construction for what is
    available here.

    FILE FORMAT
      magic     8   "SHBNAES1"
      salt     32
      iv       16
      mac      32   HMAC-SHA256(salt || iv || ciphertext)
      ciphertext...

    The MAC is what makes this more than obfuscation: a cloud provider - or
    anyone who reaches the bucket - cannot flip bytes in a backup and have the
    restore accept it.
#>

# Deliberately NO Set-StrictMode here. This file is dot-sourced into backup.ps1,
# and strict mode would apply to everything that runs after the dot-source in
# the CALLER's scope - quietly changing the rules for a script that was written
# and tested without it. A library does not get to impose that on its callers.

$script:ShbMagic = [System.Text.Encoding]::ASCII.GetBytes('SHBNAES1')
$script:ShbIter  = 200000

function Get-ShbKeys {
    param([Parameter(Mandatory)][string]$Passphrase, [Parameter(Mandatory)][byte[]]$Salt)
    # One PBKDF2 stream, split: bytes 0-31 encrypt, 32-63 authenticate. Reusing
    # a single key for both would be a real weakness, and deriving twice costs
    # twice the time for no benefit.
    $kdf = New-Object System.Security.Cryptography.Rfc2898DeriveBytes(
        $Passphrase, $Salt, $script:ShbIter, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $material = $kdf.GetBytes(64)
        return @{ Enc = $material[0..31]; Mac = $material[32..63] }
    } finally { $kdf.Dispose() }
}

function Protect-ShbFile {
    param(
        [Parameter(Mandatory)][string]$InFile,
        [Parameter(Mandatory)][string]$OutFile,
        [Parameter(Mandatory)][string]$Passphrase
    )
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $salt = New-Object byte[] 32; $rng.GetBytes($salt)
        $iv   = New-Object byte[] 16; $rng.GetBytes($iv)
    } finally { $rng.Dispose() }

    $keys = Get-ShbKeys -Passphrase $Passphrase -Salt $salt
    $aes = [System.Security.Cryptography.Aes]::Create()
    try {
        $aes.KeySize = 256; $aes.Mode = 'CBC'; $aes.Padding = 'PKCS7'
        $aes.Key = $keys.Enc; $aes.IV = $iv

        # Streamed, never Get-Content: these dumps carry scanned attachments and
        # can be hundreds of megabytes. Reading one into a PowerShell byte array
        # is how a nightly job turns into an out-of-memory failure at 3am.
        $tmp = "$OutFile.cipher.tmp"
        $in  = [System.IO.File]::OpenRead($InFile)
        $out = [System.IO.File]::Create($tmp)
        try {
            $enc = $aes.CreateEncryptor()
            try {
                $cs = New-Object System.Security.Cryptography.CryptoStream($out, $enc, 'Write')
                try { $in.CopyTo($cs, 1MB); $cs.FlushFinalBlock() } finally { $cs.Dispose() }
            } finally { $enc.Dispose() }
        } finally { $in.Dispose(); $out.Dispose() }

        # MAC covers salt+iv too, so neither can be swapped without detection.
        $hmac = New-Object System.Security.Cryptography.HMACSHA256(,$keys.Mac)
        try {
            $cipherIn = [System.IO.File]::OpenRead($tmp)
            try {
                $hmac.TransformBlock($salt, 0, $salt.Length, $null, 0) | Out-Null
                $hmac.TransformBlock($iv,   0, $iv.Length,   $null, 0) | Out-Null
                $buf = New-Object byte[] 1048576
                while (($n = $cipherIn.Read($buf, 0, $buf.Length)) -gt 0) {
                    $hmac.TransformBlock($buf, 0, $n, $null, 0) | Out-Null
                }
                $hmac.TransformFinalBlock([byte[]]@(), 0, 0) | Out-Null
                $mac = $hmac.Hash
            } finally { $cipherIn.Dispose() }
        } finally { $hmac.Dispose() }

        $final = [System.IO.File]::Create($OutFile)
        try {
            $final.Write($script:ShbMagic, 0, $script:ShbMagic.Length)
            $final.Write($salt, 0, $salt.Length)
            $final.Write($iv,   0, $iv.Length)
            $final.Write($mac,  0, $mac.Length)
            $cipherIn = [System.IO.File]::OpenRead($tmp)
            try { $cipherIn.CopyTo($final, 1MB) } finally { $cipherIn.Dispose() }
        } finally { $final.Dispose() }
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    } finally { $aes.Dispose() }
}

function Unprotect-ShbFile {
    param(
        [Parameter(Mandatory)][string]$InFile,
        [Parameter(Mandatory)][string]$OutFile,
        [Parameter(Mandatory)][string]$Passphrase
    )
    $in = [System.IO.File]::OpenRead($InFile)
    try {
        $header = New-Object byte[] 88   # 8 magic + 32 salt + 16 iv + 32 mac
        if ($in.Read($header, 0, 88) -ne 88) { throw 'File is too short to be a Shabana encrypted backup.' }
        for ($i = 0; $i -lt 8; $i++) {
            if ($header[$i] -ne $script:ShbMagic[$i]) { throw 'Not a Shabana encrypted backup (bad header).' }
        }
        $salt = $header[8..39]; $iv = $header[40..55]; $expected = $header[56..87]

        $keys = Get-ShbKeys -Passphrase $Passphrase -Salt $salt

        # Verify BEFORE decrypting. Decrypting first and checking after would
        # mean writing attacker-chosen bytes to disk and hoping nobody uses them.
        $hmac = New-Object System.Security.Cryptography.HMACSHA256(,$keys.Mac)
        try {
            $hmac.TransformBlock($salt, 0, $salt.Length, $null, 0) | Out-Null
            $hmac.TransformBlock($iv,   0, $iv.Length,   $null, 0) | Out-Null
            $buf = New-Object byte[] 1048576
            while (($n = $in.Read($buf, 0, $buf.Length)) -gt 0) {
                $hmac.TransformBlock($buf, 0, $n, $null, 0) | Out-Null
            }
            $hmac.TransformFinalBlock([byte[]]@(), 0, 0) | Out-Null
            $actual = $hmac.Hash
        } finally { $hmac.Dispose() }

        # Fixed-time compare - no early exit on the first differing byte.
        $diff = 0
        for ($i = 0; $i -lt 32; $i++) { $diff = $diff -bor ($actual[$i] -bxor $expected[$i]) }
        if ($diff -ne 0) {
            throw 'Wrong passphrase, or this backup has been altered or corrupted. Nothing was written.'
        }

        $in.Position = 88
        $aes = [System.Security.Cryptography.Aes]::Create()
        try {
            $aes.KeySize = 256; $aes.Mode = 'CBC'; $aes.Padding = 'PKCS7'
            $aes.Key = $keys.Enc; $aes.IV = $iv
            $dec = $aes.CreateDecryptor()
            try {
                $out = [System.IO.File]::Create($OutFile)
                try {
                    $cs = New-Object System.Security.Cryptography.CryptoStream($out, $dec, 'Write')
                    try { $in.CopyTo($cs, 1MB); $cs.FlushFinalBlock() } finally { $cs.Dispose() }
                } finally { $out.Dispose() }
            } finally { $dec.Dispose() }
        } finally { $aes.Dispose() }
    } finally { $in.Dispose() }
}
