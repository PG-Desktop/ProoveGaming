# BLAZE / X43 — Wire Protocol (as actually used)

This documents the protocol **as verified on our physical unit** via a full USB
capture, not just as the spec describes it. Where the capture disagrees with the
spec or the OEM exe, **the capture wins** — that's what the firmware really does.

- Spec reference: [`docs/x43_protocol_904a4f8.md`](../../x43_protocol_904a4f8.md) (Easen v1.0)
- OEM reference: [`docs/oem-vs750-protocol.md`](../../oem-vs750-protocol.md)
- Ground-truth capture: `C:\captures\cap-10.07.2026.pcapng` (captured 2026-07-10 with USBPcap while the OEM VS750 app configured the mouse)

## Framing

Two-part command: an 8-byte **header** followed (for write commands) by one or more
64-byte **data blocks**.

- **Header** → HID **SET_REPORT (feature)**, control transfer, `bmRequestType 0x21`:
  ```
  [0]=cmd  [1]=profile  [2]=index  [3]=0  [4]=0  [5]=status  [6]=times  [7]=cs
  ```
- **Data block(s)** → **interrupt-OUT on endpoint `0x03`**, 64 bytes each.
- **Checksum** `cs = (sum of bytes 0..6) XOR 0xAA`. Per the Easen spec the firmware
  **does not actually validate** the checksum, but we send it correctly anyway.

`status`/`times` for multi-block writes (param_1):
- `times` = number of 64-byte blocks that follow (1..3).
- `status` = bitmask of which blocks changed: `0x01`=block0, `0x02`=block1, `0x04`=block2 (`0x07`=all).
- The OEM always sends `times=3` + the whole cache. **We write single blocks**
  (`status 0x01` or `0x02`, `times 1`) so a partial update never clobbers the other
  blocks we didn't intend to touch. This is deliberate — see the RGB-safety note below.

## Command set on this unit

The device responds to the classic Easen **param protocol only**:

| Cmd | Name | Used by us |
|-----|------|-----------|
| `0x02` | SetParameter_0 (64 B) | report rate, dpi_stage, LED-effect header |
| `0x03` | SetParameter_1 (up to 3×64 B) | DPI table, colours, key matrix |
| `0x80` | SyncData / query | apply/commit **and** trigger a current-state `0x05` notification |

> **Do NOT assume `0x0B/0x0C/0x0D/0x10/0x12/0x13` work here.** Those appear in the
> OEM `VS750.exe` symbol table but belong to *other* MCU families (hid560 etc.).
> The capture confirms VS750 drives *this* PID with `0x02`/`0x03` only. Our
> param_0/param_1 approach is correct.

## param_0 (cmd 0x02) — 64-byte block

```
[0] reportrate     raw ms value: 0x01=1000Hz, 0x02=500Hz, 0x04=250Hz, 0x08=125Hz
                   ⚠️ any param_0 write (incl. an LED write) sets this — preserve it,
                   don't hardcode, or an RGB change resets the polling rate.
[1] dpi_stage      active DPI stage 0..7
[2] ledEffect      LED effect id (0x00 = All Off)
[3] ledBrightness
[4] ledSpeed
[5] ledColor
[6] ledOption      (flow direction)
[7..63] reserved
```

> ### ⚠️ Report rate is a raw ms value, not an index
> We originally encoded report rate as an index `0–3`. **Wrong.** The capture proves
> the firmware wants the millisecond value directly:
> ```
> 1000 Hz → 0x01   500 Hz → 0x02   250 Hz → 0x04   125 Hz → 0x08
> ```
> Fixed in `EasenMouseService.SetReportRateAsync`.

> ### ⚠️ Byte[2] is why changing DPI used to kill the LED
> Because `dpi_stage` and `ledEffect` share the *same* param_0 block, a naive
> "set active DPI stage" that rewrites param_0 also rewrites the LED effect byte —
> and `0x00` there means All Off. See [led.md](led.md). Our DPI-stage writes are
> now block-scoped so they never disturb LED state.

## param_1 (cmd 0x03) — three 64-byte blocks

### Block 0 (index 0) — DPI table & sensor config
```
[0] btn_debounce   (ms, ≤50)
[1] wheel_debounce  (unused)
[2] sensor_config   (unused)
[3] sensor_lift     lift-off height
[4] dpiStage_Max    number of DPI stages (max 8)
[5..8] scale_x / scale_y
[9..24]  dpi_x_value[8]  8 × 16-bit little-endian
[25..40] dpi_y_value[8]
[41..63] reserved
```
- Capture: VS750 wrote `dpiStage_Max = 7` with debounce/lift/scale all `0`.
- **Our default is 7 stages: `200, 800, 1600, 3200, 6400, 9600, 12000`** (`CreateDefaultBlock0`),
  matching the UI. A previous default of 6 stages made the UI's `12000` unselectable — fixed.

