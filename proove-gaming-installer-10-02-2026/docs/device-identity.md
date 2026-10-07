# Identifying a physical device — what is available, and what is missing

Reference for deciding how the app tells one physical unit from another (two identical mice, or the
same mouse after it is moved to a different USB port). Written to be readable without the codebase,
so it can be shared with the team and with the hardware vendor.

Measured on real hardware 2026-08-04 with `HidIdentityProbe` in `ProoveGaming.Device.Testing`.

---

## TL;DR

- **PG Taipan reports no USB serial number.** Neither does BLAZE (tested earlier, two units).
- Because there is no serial, Windows **derives** the device's Container ID from the **USB port it is
  plugged into**. That is what the app currently uses as its per-device key.
- Consequence: **move the device to another port and the app sees a brand-new device** — its saved
  settings do not follow it.
- Nothing else currently exposed by the device is unique per unit. Manufacturer, product name,
  firmware revision and every HID descriptor value are identical across units of the same model.
- **The clean fix is firmware-side:** the device should report an `iSerialNumber` in its USB device
  descriptor that is **unique per unit**. Everything else is a workaround — and the word *unique* is
  load-bearing, see below.
- **The PG Cosmo headset shows a serial makes the Container ID port-stable.** Measured 2026-08-05,
  it reports a real `iSerialNumber` (`00006F64096B22E7`), so its Container ID is derived from the
  serial **not** the port, and moving it between USB ports keeps the same `deviceKey` and the same
  saved settings. That half of the claim holds.
