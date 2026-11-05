# ClipLine real-desktop UI click verification (--ui-click).
# The app injects real mouse/keyboard input through the system input queue,
# opens the Win32 card menu, clicks items by their visible text, and writes
# %TEMP%\clipline-uiclick\ui-click-result.txt plus PNG evidence.
# Requires an interactive desktop session and no other ClipLine instance.
# NOTE: keep this file ASCII-only (Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI).
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\ClipLine.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\ClipLine.exe'
if (-not (Test-Path $exe)) {
    throw "Missing $exe - build first (bash tools/verify.sh)."
}

$others = @(Get-Process -Name ClipLine -ErrorAction SilentlyContinue)
if ($others.Count -gt 0) {
    Write-Output "WARNING: $($others.Count) ClipLine process(es) already running; stop them first for reliable hit-testing."
}

$result = Join-Path $env:TEMP 'clipline-uiclick\ui-click-result.txt'
$startedAt = (Get-Date).AddSeconds(-2)

Push-Location (Split-Path $exe)
try {
    $null = & $exe --ui-click 2>&1 | Out-String
    $code = $LASTEXITCODE
}
finally { Pop-Location }

$fresh = $false
if (Test-Path $result) {
    $fresh = (Get-Item $result).LastWriteTime -ge $startedAt
}
if ($fresh) {
    Get-Content -Raw -Encoding UTF8 $result
} else {
    Write-Output "No fresh result file produced; process exit code $code"
    $code = 1
}
Write-Output "ExitCode=$code"
exit $code
