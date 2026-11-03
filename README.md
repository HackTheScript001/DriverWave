# DriverWave

**A SecondWave IT Systems product.**

Drivers, cleanup and maintenance for Windows PCs – built for the first hour after a fresh install.

## What it does

### Drivers
- **Missing drivers** – scans every device Windows can see, flags ones with no driver
  (Device Manager's yellow triangles) or on a basic fallback driver, and installs the right
  signed driver from Windows Update. If the PC has no internet because the network adapter
  has no driver, it installs one from the built-in Ethernet/Wi-Fi pack first.
- **Graphics card** – the latest driver straight from the manufacturer: NVIDIA drivers are
  looked up and installed silently; AMD and Intel run their official auto-detect tools.
- **Driver backup** – saves every installed driver to a folder or USB stick before a reinstall,
  and puts them all back afterwards with no internet needed.

### Tools
- **Uninstaller** – runs a program's own uninstaller, then finds the folders, shortcuts and
  registry keys it left behind. Registry keys are backed up and files go to the Recycle Bin.
- **System cleanup** – temp files, Windows Update leftovers, crash dumps, thumbnail and shader
  caches, browser caches and the Recycle Bin.
- **Registry cleaner** – deliberately cautious: only entries that point at programs or files
  that definitely no longer exist. Every change is backed up and can be restored.
- **Startup programs** – turn programs off so Windows starts faster (the same switch Task Manager uses).

A System Restore point is made before drivers are installed, programs are uninstalled or the
registry is changed.

## Download

Get the latest `DriverWave-x.y.z-Setup.exe` from
[Releases](https://github.com/HackTheScript001/DriverWave/releases/latest).
It needs Windows 10 or 11 (64-bit) and admin rights. Nothing else is required.

DriverWave updates itself: when a new version is out, an **Update now** button appears in the app.

## Beta

DriverWave is in beta and free to use. Found a bug or a device it couldn't sort?
[Open an issue](https://github.com/HackTheScript001/DriverWave/issues) and attach
`C:\ProgramData\DriverWave\DriverWave.log`.

Backups of anything DriverWave changes are kept in `C:\ProgramData\DriverWave\Backups`.

## Licence

DriverWave is free to use but is not open source. See [LICENSE](LICENSE).
Drivers it downloads belong to Microsoft, NVIDIA, AMD and Intel and are covered by their own licences.

---

DriverWave is owned and developed by **SecondWave IT Systems** (Bedford & Central Bedfordshire).
Contact: liam@secondwaveitsystems.cloud
