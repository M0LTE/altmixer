# Spike findings (2026-09-24, Windows 11 Pro 26100)

Probe: `spike/Probe` (C# .NET 10 console, raw COM interop). Run `Probe.exe <cmd>`:
`list [--all]`, `defaults`, `setdefault <dev> [all|default|comms]`, `watch [--lock] [secs]`, `bench-revert`,
`props <dev>`, `topo <dev>`, `measure <dev>`, `sessions`, `approute`, `fx <dev> [get|on|off|rewrite]`,
`setprop <dev> <fx 0|1> <fmtid> <pid> <uint|true|false>`, `format <dev>`, `snapshot <file>`, `diff <a> <b>`.
`<dev>` = index from `list`, or a substring of the name/id.

## Nothing needs elevation
All endpoint property writes via `IPolicyConfig.SetPropertyValue` (and even `IMMDevice.OpenPropertyStore(STGM_READWRITE)`)
succeed unelevated and persist to HKLM (audiosrv brokers them). Start-at-login can be a plain HKCU Run entry.

## Defaults
- `IPolicyConfig.SetDefaultEndpoint` works for all three roles. Console+Multimedia = "Default", Communications separate.
- Lock revert: ~15-30 ms from `OnDefaultDeviceChanged` to revert issued.
- Each change fires `OnDefaultDeviceChanged` **twice**; re-read actual state, debounce, never act on event payload alone.
- Never call audio APIs inside the notification callback; hand off to a worker thread.
- Windows only steals the default when an endpoint is **new** (first-ever arrival). Re-plugging a known device does not.

## Device identity
- Endpoint ID follows the USB device instance ID.
- Devices **with** a USB serial (AIOC: `USB\VID_1209&PID_7388\D4C9081B`) keep the same endpoint ID on any port.
- Devices **without** a serial (CM108 `0D8C:0012`, Icom/TI USB Audio CODEC `08BB:2902`) get an instance ID derived
  from the port (`5&2FCC3441&0&8`), so a new port = new endpoint = new "device" (and a default-device hijack).
- "Adopt": match by VID/PID + interface name when a serial-less device appears on a new port.
- Endpoint → KS filter path (contains VID/PID + instance) via `IDeviceTopology` → connector → `GetTopologyObject().GetDeviceId()`.

## Levels (dB)
- `IAudioEndpointVolume` dB ranges are driver-reported and **not** always ≤ 0:
  - USB Audio CODEC mic: [-96 .. +30], hw support 0x0 (software volume). **Measured: +30 dB label = +29.9 dB real
    digital gain** (−54.8 → −24.9 dBFS). Windows "100" on this mic = +30 dB.
  - CM108 mic [-12 .. +23] (hardware PGA; couldn't verify with no input signal).
  - Logitech StreamCam mic [+18 .. +54]: entirely above 0, labels look fictitious → "uncalibrated".
  - AIOC [-96 .. 0], behaves.
- Render session volume (`ISimpleAudioVolume`) is 0..1 linear → dB = 20·log10(v), max 0 dB, independent of the endpoint.
  It is per app **per device**.
- **Capture session volume is an alias of the endpoint level**: setting a (mmsys meter) session on the CM108 mic to
  1.0 moved the mic endpoint from −1 dB to its +23 dB maximum, bypassing any 0 dB cap. Never manage capture session
  volume/mute; manage the device level.

## Hidden hardware controls (IDeviceTopology)
Walk: capture = `EnumPartsOutgoing` from the endpoint's connected connector; render = `EnumPartsIncoming`.
- CM108: capture Volume, **AGC** (IAudioAutoGainControl), Mute; plus a **mic→speaker monitor path**
  (SuperMix: Mute + Volume [-23 .. +8]) not shown in Settings. Must be surfaced and lockable.
- USB Audio CODEC, AIOC: no hardware controls.

## Where each setting lives (endpoint property store unless noted)
| Setting | Key | Values |
|---|---|---|
| Audio enhancements | FX store `{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5` | UI4: 1 = off; absent/0 = on |
| Exclusive: allow | `{b3f8fa53-0004-438e-9003-51a46e139bfc},3` | UI4 0/1; absent = 1 |
| Exclusive: priority | `{b3f8fa53-0004-438e-9003-51a46e139bfc},4` | UI4 0/1; absent = 1 |
| Listen to this device | `{24dbb0fc-9311-4b3d-9cf0-18ff155639d4},1` | BOOL |
| Listen: target / power | `{24dbb0fc-...},0` target id (absent = default) ; `,2` BOOL (power mgmt?) |
| Device format | `{f19f064d-082c-4e27-bc73-6882a1bb8e4c},0` | WAVEFORMATEXTENSIBLE blob; set via `IPolicyConfig.SetDeviceFormat` (untested) |
| Spatial sound | `{908dba32-edff-4c28-8e45-c918561f6748},2` | blob containing provider CLSID (Windows Sonic = `{b53d940c-b846-4831-9f76-d102b9b725a0}`) — needs follow-up |
| Comms ducking | HKCU `Software\Microsoft\Multimedia\Audio\UserDuckingPreference` | DWORD 0 mute, 1 −80%, 2 −50%, 3 nothing |
| Enable/disable device | `IPolicyConfig.SetEndpointVisibility` | untested |

Settings app rewrites ~20 unrelated properties on any change → compare values against desired state, don't trust events.

## Per-app routing
- `Windows.Media.Internal.AudioPolicyConfig` factory, IID `ab3d4648-e242-459f-b02f-541c70306324`, vtable slots
  25 = Set, 26 = Get (pid, flow, role, HSTRING `\\?\SWD#MMDEVAPI#{endpoint}#{e6327cad…|2eef81be…}`). Works on 26100.
- Persisted per exe under HKCU `Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore`,
  so routes are readable for apps that aren't running.
