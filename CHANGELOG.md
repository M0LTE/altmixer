# Changelog

Release notes are taken from the section matching the tag (e.g. `## 0.1.0`). If there isn't one, the release lists the
commits since the previous tag instead.

## 0.3.0

- **App icon**: three mixer faders on a blue tile, used for the exe, window, taskbar, Start menu shortcut and
  Add/Remove Programs.
- The tray icon now uses the app icon, with an amber dot when a setting has changed outside AltMixer.
- Repository tidy: the investigation tooling is gone; its findings live on in `docs/windows-audio.md`.

## 0.2.0

- **Reorder devices** by dragging the handle on each card, or with ↑ / ↓ on a focused handle. The order is saved,
  unplugged devices keep their place, and a device adopted on a new USB port takes its old position.
- The drag handle is exposed to screen readers as a thumb control.

## 0.1.0

First release.

- **One pane, all in dB, nothing above 0 dB.** Levels a driver allows above unity (the Icom/TI USB Audio CODEC mic goes
  to +30 dB of real digital gain) are flagged, and drivers whose range never reaches 0 dB are marked uncalibrated.
- **Desired state.** Whatever you set in AltMixer is what you want. Changes made elsewhere are flagged with one-click
  restore (↺) or keep (✓), or put back automatically when the setting is locked (🔒).
- **Default and communications devices**, output and input. Lock them and plugging in a new device can't take over.
- **Per device**: level, mute, balance, audio enhancements, exclusive mode, sample format, spatial sound, "Listen to this
  device", enable/disable, and hidden driver controls such as the CM108's AGC and mic-to-speaker monitor path.
- **Per app**: output level and mute per device, and which device the app uses.
- **Communications ducking**.
- **USB devices on a new port**: serial-less interfaces (CM108, Icom codecs) that move port can take their old settings
  with them.
- Tray icon, start at login, no admin rights needed. Disconnected devices are hidden and re-applied when they return.
