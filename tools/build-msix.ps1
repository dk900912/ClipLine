# ClipLine v1 release build: signed MSIX + unsigned portable ZIP.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-msix.ps1 [-SkipBuild] [-Install]
# NOTE: keep this file ASCII-only (Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI).
# Design: this script NEVER deletes anything. It copies only the payload it needs
# from the dotnet output into a fresh staging folder, so build artifacts can be
# rebuilt repeatedly without recursive Remove-Item.
param(
    [switch]$SkipBuild,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
# Single source of truth: src/ClipLine.Domain/BuildInfo.cs drives the product
# version, the MSIX Identity version, the asset filenames and the Release tag.
$buildInfo = Get-Content (Join-Path $root 'src\ClipLine.Domain\BuildInfo.cs') -Raw
$relVer = [regex]::Match($buildInfo, '(?<![A-Za-z])Version\s*=\s*"([^"]+)"').Groups[1].Value
$msixVer = [regex]::Match($buildInfo, 'MsixVersion\s*=\s*"([^"]+)"').Groups[1].Value
if (-not $relVer -or -not $msixVer) { throw 'Cannot read version from BuildInfo.cs' }
Write-Output "[version] product=$relVer msix=$msixVer tag=v$relVer"
$stage = Join-Path $dist 'msix-stage-v1'
$portable = Join-Path $dist "ClipLine_${msixVer}_x64_portable"
$msix = Join-Path $dist "ClipLine_${msixVer}_x64.msix"
$zip = Join-Path $dist "ClipLine_${msixVer}_x64_portable.zip"
$cer = Join-Path $dist 'ClipLine.cer'
$pfx = Join-Path $dist 'ClipLine.pfx'
$pfxPassword = 'ClipLineV1Local'
$outputDir = Join-Path $root 'src\ClipLine.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64'
$kitBin = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64'
$makeappx = Join-Path $kitBin 'makeappx.exe'
$signtool = Join-Path $kitBin 'signtool.exe'

if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist -Force | Out-Null }
if (-not (Test-Path $makeappx)) { throw "Missing $makeappx (Windows SDK 10.0.26100 x64)." }
if (-not (Test-Path $signtool)) { throw "Missing $signtool (Windows SDK 10.0.26100 x64)." }

# ---- 1. build the self-contained WinUI 3 app ----
if (-not $SkipBuild) {
    $sdk = Join-Path $env:TEMP 'clipline-tools\dotnet\dotnet.exe'
    if (-not (Test-Path $sdk)) {
        $found = Get-Command dotnet.exe -ErrorAction SilentlyContinue
        if ($found) { $sdk = $found.Source } else { throw 'No .NET SDK found.' }
    }
    & $sdk build (Join-Path $root 'src\ClipLine.App\ClipLine.App.csproj') -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with $LASTEXITCODE" }
}
if (-not (Test-Path (Join-Path $outputDir 'ClipLine.exe'))) { throw "Missing ClipLine.exe in $outputDir" }

# ---- 2. stage package content (filtered copy, no deletions) ----
if (-not (Test-Path $stage)) { New-Item -ItemType Directory -Path $stage -Force | Out-Null }
if (-not (Test-Path (Join-Path $stage 'Assets'))) {
    New-Item -ItemType Directory -Path (Join-Path $stage 'Assets') -Force | Out-Null
}
$keepLanguages = @('zh-CN', 'en-US')
Get-ChildItem $outputDir -Force | Where-Object {
    if ($_.PSIsContainer) {
        $isLanguage = $_.Name -match '^[a-z]{2}(-[A-Za-z0-9]+)+$'
        -not ($isLanguage -and ($keepLanguages -notcontains $_.Name))
    }
    else { $_.Extension -ne '.pdb' }
} | ForEach-Object { Copy-Item $_.FullName -Destination $stage -Recurse -Force }
# Inject the MSIX Identity version (do NOT touch MinVersion/MaxVersionTested).
# The manifest contains Chinese text: Get-Content -Raw would decode it as ANSI
# in Windows PowerShell 5.1 and corrupt the XML (makeappx C00CEE3B).
$utf8 = New-Object System.Text.UTF8Encoding -ArgumentList $false
$manifestText = [System.IO.File]::ReadAllText(
    (Join-Path $root 'packaging\AppxManifest.xml'), [System.Text.Encoding]::UTF8)
$manifestText = [regex]::Replace($manifestText,
    '(<Identity[^>]*?\bVersion=")[\d.]+(")', '${1}' + $msixVer + '${2}')
[System.IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifestText, $utf8)

# ---- 3. rasterize E "double line" brand assets ----
[void][Reflection.Assembly]::LoadWithPartialName('System.Drawing')

