# ProoveGaming X43/BLAZE — RGB LED Control Investigation

## Device Identification

| Field | Value |
|-------|-------|
| Product | ProoveGaming X43/BLAZE gaming mouse |
| USB VID | 0x04D9 (Holtek Semiconductor) |
| USB PID | 0xA245 |
| MCU | Holtek HT68FB571 |
| LED matrix | 21 columns × 6 rows = 126 LEDs |
| Layout | Row-major, left-to-right, top-to-bottom |

---

## What Works (confirmed)

- **HID feature report reads** — `HidD_GetFeature` succeeds on the main HID interface.
- **DPI value** — readable and writable via the old Easen 9-byte feature-report protocol (cmd=0x82 read, cmd=0x03 write).
- **Battery level** — reads return valid data via WriteFile + ReadFile on mi_01.
- **WriteFile** — returns `true` with the correct byte count on every candidate interface. The OS accepts every packet we send.
- **USB connection** — the device enumerates correctly as VID 04D9 / PID A245 with at least two HID interfaces (mi_00, mi_01).

---

## What Does NOT Work

**No LED has ever lit up**, across every protocol and parameter combination tried:

- All brightness levels (0x01 – 0xFF; also OEM-matched range 1–9)
- All effect modes: rainbow/flowing, always-on/static, breathing, neon
- All LED channels attempted independently: Bl (key backlight), Lg (logo), Sd (side), Lt (LED matrix screen)
- Both known protocol families (see below)
- All available HID interfaces (mi_00, mi_01, mi_02 where present)

---

## Protocols Attempted

### Protocol 1 — Old Easen 9-byte Feature-Report Protocol

This is the same protocol that successfully controls DPI on this device. It uses `HidD_SetFeature` (EP0 control transfer) for 9-byte command headers and `WriteFile` (interrupt-OUT) for 64-byte data blocks.

**Command format** (9 bytes via `HidD_SetFeature`):
```
[0x00][CMD][P1][P2][P3][P4][P5][P6][CS]
  CS = (CMD + P1 + P2 + P3 + P4 + P5 + P6) & 0xFF ^ 0xAA
```

#### Attempt 1a — Param write without ACTIVATE

Sequence sent:
1. `HidD_SetFeature` `{00 02 00 00 00 00 00 01 CS}` — announce param_0 write (1 block)
2. `WriteFile` 65 bytes — 64-byte param_0: `byte[2]=effect, byte[3]=brightness, byte[4]=speed, byte[5]=colorSlot`
3. `HidD_SetFeature` `{00 03 00 00 00 00 07 03 CS}` — announce param_1 write (3 blocks)
4. `WriteFile` × 3 — 192 bytes of param_1, LED RGB color table at bytes 88–111 (8 slots × 3 bytes)

**Result:** No LED response. `WriteFile` returned `true`. Activate command (cmd=0x01) was never sent — changes were staged in firmware but never applied.

#### Attempt 1b — cmd=0x0A RGB Runtime Control

This command controls the LED effect, brightness, speed, and color slot directly at runtime without writing persistent params. No `WriteFile` data blocks are needed — four `HidD_SetFeature` calls only.

Format per category:
```
{0x00, 0x0A, CATEGORY, VALUE, 0x04, 0x00, 0x00, 0x00, CS}
  Categories: 0x00=effect  0x01=brightness  0x02=speed  0x03=colorSlot
  Effect values (old Easen): 0x01=Flowing/Rainbow  0x02=Always-On  0x03=Neon  0x04=Breathing
```

Sequence tried:
- `{00 0A 00 01 04 00 00 00 CS}` — effect = rainbow (0x01, built-in colour cycle)
- `{00 0A 01 06 04 00 00 00 CS}` — brightness = 6 (OEM default)
- `{00 0A 02 03 04 00 00 00 CS}` — speed = 3
- `{00 0A 00 02 04 00 00 00 CS}` — effect = always-on (0x02)
- `{00 0A 03 00 04 00 00 00 CS}` — color slot = 0

Tried on all HID interfaces for the device.

**Result:** No LED response.

#### Attempt 1c — Full param write + ACTIVATE

Complete sequence:
1. cmd=0x02: write param_0 (1 block, 64 bytes) — effect, brightness, speed, colorSlot
2. cmd=0x03: write param_1 (3 blocks, 192 bytes) — LED RGB colors at bytes 88–111
3. cmd=0x01: **ACTIVATE** — `{00 01 00 00 00 00 00 00 CS}` — apply staged params

This was the first time cmd=0x01 (ACTIVATE) was ever sent. Without it, staged params are never applied to hardware.

**Result:** No LED response.

---

