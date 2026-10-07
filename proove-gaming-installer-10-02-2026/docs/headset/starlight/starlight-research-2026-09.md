# PG Starlight — dongle research, September 2026

**Device:** PG Starlight, USB `VID_0D8C` / `PID_036D`, over its 2.4G dongle.
**Input:** `C:\Starlight_SDK` (customer drop, 2026-09) — CmXearSDK V1.0.28.0 + the AUSDOM A3 Pro
driver/APO package.
**Dates:** 2026-09-09 / 2026-09-10. All results measured on connected hardware.

This consolidates the whole investigation. It supersedes the separate notes written during it.

---

## 1. The headline answer

**RGB over the dongle: no. Nothing available today** — and this is now a measured negative rather
than an absence of results.

Two other things came out of the work that matter more than they first look:

- **Report `0xFD` is alive and parses commands.** It was recorded as dead in August; that was a
  measurement error, corrected below.
- **A CM002X1 coprocessor is present on Starlight.** It was believed absent.

Neither unlocks lighting, but both change what we tell the vendor and what is worth trying next.

---

## 2. What the SDK folder is, and what it gives us

```
C:\Starlight_SDK\
  CmXearSDK DemoApp使用說明.pdf                  Chinese demo-app guide  <- the key document
  AUSDOM_A3_Pro\
    CmXearSDK_V1.0.28.0_C-Media\                 SDK: DLL, WPF demo app, C# wrapper, API ref rev 0.9
    CM002X1_AUSDOM_A3_PRO_Driver_APO_Package_V1.0.0.1\
      CM002X1_AUSDOM_A3_PRO_GUID.txt             the licence GUID
      ...APO_V2.0.0.1/ 01_EXTENSION/ 02_APO/     extension INF, APO INF, CmApoIce.dll, .crt
```

Two provenance facts:

1. **It is the same bundle we already had.** The 2026-08-07 vendor request lists both items under
   *"What we do not need"*. Nothing in it postdates that request, and it answers none of the four
   questions we asked.
2. **The driver/APO package targets a different product.** Every INF binds
   `USB\VID_0D8C&PID_0367&MI_00` — the AUSDOM A3 Pro. Ours is `PID_036D`. The `.cat` files are
   signed for those IDs, so the INFs cannot be retargeted without breaking their signature.

### The SDK has no LED, battery or power-state API at all

Its only lighting property, `SystemDynamicLighting`, is a **read** of the Windows Dynamic Lighting
setting (it watches `HKCU\Software\Microsoft\Lighting` via `RegNotifyChangeKeyValue`) so an app knows
when to back off. It is not an LED control.

### Property matrix, measured

`CmXearInit()` returns `CE_OK`. Both Starlight endpoints enumerate with the right VID/PID
(3468/877). Across the two endpoints: **68 x `CE_OK`, 156 x `CE_LicenseError`,
12 x `CE_UnsupportedError`.**

Working (identity, format, state):

```
ControllerDisplayName    PG Starlight
USBDeviceInstanceID      USB\VID_0D8C&PID_036D\00006F64096B22E7
DriverVersion            10.0.26100.8972      JackType  7 Headphone / 2 Microphone
DefaultFormat            2ch/16bit/48000Hz (render), 1ch/16bit/48000Hz (capture)
VolumeRange              [-28.375 .. -0.941 dB, step 0.188]
VolumeLevel(master)      -8.946 dB            VolumeLevelScalar  0.51
PeakValue(master)        0.195                <- live output level
MutingState              0                    DefaultDevice(Multimedia/Communication)  1 / 1
ForegroundProcessID      22140                SystemDynamicLighting  1
```

`VolumeLevel`, `VolumeLevelScalar` and `PeakValue` take a channel index (`-1` = master);
`DefaultDevice` takes a `CmDefaultRole`. Read bare they return `CE_ParameterError`, which looks like
a failure and is not.

Blocked (all 78 effect properties): playback EQ, mic EQ, noise reduction, echo cancellation,
beamforming, magic voice, virtual bass, adaptive volume, speaker shifter, environment effects,
headphone-speaker virtualisation, compressor, noise gate, voice clarity, audio brilliant, head
tracking, vigilance mode.

