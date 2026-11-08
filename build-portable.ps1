<#
  Builds a portable AdbFleet: a folder (and zip) that runs on any Windows 10/11 x64 PC with
  nothing installed. It bundles the official Android platform-tools (adb) and scrcpy, and a
  portable.txt marker so settings, logs, downloads and screenshots stay next to the exe.

  Usage:  pwsh -File build-portable.ps1 [-SingleFile:$false] [-SkipTools]
#>
param(
    [switch]$SkipTools,
    [bool]$SingleFile = $true
)
$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$proj    = Join-Path $root 'AdbFleet\AdbFleet.csproj'
$dist    = Join-Path $root 'dist'
$out     = Join-Path $dist 'AdbFleet'
$zipPath = Join-Path $dist 'AdbFleet-portable-win-x64.zip'
$dotnet  = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source ?? 'C:\Program Files\dotnet\dotnet.exe'

# Keep the user's own data when rebuilding in place; everything else is replaced.
$userDirs = 'data', 'downloads', 'screenshots'
$keep = Join-Path $dist ".keep-$([guid]::NewGuid().ToString('N').Substring(0,8))"
if (Test-Path $out) {
    foreach ($u in $userDirs) {
        $src = Join-Path $out $u
        if (Test-Path $src) { New-Item -ItemType Directory -Force $keep | Out-Null; Move-Item $src (Join-Path $keep $u) }
    }
    Remove-Item $out -Recurse -Force
}
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host "Publishing AdbFleet (single file: $SingleFile)" -ForegroundColor Cyan
$props = @(
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $out,
    '-p:SatelliteResourceLanguages=en',
    '-p:DebugType=none'
)
if ($SingleFile) {
    $props += '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true'
}
& $dotnet publish $proj @props
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if (-not $SkipTools) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "adbfleet-build-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Force $tmp | Out-Null
    try {
        Write-Host "Downloading Android platform-tools (official Google build)" -ForegroundColor Cyan
        $pt = Join-Path $tmp 'platform-tools.zip'
        Invoke-WebRequest 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip' -OutFile $pt
        Expand-Archive $pt -DestinationPath $out -Force          # creates $out\platform-tools

        Write-Host "Downloading scrcpy (latest GitHub release)" -ForegroundColor Cyan
        $rel   = Invoke-RestMethod 'https://api.github.com/repos/Genymobile/scrcpy/releases/latest' -Headers @{ 'User-Agent' = 'AdbFleet-build' }
        $asset = $rel.assets | Where-Object { $_.name -like 'scrcpy-win64-*.zip' } | Select-Object -First 1
        if (-not $asset) { throw "No scrcpy win64 asset in $($rel.tag_name)" }
        $sz = Join-Path $tmp 'scrcpy.zip'
        Invoke-WebRequest $asset.browser_download_url -OutFile $sz
        $sx = Join-Path $tmp 'scrcpy'
        Expand-Archive $sz -DestinationPath $sx -Force
        # The zip has one top-level folder (scrcpy-win64-vX.Y); flatten it to $out\scrcpy.
        $inner = Get-ChildItem $sx -Directory | Select-Object -First 1
        Move-Item $inner.FullName (Join-Path $out 'scrcpy')
        # scrcpy ships its own adb; remove it so only one adb version runs (AdbFleet points scrcpy at the bundled one).
        Get-ChildItem (Join-Path $out 'scrcpy') -Filter 'adb*' | Remove-Item -Force
        Get-ChildItem (Join-Path $out 'scrcpy') -Filter 'AdbWin*.dll' | Remove-Item -Force
        Write-Host "  scrcpy $($rel.tag_name)"
    }
    finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

Set-Content (Join-Path $out 'portable.txt') @"
AdbFleet portable

This file switches AdbFleet into portable mode. While it is here:
  data\         settings, remembered devices, presets and logs
  downloads\    saved APKs and logcat files
  screenshots\  screenshots and screen recordings
  platform-tools\, scrcpy\  the bundled adb and scrcpy

Copy the whole folder to move AdbFleet, with its settings, to another PC or USB stick.
Delete this file to store settings in %LocalAppData%\AdbFleet instead.
"@

# Zip without personal data, then put the kept folders back.
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Write-Host "Zipping" -ForegroundColor Cyan
Compress-Archive -Path $out -DestinationPath $zipPath -CompressionLevel Optimal
if (Test-Path $keep) {
    Get-ChildItem $keep -Directory | ForEach-Object { Move-Item $_.FullName (Join-Path $out $_.Name) }
    Remove-Item $keep -Recurse -Force
    Write-Host "Kept your data, downloads and screenshots folders" -ForegroundColor Cyan
}

$files = Get-ChildItem $out -Recurse -File
'{0} files, {1:N0} MB in {2}' -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), $out
'{0:N0} MB zip: {1}' -f ((Get-Item $zipPath).Length / 1MB), $zipPath