### Protocol 2 — 睿鼎荣 (Ruidingrong) V3.1 0xAA-framed Protocol

Based on manufacturer's protocol document `软件驱动协议-20251028-V3.1.xlsx`.

**Packet format** — 64-byte payload via `WriteFile`:
```
[LEAD][0xAA][CMD][ADDR_L][ADDR_H][LEN][CRC_L][CRC_H][RESULT][DATA…]
  LEAD = 0x00 (no HID Report ID, Windows strips it) OR 0xAA (Report ID = 0xAA)
```

Both lead-byte encodings were tried:
- **Encoding A** (`LEAD=0x00`): Windows HID layer strips byte[0]; device sees 64 bytes starting at 0xAA.
- **Encoding B** (`LEAD=0xAA`): 0xAA is a registered HID Output Report ID; device sees 65 bytes from buf[0]. This is the most likely encoding for a device where mouse movement also shares the HID interface.

#### Attempt 2a — Session + function area write (minimal safe sequence)

Command sequence:
1. `0x10` — Start session
2. `0x15` — Write function area (64 bytes) — all four LED channels (Bl/Lg/Sd/Lt) set to the target colour and effect
3. `0x11` — Stop session

Tried on all interfaces, both lead-byte encodings.

**Result:** `WriteFile` returns `true`. No ACK from device (expected `byte[7]=0x55`). No LED response.

#### Attempt 2b — Session + function area + live-sync (original full sequence)

Additional commands tried after 0x15:
- `0x2E` — Bl live-sync (252 bytes RGB565) ← **CAUTION: see Critical Discovery below**
- `0x3C` — Lt live-sync (252 bytes RGB565)
- `0x23` — Bl custom define (252 bytes RGB888)
- `0x3B` — Lt custom define (252 bytes RGB888)

**Result:** Same as 2a. No ACK, no LED response.

---

### Critical Discovery: cmd=0x2E is a RESET command

Protocol document string #32 reads (translated from Chinese):
> 发送一条复位命令：0XAA 0X2E 0X00 0X00 0X00 0X00 0X00 0X00
> ("Send a reset command: 0xAA 0x2E 0x00 0x00 0x00 0x00 0x00 0x00")

**0x2E = device REBOOT/RESET** in the old/alternate firmware. The user observed the device disconnecting and reconnecting after LED attempts — this was caused by us sending 0x2E on every call. This command was removed from subsequent attempts. Any LED change seen momentarily before the reconnect was a firmware-reset artefact, not a successful LED write.

---

## Key Diagnostic Observations

### Observation 1: No ACK reply to any 0xAA-framed command

The V3.1 protocol requires the device to echo every command back with `byte[7]=0x55` (OK) or `0x0F` (fail). We read from all interface paths after every command. **No reply received on any interface within 400 ms.** Two possible explanations:

- **A.** This device does not implement the 睿鼎荣 V3.1 protocol at all (wrong protocol for this PID).
- **B.** We are writing to a wrong endpoint — the HID interrupt-OUT we open is not the one the device listens to for these commands.

### Observation 2: WriteFile always returns true

On every interface, `WriteFile` returns `true` with the full byte count written. This only means the Windows HID stack accepted the buffer — it does not confirm the device firmware processed it. The OS will silently discard writes to endpoints that have no registered consumer.

### Observation 3: DPI read/write works; LED does not

The old Easen 9-byte feature-report protocol successfully reads and writes DPI on this device using `HidD_SetFeature` / `HidD_GetFeature`. The LED control commands (cmd=0x0A, cmd=0x02+0x03+0x01) use exactly the same transport and format. If the device were simply ignoring our feature reports entirely, DPI reads would also fail. The fact that DPI works but LED does not implies the device understands the feature-report transport, but either:

- The LED-specific commands (0x0A, 0x02, 0x03) are not implemented in this firmware version, or
- They require a precondition we have not met (a mode-switch, a different profile, etc.)

### Observation 4: Hardware — 5-pin USB connector, 4-pin cable

The PCB's external USB connector has 5 positions; the cable uses 4 pins. This is normal: standard USB 2.0 uses VBUS, D+, D−, GND (4 pins). The 5th position is typically Shield or OTG-ID and is not required for USB data or LED operation. This is **not** a suspected cause.

---

## Current Hypotheses (ordered by likelihood)

1. **The LED requires a firmware-specific command not documented in either protocol we have.**
   The mouse firmware may be a custom build of Holtek HT68FB571 with different command codes for LED than the keyboard-centric 睿鼎荣 V3.1 document describes.

