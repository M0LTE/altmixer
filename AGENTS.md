# AGENTS.md

Notes for anyone (human or agent) changing AltMixer. The README covers what it does; `docs/windows-audio.md` records
where Windows keeps each setting and what was measured. Read that before touching `src/AltMixer.Core/Audio` or `Interop`.

## Build, test, run

- `dotnet build`, `dotnet test`, `dotnet publish src/AltMixer -c Release -o publish`. **Exit AltMixer before publishing**:
  the running exe locks `publish\AltMixer.exe`, and only one copy runs at a time (single-instance mutex).
- `AltMixer.exe --dump out.txt` reads everything once against the saved desired state and writes it out without changing
  anything. Use it to check what the app sees before looking at the UI.
- UI automation IDs are `value:`, `lock:`, `restore:`, `accept:` or `handle:` followed by the setting or device id. Use
  them to drive the real app in tests.
- This is the owner's live radio setup. Don't change mic/radio levels or defaults on real devices without asking: mic
  levels feed transmit audio. The CM108 speaker output (nothing connected) is a safe device to experiment on.

## Invariants

- **Nothing above 0 dB.** A level's cap is `clamp(0, min, max)`. The UI caps the slider, `AudioSystem.ClampLevel` caps
  writes, and `Reconciler.Initial` caps first-seen values. Keep all three.
- **Desired state is keyed by setting id** (`Ids` in `Model.cs`). Renaming an id orphans users' saved values: bump
  `DesiredStore.CurrentVersion` and migrate in `Migrate()`, with a test.
- **All audio COM runs on the engine's MTA thread.** The UI only sees immutable `EngineState` and posts commands.
  Never call audio APIs inside a Windows notification callback; set the wake event and let the engine re-read.
- **Act on re-read state, not event payloads.** Windows sends duplicate notifications, and the Settings app rewrites ~20
  unrelated properties on any change.
- **No elevation.** Everything works unelevated via `IPolicyConfig`; don't add a manifest requiring admin.

## Windows audio gotchas (verified on 26100)

- **A capture session's volume is an alias of the mic's level.** Setting it moved a CM108 mic to +23 dB. Never manage
  capture session volume or mute. Output (render) session volume is independent, and per app **per device**.
- Driver dB ranges aren't to be trusted: the Icom codec mic goes to +30 dB of real digital gain, and the StreamCam
  reports +18 to +54 dB (uncalibrated).
- Windows steals the default device only when an endpoint is new. USB devices without a serial number get a new
  endpoint on every new port, which is why there's "adopt".
- `IPolicyConfig` and the per-app routing factory (vtable slots 25/26) are undocumented. Re-check them on new Windows
  builds.
- COM interop: every string parameter needs `[MarshalAs(UnmanagedType.LPWStr)]`, and every bool needs
  `[MarshalAs(UnmanagedType.Bool)]` (COM defaults to BSTR / VARIANT_BOOL). Getting either wrong corrupts silently.
- Topology walk direction: capture walks outgoing and render walks incoming, and off the endpoint's own path it stops
  at mixer nodes (otherwise a mic claims the speaker's volume).

## WPF gotchas

- The Fluent theme is `ThemeMode="System"` (WPF0001 suppressed). Use its theme brushes (`TextFillColor…`,
  `CardBackgroundFillColor…`, `SystemFillColorCaution…`), not hard-coded colours, so light and dark both work.
- Controls inherit `FontFamily`: anything inside an icon-font button (e.g. its `ContextMenu`) needs the font set back.
- `Border` has no automation peer. Use `DragHandle`, or another control, for anything that must be reachable.
- `UseWindowsForms` (for the tray `NotifyIcon`) adds implicit usings that clash with WPF; they're removed in the csproj.

## Releasing

1. Add a `## x.y.z` section at the top of `CHANGELOG.md`. The release notes come from it, and the release falls back to
   a commit list if the heading doesn't match the tag exactly. Take care not to insert it into the intro paragraph,
   which also mentions `## 0.1.0`.
2. Commit, `git tag x.y.z`, then push the commit and the tag. CI tests, builds, and publishes a release named `x.y.z`
   with exactly two files: the MSI and the single-file exe. A manual workflow run is a dry run.
3. Never change the MSI `UpgradeCode`. Versions must be numeric `x.y.z`.

Credit the owner as **Tom M0LTE <tom@m0lte.uk>** (commits, MSI Manufacturer, copyright). Don't infer other names.
`build/make-icon.ps1` regenerates `src/AltMixer/AltMixer.ico`.
