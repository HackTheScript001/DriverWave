# Offline network drivers

Anything in this folder is bundled into the installer and copied to
`C:\Program Files\DriverWave\OfflineDrivers`. When DriverWave finds a PC with
no internet and a network adapter that's missing its driver, it offers to
install these first so it can get online and fetch everything else.

## What to put here

Extracted driver folders (the ones containing `.inf`, `.sys` and `.cat` files),
one folder per vendor/family, for example:

```
OfflineDrivers/
  Intel-Ethernet/        e1d, e1r, e2f (I219-V, I225-V, I226-V)
  Intel-WiFi/            Netwtw10 / Netwtw14 (AX200, AX201, AX210, AX211, BE200)
  Realtek-Ethernet/      rt640x64 (RTL8111/8168, RTL8125 2.5GbE)
  Realtek-WiFi/          RTL8821CE, RTL8822CE, RTL8852BE
  Killer/                Killer E3100 / AX1650 / AX1675
  MediaTek-WiFi/         MT7921, MT7922
```

Only extracted `.inf` driver packages work. Installers (`.exe`/`.msi`) won't.
To pull the `.inf` files out of a vendor `.exe`, run it with `/extract` or open
it with 7-Zip.

## Licensing

Only add drivers whose licence allows redistribution. Intel and Realtek
generally allow redistributing their unmodified driver packages, but check
each package's licence file and keep it in the folder alongside the driver.

## Size

Keep the pack under about 150 MB so the installer stays a sensible download.
GitHub blocks single files over 100 MB; for large packs, enable Git LFS
(`git lfs track "OfflineDrivers/**"`).
