<#
  Builds a portable AdbFleet: a folder (and zip) that runs on any Windows 10/11 x64 PC with
  nothing installed. adb and scrcpy are not included; on first use the app offers to download
  the official builds into the folder, or to use copies already on the PC.

  Usage:  pwsh -File build-portable.ps1 [-SingleFile:$false] [-Version 1.0.0]
#>
param(
    [bool]$SingleFile = $true,
    [string]$Version = ""
)
$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$proj    = Join-Path $root 'AdbFleet\AdbFleet.csproj'
$dist    = Join-Path $root 'dist'
$out     = Join-Path $dist 'AdbFleet'
$zipName = if ($Version) { "AdbFleet-$Version-portable-win-x64.zip" } else { 'AdbFleet-portable-win-x64.zip' }
$zipPath = Join-Path $dist $zipName
$dotnet  = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source ?? 'C:\Program Files\dotnet\dotnet.exe'

# Folders the app creates while you use it: kept when rebuilding in place, never put in the zip.
$userDirs = 'data', 'downloads', 'screenshots', 'platform-tools', 'scrcpy'
$keep = Join-Path $dist ".keep-$([guid]::NewGuid().ToString('N').Substring(0,8))"
if (Test-Path $out) {
    foreach ($u in $userDirs) {
        $src = Join-Path $out $u
        if (Test-Path $src) { New-Item -ItemType Directory -Force $keep | Out-Null; Move-Item $src (Join-Path $keep $u) }
    }
    Remove-Item $out -Recurse -Force
}
New-Item -ItemType Directory -Force $out | Out-Null

try {
    Write-Host "Publishing AdbFleet (single file: $SingleFile)" -ForegroundColor Cyan
    $props = @(
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $out,
        '-p:SatelliteResourceLanguages=en',
        '-p:DebugType=none'
    )
    if ($Version) { $props += "-p:Version=$Version" }
    if ($SingleFile) {
        $props += '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true'
    }
    & $dotnet publish $proj @props
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    Set-Content (Join-Path $out 'portable.txt') @"
AdbFleet portable

This file switches AdbFleet into portable mode. While it is here, everything stays in this folder:
  data\            settings, remembered devices, presets and logs
  downloads\       saved APKs and logcat files
  screenshots\     screenshots and screen recordings
  platform-tools\  adb, if you let AdbFleet download it
  scrcpy\          scrcpy, if you let AdbFleet download it

adb and scrcpy are not included. The first time they are needed, AdbFleet offers to download
the official builds (from Google and from GitHub) or to use copies already on this PC.

Copy the whole folder to move AdbFleet, with its settings, to another PC or USB stick.
Delete this file to store settings in %LocalAppData%\AdbFleet instead.
"@

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Host "Zipping" -ForegroundColor Cyan
    Compress-Archive -Path $out -DestinationPath $zipPath -CompressionLevel Optimal
}
finally {
    # Put the kept folders back even if the build failed.
    if (Test-Path $keep) {
        Get-ChildItem $keep -Directory | ForEach-Object {
            $dest = Join-Path $out $_.Name
            if (-not (Test-Path $dest)) { Move-Item $_.FullName $dest }
        }
        Remove-Item $keep -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Kept your data and downloaded tools in $out" -ForegroundColor Cyan
    }
}

$files = Get-ChildItem $out -Recurse -File
'{0} files, {1:N0} MB in {2}' -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), $out
'{0:N0} MB zip: {1}' -f ((Get-Item $zipPath).Length / 1MB), $zipPath
