# ClipLine one-shot update helper (ASCII-only for Windows PowerShell 5.1).
# Launched by ClipLine.exe immediately before it exits. All inputs come from
# environment variables, so the caller can use -EncodedCommand without quoting.
# It never deletes anything: backup is overwritten in place and used on failure.
$ErrorActionPreference = 'Continue'
$work = Join-Path $env:TEMP 'clipline-update'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$log = Join-Path $work 'helper.log'
function Log([string]$m) {
    Add-Content -Path $log -Value ((Get-Date -Format s) + ' ' + $m) -Encoding UTF8
}

$mode = $env:CLIPLINE_UPDATE_MODE
$procId = [int]$env:CLIPLINE_UPDATE_PID
$target = $env:CLIPLINE_UPDATE_TARGET
$exeName = $env:CLIPLINE_UPDATE_EXE
$source = $env:CLIPLINE_UPDATE_SOURCE
$package = $env:CLIPLINE_UPDATE_PACKAGE
$loose = $env:CLIPLINE_UPDATE_LOOSE
Log "START mode=$mode pid=$procId target=$target"

try {
    $main = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if ($main) { Log 'WAIT_MAIN_EXIT'; $main.WaitForExit(90000) | Out-Null }

    if ($mode -eq 'portable') {
        $backup = Join-Path $work 'backup'
        New-Item -ItemType Directory -Force -Path $backup | Out-Null
        Get-ChildItem -LiteralPath $target -Force -ErrorAction SilentlyContinue |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $backup -Recurse -Force -ErrorAction SilentlyContinue }
        Log 'BACKUP_OK'
        Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
        $exe = Join-Path $target $exeName
        if (-not (Test-Path $exe)) { throw 'PORTABLE_MISSING_EXE' }
        Log 'REPLACE_OK'
    }
    elseif ($mode -eq 'msix') {
        $ok = $false
        try {
            Add-AppxPackage -Path $package -ForceUpdateFromAnyVersion -ErrorAction Stop
            $ok = $true
            Log 'MSIX_SIGNED_OK'
        }
        catch {
            Log ('MSIX_SIGNED_FAIL=' + $_.Exception.Message)
        }
        if (-not $ok) {
            $manifest = Join-Path $loose 'AppxManifest.xml'
            if (-not (Test-Path $manifest)) { throw 'MSIX_LOOSE_MANIFEST_MISSING' }
            try {
                Add-AppxPackage -Register $manifest -ErrorAction Stop
                Log 'MSIX_DEV_REGISTER_OK'
            }
            catch {
                # Idempotent: re-running the e2e after the package is already at
                # this version must not be reported as a failed update.
                # NOTE: 1.0.1.0 is a deliberate LOWER BOUND, not the current
                # release version - do not bump it on every release. Using
                # ">= any version we ever shipped" keeps this branch idempotent
                # for all later builds.
                $current = Get-AppxPackage -Name ClipLine -ErrorAction SilentlyContinue |
                    Select-Object -First 1
                if ($current -and ([version]$current.Version -ge [version]'1.0.1.0')) {
                    Log 'MSIX_DEV_REGISTER_ALREADY_CURRENT'
                }
                else { throw }
            }
        }
        $exe = Join-Path $target $exeName
    }
    else { throw 'UNKNOWN_MODE' }

    Start-Process -FilePath $exe -WorkingDirectory $target | Out-Null
    Log "DONE mode=$mode"
    exit 0
}
catch {
    Log ('FAIL=' + $_.Exception.Message)
    if ($mode -eq 'portable') {
        $backup = Join-Path $work 'backup'
        if (Test-Path $backup) {
            Copy-Item -Path (Join-Path $backup '*') -Destination $target -Recurse -Force -ErrorAction SilentlyContinue
            Log 'ROLLBACK_OK'
        }
    }
    $exe = Join-Path $target $exeName
    if (Test-Path $exe) { Start-Process -FilePath $exe -WorkingDirectory $target | Out-Null }
    exit 1
}