function New-BrandIcon([int]$size, [string]$path) {
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList @($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 79, 91, 213))
        $glow = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(150, 42, 169, 224))
        $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
        $soft = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(205, 255, 255, 255))
        $orange = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 239, 138, 36))
        try {
            $g.FillRectangle($bg, 0, 0, $size, $size)
            $g.FillRectangle($glow, [int]($size * 0.52), 0, [int]($size * 0.48), $size)
            $lineH = [Math]::Max(1, [int]($size * 0.055))
            $x = [int]($size * 0.16)
            $w = [int]($size * 0.68)
            $g.FillRectangle($white, $x, [int]($size * 0.34), $w, $lineH)
            $g.FillRectangle($soft, $x, [int]($size * 0.60), $w, $lineH)
            $g.FillRectangle($orange, [int]($size * 0.45), [int]($size * 0.32),
                [Math]::Max(2, [int]($size * 0.10)), [int]($size * 0.32))
        }
        finally {
            $orange.Dispose(); $soft.Dispose(); $white.Dispose(); $glow.Dispose(); $bg.Dispose()
        }
    }
    finally {
        $g.Dispose()
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
    }
}

$assets = Join-Path $stage 'Assets'
New-BrandIcon 50 (Join-Path $assets 'StoreLogo.png')
New-BrandIcon 44 (Join-Path $assets 'Square44x44Logo.png')
New-BrandIcon 71 (Join-Path $assets 'SmallTile.png')
New-BrandIcon 150 (Join-Path $assets 'Square150x150Logo.png')
Write-Output "[stage] files = $((Get-ChildItem $stage -Recurse -File).Count)"

# ---- 4. code signing certificate (user scope, no admin required) ----
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq 'CN=ClipLine' } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=ClipLine' `
        -KeyUsage DigitalSignature -FriendlyName 'ClipLine v1 Local Signing' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}false')
}
Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null
$securePwd = ConvertTo-SecureString -String $pfxPassword -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $securePwd -Force | Out-Null
Write-Output "[cert] CN=ClipLine thumbprint=$($cert.Thumbprint)"

# ---- 5. pack + sign ----
$packLog = & $makeappx pack /o /d $stage /p $msix 2>&1
if ($LASTEXITCODE -ne 0) { $packLog | Select-Object -Last 20; throw "makeappx pack failed with $LASTEXITCODE" }
$signLog = & $signtool sign /fd SHA256 /f $pfx /p $pfxPassword $msix 2>&1
if ($LASTEXITCODE -ne 0) { $signLog | Select-Object -Last 20; throw "signtool sign failed with $LASTEXITCODE" }
Write-Output '[pack] makeappx + signtool sign OK'

$info = Get-Item $msix
Write-Output "MSIX=$($info.FullName)"
Write-Output "SIZE=$([Math]::Round($info.Length / 1MB, 2)) MB"
Write-Output "SHA256=$((Get-FileHash $msix -Algorithm SHA256).Hash)"

# ---- 5b. portable build: same payload, no manifest, no signing required ----
if (-not (Test-Path $portable)) { New-Item -ItemType Directory -Path $portable -Force | Out-Null }
Get-ChildItem $stage -Force | Where-Object { $_.Name -notin @('AppxManifest.xml', 'Assets') } |
    ForEach-Object { Copy-Item $_.FullName -Destination $portable -Recurse -Force }
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zip -CompressionLevel Optimal -Force
$zipInfo = Get-Item $zip
Write-Output "PORTABLE=$($zipInfo.FullName)"
Write-Output "PORTABLE_SIZE=$([Math]::Round($zipInfo.Length / 1MB, 2)) MB"
Write-Output "PORTABLE_SHA256=$((Get-FileHash $zip -Algorithm SHA256).Hash)"

# ---- 5c. GitHub Release assets: SHA-256 sidecars + asset list ----
# The updater downloads <asset> and <asset>.sha256 and refuses to install on mismatch.
$releaseAssets = @($msix, $zip)
foreach ($asset in $releaseAssets) {
    $hash = (Get-FileHash $asset -Algorithm SHA256).Hash
    $name = Split-Path $asset -Leaf
    [System.IO.File]::WriteAllText(($asset + '.sha256'), ($hash + '  ' + $name + "`r`n"), $utf8)
    Write-Output "ASSET_SHA256=$name $hash"
}
$assetList = Join-Path $dist 'release-assets.txt'
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("TAG=v$relVer")
foreach ($asset in $releaseAssets) {
    $name = Split-Path $asset -Leaf
    $lines.Add($asset)
    $lines.Add($asset + '.sha256')
}
[System.IO.File]::WriteAllLines($assetList, $lines, $utf8)
Write-Output "RELEASE_ASSETS=$assetList"

# ---- 6. optional local install ----
if ($Install) {
    Add-AppxPackage -Path $msix
    $pkg = Get-AppxPackage -Name 'ClipLine' | Select-Object -First 1
    if ($pkg) { Write-Output "INSTALLED=$($pkg.PackageFullName)" }
    else { throw 'Add-AppxPackage returned but package was not found.' }
}
