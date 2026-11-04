# ClipLine renderer-shell smoke test. Requires an interactive desktop session.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools/smoke.ps1
# WinExe has no console; the app writes %TEMP%\clipline-smoke\smoke-result.txt.
# NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads BOM-less .ps1
# as ANSI, so non-ASCII characters break parsing.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\ClipLine.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\ClipLine.exe'
if (-not (Test-Path $exe)) {
    throw "Missing $exe - build first (bash tools/verify.sh or dotnet build src/ClipLine.App)."
}

$result = Join-Path $env:TEMP 'clipline-smoke\smoke-result.txt'
Remove-Item $result -ErrorAction SilentlyContinue

Push-Location (Split-Path $exe)
try {
    $null = & $exe --smoke 2>&1 | Out-String
    $code = $LASTEXITCODE
}
finally { Pop-Location }

if (Test-Path $result) {
    Get-Content -Raw -Encoding UTF8 $result
} else {
    "No result file produced; process exit code $code"
}
"ExitCode=$code"
exit $code