- **But that serial is NOT unique, so it does not give per-unit identity.** Measured 2026-08-07: the
  **PG Starlight** headset (PID `0x036D`, Jieli silicon) reports the **byte-identical** string
  `00006F64096B22E7` as Cosmo (PID `0x0334`, C-Media silicon). Two different products from two
  different SoC vendors share one serial, so it is an **ODM firmware constant**, not a unit
  identifier. See [Serial numbers that are not unique](#serial-numbers-that-are-not-unique).

---

## What the app keys on today

```
device identity = VendorID + ProductID + deviceKey
```

where `deviceKey` is the Windows **Container ID** — a GUID that is the same across all of one
physical device's HID interfaces and different for a second, identical device. That part works: two
identical mice plugged in at once are correctly separated.

**For a device with no serial number, the weakness is stability, not uniqueness.** The Container ID
is generated from the device's position on the USB bus, so it changes when the device changes port —
but two such devices plugged in at once still occupy different ports and are still separated.

**For a device that reports a serial, the trade is reversed, and that is the more dangerous shape.**
The Container ID stops moving between ports, but it is now derived from the serial — so if the
firmware ships a *constant* serial rather than a per-unit one, two units of that model plausibly
collapse onto one `deviceKey`. Port-derived IDs fail loudly and recoverably (settings appear to
vanish, replugging the original port brings them back); a shared serial fails silently and
permanently (two devices quietly share one set of settings). See
[Serial numbers that are not unique](#serial-numbers-that-are-not-unique) — this is not
hypothetical, it is what this ODM currently ships.

---

## Full inventory — PG Taipan (VID `0x248A`, PID `0x8373`, 2.4G receiver)

### Layer 1 — values our code can read today (HidSharp, cross-platform)

| Field | Value | Unique per unit? |
| --- | --- | --- |
| `VendorID` | `0x248A` (9354) | No — per vendor |
| `ProductID` | `0x8373` (33651) | No — per model/transport |
| `ReleaseNumber` (bcdDevice) | `2.15` (raw `0x0215`) | No — per firmware build |
| **`SerialNumber`** | **not present** (read throws `DeviceIOException`) | — **this is the gap** |
| `Manufacturer` | `Telink` | No |
| `ProductName` | `PG Taipan` | No |
| `FriendlyName` | `PG Taipan` | No |
| `DevicePath` / `FileSystemName` | `\\?\hid#vid_248a&pid_8373&mi_00&col04#8&6be9053&0&0003#{...}` | Port-derived |
| Max report length in/out/feature | varies per collection (e.g. 33/33/8) | No — per model |

### Layer 2 — values Windows exposes underneath (not used by us today)

| Property | Value | Unique per unit? |
| --- | --- | --- |
| `DEVPKEY_Device_ContainerId` | `{9C251105-6F01-11F1-8A84-60FF9EA44D63}` | Yes, but **port-derived** (see below) |
| `DEVPKEY_Device_InstanceId` (USB node) | `USB\VID_248A&PID_8373\6&19573F1B&0&1` | Port-derived |
| `DEVPKEY_Device_HardwareIds` | `USB\VID_248A&PID_8373&REV_0215` | No — model + firmware rev |
| `DEVPKEY_Device_BusReportedDeviceDesc` | `PG Taipan` | No |
| `DEVPKEY_Device_LocationInfo` | `Port_#0001.Hub_#0002` | Port, by definition |
| `DEVPKEY_Device_Address` | `1` | Port, by definition |
| `DEVPKEY_Device_Parent` | `USB\VID_05E3&PID_0608\...` (the hub) | Port, by definition |

**Nothing in either layer is both unique per unit and stable across ports.**

---

## How to tell whether a device has a serial number

Look at the last segment of its USB instance ID:

```
USB\VID_248A&PID_8373\6&19573F1B&0&1      <- contains '&'  => NO serial; Windows made this up
                      ^^^^^^^^^^^^^^^^      from the hub/port topology

USB\VID_13D3&PID_3602\000000000           <- no '&'        => this IS the device's own
                      ^^^^^^^^^              iSerialNumber string
```

The second example is a real device on the same test machine. Note its serial is `000000000` — a
placeholder the manufacturer never programmed. So even when a serial *is* present it may not be
unique; it has to be both **present and distinct per unit** to be usable.

This is the single check to run on any new device: if the suffix contains `&`, that model cannot be
identified across ports without a firmware change.

---

## Options, in order of preference

### 1. Firmware reports a unique `iSerialNumber` — the real fix

The device descriptor gains a per-unit serial string, **programmed at manufacture with a distinct
value per unit**. Then:

- Windows uses it in the instance ID and the Container ID becomes **stable across ports**.
- Our existing `deviceKey` mechanism keeps working unchanged — no app rework, the value simply stops
  moving.
- Standard, portable, and works for any host software, not just ours.

**Requires the vendor.** This is the ask — and it must be phrased as *per-unit unique*, not merely
*present*, because this ODM already ships a serial that is neither.

#### Serial numbers that are not unique

Measured 2026-08-07. **PG Cosmo** (PID `0x0334`, "THI Headphone", C-Media silicon) and **PG
Starlight** (PID `0x036D`, "Jieli Technology") both report:

```
iSerialNumber = 00006F64096B22E7
```

Byte-identical, across two different products built on two different SoC vendors' silicon. That is
an ODM firmware constant baked into a shared image, not an identifier.

**What this does and does not break:**

- **Across models it is harmless.** Container ID incorporates VID/PID, so a Cosmo and a Starlight
  still get different Container IDs despite the shared serial. Nothing collides today.
- **Within one model it is very likely worse than having no serial at all.** Two units of the same
  model would derive their Container ID from the same VID, PID *and* serial — so they would plausibly
  share a `deviceKey` and silently share saved settings. Unlike the port-derived case, that collision
  is invisible to the user and cannot be cleared by replugging.

**Not measured:** we have one Cosmo and one Starlight, not two of either. The same-model collision is
strongly implied by how Windows derives the Container ID, but it has not been observed. Test it
before any customer plausibly owns two.

**Consequence for the vendor ask.** "Report an `iSerialNumber`" is not sufficient and this ODM is the
proof — they already do, and it does not identify anything. The request has to be that the value is
**distinct per manufactured unit**, and it is worth asking how the current constant is assigned,
since a shared image is exactly what produces one.

### 1b. Use the Bluetooth address — already available, no firmware change

Measured 2026-08-04: in **Bluetooth** mode the TAIPAN enumerates as a BLE device whose interface
path embeds its Bluetooth MAC address:

```
{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&02248a_pid&8266_rev&0001_d101779c590f&col04
                                                                       ^^^^^^^^^^^^
                                                                       D1:01:77:9C:59:0F
```

A BLE address **is** a per-unit identifier, and unlike the Container ID it does not depend on which
port anything is plugged into — it is a property of the device itself. So over Bluetooth we already
have exactly the stable unique key that is missing over USB and 2.4G.

Limits: it is only visible while the mouse is in Bluetooth mode, so it cannot identify the same unit
when it is cabled or on the dongle. It could still be useful as a *correlation* key — recorded once
over Bluetooth and matched to the unit's other transports — but that only works for users who use
Bluetooth at all, so it is not a general answer on its own. Worth knowing it exists.

### 2. Read a unique ID over the vendor protocol

Many wireless MCUs already hold a unique identifier — a chip UID, or the MAC/pairing address the
2.4G and Bluetooth links need in order to bond. If the firmware can expose that over the existing
config protocol, we could read it at connect time and use it as the device key.

Stable across ports and requires no manufacturing change, but is proprietary to us and needs a
protocol command that does not currently exist. **Requires the vendor** to confirm whether such an
identifier is readable, and via which command.

### 3. Write our own identifier into device flash

Generate a GUID on first connect, store it in a spare region of the device's configuration memory,
read it back on subsequent connects.

Fully under our control and stable across ports, but: needs a guaranteed-free flash region the
firmware will not overwrite, is lost by a factory reset, and — critically for Taipan — currently
**cannot be written over the 2.4G dongle at all** (see
`docs/mouse/taipan/taipan-dongle-config-findings-2026-08-04.md`), so it would only work over the
cable.

### 4. Accept it and scope settings to the model, not the unit

Where per-unit identity is not essential, key on `VendorID + ProductID` only. This is already what
**presets** do — they are shared across all units of one model deliberately.

Costs nothing and is immune to the whole problem, but cannot support "these two identical mice have
different button layouts".

---

## What we would like from the vendor

1. **Does the firmware program a unique `iSerialNumber`?** Today Taipan reports none, so Windows
   falls back to port-derived identity and a user moving the receiver to another USB port loses the
   association with their saved settings. Can a per-unit serial be added to the USB device
   descriptor?
2. **Is there a readable unique ID in the chip?** A UID, or the 2.4G/Bluetooth pairing address. If
   one is readable over the existing protocol, which command and address returns it?
3. **If neither is possible:** is there a region of configuration flash guaranteed not to be touched
   by firmware or by a factory reset, that host software may use to store an identifier?

---

## How to reproduce these findings

```
dotnet run --project src/ProoveGaming.Device.Testing
```
with `HidIdentityProbe.Run(onlyVendorId: 0x248A)` selected in `Program.cs`. Read-only. Omit the
argument to dump every HID device on the machine.

## Still to verify

The port-derivation of the Container ID is documented Windows behaviour for devices without a
serial, and everything measured here is consistent with it — but it has not been *demonstrated* on
this hardware. To prove it: note the Container ID above, move the receiver to a different USB port,
re-run the probe, and compare. A changed GUID confirms it directly.

## Related

- `docs/plans/add-per-device-identity/` — the plan that introduced `deviceKey`; it flagged this
  exact risk under "Container-ID stability" and left the serial check as an open question. This
  document is that answer: **no serial, on either model tested.**
- `docs/CONTEXT.md` — `Container ID`, `deviceKey`.