### Block 1 (index 1) — colours
```
[0..23]  DPI-stage indicator RGB (8 × RGB)  — VS750 wrote all 0
[24..47] LED-effect RGB slots (8 × RGB)     — VS750 wrote red,blue,green,yellow,cyan,magenta,orange,white
[48..63] reserved
```
- `SetLedColorAsync` writes **block 1 only** (via `WriteLedColoursBlockAsync`) so it
  can't disturb the DPI table or key matrix.

### Block 2 (index 2) — key matrix (16 keys × 4 bytes)
Entry format: `[code, b1, b2, mode]` (mode is byte 3). Byte-exact values VS750 wrote:

| Slot | Bytes | Meaning |
|------|-------|---------|
| KEY0 | `F0 00 00 02` | Mouse Left |
| KEY1 | `F1 00 00 02` | Mouse Right |
| KEY2 | `F2 00 00 02` | Mouse Middle (wheel) |
| KEY3 | `F4 00 00 02` | Mouse B5 / Forward |
| KEY4 | `F3 00 00 02` | Mouse B4 / Back |
| KEY5 | `03 00 00 0B` | **DPI+ loop** (mode 0x0B, DPI switch) |
| KEY6 | `03 00 00 06` | **Report-rate loop** (mode 0x06 — the physical polling button) |
| KEY7 | `03 00 00 0A` | **LED effect cycle** (mode 0x0A) |
| KEY8–13 | `00 00 00 02` | null mouse buttons |
| KEY14 | `00 01 00 04` | ACPI |
| KEY15 | `00 02 00 04` | ACPI |

`CreateDefaultKeyMatrix` now reproduces this exactly.
Full key-code / mode tables: [`docs/x43_protocol_904a4f8.md`](../../x43_protocol_904a4f8.md) §V.

The three "loop" buttons (KEY5 DPI, KEY6 rate, KEY7 LED) all share loop-code `b0=0x03`
and differ only by mode. **KEY6 mode `0x06` is the working factory report-rate binding
VS750 actually writes** — *not* the `[03 00 02 F0]` (mode `0xF0`/type-02) form some docs
list, which does not work on this unit. Its loop steps the polling **period** up
(1→2→4→8 ms, wraps), which reads as Hz **1000→500→250→125** — i.e. Hz *decreasing*. There
is no documented decrement/reverse loop, so a physical button that cycles Hz **min→max**
is very likely not expressible on this firmware. Do not rewrite the key matrix to chase
it without the recovery tool (`ProoveGaming.Device.Testing` → `X43Mouse.RestoreAll()`)
staged — matrix writes previously bricked the buttons.

## Notification path (device → host)

The firmware pushes a 7-byte report on `MI_01 Col04` (`0xFF03/0x00F0`) whenever a
monitored parameter changes (e.g. the physical DPI button is pressed):
```
[0]=0x05  [1]=profile  [2]=reportRate  [3]=dpi_stage  [4]=ledEffect  [5]=0  [6]=0
```
This is **change-only** — there is no *spontaneous* "current state" broadcast on
connect.

### Querying the current state (command 0x80)
To learn the current state on demand, **send command `0x80`** and the firmware
answers by pushing exactly this `0x05` report:
```
host → device:  [80 00 00 00 00 00 00 …]   (SET_REPORT feature)
device → host:  05 <profile> <rate> <dpi_stage> <ledEffect> 00 00   (Col04 notify)
```
This is the **only** way to read the active DPI stage — HID **feature reads return
all zeros** on this firmware (confirmed from the OEM app's own traffic). Verified
from a cold-start capture (`cap-10.07.2026-with-startup.pcapng`), where VS750's sole
startup query was this `0x80`. See [dpi-stage.md](dpi-stage.md) for how we consume it.

## Reproducing the capture analysis

The pcapng was parsed with a small standalone reader (no tshark needed — tshark's
`usb.capdata`/`usbhid.data` fields came back empty for these control+interrupt
transfers). Reference notes on the USBPcap record layout:

```
USBPcap record: headerLen@byte0 (u16), infoByte@16 (bit0: 0=submit,1=complete),
                device@19-20, endpoint@21, transfer@22 (0x01=interrupt,0x02=control),
                dataLen@23 (u32). Data follows the header; control adds an 8-byte
                setup packet after the header.
pcapng: Enhanced Packet Block type = 0x00000006; capturedLen at block+8+16; data at block+8+20.
```
Throwaway parser scripts lived in the session scratchpad (`parse_pcapng.py`,
`parse_reads.py`, `parse_getreport.py`). They are not part of the repo; re-derive
from the notes above if you need to re-analyse a capture.