2. **A prerequisite command or mode switch is missing.**
   Some devices require a "vendor mode unlock" or specific sequence (e.g., read device info, select profile) before accepting LED commands. The OEM software may send this before any LED packets.

3. **The LED interface is not exposed as a standard HID interrupt-OUT endpoint.**
   The device may use a WinUSB, vendor-class, or raw USB bulk endpoint for LED data that the Windows HID driver does not surface. The OEM software may open this endpoint directly via `SetupDi` / WinUSB APIs rather than the standard HID path.

4. **The 0xAA protocol applies to a different firmware variant.**
   The V3.1 document may describe a keyboard product line. The mouse (PID 0xA245) may ship with a completely different firmware that has different LED command codes.

---

## Questions for Manufacturer / Firmware Team

1. **Which USB interface and endpoint receives LED commands on PID 0xA245?**
   - Same interface as DPI (feature report on mi_00)?
   - A dedicated interface (mi_02) with 64-byte interrupt-OUT?
   - A non-HID interface (WinUSB / vendor-class / bulk)?

2. **Does PID 0xA245 implement the 睿鼎荣 V3.1 0xAA-framed protocol for LED?**
   - The V3.1 document describes keyboard products. Is it unchanged for this mouse?
   - If different, what protocol version or document applies?

3. **What is the exact first LED packet the OEM software sends after connecting this device?**
   - Just the first packet (cmd=0x??, payload) is enough to identify the protocol in use.

4. **Is there a required initialisation or unlock sequence before LED commands are accepted?**
   - E.g., select a profile, send a "vendor mode" command, or issue a specific read first?

5. **What effect code produces a solid single colour on this mouse's LED?**
   - V3.1 doc: 0x04 = always-on. Old Easen doc: 0x02 = always-on. Which applies?

6. **Is the LED backlight controlled via the old Easen cmd=0x0A (RGB Key Control)?**
   - Format: `{0x00, 0x0A, CATEGORY, VALUE, 0x04, 0x00, 0x00, 0x00, CS}`
   - Categories: 0x00=effect, 0x01=brightness, 0x02=speed, 0x03=colorSlot
   - Has this command been deprecated or replaced in recent firmware?

---

## Recommended Next Step: USB Packet Capture

The fastest path to resolution is capturing what the OEM ProoveGaming software actually sends.

**Procedure:**

1. Install [USBPcap](https://desowin.org/usbpcap/) + [Wireshark](https://www.wireshark.org/)
2. Open Wireshark → select the USBPcap interface that corresponds to the mouse's USB port
3. Start capture
4. Launch the OEM ProoveGaming software and connect the mouse
5. Set any LED colour or effect in the OEM software
6. Stop capture and save as `.pcapng`
7. Filter by `usb.idVendor == 0x04d9 && usb.idProduct == 0xa245` to isolate mouse traffic

**What to look for in the capture:**
- The USB endpoint address of the first OUT packet (identifies the correct interface)
- The first 8–16 bytes of the first LED-setting packet (identifies the protocol)
- Whether packets go via HID Feature (bmRequestType=0x21) or interrupt-OUT (endpoint direction=OUT)

From the capture we can read the exact interface, packet format, and byte sequence needed.

---

## Full Attempt Summary

| # | Attempt | Transport | Interface | ACK received | LED response |
|---|---------|-----------|-----------|-------------|-------------|
| 1 | Old Easen param_0 write (no ACTIVATE) | SetFeatureReport + WriteFile | mi_00 | n/a | none |
| 2 | Old Easen param_0 + param_1 write (no ACTIVATE) | SetFeatureReport + WriteFile | mi_00/mi_01 | n/a | none |
| 3 | Old Easen cmd=0x0A runtime LED control | SetFeatureReport only | all interfaces | n/a | none |
| 4 | Old Easen full write + ACTIVATE (cmd=0x01) | SetFeatureReport + WriteFile | all interfaces | n/a | none |
| 5 | V3.1 0x10+0x15+0x11 (minimal, leadByte=0x00) | WriteFile | all interfaces | none (timeout) | none |
| 6 | V3.1 0x10+0x15+0x11 (minimal, leadByte=0xAA) | WriteFile | all interfaces | none (timeout) | none |
| 7 | V3.1 0x10+0x15+0x2E+0x11 (with Bl live-sync) | WriteFile | mi_00/mi_02 | none | **device reset** |
| 8 | V3.1 0x10+0x15+0x3C+0x11 (with Lt live-sync) | WriteFile | mi_00/mi_02 | none | none |
| 9 | V3.1 0x10+0x15+0x23+0x3B+0x11 (custom define) | WriteFile | mi_00/mi_02 | none | none |
| 10 | All above, all brightness/effect combinations | both | all | none | none |
