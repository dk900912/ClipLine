# End-to-end verification for the GitHub Release auto-update chain (plan D).
# Runs a REAL portable ClipLine.exe against a LOCAL simulated Releases API.
# It verifies: check -> download -> SHA-256 -> install -> helper replaces files
# -> app restarts. It never touches the real dist package or the network.
# NOTE: ASCII-only (Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI).
param(
    [int]$Port = 8791,
    [int]$TimeoutSec = 120
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$work = Join-Path $env:TEMP 'clipline-update-e2e'
$app = Join-Path $work 'app'
$assets = Join-Path $work 'assets'
$helperLog = Join-Path $env:TEMP 'clipline-update\helper.log'
$state = Join-Path $env:LOCALAPPDATA 'ClipLine\state.json'
$stateBackup = Join-Path $work 'state-backup.json'
$stamp = Get-Date -Format 'yyyyMMddHHmmss'
$markerName = "update-marker-$stamp.txt"

New-Item -ItemType Directory -Force -Path $work, $app, $assets | Out-Null
if (Test-Path $state) { Copy-Item $state $stateBackup -Force }
$env:CLIPLINE_UPDATE_REPO = 'dk900912/ClipLine'
$env:CLIPLINE_UPDATE_API = "http://127.0.0.1:$Port"
$env:CLIPLINE_AUTO_UPDATE = '1'

$serverProc = $null
$appProc = $null
$verdict = 'FAIL'
try {
    Stop-Process -Name ClipLine -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
    # helper.log is append-only: a previous run's "DONE" must not make this
    # run pass without actually performing the update.
    $helperDir = Split-Path -Parent $helperLog
    New-Item -ItemType Directory -Force -Path $helperDir | Out-Null
    [System.IO.File]::WriteAllText($helperLog, '')

    # ---- 1. isolated copy of the freshly built portable payload ----
    $portable = Get-ChildItem $dist -Directory -Filter 'ClipLine_*_x64_portable' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $portable) { throw 'No portable build in dist. Run tools\build-msix.ps1 first.' }
    Copy-Item (Join-Path $portable.FullName '*') $app -Recurse -Force
    $zipSrc = Get-ChildItem $dist -Filter 'ClipLine_*_x64_portable.zip' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $zipSrc) { throw 'No portable ZIP in dist.' }
    Write-Output "SOURCE_ZIP=$($zipSrc.Name)"

    # ---- 2. build a simulated NEW release asset with a unique marker ----
    $newName = 'ClipLine_9.9.9.0_x64_portable.zip'
    $newZip = Join-Path $assets $newName
    Copy-Item $zipSrc.FullName $newZip -Force
    $marker = Join-Path $work $markerName
    [System.IO.File]::WriteAllText($marker, "e2e-replaced-$stamp")
    Compress-Archive -Path $marker -Update -DestinationPath $newZip
    $hash = (Get-FileHash $newZip -Algorithm SHA256).Hash
    [System.IO.File]::WriteAllText(($newZip + '.sha256'), ($hash + '  ' + $newName + "`r`n"))
    $msix = Get-ChildItem $dist -Filter 'ClipLine_*_x64.msix' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($msix) {
        Copy-Item $msix.FullName (Join-Path $assets $msix.Name) -Force
        $mHash = (Get-FileHash $msix.FullName -Algorithm SHA256).Hash
        [System.IO.File]::WriteAllText((Join-Path $assets ($msix.Name + '.sha256')), ($mHash + '  ' + $msix.Name + "`r`n"))
    }
    Write-Output "MOCK_ASSET=$newName SHA256=$hash MARKER=$markerName"

    # ---- 3. local Releases API ----
    $node = Get-Command node.exe -ErrorAction Stop
    $serverProc = Start-Process -FilePath $node.Source -PassThru -WindowStyle Hidden `
        -ArgumentList @((Join-Path $root 'tools\mock-release-server.mjs'), $assets, "$Port")
    Start-Sleep -Seconds 2
    # Probe with a bare TCP connect: an HTTP GET here would consume the mock
    # server's single "new version" response and the app would see no update.
    $tcp = New-Object System.Net.Sockets.TcpClient
    try { $tcp.Connect('127.0.0.1', $Port); Write-Output 'SERVER_TCP=True' }
    catch { Write-Output "SERVER_TCP=False $($_.Exception.Message)" }
    finally { $tcp.Close() }

    # ---- 4. start the real app; it checks once after 8 seconds ----
    $appProc = Start-Process -FilePath (Join-Path $app 'ClipLine.exe') -WorkingDirectory $app -PassThru
    Write-Output "APP_STARTED_PID=$($appProc.Id) DIR=$app"

    # ---- 5. wait for the one-shot helper to replace files and restart ----
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $markerFound = $false
    $helperDone = $false
    while ((Get-Date) -lt $deadline) {
        if (Test-Path (Join-Path $app $markerName)) { $markerFound = $true }
        if (Test-Path $helperLog) {
            $text = Get-Content $helperLog -Raw -ErrorAction SilentlyContinue
            if ($text -and $text.Contains('DONE mode=portable')) { $helperDone = $true }
        }
        if ($markerFound -and $helperDone) { break }
        Start-Sleep -Seconds 2
    }
    Start-Sleep -Seconds 2
    $running = @(Get-Process -Name ClipLine -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($app, [StringComparison]::OrdinalIgnoreCase) })
    $newVersionContent = ''
    $markerPath = Join-Path $app $markerName
    if (Test-Path $markerPath) { $newVersionContent = (Get-Content $markerPath -Raw).Trim() }

    Write-Output "MARKER_REPLACED=$markerFound CONTENT=$newVersionContent"
    Write-Output "HELPER_DONE=$helperDone"
    Write-Output "APP_RESTARTED=$($running.Count -gt 0) COUNT=$($running.Count)"
    Write-Output "EXPECTED_CONTENT=e2e-replaced-$stamp"
    if (Test-Path $helperLog) {
        Write-Output '--- helper.log (tail) ---'
        Get-Content $helperLog -Tail 12
    }
    $verdict = if ($markerFound -and $helperDone -and ($running.Count -gt 0) -and
                   ($newVersionContent -eq "e2e-replaced-$stamp")) { 'PASS' } else { 'FAIL' }
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
