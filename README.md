# DriverWave

Finds and installs missing drivers after a fresh Windows install.

Run it on a newly installed PC and it will:

1. **Scan your hardware** – every device Windows can see, flagging ones with no driver
   (Device Manager's yellow triangles) or running on a basic Microsoft fallback driver.
2. **Get you online if needed** – if the network adapter has no driver, it installs one from
   the built-in pack of common Ethernet and Wi-Fi drivers.
3. **Find the right drivers on Windows Update** – the same signed drivers Microsoft publishes
   for your exact hardware IDs, matched device by device.
4. **Install them** – after making a System Restore point, so you can roll back.

Newer drivers for devices that already work are listed too, unticked by default.

## Download

Get the latest `DriverWave-x.y.z-Setup.exe` from
[Releases](https://github.com/HackTheScript001/DriverWave/releases/latest).
It needs Windows 10 or 11 (64-bit) and admin rights. Nothing else is required –
.NET is built into the app.

DriverWave updates itself: when a new version is out, an **Update now** button appears
at the top of the window.

## Beta

DriverWave is in beta and free to try. Found a device it couldn't sort, or a bug?
[Open an issue](https://github.com/HackTheScript001/DriverWave/issues) and attach
`C:\ProgramData\DriverWave\DriverWave.log`.

## How it works

| Part | File |
| --- | --- |
| Hardware scan (WMI `Win32_PnPEntity`) | `src/DriverWave/Services/HardwareScanner.cs` |
| Windows Update driver search/download/install (Windows Update Agent API) | `src/DriverWave/Services/WindowsUpdateDrivers.cs` |
| Bundled network drivers (`pnputil /add-driver … /install`) | `src/DriverWave/Services/OfflineDriverPack.cs` |
| Restore point | `src/DriverWave/Services/RestorePoint.cs` |
| In-app updates from GitHub Releases | `src/DriverWave/Services/Updater.cs` |
| Installer (Inno Setup) | `installer/DriverWave.iss` |
| Build + release pipeline | `.github/workflows/release.yml` |

Every push to `main` builds a new installer and publishes it as release `v1.0.<build number>`.
