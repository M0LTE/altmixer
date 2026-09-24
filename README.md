# AltMixer

A Windows audio mixer for radio amateurs, built on **desired state**. Whatever you set in AltMixer is what you want.
If anything else changes it (Windows, a driver, an app, a newly plugged-in device), AltMixer flags it and restores it
with one click. Lock a setting and AltMixer puts it back automatically.

- Everything is in one pane: default devices, every output and input, running applications, system settings.
- Everything is in dB, and **nothing goes above 0 dB**. Devices whose driver allows more (the Icom/TI "USB Audio CODEC"
  mic goes to +30 dB, which is real digital gain) are flagged. Drivers whose range never reaches 0 dB (e.g. Logitech
  StreamCam, 18 to 54 dB) are marked as uncalibrated.
- Default **and** Communications devices, for output and input. Lock them and plugging something in can't steal them.
- Per device: level, mute, balance, audio enhancements, exclusive mode, sample format, spatial sound, "Listen to this
  device", enable/disable, and hidden driver controls such as the CM108's AGC and mic→speaker monitor path.
- Per application: level, mute, and which device it uses ("Chrome always goes to the Realtek").
- System: communications ducking.
- Disconnected devices are hidden, and their settings are applied again when they return. A serial-less USB device
  (CM108, Icom codec) that moves to another port looks new to Windows, so AltMixer offers to reuse its old settings.
- Runs in the notification area and can start at login (menu **⋯ → Start at login**). No admin rights needed.

## Using it

| Marker | Meaning |
|---|---|
| ⚠ ↺ ✓ | Changed outside AltMixer. ↺ restores your value; ✓ keeps the new one. Hover ⚠ for details. |
| 🔓 / 🔒 | Unlocked: changes are flagged. Locked: changes are put back automatically. |
| red ⚠ | Locked, but something keeps changing it (5 times in 30 s), so AltMixer has stopped fighting. Restore to try again. |

On first run AltMixer takes the current state as desired, except that levels above 0 dB are flagged to come down.
Desired state lives in `%APPDATA%\AltMixer\state.json`.

## Install

Download the MSI (or the portable exe) from [Releases](https://github.com/M0LTE/altmixer/releases). Requires Windows 11
x64 and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

## Build and run

Requires the .NET 10 SDK.

```
dotnet build
dotnet test
dotnet publish src/AltMixer -c Release -o publish      # single-file publish\AltMixer.exe (exit AltMixer first)
dotnet build installer/AltMixer.Installer.wixproj -c Release -p:ProductVersion=1.2.3   # MSI from publish\
publish\AltMixer.exe [--tray] [--dump out.txt]
```

`--dump` writes everything AltMixer sees (devices, settings, ranges, drift) to a file and exits without changing anything.

## Releasing

Add a `## 1.2.3` section to `CHANGELOG.md`, commit, then tag and push:

```
git tag 1.2.3 && git push origin 1.2.3
```

`.github/workflows/release.yml` tests, builds the exe and MSI, and publishes a GitHub Release named `1.2.3` with those
two files attached. The notes come from the changelog section, or from the commits since the previous tag if there's no
section. Running the workflow by hand does a dry run: everything is built and uploaded as a workflow artifact, and no
release is made.

## Layout

- `src/AltMixer.Core`: no UI.
  - `Interop/`: Core Audio COM definitions, including undocumented `IPolicyConfig` and per-app routing.
  - `Audio/AudioSystem.cs`: reads everything into a `Snapshot` and applies single settings.
  - `Audio/Topology.cs`: finds hidden driver controls.
  - `State/`: `DesiredStore` (JSON, profile-ready) and `Reconciler` (drift, locks, fight detection, adopt).
  - `Engine.cs`: a dedicated MTA thread that reacts to Windows notifications (1 s poll as backstop), reconciles, and
    enforces locks, so it keeps working while the window is hidden.
- `src/AltMixer`: WPF UI (Fluent theme, follows Windows light/dark), tray icon, start at login.
- `tests/AltMixer.Core.Tests`: reconciler and store tests.
- `installer/`: WiX MSI. `build/release-notes.ps1`: release notes.
- `spike/`: the investigation. See `spike/FINDINGS.md` for where Windows keeps each setting and what was measured.

## Known limitations

- Per-app settings apply to apps that are running. Apps that choose their own device (WSJT-X, fldigi) ignore Windows'
  per-app routing, so pick the device inside those apps.
- Locking the default output's level also undoes the keyboard volume keys, which is the lock doing its job.
- Spatial sound is restored by writing back the saved value; whether Windows applies it without re-opening the stream
  isn't verified yet.
- "Listen to this device" always plays through the default output; choosing a different target isn't supported yet.
- One profile. The store is keyed by profile so named profiles can be added.

## Licence

[GNU AGPL v3](LICENSE). Copyright (C) 2026 M0LTE.
