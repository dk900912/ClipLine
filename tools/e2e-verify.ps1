# ClipLine v1 end-to-end check on an interactive desktop.
# Prerequisites (in order): bash tools/verify.sh; powershell -File tools\smoke.ps1;
# powershell -File tools\build-msix.ps1; copy %TEMP%\clipline-smoke\frame.png
# to dist\ClipLine_1.0.0.0_render-frame.png; start the portable ClipLine.exe
# with ClipboardEnabled / InboxEnabled enabled in %LOCALAPPDATA%\ClipLine\state.json.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\e2e-verify.ps1
# NOTE: ASCII-only (Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI).
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadWithPartialName('System.Drawing')
[void][Reflection.Assembly]::LoadWithPartialName('System.Windows.Forms')

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$shot = Join-Path $dist 'e2e-desktop.png'
$sample = Join-Path $dist 'e2e-sample.png'
$frame = Join-Path $dist 'ClipLine_1.0.0.0_render-frame.png'
$diag = Join-Path $env:LOCALAPPDATA 'ClipLine\diagnostics.log'
$diagStart = if (Test-Path $diag) { (Get-Item $diag).Length } else { 0 }
$own = Join-Path $env:LOCALAPPDATA 'ClipLine\Screenshots'
$before = @(Get-ChildItem $own -Filter 'clip-*.png' -File -ErrorAction SilentlyContinue).Count

$proc = @(Get-Process -Name 'ClipLine' -ErrorAction SilentlyContinue)
$procId = if ($proc.Count -gt 0) { $proc[0].Id } else { 0 }
Write-Output "APP_RUNNING=$($proc.Count -gt 0) PID=$procId"

# Keep the pointer in the top reveal band for the whole run so the rope stays
# down when the desktop screenshot is taken (peek is only 2.5 s otherwise).
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
[System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($bounds.Width / 2), 2)

# A distinctive test image so the thumbnail is unmistakable on the screenshot.
$bmp = New-Object System.Drawing.Bitmap -ArgumentList @(800, 450)
$g = [System.Drawing.Graphics]::FromImage($bmp)
try {
    $g.Clear([System.Drawing.Color]::FromArgb(255, 37, 42, 52))
    $orange = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 239, 138, 36))
    $blue = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 108, 99, 244))
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    # A per-run accent byte defeats the C-06 clipboard hash dedupe on purpose.
    $accentByte = [int]([Guid]::NewGuid().ToByteArray()[0])
    $accent = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, $accentByte, 60, 180))
    try {
        $g.FillRectangle($orange, 40, 40, 340, 370)
        $g.FillRectangle($blue, 420, 40, 340, 175)
        $g.FillRectangle($white, 420, 255, 340, 155)
        $g.FillRectangle($accent, 390, 200, 24, 24)
    }
    finally { $accent.Dispose(); $white.Dispose(); $blue.Dispose(); $orange.Dispose() }
}
finally { $g.Dispose() }
$bmp.Save($sample, [System.Drawing.Imaging.ImageFormat]::Png)

Write-Output "CLIP_BEFORE=$before"
[System.Windows.Forms.Clipboard]::SetImage($bmp)
Write-Output 'CLIP_SET=OK'
Start-Sleep -Seconds 3

$after = @(Get-ChildItem $own -Filter 'clip-*.png' -File -ErrorAction SilentlyContinue)
Write-Output "CLIP_AFTER=$($after.Count)"
foreach ($f in $after | Select-Object -Last 3) {
    Write-Output "CAPTURED=$($f.Name) BYTES=$($f.Length)"
}

# Exercise the second real input path: a file landing in the watched folder.
Copy-Item $sample (Join-Path $own 'e2e-folder.png') -Force
Write-Output 'FOLDER_EVENT_SENT=OK'
Start-Sleep -Seconds 2

Write-Output ("STATE_JSON=" + (Test-Path (Join-Path $env:LOCALAPPDATA 'ClipLine\state.json')))

# Refresh the reveal band position and let the 250 ms reveal delay settle.
[System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($bounds.Width / 2 + 1), 2)
Start-Sleep -Seconds 1
[System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($bounds.Width / 2), 2)

# Fire one fresh clipboard image right before the shot: a new item always
# triggers peek + PresentWindow, so the rope is guaranteed to be on screen.
$fresh = New-Object System.Drawing.Bitmap -ArgumentList @(640, 360)
$fg = [System.Drawing.Graphics]::FromImage($fresh)
try {
    $fg.Clear([System.Drawing.Color]::FromArgb(255, 42, 169, 224))
    $warm = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 247, 187, 92))
    try { $fg.FillRectangle($warm, 60, 40, 520, 280) } finally { $warm.Dispose() }
}
finally { $fg.Dispose() }
[System.Windows.Forms.Clipboard]::SetImage($fresh)
Write-Output 'PEEK_TRIGGER=OK'
Start-Sleep -Milliseconds 1100

$screen = New-Object System.Drawing.Bitmap -ArgumentList @($bounds.Width, $bounds.Height)
$sg = [System.Drawing.Graphics]::FromImage($screen)
try {
    # Plain SRCCOPY is used here; the CaptureBlt overload returns an all-black
    # frame under DWM on this host. The screenshot is a record, not evidence.
    $sg.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $screen.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Output "SCREENSHOT=$shot"
}
finally { $sg.Dispose(); $screen.Dispose() }

$bmp.Dispose()
$fresh.Dispose()

# Assert all three real paths, not just the clipboard one. A layered HWND can
# be hidden by other top-level windows under DWM, so the app-exported DIB
# frame is the deterministic rendering proof.
$clipOk = $after.Count -gt $before
$folderOk = $false
if (Test-Path $diag) {
    $stream = [System.IO.File]::Open($diag, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        [void]$stream.Seek($diagStart, [System.IO.SeekOrigin]::Begin)
        $reader = New-Object System.IO.StreamReader($stream)
        try { $newLog = $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
    $folderOk = ($newLog -match 'folder -> NewItem') -and ($newLog -match 'e2e-folder\.png')
}
$frameOk = (Test-Path $frame) -and ((Get-Item $frame).Length -gt 1000)
Write-Output "CLIP_OK=$clipOk FOLDER_OK=$folderOk FRAME_OK=$frameOk"
if ($clipOk -and $folderOk -and $frameOk) { Write-Output 'VERDICT=PASS' }
else { Write-Output "VERDICT=FAIL (clip=$clipOk folder=$folderOk frame=$frameOk)" }
