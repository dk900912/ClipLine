# End-to-end verification for the MSIX channel of plan D auto-update.
# Runs a REAL unpacked ClipLine.exe (with AppxManifest.xml, so IsPackaged=true)
# against a LOCAL simulated Releases API. Verifies: check -> download -> SHA-256
# -> helper Add-AppxPackage (signed path) -> developer-mode Register fallback
# -> app restart. Requires Windows Developer Mode (AllowDevelopmentWithoutDevLicense=1).
# NOTE: ASCII-only (Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI).
param(
    [int]$Port = 8792,
    [int]$TimeoutSec = 120
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'msix-stage-v1'
$work = Join-Path $env:TEMP 'clipline-update-e2e-msix'
$app = Join-Path $work 'app'
$assets = Join-Path $work 'assets'
$helperLog = Join-Path $env:TEMP 'clipline-update\helper.log'
$state = Join-Path $env:LOCALAPPDATA 'ClipLine\state.json'
$stateBackup = Join-Path $work 'state-backup.json'

New-Item -ItemType Directory -Force -Path $work, $app, $assets | Out-Null
if (Test-Path $state) { Copy-Item $state $stateBackup -Force }
$env:CLIPLINE_UPDATE_REPO = 'dk900912/ClipLine'
$env:CLIPLINE_UPDATE_API = "http://127.0.0.1:$Port"
$env:CLIPLINE_AUTO_UPDATE = '1'

$serverProc = $null
$verdict = 'FAIL'
try {
    Stop-Process -Name ClipLine -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
    # helper.log is append-only: a previous run's "DONE" must not make this
    # run pass without actually performing the update.
    $helperDir = Split-Path -Parent $helperLog
    New-Item -ItemType Directory -Force -Path $helperDir | Out-Null
    [System.IO.File]::WriteAllText($helperLog, '')
    if (-not (Test-Path (Join-Path $stage 'AppxManifest.xml'))) {
        throw 'No MSIX staging dir. Run tools\build-msix.ps1 first.'
    }
    $devMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
    Write-Output "DEV_MODE=$devMode"
    if ($devMode -ne 1) { throw 'Developer Mode is required for the MSIX Register fallback.' }

    # ---- isolated copy of the packaged layout (manifest present => MSIX channel) ----
    Copy-Item (Join-Path $stage '*') $app -Recurse -Force
    Write-Output "APP_MANIFEST=$(Test-Path (Join-Path $app 'AppxManifest.xml'))"

    # ---- simulated NEW release: a signed .msix plus its SHA-256 sidecar ----
    $msix = Get-ChildItem $dist -Filter 'ClipLine_*_x64.msix' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $msix) { throw 'No MSIX in dist.' }
    $newName = 'ClipLine_9.9.9.0_x64.msix'
    $newMsix = Join-Path $assets $newName
    Copy-Item $msix.FullName $newMsix -Force
    $hash = (Get-FileHash $newMsix -Algorithm SHA256).Hash
    [System.IO.File]::WriteAllText(($newMsix + '.sha256'), ($hash + '  ' + $newName + "`r`n"))
    Write-Output "MOCK_ASSET=$newName SHA256=$hash SOURCE=$($msix.Name)"

    # ---- local Releases API ----
    $node = Get-Command node.exe -ErrorAction Stop
    $serverProc = Start-Process -FilePath $node.Source -PassThru -WindowStyle Hidden `
        -ArgumentList @((Join-Path $root 'tools\mock-release-server.mjs'), $assets, "$Port")
    Start-Sleep -Seconds 2
    $tcp = New-Object System.Net.Sockets.TcpClient
    try { $tcp.Connect('127.0.0.1', $Port); Write-Output 'SERVER_TCP=True' }
    catch { Write-Output "SERVER_TCP=False $($_.Exception.Message)" }
    finally { $tcp.Close() }

    # ---- real app; it checks once after 8 seconds ----
    $appProc = Start-Process -FilePath (Join-Path $app 'ClipLine.exe') -WorkingDirectory $app -PassThru
    Write-Output "APP_STARTED_PID=$($appProc.Id) CHANNEL=msix"

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $helperDone = $false
    $registered = $false
    $pkgVersion = ''
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $helperLog) {
            $text = Get-Content $helperLog -Raw -ErrorAction SilentlyContinue
            if ($text -and $text.Contains('DONE mode=msix')) { $helperDone = $true }
        }
        $pkg = Get-AppxPackage -Name ClipLine -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($pkg) {
            $pkgVersion = "$($pkg.Version)"
            if ([version]$pkgVersion -ge [version]'1.0.1.0') { $registered = $true }
        }
        if ($helperDone -and $registered) { break }
        Start-Sleep -Seconds 2
    }
    Start-Sleep -Seconds 2
    $running = @(Get-Process -Name ClipLine -ErrorAction SilentlyContinue)
    if (Test-Path $helperLog) {
        Write-Output '--- helper.log (tail) ---'
        Get-Content $helperLog -Tail 14
    }
    Write-Output "HELPER_DONE=$helperDone"
    Write-Output "PACKAGE_VERSION=$pkgVersion REGISTERED=$registered"
    Write-Output "APP_RESTARTED=$($running.Count -gt 0) COUNT=$($running.Count)"
    $verdict = if ($helperDone -and $registered -and ($running.Count -gt 0)) { 'PASS' } else { 'FAIL' }
}
catch {
    Write-Output "EXCEPTION=$($_.Exception.Message)"
    $verdict = 'FAIL'
}
finally {
    Stop-Process -Name ClipLine -Force -ErrorAction SilentlyContinue
    if ($serverProc -and -not $serverProc.HasExited) { Stop-Process -Id $serverProc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 400
    if (Test-Path $stateBackup) { Copy-Item $stateBackup $state -Force }
    Write-Output 'STATE_RESTORED=True'
}
Write-Output "VERDICT=$verdict"
if ($verdict -ne 'PASS') { exit 1 }
exit 0