### Writes that land, licence-free

Verified by read-back, every value restored:

| Property | Result |
| --- | --- |
| `MutingState` | **lands** — mute/unmute in software |
| `VolumeLevelScalar` | **lands** — 0.51 to 0.30 and back |
| `SystemEffectsControl` | **lands** — the Windows "audio enhancements" switch |
| `DefaultDevice` (both roles) | **lands** |
| `CmPlayAudio` per channel | **lands** — tone played FRONT_LEFT then FRONT_RIGHT |
| `DefaultFormat` | `CE_PropertyError` — **correctly rejected** |

`DefaultFormat` is not a broken setter. `IsFormatSupported` was queried directly: `44100` no,
`96000` no, `24-bit` no, `48000/16/2ch` yes. **The headset is 48 kHz/16-bit/stereo only**, so there
is no format choice to expose.

### The licence: GUID found, activation succeeds, effects stay locked

The Chinese demo-app guide gives the bring-up order and names the licence file:

> Install the Driver and APO, copy the `*.crt` beside `CmXearSDK.dll`, Initialize, **fill in the
> License, i.e. the GUID; different devices have different GUIDs, found in the supplied attachment
> `XXXX_GUID.txt`**, Get endpoints, **License Information shows whether the APO is mounted**, then
> test (*"currently only EQ can be tested"*).

`CM002X1_AUSDOM_A3_PRO_GUID.txt` matches that pattern and holds
`158FD125-36BD-4A73-9E07-518869EDD288`. Measured:

```
CmActivateLicense(158fd125-...) -> CE_OK
CmGetLicenseSettings()          -> CE_OK  158fd125-...   (registered globally)
per endpoint: ActivatedLicense (empty), SupportedLicenseCategory 0
```

**Why that is not yet a verdict.** Per the guide, the per-endpoint License Information is *the APO
reporting on itself*. With no APO mounted there is nothing to report, whatever the hardware holds.
So "hardware not licensed" and "APO not installed" remain indistinguishable. Confirmed on this
machine: `CmApoIce.dll` is absent from `System32`, and the Starlight endpoint's `FxProperties` holds
only the stock Microsoft APO GUIDs.

To test it, the APO would have to be bound by hand — registering C-Media's five CLSIDs
(`{22677C03..07}-5EC6-443C-8082-8D4A1662F4A3`) and pointing the endpoint's `FxProperties` at them.
`BUILTIN\Administrators` already holds `SetValue` on that key, so it needs elevation but no
ownership change. **Not run.** It is a diagnostic only, never a shipping mechanism.

---

## 3. Report `0xFD` is alive — a correction

**Recorded (2026-08-07):** *"Report `0xFD` returns nothing, in every state"* — 32 zero bytes across
30 samples spanning a power cycle.

**Corrected:** the measurement was wrong, not the device. `0xFD` is a **request/response** HID-to-I2C
bridge, per C-Media's *CM002X1 Xear Audio Technology Authentication User Guide* sections 3.2-3.4:

```
Set Output Report (32 B)   FD 12 7A <map_addr> <len> ...        host asks the device to I2C-read
        (device performs the I2C transaction into a 16-byte buffer)
Get Input Report  (32 B)   FD 13 7A <map_addr> <len> <data...>  host collects the result
```

`0x11` = I2C write, `0x12` = I2C read, `0x13` = return data. Slave address fixed at `0x7A`,
`len` <= 16.

The August test called `HidD_GetInputReport` **without ever sending the `0x12` request**, so it read
an unpopulated buffer. Zeros is exactly what that returns.

Sent correctly: **256 / 256** sub-addresses echo `FD 13 7A <map> <len>`; **162** return non-zero
data.

### The control that proves a chip is there

The header echo alone only proves the *firmware* implements the bridge. The divergence below proves
a *chip* is attached — an I2C slave that never ACKs leaves the bus pulled high, reading all `0xFF`:

