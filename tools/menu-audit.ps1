# ClipLine v1 full menu audit for the tray menu and card context menu.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\menu-audit.ps1
# The WinExe has no console; the app writes %TEMP%\clipline-menu-audit\menu-audit-report.txt.
# NOTE: keep this file ASCII-only (Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI).
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\ClipLine.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\ClipLine.exe'
if (-not (Test-Path $exe)) {
    throw "Missing $exe - build first (bash tools/verify.sh)."
}

$result = Join-Path $env:TEMP 'clipline-menu-audit\menu-audit-report.txt'
$startedAt = (Get-Date).AddSeconds(-2)

Push-Location (Split-Path $exe)
try {
    $null = & $exe --menu-audit 2>&1 | Out-String
    $code = $LASTEXITCODE
}
finally { Pop-Location }

if ((Test-Path $result) -and ((Get-Item $result).LastWriteTime -ge $startedAt)) {
    $text = Get-Content -Raw -Encoding UTF8 $result
    Write-Output $text
    $dist = Join-Path $root 'dist'
    if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist -Force | Out-Null }
    Copy-Item $result (Join-Path $dist 'menu-audit-report.txt') -Force
    Write-Output "REPORT=$dist\menu-audit-report.txt"
} else {
    Write-Output "No fresh report produced; process exit code $code"
}
Write-Output "ExitCode=$code"
exit $code
