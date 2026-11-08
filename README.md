# AdbFleet

A WinUI 3 rewrite of ADB AppControl for Windows: manage apps and devices over adb, but on **any number of devices at once**.

## What changed from ADB AppControl

| ADB AppControl 1.8.6 | AdbFleet |
| --- | --- |
| One device at a time; with two or more connected it stops with `CONNECTED 2 DEVICES` | Every USB and Wi-Fi device is listed and usable at the same time |
| Actions run on the single device | Actions run on every *checked* device in parallel, with a ✓ or ✗ per device |
| Paid "Extended" features, activation and captcha | No activation; everything is available |
| Needs the ACBridge helper app on the device | Nothing is installed on the device |
| WinForms on .NET Framework 4.6.1 | WinUI 3 (Windows App SDK 2.6) on .NET 10 |

## Features

- **Devices:** USB and Wi-Fi in one list, each with its own colour, name and IP address. A dot shows whether the device answers on the network, regardless of whether adb is connected:
  - green: answers a ping, or its adb port is open
  - red: no answer
  - grey: not checked yet, or no IP to check

  Connection state shows through the dimmed text and the status line under the address. Availability is checked at startup, when a new device appears, and when you click ↻ below the list. ↻ also reconnects remembered Wi-Fi devices that are reachable but not connected. Devices are remembered between sessions. Double-click a disconnected device to retry it, and use its ⋯ menu to reconnect, disconnect, copy its address or remove it from the list. You can also connect by IP, pair with a code (Android 11+), find devices on the network (mDNS), and switch a USB device to Wi-Fi debugging.
- **Overview** (the first tab): everything about the selected device.
  - Storage, memory and battery (or up time on TVs).
  - System: model, Android version, build, security patch, CPU, serial number.
  - Display and network details, with buttons to copy them or refresh.

  Check two or more devices to compare them side by side in a sortable table, which can be copied as CSV. Values that need a look show in amber:
  - under 10% storage or memory free
  - a security patch over two years old
  - low battery
  - a proxy that is on

  Details are read in the background when a device connects and when you click ↻, and hovering a device name in the list shows a summary.
- **One row per device:** a phone connected by USB and Wi-Fi at the same time is recognised by its hardware serial and shown once. Actions keep using the connection that came first, and if that one drops, the other takes over the same row with the same colour.
- **Apps:** list user, system, disabled and removed apps; search; per-app details. Disable, enable, uninstall (optionally keeping data), restore removed system apps, launch, force stop, clear data, and save APKs (split APKs included). Presets save a selection of packages to reuse on other devices. Removing core system packages asks for confirmation first.
- **Install:** drag and drop APK, APKS, XAPK (including OBB data) and APKM files, or a folder of split APKs. Options cover replace, downgrade, grant permissions, old target SDK on Android 14+, non-streamed installs and all users. Common install failures are explained in plain language.
- **Proxy:** each device's global HTTP proxy is read when it connects and when you click ↻. A device using a proxy shows the proxy address in amber under its name. Tools → Proxy lists the proxy for each selected device, removes it, or sets a new one; the address defaults to this PC's IP on the devices' network. Remove proxy is also in the device's ⋯ menu.
- **Tools:** remote keys including a D-pad for TVs, text input, screenshots, screen mirroring and recording through scrcpy (one window per device), resolution and density changes, reboot modes, and device details.
- **Console:** run any adb command on all target devices at once. Output is colour-coded by device and quick commands can be saved. A live **logcat** view has level and text filters and can save to a file.
- **Processes:** running processes with memory use, optional auto refresh, and force stop.

## Build and run

Requirements: Windows 10 1809+ / 11, the .NET 10 SDK.

```powershell
cd AdbFleet
dotnet build -c Debug
# self-contained build, no runtime install needed on the target PC:
dotnet publish -c Release --self-contained true -o ..\publish
..\publish\AdbFleet.exe
```

### Portable build

```powershell
pwsh -File build-portable.ps1
```

This produces `dist\AdbFleet\` and `dist\AdbFleet-portable-win-x64.zip` (about 100 MB). The folder contains:
- `AdbFleet.exe`: one self-contained file with the .NET runtime and Windows App SDK inside
- `platform-tools\`: the official Google adb build
- `scrcpy\`: the latest scrcpy release
- `portable.txt`: the marker that turns on portable mode

In portable mode everything stays beside the exe: `data\` holds settings, remembered devices, presets and logs, and `downloads\` and `screenshots\` hold saved files. The bundled adb and scrcpy are always used and are found relative to the exe, so the folder keeps working from a USB stick with any drive letter. Delete `portable.txt` to store settings in `%LocalAppData%` instead.

Pass `-SingleFile:$false` for a classic multi-file folder, or `-SkipTools` to leave out adb and scrcpy.

### Finding adb

The installed (non-portable) app finds `adb.exe` in this order:
1. The path set in Settings
2. `platform-tools` next to the app or in `%LocalAppData%\AdbFleet`
3. The Android SDK
4. `PATH`
5. An existing ADB AppControl folder in Downloads

**Settings → Download latest platform-tools** fetches the official Google build. scrcpy is found the same way, or downloaded from its GitHub releases. Settings, logs and presets live in `%LocalAppData%\AdbFleet`.

## Layout

```
AdbFleet/
  Models/      Device, AppInfo, LogEntry, ...
  Services/    AdbClient (process wrapper), DeviceService (device tracking),
               Batch (parallel multi-device runner), PackageOps, InstallOps,
               DeviceOps, Downloader, Settings, LogService, Fleet (service hub)
  Views/       Apps, Install, Tools, Console, Processes, Settings pages; Connect dialog
  MainWindow   title bar, device rail, target chips, navigation
```