```
slave 0x7A (real)  C7-B5-C8-1A-CA-94-CB-05-CD-8B-CE-44-D0-D0-D0-7A
slave 0x10         FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF
slave 0x50         FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF
slave 0x00         FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF
```

**A real I2C device acknowledges at `0x7A`** — the address C-Media documents for the CM002X1. The
claim that Starlight is Jieli silicon with the licensing part absent is withdrawn.

**Do not mine the payload bytes.** They are unstable across reads. For a challenge/response
authentication part that is expected — the real flow writes a cipher with `0x11` first, and we
deliberately never do.

Buffer semantics, established separately: it is **consume-on-read** (one-shot), and a planted value
survives at least 300 ms.

---

## 4. RGB: the measured negative

The August round sent 12 framings x 16 operation modes and concluded *"none changed the LED"*. The
vendor letter states the flaw itself: *"we cannot distinguish rejected from ignored"* — `0xFD` is a
declared output report, so Windows accepts any write whether or not firmware parses it.

`0x12` supplied the missing signal: a **response oracle**. Plant a marker with a documented `0x12`
read, send the candidate command, read once. Marker intact means nothing observable happened.

**Positive control** — essential, because a broken write path looks identical to an ignored command.
A known-handled `0x12` at sub-address `0x5A`, pushed through the identical plant/send/read code, came
back `FD-13-7A-5A-10-...`: **DETECTED**. The oracle works end to end.

**Result** — all three documented LED commands, both framings:

| Command | `FD <cmd> ...` | `FD FF <cmd> ...` |
| --- | --- | --- |
| `0x03` Configuration (Static, 1 colour, 0 ms) | marker intact | marker intact |
| `0x04` Colour Array (offset 0, RGB) | marker intact | marker intact |
| `0x01` Start | marker intact | marker intact |

Every write returned `ok` at the HID layer and produced **no response at all**, while `0x12` answers
reliably through the same path.

**Confirmed visually** (2026-09-10, by the user, interactive run pausing after every command with the
headset in view): **no change to the LED at any point** — no colour, effect, brightness or flicker,
on either framing.

**Conclusion: the dongle's command dispatch on `0xFD` handles the I2C bridge and does not recognise
the CM002X1 LED command set.** Both signals agree. This is the first time the lighting negative rests
on a measurement rather than on the absence of a visible change.

### The hardware CAN do what we want — we just cannot reach it

Confirmed by the user 2026-09-10: the headset's own RGB button still cycles effects normally —
**wave, and static with a colour**. So the firmware supports static colour lighting. The gap is
purely the host-side command path, not a hardware limitation.

### What this does not prove

1. **Silence alone would not be proof of non-handling** — these commands are fire-and-forget and are
   not documented to reply. That gap is closed here by the visual result, but it would reopen for any
   command that acts without replying and without a visible effect.
2. **The LED is on the HEADSET; USB reaches only the DONGLE.** The CM002X1 LED specification was
   written for a wired demo board where the chip on the USB bus is the chip driving the LED. Here any
   command must be relayed over 2.4G, and nothing documents that relay. A well-formed command could
   be parsed by the dongle and never reach the headset. **We cannot distinguish that from "wrong
   command set" with anything we can send.**
3. Only 2 of the 12 framings were retried with the oracle.

---

## 5. Capability summary, from the app's perspective

### Available today, no vendor dependency

All of it is Windows Core Audio underneath, which `AudioEndpointService` already calls directly.
**None of it needs the Xear SDK.**

| Capability | Route |
| --- | --- |
| Mute / unmute | `IAudioEndpointVolume.SetMute` — already declared in the COM interop, never called |
| Set as default device | `IPolicyConfig` |
| Headset / channel test | play a WAV to a chosen endpoint + channel mask |
| Live level meters (mic + output) | `IAudioMeterInformation.GetPeakValue` |
| Push instead of poll *(internal)* | `IAudioEndpointVolumeCallback`, `IMMNotificationClient`. `DeviceDetailsViewModel.Audio.cs` says *"Core Audio gives us no cheap change notification on a specific endpoint here"* and polls at 1 s; `AudioMuteMonitor` polls at 2 s. **That comment is wrong.** |

