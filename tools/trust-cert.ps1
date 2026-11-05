# Trust the local ClipLine code-signing certificate for MSIX sideloading.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\trust-cert.ps1
# NOTE: ASCII-only (Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cer = Join-Path $root 'dist\ClipLine.cer'
if (-not (Test-Path $cer)) { throw "Missing $cer - run build-msix.ps1 first." }

$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cer)
$util = Join-Path $env:windir 'System32\certutil.exe'
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($id)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

# AppX deployment validates the signature chain as SYSTEM, so CURRENT_USER
# stores alone are not enough (0x800B0109). With admin rights we write to the
# machine TrustedPeople store, which is silent. Without admin rights we must NOT
# attempt a Root-store write: Windows pops an interactive confirmation dialog
# that hangs non-interactive sessions. Print the one-click manual step instead.
if ($isAdmin) {
    & $util -f -addstore 'TrustedPeople' $cer | Out-Null
    Write-Output "MACHINE_TRUSTEDPEOPLE_EXIT=$LASTEXITCODE"
    Write-Output 'TRUST=OK'
}
else {
    & $util -user -f -addstore 'TrustedPeople' $cer | Out-Null
    Write-Output "USER_TRUSTEDPEOPLE_EXIT=$LASTEXITCODE"
    Write-Output 'TRUST=MANUAL_REQUIRED'
    Write-Output 'STEP=Double-click dist\ClipLine.cer, choose Install Certificate,'
    Write-Output '     select Local Machine / Trusted Root Certification Authorities,'
    Write-Output '     then re-run: Add-AppxPackage -Path dist\ClipLine_1.0.0.0_x64.msix'
    Write-Output 'ALTERNATIVE=Use ClipLine_1.0.0.0_x64_portable.zip (no signing, no admin).'
}
Write-Output "THUMBPRINT=$($cert.Thumbprint)"