Not worth building: **audio format selection** — the headset is 48 kHz/16-bit/stereo only.

**Nothing from this list is implemented.** A mute toggle was built on 2026-09-10 and then removed at
the user's request; the app is unchanged.

### Blocked on the vendor

Every locked effect property in section 2. **This is the only group where the Xear SDK would earn its
dependency** — they are DSP performed by CMAPO_ICE and we cannot write them ourselves.

### Not reachable

| | Why |
| --- | --- |
| RGB effects | Section 4 |
| Battery over the dongle | Nothing reports it. The Bluetooth transport already shows a level. |
| Power on/off state | Endpoints stay `ACTIVE` with the headset switched off. |
| The headset's own mic-mute button | Firmware never sends the Mute usage it declares on `COL01`. |
| Volume wheel position | Never reported. Effective loudness is Windows level x wheel position, and the two cannot see each other. |

### Why not integrate CmXearSDK for the working half

It would add a 1.3 MB unredistributable native DLL, a certificate file, a C-Media licensing
dependency and a second device model keyed by the SDK's own endpoint GUID rather than
[[Container ID]] — for capabilities we can call directly.

---

## 6. What to send the vendor

The 2026-08-07 letter should be **corrected before it is sent again**: its section 1 ("Report `0xFD`
returns nothing, in every state") is now known to be wrong, and leading with a bad measurement
undercuts the questions that are still open.

The replacement position:

1. Report `0xFD` is alive and parses commands (`0x12`/`0x13` answer reliably); a CM002X1
   acknowledges at I2C `0x7A`.
2. The documented CM002X1 LED commands produce **no response on any framing tried**, and move
   nothing — with a positive control proving the test detects handled commands. The headset's own
   button still drives wave and static-colour effects, so the hardware is capable.
   **What is the LED command framing for `PID_036D`, and does it go to the dongle or the headset?**
   *A capture of your own software driving the LED answers both at once and needs nothing written.*
3. **Reissue the extension and APO package signed for `VID_0D8C&PID_036D`** — the supplied one binds
   `PID_0367` and cannot be retargeted without breaking its catalogue.
4. **Confirm `158FD125-36BD-4A73-9E07-518869EDD288`** is the correct licence GUID for this product.

The original four questions (power state, mic-mute state, LED framing, battery) all remain open.

---

## 7. Reproducing

```
starlight console                          menu: coprocessor probe (read-only), audio, RGB verdict
starlight xear [--activate [guid]] [cosmo] full SDK property matrix
starlight xear-write --i-mean-it           the licence-free setters, each restored
starlight rgb-oracle --i-mean-it [--colour RRGGBB]
```

All under `dotnet run --project ./src/ProoveGaming.Device.Testing --`. The write arms refuse without
`--i-mean-it`. The Xear DLL is loaded in place from the customer drop, so no vendor binary enters
this repo.

**Guardrails observed throughout, and to keep:** never `0x02` (LED Stop erases the flash LED table),
never `0x11` (I2C write to the authentication coprocessor), never a blind opcode sweep.

### The one experiment left, and why it was not run

With a working oracle the real command set could be **mapped** — send a byte, see whether the device
answers — instead of guessed. It is the highest-value experiment remaining on this device.

It is **not authorised**. The plan's stop stands: *"We will not sweep unknown opcodes at a vendor
channel that persists to flash — that risks putting customer hardware into a state we cannot
recover."* BLAZE is the precedent (`docs/mouse/blaze/`), where writing a vendor channel the wrong way
bricked the buttons. If it is ever authorised: denylist first (`0x02`, `0x11`, reset/DFU ranges),
zero payloads, one opcode at a time with an oracle read and an enumeration check between each, and
abort on the first sign the device stops enumerating.

### Still outstanding, and cheap

Run `starlight xear cosmo` with the **Cosmo dongle** plugged in. Cosmo carries the CM002X1 too; if it
reports a non-zero `SupportedLicenseCategory` with this same certificate and no APO installed, that
alone settles whether the licence reading needs the APO — with no registry work at all. Cosmo was on
Bluetooth only throughout this investigation.
